using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Susu.Probes.PluginHost;

/// <summary>
/// PER01 WebView2 fallback route (PLAN 2.4): hidden WebView2 page, sandboxed iframe, one
/// module Worker per plugin. Uses the same Broker, plugin files and call API as HostSession.
/// </summary>
internal sealed partial class WebViewHost : IDisposable
{
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_wvp_start", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int Start(string userData, string hostFolder, string pluginFolder, delegate* unmanaged[Cdecl]<nint, char*, void> onMessage, nint opaque, out nint session, double* timings);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_wvp_post", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int Post(nint session, string json);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_wvp_memory")]
    private static partial int MemoryNative(nint session, out ulong pws, out ulong privateBytes, out int processes, out int unreadable);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_wvp_browser_pid")]
    private static partial uint BrowserPidNative(nint session);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_wvp_stop")]
    private static partial int Stop(nint session, out int exited);

    internal sealed record Message(string Type, string? PluginId = null, string? RequestId = null, string? Grant = null, int CallId = 0, int ApiId = 0,
        string? Op = null, JsonElement? Args = null, bool Ok = false, JsonElement? Result = null, PluginErrorInfo? Error = null, string? Url = null,
        string? Capability = null, JsonElement? Request = null, JsonElement? Config = null, JsonElement? Value = null, double Ms = 0, double RebuildMs = 0, int[]? FailedCalls = null, string? Text = null);

    private readonly GCHandle self;
    private nint session;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Message>> calls = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Message>> loads = new();
    private readonly ConcurrentDictionary<string, string> callGrants = new();
    private int nextCall;
    public Broker Broker { get; } = new();
    public int BrowserPid { get; private set; }
    public double[] Timings { get; } = new double[4];
    public ConcurrentQueue<Message> Faults { get; } = new();

    public unsafe WebViewHost(string userData, string hostFolder, string pluginFolder)
    {
        self = GCHandle.Alloc(this);
        fixed (double* t = Timings)
        {
            int hr = Start(userData, hostFolder, pluginFolder, &OnMessage, GCHandle.ToIntPtr(self), out session, t);
            if (hr < 0) { if (session != 0) Stop(session, out _); self.Free(); Marshal.ThrowExceptionForHR(hr); }
            BrowserPid = (int)BrowserPidNative(session);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnMessage(nint opaque, char* json)
    {
        try
        {
            var host = (WebViewHost)GCHandle.FromIntPtr(opaque).Target!;
            var message = JsonSerializer.Deserialize(new string(json), WebViewJson.Default.Message);
            if (message is not null) host.Dispatch(message);
        }
        catch { /* malformed page message: ignored (benchmark harness) */ }
    }

    private void Dispatch(Message m)
    {
        switch (m.Type)
        {
            case "ApiCall":
                var frame = new Frame(Protocol.Version, FrameTypes.ApiCall, m.RequestId, m.PluginId, Grant: m.Grant,
                    Payload: Protocol.Element(new ApiCallPayload(m.ApiId, m.CallId, m.Op ?? "", m.Args ?? Protocol.Element("null")), PluginJson.Default.ApiCallPayload));
                _ = Task.Run(async () =>
                {
                    var result = await Broker.HandleAsync(frame);
                    Send(new Message("ApiResult", m.PluginId, ApiId: result.ApiId, Ok: result.Ok, Value: result.Value));
                });
                break;
            case "Completed" or "Failed":
                if (m.RequestId is not null && calls.TryRemove(m.RequestId, out var waiter))
                {
                    if (callGrants.TryRemove(m.RequestId, out var grant)) Broker.Revoke(grant);
                    waiter.TrySetResult(m);
                }
                break;
            case "Loaded":
                if (m.PluginId is not null && loads.TryRemove(m.PluginId, out var load)) load.TrySetResult(m);
                break;
            case "Diagnostic":
                Console.Error.WriteLine($"webview diagnostic: {m.Text}");
                break;
            case "RuntimeFault":
                Faults.Enqueue(m);
                break;
        }
    }

    private void Send(Message message) => Marshal.ThrowExceptionForHR(Post(session, JsonSerializer.Serialize(message, WebViewJson.Default.Message)));

    public Message Load(string pluginId, int timeoutMs = 10000)
    {
        var waiter = loads[pluginId] = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(new Message("Load", pluginId, Url: $"https://susu-plugin.example/{pluginId}/main.js"));
        if (!waiter.Task.Wait(timeoutMs)) throw new TimeoutException($"WebView load {pluginId} timed out.");
        return waiter.Task.Result;
    }

    public (string RequestId, int CallId, Task<Message> Result) Invoke(string pluginId, string capability, string requestJson, IEnumerable<string> origins)
    {
        int callId = Interlocked.Increment(ref nextCall);
        string requestId = $"w{callId}-{Guid.NewGuid():N}";
        var grant = Broker.Issue(requestId, pluginId, callId, origins);
        var waiter = calls[requestId] = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        callGrants[requestId] = grant.Grant;
        Send(new Message("Invoke", pluginId, requestId, grant.Grant, callId, Capability: capability, Request: Protocol.Element(requestJson), Config: Protocol.Element("{}")));
        return (requestId, callId, waiter.Task);
    }

    public void Cancel(string pluginId, int callId) => Send(new Message("Cancel", pluginId, CallId: callId));

    public (double PwsMiB, double PrivateMiB, int Processes, int Unreadable) Memory()
    {
        Marshal.ThrowExceptionForHR(MemoryNative(session, out ulong pws, out ulong privateBytes, out int processes, out int unreadable));
        return (pws / 1048576.0, privateBytes / 1048576.0, processes, unreadable);
    }

    /// <summary>Closes the controller and environment; true when BrowserProcessExited was observed.</summary>
    public bool Close()
    {
        if (session == 0) return true;
        Stop(session, out int exited);
        session = 0;
        return exited == 1;
    }

    public void Dispose() { Close(); if (self.IsAllocated) self.Free(); }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(WebViewHost.Message))]
internal partial class WebViewJson : JsonSerializerContext;
