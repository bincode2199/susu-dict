using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Plugins;

/// <summary>
/// A plugin package's <c>translate</c> capability exposed as <see cref="ITranslationProvider"/>
/// (F04.4: the channel F05/F06 build real adapters against). One instance per configured
/// provider instance/capability; the JS engine call happens on <see cref="HostSession"/>'s child
/// process, never on the calling thread.
/// </summary>
public sealed class PluginProvider(string pluginId, string serviceId, string displayName, TranslationLimits limits, Func<HostSession> session, IReadOnlyList<string> hostOrigins) : ITranslationProvider
{
    public string ServiceId { get; } = serviceId;
    public string DisplayName { get; } = displayName;
    // Same account/origin limiter identity as any other provider on this package (ARCHITECTURE 5.1).
    public string LimiterKey { get; } = $"plugin:{pluginId}";
    public TranslationLimits Limits { get; } = limits;

    public bool SupportsLanguagePair(string from, string to) => true; // manifest-declared pairs are F05/F06 adapter concern

    public async Task<ProviderOutcome> TranslateAsync(TranslateCall call, Func<string, ValueTask> onChunk, CancellationToken cancellationToken)
    {
        var host = session();
        string requestJson = JsonSerializer.Serialize(new TranslateRequest(call.Text), ContractsJson.Default.TranslateRequest);
        var (requestId, callId, task) = host.Invoke(pluginId, "translate", requestJson, call.AttemptId, hostOrigins);
        await using var registration = cancellationToken.Register(() => host.Cancel(pluginId, requestId, call.AttemptId, callId));
        IpcEnvelope envelope;
        try { envelope = await task.WaitAsync(call.Timeout, cancellationToken); }
        catch (TimeoutException) { host.Cancel(pluginId, requestId, call.AttemptId, callId); return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Timeout)); }
        var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
        if (!completed.Ok || completed.Result is null)
        {
            var kind = ErrorKinds.FromPlugin(completed.Error?.Kind);
            return new ProviderOutcome.Failure(new ProviderError(kind, completed.Error?.Detail));
        }
        var result = completed.Result.Value.Deserialize(ContractsJson.Default.TranslateResult)!;
        return new ProviderOutcome.Success(result.Text, result.DetectedFrom);
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
