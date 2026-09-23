using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Susu.Windows;

namespace Susu.Probes.PluginHost;

/// <summary>
/// Plugin-host child (F00 prototype of `susu.exe --plugin-host`). Authenticates the
/// server, then runs one runtime per plugin on a single engine thread. The IPC reader
/// only enqueues work; it never touches a runtime.
/// </summary>
internal sealed class ChildHost : IRuntimeCallbacks
{
    private const int MaxApiPerCall = 4;
    private readonly Stream pipe;
    private readonly object writeGate = new();
    private readonly RuntimeFactory factory;
    private readonly string engineName;
    private readonly string pluginRoot;
    private readonly BlockingCollection<Action> work = new(boundedCapacity: 1024);
    private readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (string PluginId, int CallId)> apis = [];
    private long sequence;
    private int logBudget = 20;
    private long logWindow = Stopwatch.GetTimestamp();

    private sealed class Slot(string pluginId, LoadPayload load, ExecutionBudget budget)
    {
        public string PluginId { get; } = pluginId;
        public LoadPayload Load { get; } = load;
        public ExecutionBudget Budget { get; } = budget;
        public IPluginRuntime? Runtime { get; set; }
        public Dictionary<int, Call> Calls { get; } = [];
    }

    private sealed class Call(string requestId, string grant)
    {
        public string RequestId { get; } = requestId;
        public string Grant { get; } = grant;
        public HashSet<int> Apis { get; } = [];
    }

    private ChildHost(Stream pipe, RuntimeFactory factory, string engineName, string pluginRoot)
    { this.pipe = pipe; this.factory = factory; this.engineName = engineName; this.pluginRoot = pluginRoot; }

    /// <summary>Exit codes: 0 clean shutdown, 20 server PID mismatch, 21 nonce missing, 22 connect failure, 23 protocol error.</summary>
    public static int Run(string pipeName, int expectedServerPid, RuntimeFactory factory, string engineName)
    {
        string? nonce = Console.In.ReadLine();
        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length > 128) { Console.WriteLine("nonce-missing"); return 21; }
        // Overlapped: a synchronous handle serializes the reader's pending ReadFile with engine-thread writes.
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { client.Connect(3000); }
        catch (Exception error) { Console.WriteLine($"connect-failed {error.GetType().Name} 0x{error.HResult:X8}"); return 22; }
        int serverPid = ContainerHost.GetServerPid(client.SafePipeHandle);
        if (serverPid != expectedServerPid) { Console.WriteLine($"server-pid-mismatch expected={expectedServerPid} actual={serverPid}"); return 20; }
        var host = new ChildHost(client, factory, engineName, AppContext.BaseDirectory);
        host.Send(new Frame(Protocol.Version, FrameTypes.Hello, Payload: Protocol.Element(new HelloPayload(nonce, "plugin-host", engineName, Environment.ProcessId), PluginJson.Default.HelloPayload)));
        var engineThread = new Thread(host.EngineLoop) { IsBackground = true, Name = "susu-engine-0" };
        engineThread.Start();
        try
        {
            while (Protocol.Read(client) is { } frame)
            {
                if (frame.Type == FrameTypes.Shutdown) break;
                host.Dispatch(frame);
            }
            return 0;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or EndOfStreamException or IOException)
        {
            Console.WriteLine($"protocol-error {error.GetType().Name}: {error.Message}");
            return 23;
        }
        finally
        {
            host.work.CompleteAdding();
            engineThread.Join(2000);
        }
    }

    private void Send(Frame frame) => Protocol.Write(pipe, frame with { Sequence = Interlocked.Increment(ref sequence) }, writeGate);

    private void Dispatch(Frame frame)
    {
        switch (frame.Type)
        {
            case FrameTypes.Load:
                var load = frame.Payload!.Value.Deserialize(PluginJson.Default.LoadPayload)!;
                work.Add(() => DoLoad(frame.PluginId!, load));
                break;
            case FrameTypes.Invoke:
                var invoke = frame.Payload!.Value.Deserialize(PluginJson.Default.InvokePayload)!;
                work.Add(() => DoInvoke(frame.PluginId!, frame.RequestId!, frame.Grant!, invoke));
                break;
            case FrameTypes.ApiResult:
                var result = frame.Payload!.Value.Deserialize(PluginJson.Default.ApiResultPayload)!;
                work.Add(() => DoSettle(result));
                break;
            case FrameTypes.Cancel:
                var cancel = frame.Payload!.Value.Deserialize(PluginJson.Default.CancelPayload)!;
                work.Add(() => DoCancel(frame.PluginId!, cancel.CallId));
                break;
            case FrameTypes.Stats:
                work.Add(() => Send(new Frame(Protocol.Version, FrameTypes.Stats, Payload: Protocol.Element(
                    new StatsPayload(slots.Values.Sum(s => s.Runtime?.EngineBytes ?? 0), slots.Count), PluginJson.Default.StatsPayload))));
                break;
            default:
                throw new InvalidDataException($"Frame type '{frame.Type}' is not accepted by the plugin host.");
        }
    }

    private void EngineLoop()
    {
        foreach (var action in work.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception error) { Console.WriteLine($"engine-error {error.GetType().Name}: {error.Message}"); }
        }
        foreach (var slot in slots.Values) { slot.Runtime?.Dispose(); slot.Budget.Dispose(); }
    }

    private void DoLoad(string pluginId, LoadPayload load)
    {
        var timer = Stopwatch.StartNew();
        string? error = null;
        if (slots.ContainsKey(pluginId)) error = "already loaded";
        else
        {
            var slot = new Slot(pluginId, load, new ExecutionBudget());
            error = Build(slot);
            if (error is null) slots[pluginId] = slot;
            else { slot.Runtime?.Dispose(); slot.Budget.Dispose(); }
        }
        long bytes = slots.TryGetValue(pluginId, out var loaded) ? loaded.Runtime!.EngineBytes : 0;
        Send(new Frame(Protocol.Version, FrameTypes.Loaded, PluginId: pluginId,
            Payload: Protocol.Element(new LoadedPayload(error is null, error, timer.Elapsed.TotalMilliseconds, bytes), PluginJson.Default.LoadedPayload)));
    }

    private string? Build(Slot slot)
    {
        string root = Path.GetFullPath(Path.Combine(pluginRoot, slot.Load.Directory));
        if (!root.StartsWith(Path.GetFullPath(pluginRoot), StringComparison.OrdinalIgnoreCase)) return "plugin directory outside host resources";
        int memory = Math.Clamp(slot.Load.MemoryMiB, 16, 256);
        try { slot.Runtime = factory(slot.PluginId, root, memory, slot.Budget, this); }
        catch (Exception error) { return error.Message; }
        string? message = slot.Runtime.Load(slot.Load.Entry, out int status);
        return status == 2 ? "load exceeded execution budget" : message;
    }

    private void DoInvoke(string pluginId, string requestId, string grant, InvokePayload invoke)
    {
        if (!slots.TryGetValue(pluginId, out var slot) || slot.Runtime is null)
        {
            SendFailed(pluginId, requestId, invoke.CallId, "bad_response", "plugin not loaded");
            return;
        }
        if (slot.Calls.Count >= 2 || slot.Calls.ContainsKey(invoke.CallId))
        {
            SendFailed(pluginId, requestId, invoke.CallId, "bad_response", "runtime busy");
            return;
        }
        slot.Calls[invoke.CallId] = new Call(requestId, grant);
        int status = slot.Runtime.Invoke(invoke.CallId, invoke.Capability, invoke.Request.GetRawText(), invoke.Config.GetRawText());
        After(slot, status, invoke.CallId);
    }

    private void DoSettle(ApiResultPayload result)
    {
        if (!apis.Remove(result.ApiId, out var owner)) return; // late result after cancel/rebuild: dropped
        if (!slots.TryGetValue(owner.PluginId, out var slot) || slot.Runtime is null) return;
        if (slot.Calls.TryGetValue(owner.CallId, out var call)) call.Apis.Remove(result.ApiId);
        int status = slot.Runtime.Settle(result.ApiId, result.Ok, result.Value.GetRawText());
        After(slot, status, owner.CallId);
    }

    private void DoCancel(string pluginId, int callId)
    {
        if (!slots.TryGetValue(pluginId, out var slot) || slot.Runtime is null || !slot.Calls.Remove(callId, out var call)) return;
        // Host-side cancellation is authoritative: report now, reject pending API
        // promises, abort the plugin-visible signal; any later completion is dropped.
        SendFailed(pluginId, call.RequestId, callId, "cancelled", null);
        int status = 0;
        foreach (int apiId in call.Apis)
        {
            apis.Remove(apiId);
            status = slot.Runtime.Settle(apiId, false, "{\"name\":\"AbortError\",\"kind\":\"cancelled\"}");
            if (status == 2) break;
        }
        if (status != 2) status = slot.Runtime.Abort(callId);
        After(slot, status, callId);
    }

    private void After(Slot slot, int status, int callId)
    {
        if (status != 2) return;
        // Budget exceeded: fail every in-flight call of this runtime, then rebuild it.
        var failed = slot.Calls.Keys.ToArray();
        foreach (var (id, call) in slot.Calls)
        {
            foreach (int apiId in call.Apis) apis.Remove(apiId);
            SendFailed(slot.PluginId, call.RequestId, id, "timeout", "execution budget exceeded");
        }
        slot.Calls.Clear();
        var timer = Stopwatch.StartNew();
        slot.Runtime?.Dispose();
        slot.Runtime = null;
        string? error = Build(slot);
        Send(new Frame(Protocol.Version, FrameTypes.RuntimeFault, PluginId: slot.PluginId,
            Payload: Protocol.Element(new FaultPayload(error is null ? "budget-rebuilt" : $"rebuild-failed: {error}", failed, timer.Elapsed.TotalMilliseconds), PluginJson.Default.FaultPayload)));
    }

    private void SendFailed(string pluginId, string requestId, int callId, string kind, string? detail)
        => Send(new Frame(Protocol.Version, FrameTypes.Failed, requestId, pluginId,
            Payload: Protocol.Element(new CompletedPayload(callId, false, null, new PluginErrorInfo(kind, detail)), PluginJson.Default.CompletedPayload)));

    int IRuntimeCallbacks.ApiCall(string pluginId, int apiId, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        int callId = root.GetProperty("callId").GetInt32();
        if (!slots.TryGetValue(pluginId, out var slot) || !slot.Calls.TryGetValue(callId, out var call)) return 1;
        if (call.Apis.Count >= MaxApiPerCall) return 1;
        call.Apis.Add(apiId);
        apis[apiId] = (pluginId, callId);
        // Grant and request identity come from this host's call table, never from plugin input.
        Send(new Frame(Protocol.Version, FrameTypes.ApiCall, call.RequestId, pluginId, Grant: call.Grant,
            Payload: Protocol.Element(new ApiCallPayload(apiId, callId, root.GetProperty("op").GetString() ?? "", root.GetProperty("args").Clone()), PluginJson.Default.ApiCallPayload)));
        return 0;
    }

    void IRuntimeCallbacks.Completed(string pluginId, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        int callId = root.GetProperty("callId").GetInt32();
        if (!slots.TryGetValue(pluginId, out var slot) || !slot.Calls.Remove(callId, out var call)) return; // late: dropped
        foreach (int apiId in call.Apis) apis.Remove(apiId);
        bool ok = root.GetProperty("ok").GetBoolean();
        PluginErrorInfo? error = null;
        if (!ok)
        {
            var e = root.GetProperty("error");
            error = new PluginErrorInfo(e.GetProperty("kind").GetString() ?? "bad_response", e.TryGetProperty("detail", out var d) ? d.GetString() : null);
        }
        Send(new Frame(Protocol.Version, ok ? FrameTypes.Completed : FrameTypes.Failed, call.RequestId, pluginId,
            Payload: Protocol.Element(new CompletedPayload(callId, ok, ok ? root.GetProperty("result").Clone() : null, error), PluginJson.Default.CompletedPayload)));
    }

    void IRuntimeCallbacks.Log(string pluginId, string json)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - logWindow > Stopwatch.Frequency) { logWindow = now; logBudget = 20; }
        if (logBudget-- <= 0) return; // PLAN 4.5.4: at most 20 per second, excess dropped
        Send(new Frame(Protocol.Version, FrameTypes.Log, PluginId: pluginId, Payload: Protocol.Element(json)));
    }
}
