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

    private void Send(IpcEnvelope envelope) => IpcTransport.Write(pipe, envelope with { Sequence = Interlocked.Increment(ref sequence) }, writeGate);

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
    public (string RequestId, int CallId, Task<IpcEnvelope> Result) Invoke(string pluginId, string capability, string requestJson, string jobId, IEnumerable<string> origins, IEnumerable<string>? secrets = null, string configJson = "{}", IEnumerable<string>? handles = null)
    {
        int callId = Interlocked.Increment(ref nextCall);
        string requestId = $"r{callId}-{Guid.NewGuid():N}";
        var grant = Broker.Issue(requestId, pluginId, callId, origins, secrets, handles);
        var waiter = calls[requestId] = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        callGrants[requestId] = grant.Grant;
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

    private static JsonElement Element(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)ContractsJson.Default.GetTypeInfo(typeof(T))!);

    private void ReadLoop()
    {
        try
        {
            while (IpcTransport.Read(pipe) is { } envelope)
            {
                switch (envelope.Type)
                {
                    case IpcMessageType.ApiCall:
                        _ = Task.Run(async () =>
                        {
                            var result = await Broker.HandleAsync(envelope);
                            try { Send(new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.ApiResult, envelope.RequestId, envelope.JobId, PluginId: envelope.PluginId, Payload: Json(result))); }
                            catch (IOException) { }
                        });
                        break;
                    case IpcMessageType.Completed or IpcMessageType.Failed:
                        if (envelope.RequestId is not null && calls.TryRemove(envelope.RequestId, out var waiter))
                        {
                            if (callGrants.TryRemove(envelope.RequestId, out var grant)) Broker.Revoke(grant); // revoke immediately at termination
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
