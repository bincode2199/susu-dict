using System.Globalization;
using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Plugins;

/// <summary>
/// An ASR package's <c>asr</c> capability (F12.2: P-R01 OpenAI, P-R02 Gemini) as <see cref="IAsrProvider"/>. The plugin gets
/// <c>{ audio: { id, mime, bytes, durationMs }, model, output, lang? }</c>: an opaque handle plus host-owned metadata, never a
/// path, bytes or Base64 (B02). The call's grant lists exactly that one handle and caps the whole upload body at the model's
/// <c>maxRequestBytes</c>, so the broker reads the leased file only for this call, only into a multipart part or a JSON
/// Base64 field, and refuses a request that would exceed the cap. The result is parsed strictly from the raw JSON (a
/// missing, non-numeric or non-finite field is <c>bad_response</c>) and validated
/// (<see cref="PluginResultValidation.ValidateSegments"/>) against the chunk's real length. Cancel and timeout send Cancel to
/// the plugin and stop the broker's upload.
/// </summary>
public sealed class PluginAsrProvider(WiredPackage package, InstanceSettings instance, SpeechModel model, Supervisor<HostSession> supervisor, IReadOnlyList<ConfigField>? schema = null) : IAsrProvider
{
    /// <summary>More segments than this in one chunk's answer is not a plausible transcript.</summary>
    public const int MaxSegments = 20_000;

    private readonly string configJson = JsonSerializer.Serialize(new Dictionary<string, string>(ConfigSchema.ForPlugin(instance.Config, schema)), ContractsJson.Default.DictionaryStringString);

    public string InstanceId => instance.Id;
    public string Model => model.Id;
    public bool Timecodes => model.Timecodes;
    public AsrLimits Limits => model.Limits ?? throw new InvalidOperationException("ASR model without declared limits");

    /// <summary>The request JSON the plugin receives.</summary>
    public static string RequestJson(AsrCall call)
        => JsonSerializer.Serialize(new AsrRequest(new FileHandleInfo(call.Audio, call.Mime, call.Bytes, DurationMs: call.DurationSeconds * 1000), call.Model, call.Output, call.Lang), ContractsJson.Default.AsrRequest);

    public async Task<AsrOutcome> TranscribeAsync(AsrCall call, CancellationToken cancellationToken)
    {
        if (call.Output == "segments" && !model.Timecodes) return Fail(ErrorKind.BadResponse, $"model '{model.Id}' has no timecodes");
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
                invocation = host.Invoke(package.PackageId, "asr", RequestJson(call), call.AttemptId, [package.Origin(instance.Config)], secrets: package.SecretNames,
                    configJson: configJson, handles: [call.Audio], instanceId: instance.Id, signer: package.Signer, maxRequestBytes: call.MaxRequestBytes);
            }
            catch (IOException) { return Fail(ErrorKind.Unavailable, "plugin host disconnected"); }
            var (requestId, callId, task) = invocation;
            void GiveUp() { try { host.Cancel(package.PackageId, requestId, call.AttemptId, callId); } catch (IOException) { } }
            IpcEnvelope envelope;
            try { envelope = await task.WaitAsync(call.Timeout, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { GiveUp(); throw; }
            catch (TimeoutException) { GiveUp(); return Fail(ErrorKind.Timeout); }
            catch (IOException) { return Fail(ErrorKind.Unavailable, "plugin host disconnected"); }
            return Interpret(envelope, call.Output, call.DurationSeconds);
        }
        finally { supervisor.Release(); }
    }

    /// <summary>Maps a Completed envelope to an outcome (public for tests).</summary>
    public static AsrOutcome Interpret(IpcEnvelope envelope, string requestedOutput, double durationSeconds)
    {
        CompletedPayload? completed;
        try { completed = envelope.Payload?.Deserialize(ContractsJson.Default.CompletedPayload); }
        catch (JsonException error) { return Fail(ErrorKind.BadResponse, error.Message); }
        if (completed is null) return Fail(ErrorKind.BadResponse, "no payload");
        if (!completed.Ok || completed.Result is null)
        {
            var retryAfter = RetryPolicy.ParseRetryAfter(completed.Error?.RetryAfterRaw, DateTimeOffset.UtcNow);
            return new AsrOutcome.Failure(new ProviderError(ErrorKinds.FromPlugin(completed.Error?.Kind), completed.Error?.Detail, retryAfter));
        }
        return Parse(completed.Result.Value, requestedOutput, durationSeconds);
    }

    /// <summary>
    /// Strict parse of the plugin's <c>AsrResult</c> (A06): <c>kind</c> must be the requested output; text needs a string
    /// <c>text</c> (empty = no speech in this chunk); segments need an array whose items each have finite numeric
    /// <c>start</c>/<c>end</c> and string <c>text</c>. Times are then checked against the chunk length (rounded up to the
    /// 10 ms vendors report in). Nothing is repaired: no clamping, sorting or evenly split times.
    /// </summary>
    public static AsrOutcome Parse(JsonElement result, string requestedOutput, double durationSeconds)
    {
        if (result.ValueKind != JsonValueKind.Object) return Fail(ErrorKind.BadResponse, "result is not an object");
        if (!result.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String) return Fail(ErrorKind.BadResponse, "kind missing");
        string kind = kindElement.GetString()!;
        if (kind != requestedOutput) return Fail(ErrorKind.BadResponse, $"asr kind '{kind}' does not match requested '{requestedOutput}'");
        if (kind == "text")
        {
            if (!result.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return Fail(ErrorKind.BadResponse, "text missing");
            return new AsrOutcome.Transcribed("text", text.GetString()!.Trim(), null);
        }
        if (kind != "segments" || !result.TryGetProperty("segments", out var array) || array.ValueKind != JsonValueKind.Array) return Fail(ErrorKind.BadResponse, "segments missing");
        if (array.GetArrayLength() > MaxSegments) return Fail(ErrorKind.BadResponse, $"too many segments ({array.GetArrayLength()})");
        var segments = new List<AsrSegment>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return Fail(ErrorKind.BadResponse, "segment is not an object");
            if (!TryNumber(item, "start", out double start) || !TryNumber(item, "end", out double end)) return Fail(ErrorKind.BadResponse, "segment start/end missing or not a finite number");
            if (!item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return Fail(ErrorKind.BadResponse, "segment text missing");
            segments.Add(new AsrSegment(start, end, text.GetString()!));
        }
        double limit = Math.Ceiling(durationSeconds * 100 - 1e-9) / 100;
        if (PluginResultValidation.ValidateSegments(new AsrResult("segments", null, [.. segments]), "segments", limit) is { } invalid) return new AsrOutcome.Failure(invalid);
        return new AsrOutcome.Transcribed("segments", AsrText.Join(segments.Select(s => s.Text)), segments);
    }

    private static bool TryNumber(JsonElement item, string name, out double value)
    {
        value = 0;
        return item.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static AsrOutcome Fail(ErrorKind kind, string? detail = null) => new AsrOutcome.Failure(new ProviderError(kind, detail));
}

/// <summary>F12.2: builds the <c>asr</c> provider of an installed ASR package instance and model from the current settings.</summary>
public static class PluginAsrProviders
{
    /// <summary>
    /// The provider for the selection of <paramref name="slot"/>, or null (nothing is called): no selection, not an ASR package,
    /// the model is unknown, text-only under video, or has no local audio format (A03/A04), or its credentials are not saved
    /// and granted for the origin its config selects.
    /// </summary>
    public static PluginAsrProvider? Create(AppSettings settings, SpeechSlot slot, Func<string, string, bool> hasSecret, Supervisor<HostSession> supervisor,
        IReadOnlyDictionary<string, IReadOnlyList<ConfigField>>? schemas = null)
    {
        var selection = settings.Speech[slot];
        if (slot == SpeechSlot.Tts || selection.Instance.Length == 0 || SpeechCatalog.Check(slot, selection) is not null) return null;
        var package = PluginTranslationProviders.WiredPackages.FirstOrDefault(p => p.InstanceId == selection.Instance && p.Credentials is SpeechPackage { Capability: Capability.Asr });
        var instance = settings.Instances.FirstOrDefault(i => i.Id == selection.Instance);
        if (package is null || instance is null || instance.Package != package.PackageId) return null;
        var speech = (SpeechPackage)package.Credentials;
        if (speech.Models.FirstOrDefault(m => m.Id == selection.Model) is not { Limits: not null } model) return null;
        if (!CredentialPackages.States(settings, speech, instance, hasSecret).All(t => t.Saved && t.Granted)) return null;
        IReadOnlyList<ConfigField>? schema = schemas is not null && schemas.TryGetValue(selection.Instance, out var fields) ? fields : null;
        return new PluginAsrProvider(package, instance, model, supervisor, schema);
    }
}
