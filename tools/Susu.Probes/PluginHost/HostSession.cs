using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Susu.Windows;

namespace Susu.Probes.PluginHost;

/// <summary>
/// Main-process side of one plugin-host child (F00 prototype): container launch,
/// pipe authentication (PID + AppContainer SID + nonce), framed IPC and broker.
/// </summary>
internal sealed class HostSession : IDisposable
{
    public sealed record StartTimings(double LaunchMs, double ConnectMs, double HelloMs);
    public sealed record Options(string Executable, string Resources, string Engine, ulong MemoryLimit = 256UL << 20, string? ProfileName = null, bool KeepProfile = false, string ChildMode = "--plugin-host", Action<string>? BeforeLaunch = null);

    private readonly ContainerHost container;
    private readonly ContainerProcess process;
    private readonly NamedPipeServerStream pipe;
    private readonly object writeGate = new();
    private readonly Thread reader;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Frame>> calls = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Frame>> loads = new();
    private readonly ConcurrentDictionary<string, string> callGrants = new();
    private TaskCompletionSource<Frame>? stats;
    private long sequence;
    private int nextCall;
    private readonly bool keepProfile;
    private AnonymousPipeServerStream? diagnostics;
    public Task<string>? Diagnostics { get; private init; }
    public Broker Broker { get; } = new();
    public StartTimings Timings { get; }
    public ConcurrentQueue<Frame> Events { get; } = new();
    public int DroppedFrames;
    public int RejectedClients { get; private init; }
    public string? ReaderError { get; private set; }
    public int ChildPid => process.Pid;
    public SafeProcessHandle Process => process.Process;
    public ContainerHost Container => container;
    public static string CurrentUserSid { get; } = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;

    private HostSession(ContainerHost container, ContainerProcess process, NamedPipeServerStream pipe, StartTimings timings, bool keepProfile)
    {
        this.container = container; this.process = process; this.pipe = pipe; Timings = timings; this.keepProfile = keepProfile;
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "susu-ipc-reader" };
        reader.Start();
    }

    public static HostSession Start(Options options)
    {
        var timer = Stopwatch.StartNew();
        string profile = options.ProfileName ?? $"Susu.F00.Host.{Guid.NewGuid():N}";
        var container = ContainerHost.Open(profile, options.Resources, allowExisting: options.KeepProfile);
        NamedPipeServerStream? pipe = null;
        ContainerProcess? process = null;
        AnonymousPipeServerStream? diagnostics = null;
        try
        {
            string pipeName = $"Susu.F00.{Guid.NewGuid():N}";
            pipe = new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, container.CreatePipe(@"\\.\pipe\" + pipeName, CurrentUserSid));
            options.BeforeLaunch?.Invoke(pipeName);
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
            // A lowbox access check needs both the user and the container SID in the DACL,
            // so other same-user processes can reach the pipe: the PID + token check is the gate.
            // Rejected clients are disconnected and the single instance waits again (bounded).
            int rejected = 0;
            using (var connectTimeout = new CancellationTokenSource(5000))
            {
                while (true)
                {
                    try { pipe.WaitForConnectionAsync(connectTimeout.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException)
                    {
                        string text = process.WaitForExit(2000) && diagnosticText.Wait(1000) ? diagnosticText.Result : "(no diagnostics)";
                        throw new InvalidOperationException($"Plugin host did not connect: {text.Trim()} (rejected clients: {rejected})");
                    }
                    int verdict = container.Verify(pipe.SafePipeHandle, process.Process, out int clientPid);
                    if (verdict == 1) break;
                    pipe.Disconnect();
                    if (++rejected >= 8) throw new UnauthorizedAccessException($"Too many unauthenticated pipe clients (last verdict {verdict}, pid {clientPid}).");
                }
            }

            double connect = timer.Elapsed.TotalMilliseconds;
            var hello = Protocol.Read(pipe) ?? throw new EndOfStreamException("No Hello.");
            var payload = hello.Type == FrameTypes.Hello ? hello.Payload?.Deserialize(PluginJson.Default.HelloPayload) : null;
            if (payload is null || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(payload.Nonce), System.Text.Encoding.ASCII.GetBytes(nonce)) || payload.ProcessId != process.Pid)
                throw new UnauthorizedAccessException("Handshake nonce/process mismatch.");
            var session = new HostSession(container, process, pipe, new StartTimings(launch, connect, timer.Elapsed.TotalMilliseconds), options.KeepProfile) { diagnostics = diagnostics, Diagnostics = diagnosticText, RejectedClients = rejected };
            return session;
        }
        catch
        {
            pipe?.Dispose();
            diagnostics?.Dispose();
            if (process is not null) { process.Dispose(); }
            container.Close(deleteProfile: !options.KeepProfile);
            throw;
        }
    }

    private void Send(Frame frame) => Protocol.Write(pipe, frame with { Sequence = Interlocked.Increment(ref sequence) }, writeGate);

    public LoadedPayload Load(string pluginId, string directory, int memoryMiB = 64, int timeoutMs = 5000)
    {
        var waiter = loads[pluginId] = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(new Frame(Protocol.Version, FrameTypes.Load, PluginId: pluginId, Payload: Protocol.Element(new LoadPayload(directory, "main.js", memoryMiB), PluginJson.Default.LoadPayload)));
        if (!waiter.Task.Wait(timeoutMs)) throw new TimeoutException($"Load {pluginId} timed out.");
        return waiter.Task.Result.Payload!.Value.Deserialize(PluginJson.Default.LoadedPayload)!;
    }

    public (string RequestId, int CallId, Task<Frame> Result) Invoke(string pluginId, string capability, string requestJson, IEnumerable<string> origins, IEnumerable<string>? secrets = null, string configJson = "{}")
    {
        int callId = Interlocked.Increment(ref nextCall);
        string requestId = $"r{callId}-{Guid.NewGuid():N}";
        var grant = Broker.Issue(requestId, pluginId, callId, origins, secrets);
        var waiter = calls[requestId] = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        callGrants[requestId] = grant.Grant;
        Send(new Frame(Protocol.Version, FrameTypes.Invoke, requestId, pluginId, Grant: grant.Grant,
            Payload: Protocol.Element(new InvokePayload(callId, capability, Protocol.Element(requestJson), Protocol.Element(configJson)), PluginJson.Default.InvokePayload)));
        return (requestId, callId, waiter.Task);
    }

    public void Cancel(string pluginId, string requestId, int callId)
        => Send(new Frame(Protocol.Version, FrameTypes.Cancel, requestId, pluginId, Payload: Protocol.Element(new CancelPayload(callId), PluginJson.Default.CancelPayload)));

    public StatsPayload Stats()
    {
        var waiter = stats = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(new Frame(Protocol.Version, FrameTypes.Stats));
        if (!waiter.Task.Wait(5000)) throw new TimeoutException("Stats timed out.");
        return waiter.Task.Result.Payload!.Value.Deserialize(PluginJson.Default.StatsPayload)!;
    }

    /// <summary>Writes raw bytes to the pipe; adversarial tests only.</summary>
    public void SendRaw(Frame frame) => Send(frame);

    private void ReadLoop()
    {
        try
        {
            while (Protocol.Read(pipe) is { } frame)
            {
                switch (frame.Type)
                {
                    case FrameTypes.ApiCall:
                        _ = Task.Run(async () =>
                        {
                            var result = await Broker.HandleAsync(frame);
                            try { Send(new Frame(Protocol.Version, FrameTypes.ApiResult, frame.RequestId, frame.PluginId, Payload: Protocol.Element(result, PluginJson.Default.ApiResultPayload))); }
                            catch (IOException) { }
                        });
                        break;
                    case FrameTypes.Completed or FrameTypes.Failed:
                        if (frame.RequestId is not null && calls.TryRemove(frame.RequestId, out var waiter))
                        {
                            if (callGrants.TryRemove(frame.RequestId, out var grant)) Broker.Revoke(grant); // revoke immediately at termination
                            waiter.TrySetResult(frame);
                        }
                        else Interlocked.Increment(ref DroppedFrames); // late or forged: dropped, diagnostic count only
                        break;
                    case FrameTypes.Loaded:
                        if (frame.PluginId is not null && loads.TryRemove(frame.PluginId, out var load)) load.TrySetResult(frame);
                        break;
                    case FrameTypes.Stats:
                        stats?.TrySetResult(frame);
                        break;
                    case FrameTypes.RuntimeFault or FrameTypes.Log:
                        Events.Enqueue(frame);
                        break;
                    default:
                        throw new InvalidDataException($"Unexpected frame {frame.Type} from plugin host.");
                }
            }
        }
        catch (Exception error) { ReaderError = $"{error.GetType().Name}: {error.Message}"; }
        foreach (var waiter in calls.Values) waiter.TrySetException(new IOException("Plugin host disconnected."));
    }

    public bool Shutdown(int timeoutMs = 2000)
    {
        try { Send(new Frame(Protocol.Version, FrameTypes.Shutdown)); } catch (IOException) { }
        return process.WaitForExit(timeoutMs);
    }

    public void Dispose()
    {
        pipe.Dispose();
        process.Dispose(); // closing the only Job handle terminates the child (kill-on-close)
        diagnostics?.Dispose();
        reader.Join(2000);
        container.Close(deleteProfile: !keepProfile);
    }
}
