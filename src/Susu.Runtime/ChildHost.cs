using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Susu.Contracts;

namespace Susu.Runtime;

/// <summary>
/// Plugin-host child process (`susu.exe --plugin-host`, ARCHITECTURE 4/6). Authenticates the
/// server by PID, then runs one runtime per loaded package on a single engine thread; the pipe
/// reader thread only enqueues work and never touches a runtime (two fixed threads, F04.2).
/// Control messages (Cancel, Shutdown) are dispatched ahead of queued Invoke/ApiResult work
/// (F04.3: control priority).
/// </summary>
public sealed class ChildHost : IRuntimeCallbacks
{
    private const int MaxApiPerCall = ProtocolLimits.MaxHostOperationsPerCall;
    private readonly Stream pipe;
    private readonly object writeGate = new();
    private readonly RuntimeFactory factory;
    private readonly string engineName;
    private readonly string pluginRoot;
    private readonly string hostBuild;
    private readonly PriorityWorkQueue work = new();
    private readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (string PluginId, int CallId)> apis = [];
    private long sequence;
    private volatile bool stopping;

    private sealed class Slot(string pluginId, LoadPayload load, ExecutionBudget budget)
    {
        public string PluginId { get; } = pluginId;
        public LoadPayload Load { get; } = load;
        public ExecutionBudget Budget { get; } = budget;
        public IPluginRuntime? Runtime { get; set; }
        public Dictionary<int, Call> Calls { get; } = [];
    }

    private sealed class Call(string requestId, string jobId, string grant)
    {
        public string RequestId { get; } = requestId;
        public string JobId { get; } = jobId;
        public string Grant { get; } = grant;
        public HashSet<int> Apis { get; } = [];
    }

    private ChildHost(Stream pipe, RuntimeFactory factory, string engineName, string pluginRoot, string hostBuild)
    { this.pipe = pipe; this.factory = factory; this.engineName = engineName; this.pluginRoot = pluginRoot; this.hostBuild = hostBuild; }

    /// <summary>Exit codes: 0 clean shutdown, 20 server PID mismatch, 21 nonce missing, 22 connect failure, 23 protocol error.</summary>
    public static int Run(string pipeName, int expectedServerPid, RuntimeFactory factory, string engineName, string hostBuild, TextReader? nonceSource = null)
    {
        string? nonce = (nonceSource ?? Console.In).ReadLine();
        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length > 128) { Console.Error.WriteLine("nonce-missing"); return 21; }
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { client.Connect(3000); }
        catch (Exception error) { Console.Error.WriteLine($"connect-failed {error.GetType().Name} 0x{error.HResult:X8}"); return 22; }
        int serverPid = Susu.Windows.ContainerHost.GetServerPid(client.SafePipeHandle);
        if (serverPid != expectedServerPid) { Console.Error.WriteLine($"server-pid-mismatch expected={expectedServerPid} actual={serverPid}"); return 20; }
        var host = new ChildHost(client, factory, engineName, AppContext.BaseDirectory, hostBuild);
        host.Send(new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.Hello, Payload: Json(new HelloPayload("plugin-host", hostBuild, nonce, Environment.ProcessId))));
        var engineThread = new Thread(host.EngineLoop) { IsBackground = true, Name = "susu-engine-0" };
        engineThread.Start();
        try
        {
            while (IpcTransport.Read(client) is { } envelope)
            {
                if (envelope.Type == IpcMessageType.Shutdown) break;
                host.Dispatch(envelope);
            }
            return 0;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or EndOfStreamException or IOException)
        {
            Console.Error.WriteLine($"protocol-error {error.GetType().Name}: {error.Message}");
            return 23;
        }
        finally
        {
            host.stopping = true;
            host.work.CompleteAdding();
            engineThread.Join(2000);
        }
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, Resolve<T>());

    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> Resolve<T>()
        => (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)ContractsJson.Default.GetTypeInfo(typeof(T))!;

    private void Send(IpcEnvelope envelope) => IpcTransport.Write(pipe, envelope with { Sequence = Interlocked.Increment(ref sequence) }, writeGate);

    private void Dispatch(IpcEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case IpcMessageType.Load:
                var load = envelope.Payload!.Value.Deserialize(Resolve<LoadPayload>())!;
                work.AddData(() => DoLoad(envelope.PluginId!, load));
                break;
            case IpcMessageType.Invoke:
                var invoke = envelope.Payload!.Value.Deserialize(Resolve<InvokePayload>())!;
                work.AddData(() => DoInvoke(envelope.PluginId!, envelope.RequestId!, envelope.JobId!, envelope.Grant!, invoke));
                break;
            case IpcMessageType.ApiResult:
                var result = envelope.Payload!.Value.Deserialize(Resolve<ApiResultPayload>())!;
                work.AddData(() => DoSettle(result));
                break;
            case IpcMessageType.Cancel:
                var cancel = envelope.Payload!.Value.Deserialize(Resolve<CancelPayload>())!;
                work.AddControl(() => DoCancel(envelope.PluginId!, cancel.CallId));
                break;
            default:
                throw new InvalidDataException($"Frame type '{envelope.Type}' is not accepted by the plugin host.");
        }
    }

    private void EngineLoop()
    {
        while (!stopping)
        {
            var action = work.TakeNext(100);
            if (action is not null) Run(action);
        }
        foreach (var action in work.DrainRemaining()) Run(action);
        foreach (var slot in slots.Values) { slot.Runtime?.Dispose(); slot.Budget.Dispose(); }
    }

    private static void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { Console.Error.WriteLine($"engine-error {error.GetType().Name}: {error.Message}"); }
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
        Send(new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.Loaded, PluginId: pluginId,
            Payload: Json(new LoadedPayload(error is null, error, timer.Elapsed.TotalMilliseconds, bytes))));
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

    private void DoInvoke(string pluginId, string requestId, string jobId, string grant, InvokePayload invoke)
    {
        if (!slots.TryGetValue(pluginId, out var slot) || slot.Runtime is null)
        {
            SendFailed(pluginId, requestId, jobId, invoke.CallId, "bad_response", "plugin not loaded");
            return;
        }
        if (slot.Calls.Count >= ProtocolLimits.MaxInFlightCallsPerRuntime || slot.Calls.ContainsKey(invoke.CallId))
        {
            SendFailed(pluginId, requestId, jobId, invoke.CallId, "busy", "runtime busy");
            return;
        }
        slot.Calls[invoke.CallId] = new Call(requestId, jobId, grant);
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
        // Host-side cancellation is authoritative: report now, reject pending API promises, abort the
        // plugin-visible signal; any later completion from the engine is dropped.
        SendFailed(pluginId, call.RequestId, call.JobId, callId, "cancelled", null);
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
        // Budget exceeded: fail every in-flight call of this runtime, then rebuild it (PLAN 4.5.4).
        var failed = slot.Calls.Keys.ToArray();
        foreach (var (id, call) in slot.Calls)
        {
            foreach (int apiId in call.Apis) apis.Remove(apiId);
            SendFailed(slot.PluginId, call.RequestId, call.JobId, id, "timeout", "execution budget exceeded");
        }
        slot.Calls.Clear();
        var timer = Stopwatch.StartNew();
        slot.Runtime?.Dispose();
        slot.Runtime = null;
        string? error = Build(slot);
        Send(new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.RuntimeFault, PluginId: slot.PluginId,
            Payload: Json(new FaultPayload(error is null ? "budget-rebuilt" : $"rebuild-failed: {error}", failed, timer.Elapsed.TotalMilliseconds))));
    }

    private void SendFailed(string pluginId, string requestId, string jobId, int callId, string kind, string? detail)
        => Send(new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.Failed, requestId, jobId, PluginId: pluginId,
            Payload: Json(new CompletedPayload(callId, false, null, new PluginErrorInfo(kind, detail)))));

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
        Send(new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.ApiCall, call.RequestId, call.JobId, PluginId: pluginId, Grant: call.Grant,
            Payload: Json(new ApiCallPayload(apiId, callId, root.GetProperty("op").GetString() ?? "", root.GetProperty("args").Clone()))));
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
        Send(new IpcEnvelope(ProtocolVersions.Ipc, ok ? IpcMessageType.Completed : IpcMessageType.Failed, call.RequestId, call.JobId, PluginId: pluginId,
            Payload: Json(new CompletedPayload(callId, ok, ok ? root.GetProperty("result").Clone() : null, error))));
    }

    void IRuntimeCallbacks.Log(string pluginId, string json)
    {
        // PLAN 4.5.4 caps plugin logging; the closed IPC message set (ARCHITECTURE 6) has no Log
        // frame, so a plugin log is folded into local diagnostics only, never forwarded to main.
        _ = pluginId; _ = json;
    }
}
