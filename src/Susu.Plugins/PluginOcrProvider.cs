using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Plugins;

/// <summary>
/// An OCR package's <c>ocr</c> capability (F11.2: P-O01 Tencent OCR, P-O02 Simple LaTeX) as <see cref="IOcrProvider"/>. The
/// plugin gets <c>{ image: { id, mime, bytes, width, height }, lang? }</c>: an opaque handle plus host-owned metadata, never a
/// path, bytes or Base64 (B01). The call's grant lists exactly that one handle, so the broker reads the leased file only for
/// this call and only into a JSON <c>bodyFiles</c> field or a multipart part (B06). The result is validated
/// (<see cref="PluginResultValidation.ValidateOcr"/>); a result without non-blank text is <see cref="OcrOutcome.NoText"/>.
/// Cancel and timeout send Cancel to the plugin and stop the broker's I/O.
/// </summary>
public sealed class PluginOcrProvider(WiredPackage package, InstanceSettings instance, Supervisor<HostSession> supervisor, IReadOnlyList<ConfigField>? schema = null) : IOcrProvider
{
    private readonly string configJson = JsonSerializer.Serialize(new Dictionary<string, string>(ConfigSchema.ForPlugin(instance.Config, schema)), ContractsJson.Default.DictionaryStringString);

    public string InstanceId => instance.Id;

    /// <summary>The request JSON the plugin receives.</summary>
    public static string RequestJson(OcrCall call)
        => JsonSerializer.Serialize(new OcrRequest(new FileHandleInfo(call.Image, call.Mime, call.Bytes, Width: call.Width, Height: call.Height), call.Lang), ContractsJson.Default.OcrRequest);

    public async Task<OcrOutcome> RecognizeAsync(OcrCall call, CancellationToken cancellationToken)
    {
        if (supervisor.Stopped) return Fail(ErrorKind.Unavailable, "plugin host stopped after repeated crashes; restart it in Settings");
        HostSession? host;
        try { host = supervisor.Acquire(); }
        catch (Exception error) { return Fail(ErrorKind.Unavailable, error.Message); }
        if (host is null) return Fail(ErrorKind.Unavailable, "plugin host is restarting");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string RequestId, int CallId, Task<IpcEnvelope> Result) invocation;
            try
            {
                invocation = host.Invoke(package.PackageId, "ocr", RequestJson(call), call.AttemptId, [package.Origin(instance.Config)], secrets: package.SecretNames,
                    configJson: configJson, handles: [call.Image], instanceId: instance.Id, signer: package.Signer);
            }
            catch (IOException) { return Fail(ErrorKind.Unavailable, "plugin host disconnected"); }
            var (requestId, callId, task) = invocation;
            void GiveUp() { try { host.Cancel(package.PackageId, requestId, call.AttemptId, callId); } catch (IOException) { } }
            IpcEnvelope envelope;
            try { envelope = await task.WaitAsync(call.Timeout, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { GiveUp(); throw; }
            catch (TimeoutException) { GiveUp(); return Fail(ErrorKind.Timeout); }
            catch (IOException) { return Fail(ErrorKind.Unavailable, "plugin host disconnected"); }
            return Interpret(envelope);
        }
        finally { supervisor.Release(); }
    }

    /// <summary>Maps a Completed envelope to an outcome (public for tests).</summary>
    public static OcrOutcome Interpret(IpcEnvelope envelope)
    {
        CompletedPayload? completed;
        try { completed = envelope.Payload?.Deserialize(ContractsJson.Default.CompletedPayload); }
        catch (JsonException error) { return Fail(ErrorKind.BadResponse, error.Message); }
        if (completed is null) return Fail(ErrorKind.BadResponse, "no payload");
        if (!completed.Ok || completed.Result is null)
        {
            var retryAfter = RetryPolicy.ParseRetryAfter(completed.Error?.RetryAfterRaw, DateTimeOffset.UtcNow);
            return new OcrOutcome.Failure(new ProviderError(ErrorKinds.FromPlugin(completed.Error?.Kind), completed.Error?.Detail, retryAfter));
        }
        OcrResult? result;
        try { result = completed.Result.Value.Deserialize(ContractsJson.Default.OcrResult); }
        catch (JsonException error) { return Fail(ErrorKind.BadResponse, error.Message); }
        if (PluginResultValidation.ValidateOcr(result) is { } invalid) return new OcrOutcome.Failure(invalid);
        var blocks = result!.Blocks.Where(b => !string.IsNullOrWhiteSpace(b.Text)).ToList();
        return blocks.Count == 0 ? new OcrOutcome.NoText() : new OcrOutcome.Recognized(blocks);
    }

    private static OcrOutcome Fail(ErrorKind kind, string? detail = null) => new OcrOutcome.Failure(new ProviderError(kind, detail));
}

/// <summary>F11.2: builds the <c>ocr</c> provider of an installed OCR package instance from the current settings.</summary>
public static class PluginOcrProviders
{
    /// <summary>
    /// The provider for <paramref name="instanceId"/>, or null when it is not an OCR package, the instance is missing, or its
    /// credentials are not saved and granted for the origin its config selects (nothing is called then).
    /// </summary>
    public static PluginOcrProvider? Create(AppSettings settings, string instanceId, Func<string, string, bool> hasSecret, Supervisor<HostSession> supervisor,
        IReadOnlyDictionary<string, IReadOnlyList<ConfigField>>? schemas = null)
    {
        var package = PluginTranslationProviders.WiredPackages.FirstOrDefault(p => p.InstanceId == instanceId && p.Credentials is OcrPackage);
        var instance = settings.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (package is null || instance is null || instance.Package != package.PackageId) return null;
        if (!CredentialPackages.States(settings, package.Credentials, instance, hasSecret).All(t => t.Saved && t.Granted)) return null;
        IReadOnlyList<ConfigField>? schema = schemas is not null && schemas.TryGetValue(instanceId, out var fields) ? fields : null;
        return new PluginOcrProvider(package, instance, supervisor, schema);
    }

    /// <summary>The first usable OCR service (<see cref="OcrCatalog.Candidates"/>: the selected one first), or null (feature unavailable).</summary>
    public static PluginOcrProvider? Resolve(AppSettings settings, Func<string, string, bool> hasSecret, Supervisor<HostSession> supervisor,
        IReadOnlyDictionary<string, IReadOnlyList<ConfigField>>? schemas = null)
        => OcrCatalog.Candidates(settings).Select(id => Create(settings, id, hasSecret, supervisor, schemas)).FirstOrDefault(p => p is not null);
}
