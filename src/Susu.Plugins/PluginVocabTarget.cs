using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Plugins;

/// <summary>
/// A vocabulary package's <c>vocab</c> capability (F15.3: P-V01 AnkiConnect, P-V02 Eudic) as <see cref="IVocabSyncTarget"/>.
/// The plugin gets <c>{ operationId, action, entryRevision, word, lang, content, entryId }</c>; the vendor secret is written by
/// the broker, never seen by the plugin (S02). What a failure means for the outbox (ARCHITECTURE 8.3, 5.1):
/// a plugin error is a request the vendor did not apply (the packages return <c>status: unknown</c> themselves once a write is on
/// the wire), so auth/quota/bad_response/unsupported_language are <see cref="VocabSyncOutcome.Failure"/> and network/timeout/
/// rate_limited are <see cref="VocabSyncOutcome.Retry"/>. What only the host can see is a write whose answer never came back:
/// a host timeout, a plugin host that died, or a cancel after the request went out all make an upsert
/// <see cref="VocabSyncOutcome.Unknown"/>. A lookup changes nothing, so the same events are a plain retry.
/// </summary>
public sealed class PluginVocabTarget(WiredPackage package, InstanceSettings instance, Supervisor<HostSession> supervisor, IReadOnlyList<ConfigField>? schema = null, TimeSpan? timeout = null) : IVocabSyncTarget
{
    private readonly string configJson = JsonSerializer.Serialize(new Dictionary<string, string>(ConfigSchema.ForPlugin(instance.Config, schema)), ContractsJson.Default.DictionaryStringString);
    private readonly VocabPackage vocab = (VocabPackage)package.Credentials;
    private readonly TimeSpan callTimeout = timeout ?? TimeSpan.FromSeconds(30);

    public string InstanceId => instance.Id;
    public bool SupportsLookup => vocab.Lookup;

    /// <summary>The request JSON the plugin receives.</summary>
    public static string RequestJson(VocabSyncRequest request)
    {
        using var content = JsonDocument.Parse(request.ContentJson);
        return JsonSerializer.Serialize(new VocabRequest(request.OperationId, request.Action == VocabAction.Lookup ? "lookup" : "upsert",
            request.EntryRevision, request.Word, request.Lang, content.RootElement.Clone(), request.EntryId), ContractsJson.Default.VocabRequest);
    }

    public async Task<VocabSyncOutcome> SendAsync(VocabSyncRequest request, CancellationToken cancellationToken)
    {
        bool write = request.Action == VocabAction.Upsert;
        VocabSyncOutcome Lost(string detail) => write ? new VocabSyncOutcome.Unknown(detail) : new VocabSyncOutcome.Retry(new ProviderError(ErrorKind.Network, detail));
        if (supervisor.Stopped) return Retry(ErrorKind.Unavailable, "plugin host stopped after repeated crashes; restart it in Settings");
        HostSession? host;
        try { host = supervisor.Acquire(); }
        catch (Exception error) { return Retry(ErrorKind.Unavailable, error.Message); }
        if (host is null) return Retry(ErrorKind.Unavailable, "plugin host is restarting");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string origin = package.Origin(instance.Config);
            // The origin comes from the instance's own configuration; only a package for a local application (AnkiConnect) may approve it.
            if (vocab.Local && VocabPackage.IsLoopback(origin)) host.Broker.ApproveLocalOrigin(origin);
            string jobId = $"vocab-{request.OperationId}-{Guid.NewGuid():N}";
            (string RequestId, int CallId, Task<IpcEnvelope> Result) invocation;
            try
            {
                invocation = host.Invoke(package.PackageId, "vocab", RequestJson(request), jobId, [origin], secrets: package.SecretNames,
                    configJson: configJson, instanceId: instance.Id, signer: package.Signer);
            }
            catch (IOException) { return Retry(ErrorKind.Unavailable, "plugin host disconnected"); }
            var (requestId, callId, task) = invocation;
            void GiveUp() { try { host.Cancel(package.PackageId, requestId, jobId, callId); } catch (IOException) { } }
            IpcEnvelope envelope;
            try { envelope = await task.WaitAsync(callTimeout, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { GiveUp(); throw; }
            catch (TimeoutException) { GiveUp(); return Lost("plugin call timed out"); }
            catch (IOException) { return Lost("plugin host disconnected"); }
            return Interpret(envelope, write);
        }
        finally { supervisor.Release(); }
    }

    private static VocabSyncOutcome Retry(ErrorKind kind, string detail) => new VocabSyncOutcome.Retry(new ProviderError(kind, detail));

    /// <summary>Maps a Completed envelope to an outcome (public for tests).</summary>
    public static VocabSyncOutcome Interpret(IpcEnvelope envelope, bool write)
    {
        // A write whose answer cannot be read may have been applied; a read that cannot be read just failed.
        VocabSyncOutcome Unreadable(string detail) => write ? new VocabSyncOutcome.Unknown(detail) : new VocabSyncOutcome.Failure(new ProviderError(ErrorKind.BadResponse, detail));
        CompletedPayload? completed;
        try { completed = envelope.Payload?.Deserialize(ContractsJson.Default.CompletedPayload); }
        catch (JsonException error) { return Unreadable(error.Message); }
        if (completed is null) return Unreadable("no payload");
        if (!completed.Ok || completed.Result is null)
        {
            var retryAfter = RetryPolicy.ParseRetryAfter(completed.Error?.RetryAfterRaw, DateTimeOffset.UtcNow);
            var error = new ProviderError(ErrorKinds.FromPlugin(completed.Error?.Kind), completed.Error?.Detail, retryAfter);
            return error.Kind switch
            {
                ErrorKind.Network or ErrorKind.Timeout or ErrorKind.RateLimited or ErrorKind.Busy => new VocabSyncOutcome.Retry(error),
                ErrorKind.Cancelled => write ? new VocabSyncOutcome.Unknown("cancelled") : new VocabSyncOutcome.Retry(error),
                _ => new VocabSyncOutcome.Failure(error),
            };
        }
        VocabResult? result;
        try { result = completed.Result.Value.Deserialize(ContractsJson.Default.VocabResult); }
        catch (JsonException error) { return Unreadable(error.Message); }
        string? remote = string.IsNullOrWhiteSpace(result?.RemoteId) || result!.RemoteId!.Length > 256 ? null : result.RemoteId;
        return result?.Status switch
        {
            "applied" when write => new VocabSyncOutcome.Applied(remote),
            "found" => new VocabSyncOutcome.Found(remote),
            "absent" when !write => new VocabSyncOutcome.Absent(),
            "unknown" when write => new VocabSyncOutcome.Unknown("vendor answer lost"),
            _ => Unreadable($"unexpected vocab status '{result?.Status}'"),
        };
    }
}

/// <summary>F15.3: builds the sync target of an installed vocabulary package instance from the current settings.</summary>
public static class PluginVocabTargets
{
    /// <summary>The target for <paramref name="instanceId"/>, or null when it is not a vocabulary package or the instance is missing.</summary>
    public static PluginVocabTarget? Create(AppSettings settings, string instanceId, Supervisor<HostSession> supervisor,
        IReadOnlyDictionary<string, IReadOnlyList<ConfigField>>? schemas = null, TimeSpan? timeout = null)
    {
        var package = PluginTranslationProviders.WiredPackages.FirstOrDefault(p => p.InstanceId == instanceId && p.Credentials is VocabPackage);
        var instance = settings.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (package is null || instance is null || instance.Package != package.PackageId) return null;
        IReadOnlyList<ConfigField>? schema = schemas is not null && schemas.TryGetValue(instanceId, out var fields) ? fields : null;
        return new PluginVocabTarget(package, instance, supervisor, schema, timeout);
    }
}
