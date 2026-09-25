using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Plugins;

/// <summary>
/// A plugin package's <c>translate</c> capability exposed as <see cref="ITranslationProvider"/>
/// (F04.4: the channel F05/F06 build real adapters against). One instance per configured
/// provider instance/capability; the JS engine call happens on the plugin-host child process,
/// never on the calling thread.
///
/// Goes through <see cref="Supervisor{HostSession}"/> rather than holding a session directly
/// (J07): a crash fails every in-flight call explicitly through the normal timeout/IOException
/// paths below (HostSession.ReadLoop faults its own waiters; nothing here ever resends a call to
/// a new process), the crashed child is replaced with 1/2/4 s backoff, and after the threshold
/// <see cref="Supervisor{TSession}.Stopped"/> is surfaced to the caller as
/// <see cref="ErrorKind.Unavailable"/> instead of hanging or silently retrying forever.
/// </summary>
public sealed class PluginProvider(string pluginId, string serviceId, string displayName, TranslationLimits limits, Supervisor<HostSession> supervisor, IReadOnlyList<string> hostOrigins,
    string? instanceId = null, string? signer = null) : ITranslationProvider
{
    public string ServiceId { get; } = serviceId;
    public string DisplayName { get; } = displayName;
    // Same account/origin limiter identity as any other provider on this package (ARCHITECTURE 5.1).
    public string LimiterKey { get; } = $"plugin:{pluginId}";
    public TranslationLimits Limits { get; } = limits;

    public bool SupportsLanguagePair(string from, string to) => true; // manifest-declared pairs are F05/F06 adapter concern

    public async Task<ProviderOutcome> TranslateAsync(TranslateCall call, Func<string, ValueTask> onChunk, CancellationToken cancellationToken)
    {
        if (supervisor.Stopped) return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Unavailable, "plugin host stopped after repeated crashes; restart it in Settings"));
        // Acquire launches on first use (or after a clean idle release) but - like the old read-only
        // TryGetCurrent - never launches while a crashed session is mid-backoff, so a call arriving
        // then does not race the relaunch Supervisor already scheduled for itself. Release (in finally)
        // keeps the session alive for the whole call so idle release can never fire underneath it.
        HostSession? host;
        try { host = supervisor.Acquire(); }
        // Lazy start (F06.2a): the launch func loads the plugin package too, so a load failure surfaces
        // here on first use instead of at app start. Leaves the session null - the next call retries a
        // fresh launch - instead of tearing down the whole feature the way an unhandled throw would.
        catch (Exception error) { return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Unavailable, error.Message)); }
        if (host is null)
            return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Unavailable, "plugin host is restarting"));
        try
        {
            string requestJson = JsonSerializer.Serialize(new TranslateRequest(call.Text, call.From, call.To), ContractsJson.Default.TranslateRequest);
            (string RequestId, int CallId, Task<IpcEnvelope> Result) invocation;
            try { invocation = host.Invoke(pluginId, "translate", requestJson, call.AttemptId, hostOrigins, instanceId: instanceId, signer: signer); }
            catch (IOException) { return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Unavailable, "plugin host disconnected")); }
            var (requestId, callId, task) = invocation;
            await using var registration = cancellationToken.Register(() => { try { host.Cancel(pluginId, requestId, call.AttemptId, callId); } catch (IOException) { } });
            IpcEnvelope envelope;
            try { envelope = await task.WaitAsync(call.Timeout, cancellationToken); }
            catch (TimeoutException) { try { host.Cancel(pluginId, requestId, call.AttemptId, callId); } catch (IOException) { } return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Timeout)); }
            catch (IOException) { return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Unavailable, "plugin host disconnected")); } // crash mid-call: no replay, Supervisor is already relaunching
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            if (!completed.Ok || completed.Result is null)
            {
                var kind = ErrorKinds.FromPlugin(completed.Error?.Kind);
                return new ProviderOutcome.Failure(new ProviderError(kind, completed.Error?.Detail));
            }
            var result = completed.Result.Value.Deserialize(ContractsJson.Default.TranslateResult)!;
            return new ProviderOutcome.Success(result.Text, result.DetectedFrom);
        }
        finally { supervisor.Release(); }
    }
}

/// <summary>
/// Base for the second production Provider kind (ARCHITECTURE 3: "only PluginProvider and
/// NativeProvider are production implementations"). ELS/SAPI adapters (F06) derive from this;
/// F04 only needs the shape to exist so ProviderKind.Native has a real home.
/// </summary>
public abstract class NativeProviderBase(string serviceId, string displayName, TranslationLimits limits) : ITranslationProvider
{
    public string ServiceId { get; } = serviceId;
    public string DisplayName { get; } = displayName;
    public string LimiterKey { get; } = $"native:{serviceId}";
    public TranslationLimits Limits { get; } = limits;
    public abstract bool SupportsLanguagePair(string from, string to);
    public abstract Task<ProviderOutcome> TranslateAsync(TranslateCall call, Func<string, ValueTask> onChunk, CancellationToken cancellationToken);
}
