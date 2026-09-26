using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Susu.Contracts;
using Susu.Windows;

namespace Susu.Plugins;

/// <summary>
/// Main-process side of one plugin-host child process (F04.1, migrated from the F00 prototype
/// tools/Susu.Probes/PluginHost/HostSession.cs): AppContainer launch, pipe authentication
/// (PID + AppContainer SID + nonce), framed IPC over <see cref="IpcEnvelope"/> and the API broker.
/// One session hosts every plugin package currently loaded in that child process.
/// </summary>
public sealed class HostSession : IHostSessionHandle
{
    public sealed record StartTimings(double LaunchMs, double ConnectMs, double HelloMs);

    public sealed record Options(
        string Executable,
        string Resources,
        string Engine,
        string? HostBuild = null,
        ulong MemoryLimit = 256UL << 20,
        string? ProfileName = null,
        bool KeepProfile = false,
        string ChildMode = "--plugin-host",
        /// <summary>Builds this session's Broker; defaults to a production-real <c>new Broker()</c>
        /// (real NetworkBroker, no file leases/secrets configured). A composition root - or a test that
        /// needs $file handle support - passes one that wires real FileLeases/ISecretStore.</summary>
        Func<Broker>? MakeBroker = null)
    {
        public string ResolvedHostBuild => HostBuild ?? Susu.Contracts.HostBuild.Current;
    }

    private readonly ContainerHost container;
    private readonly ContainerProcess process;
    private readonly NamedPipeServerStream pipe;
    private readonly object writeGate = new();
    private readonly Thread reader;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcEnvelope>> calls = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcEnvelope>> loads = new();
    private readonly ConcurrentDictionary<string, string> callGrants = new();
    /// <summary>Streaming capabilities only (F06.2a): the caller's onChunk for one in-flight Invoke,
    /// keyed by request id. Populated in <see cref="Invoke"/>, drained on Completed/Failed.</summary>
    private readonly ConcurrentDictionary<string, Func<string, ValueTask>> chunkHandlers = new();
    private long sequence;
    private int nextCall;
    private readonly bool keepProfile;
    private readonly string hostBuild;
    private AnonymousPipeServerStream? diagnostics;

    public Broker Broker { get; }
    public StartTimings Timings { get; }
    public ConcurrentQueue<IpcEnvelope> Events { get; } = new();
    public int DroppedFrames;
    public string? ReaderError { get; private set; }
    public int ChildPid => process.Pid;
    public SafeProcessHandle Process => process.Process;
    public ContainerHost Container => container;
    public static string CurrentUserSid { get; } = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;

    /// <summary>Raised when the reader loop ends (child disconnected, crashed or was told to shut down).</summary>
    public event Action? Disconnected;

    private HostSession(ContainerHost container, ContainerProcess process, NamedPipeServerStream pipe, StartTimings timings, bool keepProfile, string hostBuild, Func<Broker>? makeBroker)
    {
        this.container = container; this.process = process; this.pipe = pipe; Timings = timings; this.keepProfile = keepProfile; this.hostBuild = hostBuild;
        Broker = makeBroker?.Invoke() ?? new Broker();
        // Fire-and-forget: a slow or throwing onChunk must never block the plugin's own next
        // $http.stream.read (its await on $emit already returned once Broker replied ok).
        Broker.Progress += (requestId, text) => { if (chunkHandlers.TryGetValue(requestId, out var handler)) _ = handler(text).AsTask(); };
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "susu-ipc-reader" };
        reader.Start();
    }

    public static HostSession Start(Options options)
    {
        var timer = Stopwatch.StartNew();
        string profile = options.ProfileName ?? $"Susu.Plugin.Host.{Guid.NewGuid():N}";
        var container = ContainerHost.Open(profile, options.Resources, allowExisting: options.KeepProfile);
        NamedPipeServerStream? pipe = null;
        ContainerProcess? process = null;
        AnonymousPipeServerStream? diagnostics = null;
        try
        {
            string pipeName = $"Susu.Plugin.{Guid.NewGuid():N}";
            pipe = new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, container.CreatePipe(@"\\.\pipe\" + pipeName, CurrentUserSid));
            using var nonceOut = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            diagnostics = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            process = container.Start(options.Executable, $"{options.ChildMode} {pipeName} {Environment.ProcessId} {options.Engine}",
                [nonceOut.ClientSafePipeHandle, diagnostics.ClientSafePipeHandle], nonceOut.ClientSafePipeHandle, diagnostics.ClientSafePipeHandle, options.MemoryLimit);
            nonceOut.DisposeLocalCopyOfClientHandle();
            diagnostics.DisposeLocalCopyOfClientHandle();
            double launch = timer.Elapsed.TotalMilliseconds;
            using (var writer = new StreamWriter(nonceOut)) writer.WriteLine(nonce);
            var diagnosticText = new StreamReader(diagnostics).ReadToEndAsync();
            // A lowbox access check needs both the user and the container SID in the DACL, so other
            // same-user processes can reach the pipe: the PID + token check below is the real gate.
            int rejected = 0;
            using (var connectTimeout = new CancellationTokenSource(5000))
            {
                while (true)
                {
                    try { pipe.WaitForConnectionAsync(connectTimeout.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException)
                    {
                        string text = process.WaitForExit(2000) && diagnosticText.Wait(1000) ? diagnosticText.Result : "(no diagnostics; child may still be starting)";
                        throw new InvalidOperationException($"Plugin host did not connect (rejected clients: {rejected}): {text.Trim()}");
                    }
                    int verdict = container.Verify(pipe.SafePipeHandle, process.Process, out int clientPid);
                    if (verdict == 1) break;
                    pipe.Disconnect();
                    if (++rejected >= 8) throw new UnauthorizedAccessException($"Too many unauthenticated pipe clients (last verdict {verdict}, pid {clientPid}).");
                }
            }

            double connect = timer.Elapsed.TotalMilliseconds;
            var hello = IpcTransport.Read(pipe) ?? throw new EndOfStreamException("No Hello.");
            var payload = hello.Type == IpcMessageType.Hello ? hello.Payload?.Deserialize(ContractsJson.Default.HelloPayload) : null;
            if (payload is null || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(payload.LaunchNonce), System.Text.Encoding.ASCII.GetBytes(nonce)) || payload.ProcessId != process.Pid)
                throw new UnauthorizedAccessException("Handshake nonce/process mismatch.");
            var negotiated = ProtocolNegotiation.Negotiate(ProtocolVersions.SupportedIpc, hello.ProtocolVersion, "plugin-host", payload.Role, options.ResolvedHostBuild, payload.HostBuild);
            if (!negotiated.Accepted) throw new UnauthorizedAccessException($"Plugin host handshake rejected: {negotiated.Reason}");
            var session = new HostSession(container, process, pipe, new StartTimings(launch, connect, timer.Elapsed.TotalMilliseconds), options.KeepProfile, options.ResolvedHostBuild, options.MakeBroker) { diagnostics = diagnostics };
            return session;
        }
        catch
        {
            pipe?.Dispose();
            diagnostics?.Dispose();
            process?.Dispose();
            container.Close(deleteProfile: !options.KeepProfile);
            throw;
        }
    }

    // A payload over one frame is split by transferId/index (PLAN 4.5.4 item 4); over 4 MiB it throws
    // IpcPayloadTooLargeException before anything is written.
    private void Send(IpcEnvelope envelope) => IpcTransport.WriteFramed(pipe, envelope, writeGate, () => Interlocked.Increment(ref sequence));

    /// <summary>Sends the answer to one ApiCall. Always sends something: a result too large for the IPC
    /// transfer limit becomes a prompt bad_response denial instead of a silently lost frame (F09 finding:
    /// the plugin's promise used to wait until the card deadline).</summary>
    private void SendApiResult(IpcEnvelope call, ApiResultPayload result)
    {
        var envelope = new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.ApiResult, call.RequestId, call.JobId, PluginId: call.PluginId, Payload: Json(result));
        try { Send(envelope); }
        catch (IpcPayloadTooLargeException)
        {
            Send(envelope with { Payload = Json(Broker.Deny(result.ApiId, "response exceeds the 4 MiB transfer limit", "bad_response")) });
        }
    }

    public LoadedPayload Load(string pluginId, string directory, int memoryMiB = 64, int timeoutMs = 5000)
    {
        var waiter = loads[pluginId] = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.Load, PluginId: pluginId, Payload: Json(new LoadPayload(directory, "main.js", memoryMiB))));
        if (!waiter.Task.Wait(timeoutMs)) throw new TimeoutException($"Load {pluginId} timed out.");
        return waiter.Task.Result.Payload!.Value.Deserialize(ContractsJson.Default.LoadedPayload)!;
    }

    /// <summary><paramref name="jobId"/> correlates this call with its owning Job (F01); the plugin never sees it.
    /// <paramref name="handles"/> are file-lease ids this call is authorized to reference (e.g. an OCR image or
    /// ASR audio input already staged by the caller) - never expanded by the plugin itself.</summary>
    /// <summary><paramref name="instanceId"/>/<paramref name="signer"/> identify the configured provider
    /// instance and its confirmed package signer for S02 account/origin authorization (Broker.Issue);
    /// omitted they default to the plugin id and "unsigned:&lt;pluginId&gt;" (pre-S02 callers).</summary>
    /// <param name="onChunk">Streaming capabilities only (F06.2a P-A01 OpenAI): called with each piece the
    /// plugin pushes via ctx.$emit while this call is still in flight (fire-and-forget - never awaited
    /// before the plugin's own $http.stream.read continues). Omitted for non-streaming capabilities.</param>
    /// <param name="adoptResultFiles">F10.1 result takeover (B07): when the call completes, the host takes a reference
    /// on every response file this call produced whose id appears in the result, before the call's grant (and with it
    /// every other response file of the call) is released. The caller collects them with <see cref="TakeAdoptedFiles"/>,
    /// or gives up on the call with <see cref="Abandon"/>; either way nothing leaks.</param>
    public (string RequestId, int CallId, Task<IpcEnvelope> Result) Invoke(string pluginId, string capability, string requestJson, string jobId, IEnumerable<string> origins, IEnumerable<string>? secrets = null, string configJson = "{}", IEnumerable<string>? handles = null, string? instanceId = null, string? signer = null, Func<string, ValueTask>? onChunk = null,
        bool adoptResultFiles = false)
    {
        int callId = Interlocked.Increment(ref nextCall);
        string requestId = $"r{callId}-{Guid.NewGuid():N}";
        var grant = Broker.Issue(requestId, pluginId, callId, origins, secrets, handles, instanceId: instanceId, signer: signer);
        var waiter = calls[requestId] = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        callGrants[requestId] = grant.Grant;
        if (adoptResultFiles) lock (adoptGate) adoptPending.Add(requestId);
        if (onChunk is not null) chunkHandlers[requestId] = onChunk;
        Send(new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.Invoke, requestId, jobId, PluginId: pluginId, Grant: grant.Grant,
            Payload: Json(new InvokePayload(callId, capability, Element(requestJson), Element(configJson)))));
        return (requestId, callId, waiter.Task);
    }

    public void Cancel(string pluginId, string requestId, string jobId, int callId)
    {
        // Aborts any upstream HTTP request Broker currently has in flight for this call before the
        // child even processes the IPC message - a cancelled call must not keep running network I/O in
        // the background just because the plugin-visible promise already rejected (F05.2).
        if (callGrants.TryGetValue(requestId, out var grant)) Broker.CancelCall(grant);
        Send(new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.Cancel, requestId, jobId, PluginId: pluginId, Payload: Json(new CancelPayload(callId))));
    }

    private readonly object adoptGate = new();
    private readonly HashSet<string> adoptPending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<Susu.Storage.FileLease>> adopted = new(StringComparer.Ordinal);

    /// <summary>The response files adopted from a completed call's result (see <c>adoptResultFiles</c>); the caller now owns
    /// one reference on each and must release it through <see cref="Broker.Leases"/>. Empty when none, or taken already.</summary>
    public IReadOnlyList<Susu.Storage.FileLease> TakeAdoptedFiles(string requestId)
    {
        lock (adoptGate)
        {
            adoptPending.Remove(requestId);
            return adopted.Remove(requestId, out var files) ? files : [];
        }
    }

    /// <summary>The caller no longer wants the call's result (timeout, cancel): nothing will be adopted, and anything
    /// already adopted is released (B07: a cancelled call leaves no file behind).</summary>
    public void Abandon(string requestId)
    {
        foreach (var lease in TakeAdoptedFiles(requestId)) Broker.Leases?.Release(lease);
    }

    private void AdoptResultFiles(IpcEnvelope envelope, string grant)
    {
        string requestId = envelope.RequestId!;
        var ids = new List<string>();
        if (envelope.Type == IpcMessageType.Completed && envelope.Payload is { ValueKind: JsonValueKind.Object } payload
            && payload.TryGetProperty("result", out var result)) CollectStrings(result, ids, 0);
        // One lock for the pending check and the store, so a concurrent Abandon either prevents the adoption or sees it.
        lock (adoptGate)
        {
            if (!adoptPending.Remove(requestId)) return;
            var files = ids.Count == 0 ? [] : Broker.Adopt(grant, ids);
            if (files.Count > 0) adopted[requestId] = files;
        }
    }

    private static void CollectStrings(JsonElement element, List<string> into, int depth)
    {
        if (depth > 8 || into.Count > 64) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.String: into.Add(element.GetString()!); break;
            case JsonValueKind.Object: foreach (var p in element.EnumerateObject()) CollectStrings(p.Value, into, depth + 1); break;
            case JsonValueKind.Array: foreach (var item in element.EnumerateArray()) CollectStrings(item, into, depth + 1); break;
        }
    }

    private static int ReadApiId(IpcEnvelope envelope)
        => envelope.Payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("apiId", out var id) && id.TryGetInt32(out int value) ? value : 0;

    private static JsonElement Element(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)ContractsJson.Default.GetTypeInfo(typeof(T))!);

    private void ReadLoop()
    {
        try
        {
            var reassembler = new IpcReassembler();
            while (IpcTransport.Read(pipe) is { } frame)
            {
                if (reassembler.Accept(frame) is not { } envelope) continue; // part of a split payload
                switch (envelope.Type)
                {
                    case IpcMessageType.ApiCall:
                        _ = Task.Run(async () =>
                        {
                            ApiResultPayload result;
                            try { result = await Broker.HandleAsync(envelope); }
                            catch (Exception error) { result = Broker.Deny(ReadApiId(envelope), $"host error: {error.GetType().Name}", "bad_response"); }
                            try { SendApiResult(envelope, result); }
                            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
                        });
                        break;
                    case IpcMessageType.Completed or IpcMessageType.Failed:
                        if (envelope.RequestId is not null && calls.TryRemove(envelope.RequestId, out var waiter))
                        {
                            if (callGrants.TryRemove(envelope.RequestId, out var grant))
                            {
                                AdoptResultFiles(envelope, grant); // result takeover comes before the call's leases go (B07)
                                Broker.Revoke(grant); // revoke immediately at termination
                            }
                            chunkHandlers.TryRemove(envelope.RequestId, out _);
                            waiter.TrySetResult(envelope);
                        }
                        else Interlocked.Increment(ref DroppedFrames); // late or forged: dropped, diagnostic count only
                        break;
                    case IpcMessageType.Loaded:
                        if (envelope.PluginId is not null && loads.TryRemove(envelope.PluginId, out var load)) load.TrySetResult(envelope);
                        break;
                    case IpcMessageType.RuntimeFault:
                        Events.Enqueue(envelope);
                        break;
                    default:
                        throw new InvalidDataException($"Unexpected frame {envelope.Type} from plugin host.");
                }
            }
        }
        catch (Exception error) { ReaderError = $"{error.GetType().Name}: {error.Message}"; }
        // Plugin exit (B07): every call still open loses its grant, which stops its I/O and deletes its response files.
        foreach (var requestId in callGrants.Keys) if (callGrants.TryRemove(requestId, out var orphan)) Broker.Revoke(orphan);
        lock (adoptGate) adoptPending.Clear();
        foreach (var waiter in calls.Values) waiter.TrySetException(new IOException("Plugin host disconnected."));
        Disconnected?.Invoke();
    }

    public bool Shutdown(int timeoutMs = 2000)
    {
        try { Send(new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.Shutdown)); } catch (IOException) { }
        return process.WaitForExit(timeoutMs);
    }

    public void Dispose()
    {
        pipe.Dispose();
        process.Dispose(); // closing the only Job handle terminates the child (kill-on-close)
        diagnostics?.Dispose();
        reader.Join(2000);
        container.Close(deleteProfile: !keepProfile);
        Broker.Dispose();
    }
}
