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
///
/// <paramref name="secrets"/>/<paramref name="config"/> (F06.2a): a keyed service (P-T02/P-T03/P-A01)
/// passes the secret names its account binding grants (Broker.Issue/ResolveSecret enforce S02) and its
/// instance config (model/baseUrl/prompt/...) through to every Invoke; a keyless service like MyMemory
/// leaves both empty/default. Streamed pieces the plugin pushes via ctx.$emit reach <c>onChunk</c>.
///
/// <paramref name="prompt"/> (F07.3): the SetPrompt snapshot captured when this provider was built, so a task
/// keeps the prompt it started with. Each call (each chunk) renders it once with that call's text and languages
/// and sends the result as <see cref="TranslateRequest.Prompt"/>; the plugin never templates it again.
/// </summary>
public sealed class PluginProvider(string pluginId, string serviceId, string displayName, TranslationLimits limits, Supervisor<HostSession> supervisor, IReadOnlyList<string> hostOrigins,
    string? instanceId = null, string? signer = null, IReadOnlyList<string>? secrets = null, IReadOnlyDictionary<string, string>? config = null,
    PromptSnapshot? prompt = null, bool dictionary = false) : ITranslationProvider, IDictionaryProvider
{
    private readonly IReadOnlyList<string> secrets = secrets ?? [];
    private readonly string configJson = config is { Count: > 0 } ? JsonSerializer.Serialize(new Dictionary<string, string>(config), ContractsJson.Default.DictionaryStringString) : "{}";

    public string ServiceId { get; } = serviceId;
    public string DisplayName { get; } = displayName;
    /// <summary>The origins this instance's calls are granted; dictionary audio may be fetched only within them (F09.3, S06).</summary>
    public IReadOnlyList<string> HostOrigins { get; } = hostOrigins;
    // Same account/origin limiter identity as any other provider on this package (ARCHITECTURE 5.1).
    public string LimiterKey { get; } = $"plugin:{pluginId}";
    public TranslationLimits Limits { get; } = limits;

    public bool SupportsLanguagePair(string from, string to) => true; // manifest-declared pairs are F05/F06 adapter concern

    /// <summary>F09.2: true when this instance's <c>dictionary</c> service is enabled (PLAN 6.1); the session then looks word forms up first.</summary>
    public bool DictionaryEnabled { get; } = dictionary;

    public async Task<ProviderOutcome> TranslateAsync(TranslateCall call, Func<string, ValueTask> onChunk, CancellationToken cancellationToken)
    {
        string requestJson = JsonSerializer.Serialize(new TranslateRequest(call.Text, call.From, call.To, prompt?.Render(call.Text, call.From, call.To)), ContractsJson.Default.TranslateRequest);
        var (completed, error) = await InvokeAsync("translate", requestJson, call.AttemptId, call.Timeout, onChunk, cancellationToken);
        if (error is not null) return new ProviderOutcome.Failure(error);
        var result = completed!.Result!.Value.Deserialize(ContractsJson.Default.TranslateResult)!;
        return new ProviderOutcome.Success(result.Text, result.DetectedFrom);
    }

    /// <summary>
    /// F09.2: the package's <c>dictionary</c> capability (<c>{ word }</c> → <see cref="DictionaryResult"/>) with the same
    /// secrets, config, origin, cancel and crash handling as translate. A plugin error is a failure, never an empty entry.
    /// </summary>
    public async Task<DictionaryOutcome> LookupAsync(DictionaryCall call, CancellationToken cancellationToken)
    {
        string requestJson = $"{{\"word\":\"{JsonEncodedText.Encode(call.Word).Value}\"}}";
        var (completed, error) = await InvokeAsync("dictionary", requestJson, call.AttemptId, call.Timeout, null, cancellationToken);
        if (error is not null) return new DictionaryOutcome.Failure(error);
        DictionaryResult? result;
        try { result = completed!.Result!.Value.Deserialize(ContractsJson.Default.DictionaryResult); }
        catch (JsonException e) { return new DictionaryOutcome.Failure(new ProviderError(ErrorKind.BadResponse, e.Message)); }
        return result is null ? new DictionaryOutcome.Failure(new ProviderError(ErrorKind.BadResponse, "no dictionary result")) : new DictionaryOutcome.Entry(result);
    }

    /// <summary>One capability call through the supervised host; a completed payload with a result, or the error.</summary>
    private async Task<(CompletedPayload? Completed, ProviderError? Error)> InvokeAsync(string capability, string requestJson, string attemptId, TimeSpan timeout,
        Func<string, ValueTask>? onChunk, CancellationToken cancellationToken)
    {
        if (supervisor.Stopped) return (null, new ProviderError(ErrorKind.Unavailable, "plugin host stopped after repeated crashes; restart it in Settings"));
        // Acquire launches on first use (or after a clean idle release) but - like the old read-only
        // TryGetCurrent - never launches while a crashed session is mid-backoff, so a call arriving
        // then does not race the relaunch Supervisor already scheduled for itself. Release (in finally)
        // keeps the session alive for the whole call so idle release can never fire underneath it.
        HostSession? host;
        try { host = supervisor.Acquire(); }
        // Lazy start (F06.2a): the launch func loads the plugin package too, so a load failure surfaces
        // here on first use instead of at app start. Leaves the session null - the next call retries a
        // fresh launch - instead of tearing down the whole feature the way an unhandled throw would.
        catch (Exception error) { return (null, new ProviderError(ErrorKind.Unavailable, error.Message)); }
        if (host is null)
            return (null, new ProviderError(ErrorKind.Unavailable, "plugin host is restarting"));
        try
        {
            (string RequestId, int CallId, Task<IpcEnvelope> Result) invocation;
            try { invocation = host.Invoke(pluginId, capability, requestJson, attemptId, HostOrigins, secrets: secrets, configJson: configJson, instanceId: instanceId, signer: signer, onChunk: onChunk); }
            catch (IOException) { return (null, new ProviderError(ErrorKind.Unavailable, "plugin host disconnected")); }
            var (requestId, callId, task) = invocation;
            // Cancel is sent explicitly in the OperationCanceledException path, not from a
            // CancellationToken.Register callback: WaitAsync's own registration completes the task
            // first (callbacks run LIFO) and resumes this method inline, so an `await using`
            // registration was disposed before its callback ever ran and the vendor stream stayed
            // open (F06 verification bug 1, J03). One-shot guard: Cancel goes out at most once.
            int cancelSent = 0;
            void SendCancel() { if (Interlocked.Exchange(ref cancelSent, 1) == 0) try { host.Cancel(pluginId, requestId, attemptId, callId); } catch (IOException) { } }
            IpcEnvelope envelope;
            try { envelope = await task.WaitAsync(timeout, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { SendCancel(); throw; }
            catch (TimeoutException) { SendCancel(); return (null, new ProviderError(ErrorKind.Timeout)); }
            catch (IOException) { return (null, new ProviderError(ErrorKind.Unavailable, "plugin host disconnected")); } // crash mid-call: no replay, Supervisor is already relaunching
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            if (!completed.Ok || completed.Result is null)
            {
                var kind = ErrorKinds.FromPlugin(completed.Error?.Kind);
                // Retry-After is the vendor's own header value, parsed here (not by the plugin/HTTP
                // layer - ARCHITECTURE 5.1) so RetryPolicy.Decide sees the same TimeSpan/60 s-cap rule
                // every other provider's 429 goes through (J05).
                var retryAfter = RetryPolicy.ParseRetryAfter(completed.Error?.RetryAfterRaw, DateTimeOffset.UtcNow);
                return (null, new ProviderError(kind, completed.Error?.Detail, retryAfter));
            }
            return (completed, null);
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
