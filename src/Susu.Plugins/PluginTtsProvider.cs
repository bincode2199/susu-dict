using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Storage;

namespace Susu.Plugins;

/// <summary>
/// A speech package's <c>tts</c> capability (F10.1: P-S01 Microsoft, P-S02 Google, P-S03 Tencent) as
/// <see cref="ITtsProvider"/>. The request the plugin gets is <c>{ text, lang, voice?, rate }</c> (PLAN 4.7 plus the
/// configured speed); the plugin answers <c>{ audio: FileHandle }</c> where the handle is a response file the broker
/// wrote (raw audio body, or Base64 audio decoded out of the JSON; the plugin never sees audio bytes, B03). The host
/// adopts exactly that file before the call's grant releases every other response file (B07), checks that it really
/// is audio (a JSON/HTML error body is never played, B04), and hands it to the player as a leased clip.
/// Cancel and timeout send Cancel to the plugin, stop the broker's I/O and leave no file behind.
/// </summary>
public sealed class PluginTtsProvider(WiredPackage package, InstanceSettings instance, Supervisor<HostSession> supervisor, IReadOnlyList<ConfigField>? schema = null) : ITtsProvider
{
    private readonly string configJson = JsonSerializer.Serialize(new Dictionary<string, string>(ConfigSchema.ForPlugin(instance.Config, schema)), ContractsJson.Default.DictionaryStringString);

    public string InstanceId => instance.Id;
    public bool Native => false;

    /// <summary>The request JSON the plugin receives.</summary>
    public static string RequestJson(SpeakRequest request)
    {
        var node = new JsonObject { ["text"] = request.Text, ["rate"] = request.ClampedRate };
        if (!string.IsNullOrEmpty(request.Lang)) node["lang"] = request.Lang;
        if (!string.IsNullOrEmpty(request.Voice)) node["voice"] = request.Voice;
        return node.ToJsonString();
    }

    public async Task<AudioOutcome> SynthesizeAsync(SpeakCall call, CancellationToken cancellationToken)
    {
        if (supervisor.Stopped) return Fail(ErrorKind.Unavailable, "plugin host stopped after repeated crashes; restart it in Settings");
        HostSession? host;
        try { host = supervisor.Acquire(); }
        catch (Exception error) { return Fail(ErrorKind.Unavailable, error.Message); }
        if (host is null) return Fail(ErrorKind.Unavailable, "plugin host is restarting");
        try
        {
            var leases = host.Broker.Leases;
            if (leases is null) return Fail(ErrorKind.Unavailable, "no file lease store is configured for this host");
            (string RequestId, int CallId, Task<IpcEnvelope> Result) invocation;
            try
            {
                invocation = host.Invoke(package.PackageId, "tts", RequestJson(call.Request), call.AttemptId, [package.Origin(instance.Config)], secrets: package.SecretNames,
                    configJson: configJson, instanceId: instance.Id, signer: package.Signer, adoptResultFiles: true);
            }
            catch (IOException) { return Fail(ErrorKind.Unavailable, "plugin host disconnected"); }
            var (requestId, callId, task) = invocation;
            void GiveUp() { try { host.Cancel(package.PackageId, requestId, call.AttemptId, callId); } catch (IOException) { } host.Abandon(requestId); }
            IpcEnvelope envelope;
            try { envelope = await task.WaitAsync(call.Timeout, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { GiveUp(); throw; }
            catch (TimeoutException) { GiveUp(); return Fail(ErrorKind.Timeout); }
            catch (IOException) { host.Abandon(requestId); return Fail(ErrorKind.Unavailable, "plugin host disconnected"); }

            var files = host.TakeAdoptedFiles(requestId);
            FileLease? handedOver = null;
            try
            {
                var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
                if (!completed.Ok || completed.Result is null)
                {
                    var retryAfter = RetryPolicy.ParseRetryAfter(completed.Error?.RetryAfterRaw, DateTimeOffset.UtcNow);
                    return new AudioOutcome.Failure(new ProviderError(ErrorKinds.FromPlugin(completed.Error?.Kind), completed.Error?.Detail, retryAfter));
                }
                TtsResult? result;
                try { result = completed.Result.Value.Deserialize(ContractsJson.Default.TtsResult); }
                catch (JsonException error) { return Fail(ErrorKind.BadResponse, error.Message); }
                if (result?.Audio?.Id is not { Length: > 0 } id) return Fail(ErrorKind.BadResponse, "no audio handle in the tts result");
                var kept = files.FirstOrDefault(f => f.Id == id);
                if (kept is null) return Fail(ErrorKind.BadResponse, "the audio handle is not a response file of this call");
                string path = leases.PathOf(kept);
                byte[] head = ReadHead(path);
                // B04: the bytes themselves must be an audio container; a declared audio MIME is not enough (a JSON error Base64-encoded
                // into the audio field, or served as audio/mpeg, is never played).
                if (DictionaryAudioFetcher.SniffAudio(head) is not { } sniffed) return Fail(ErrorKind.BadResponse, $"response is not audio ({result.Audio.Mime})");
                var audio = DictionaryAudioFetcher.ExtensionFor(result.Audio.Mime) is not null ? (Mime: result.Audio.Mime.ToLowerInvariant(), Extension: "") : sniffed;
                handedOver = kept;
                return new AudioOutcome.Ready(new LeasedAudioClip(leases, kept, audio.Mime));
            }
            finally
            {
                // Every adopted file except the one handed to the player is released (a refused or failed result leaves nothing).
                foreach (var file in files) if (!ReferenceEquals(file, handedOver)) leases.Release(file);
            }
        }
        finally { supervisor.Release(); }
    }


    private static byte[] ReadHead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[16];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return buffer[..read];
        }
        catch (IOException) { return []; }
    }

    private static AudioOutcome Fail(ErrorKind kind, string? detail = null) => new AudioOutcome.Failure(new ProviderError(kind, detail));
}

/// <summary>F10.1: builds the <c>tts</c> provider of an installed speech package instance from the current settings.</summary>
public static class PluginTtsProviders
{
    /// <summary>
    /// The provider for <paramref name="instanceId"/>, or null when it is not an installed speech package, the instance is
    /// missing, or its credentials are not saved and granted for the origin its config selects (nothing is called then).
    /// </summary>
    public static PluginTtsProvider? Create(AppSettings settings, string instanceId, Func<string, string, bool> hasSecret, Supervisor<HostSession> supervisor,
        IReadOnlyDictionary<string, IReadOnlyList<ConfigField>>? schemas = null)
    {
        var package = PluginTranslationProviders.WiredPackages.FirstOrDefault(p => p.InstanceId == instanceId && p.Credentials is SpeechPackage { Capability: Capability.Tts });
        var instance = settings.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (package is null || instance is null || instance.Package != package.PackageId) return null;
        if (!CredentialPackages.States(settings, package.Credentials, instance, hasSecret).All(t => t.Saved && t.Granted)) return null;
        IReadOnlyList<ConfigField>? schema = schemas is not null && schemas.TryGetValue(instanceId, out var fields) ? fields : null;
        return new PluginTtsProvider(package, instance, supervisor, schema);
    }
}