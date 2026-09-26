using System.Text;
using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Plugins;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F10.1 P-S01 Microsoft, P-S02 Google and P-S03 Tencent TTS: the real shipped packages through the real AppContainer
/// sandbox + QuickJS + IPC + Broker/$http (+ the host tc3 signer for Tencent) and <see cref="PluginTtsProvider"/>, against
/// a LoopbackHttpServer standing in for the vendor (B03 Base64 audio decoded into a file handle, B04 raw audio and error
/// classes, B07 cancel cleanup). Needs susu.exe published (`dotnet publish src/Susu.Host -c Release -r win-x64`); skips
/// itself otherwise. Real vendor calls: not executed (no account).
/// </summary>
public class TtsPluginTests
{
    private const string AzureKey = "azure-test-key-0123456789";
    private const string GoogleKey = "google-test-key-0123456789";
    private const string SecretId = "AKIDttsTestSecretId012345";
    private const string SecretKey = "ttsTestSecretKey0123456789ab";
    // An MP3 frame header (MPEG-1 Layer III) followed by padding: sniffed as audio/mpeg.
    private static readonly byte[] Mp3 = [.. "ID3"u8.ToArray(), 3, 0, 0, 0, 0, 0, 0, .. Enumerable.Repeat((byte)0x55, 600)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? FindHostBuildOutput()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish");
            if (File.Exists(Path.Combine(candidate, "susu.exe"))) return candidate;
        }
        return null;
    }

    private static readonly Lazy<string?> staged = new(() =>
    {
        string? output = FindHostBuildOutput();
        if (output is null) return null;
        string[] packages = ["microsoft-tts", "google-tts", "tencent-tts"];
        if (packages.Any(p => !File.Exists(Path.Combine(output, "plugins", p, "main.js")))) return null;
        string dir = TestTemp.NewDir("susu-tts-it");
        foreach (string file in Directory.EnumerateFiles(output))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        foreach (string package in packages)
        {
            string target = Path.Combine(dir, "plugins", package);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(Path.Combine(output, "plugins", package))) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        return dir;
    });

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<(string, string), string> values = [];
        public void Set(string account, string name, string value) => values[(account, name)] = value;
        public bool Has(string a, string n) => values.ContainsKey((a, n));
        public IReadOnlyList<string> Names(string a) => [.. values.Keys.Where(k => k.Item1 == a).Select(k => k.Item2)];
        public void Write(string a, string n, ReadOnlySpan<char> v) => values[(a, n)] = v.ToString();
        public bool Delete(string a, string n) => values.Remove((a, n));
        public bool TryRead(string a, string n, out string v) => values.TryGetValue((a, n), out v!);
    }

    private sealed class Rig : IDisposable
    {
        public required FileLeases Leases { get; init; }
        public required Supervisor<HostSession> Supervisor { get; init; }
        public required PluginTtsProvider Provider { get; init; }
        public required AppSettings Settings { get; init; }
        public required string InstanceId { get; init; }
        public HostSession? Session;
        public Broker Broker => Session!.Broker;

        public Task<AudioOutcome> SpeakAsync(SpeakRequest request, CancellationToken? token = null, TimeSpan? timeout = null)
            => Provider.SynthesizeAsync(new SpeakCall(request, $"tts-{Guid.NewGuid():N}", timeout ?? TimeSpan.FromSeconds(30)), token ?? Ct);

        public Task<CapabilityOutcome<OptionsResult>> VoicesAsync()
            => PluginTranslationProviders.LoadOptionsAsync(Supervisor, Settings, InstanceId, OptionsSource.VoicesMethod, "voice", 1, null, TimeSpan.FromSeconds(30), Ct);

        /// <summary>B07: nothing of any call is left once the caller released what it holds.</summary>
        public async Task AssertNoFilesLeftAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((Leases.ActiveCount > 0 || Broker.ActiveResponseFiles > 0) && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
            Assert.Equal(0, Broker.ActiveResponseFiles);
            Assert.Equal(0, Leases.ActiveCount);
        }

        public void Dispose() { Supervisor.Dispose(); Leases.Dispose(); }
    }

    private static Rig? Build(string instanceId, LoopbackHttpServer server, Dictionary<string, string>? config = null, string? directory = null)
    {
        if (staged.Value is not { } dir) return null;
        var package = PluginTranslationProviders.WiredPackages.Single(p => p.InstanceId == instanceId);
        var speech = SpeechCatalog.Find(instanceId)!;
        var secrets = new FakeSecretStore();
        var bindings = new Dictionary<string, string>();
        var cfg = new Dictionary<string, string>(config ?? []) { ["baseUrl"] = server.Origin };
        string origin = package.Origin(cfg);
        var grants = new List<CredentialGrant>();
        foreach (var target in speech.Credentials)
        {
            secrets.Set("account-tts", target.Secret, target.Secret switch { "secretId" => SecretId, "secretKey" => SecretKey, _ => instanceId == SpeechCatalog.MicrosoftTts ? AzureKey : GoogleKey });
            grants.Add(new CredentialGrant(package.PackageId, package.Signer, target.Secret, origin, target.Use));
            bindings[target.Secret] = "account-tts";
        }
        var account = new AccountSettings("account-tts", "Test account", [.. speech.SecretNames], grants);
        var instance = new InstanceSettings(instanceId, package.PackageId, 1, cfg, bindings);
        var accounts = new AccountAuthorization(() => ([account], [instance]));
        var leases = new FileLeases(TestTemp.NewDir("susu-tts-leases"));
        var options = new HostSession.Options(Path.Combine(dir, "susu.exe"), dir, "quickjs", KeepProfile: false,
            MakeBroker: () => new Broker(secretStore: secrets, accounts: accounts, leases: leases));
        Rig? rig = null;
        var supervisor = new Supervisor<HostSession>(() =>
        {
            var session = HostSession.Start(options);
            session.Broker.ApproveLocalOrigin(server.Origin);
            var loaded = session.Load(package.PackageId, directory ?? package.Directory);
            if (!loaded.Ok) { session.Shutdown(2000); throw new InvalidOperationException($"{package.PackageId} failed to load: {loaded.Error}"); }
            rig!.Session = session;
            return session;
        }, SystemClock.Instance, TimeSpan.FromMinutes(10));
        var defaults = BuiltInCatalog.Defaults();
        var settings = defaults with { Instances = [.. defaults.Instances.Where(i => i.Id != instanceId), instance], Accounts = [account] };
        rig = new Rig { Leases = leases, Supervisor = supervisor, Provider = new PluginTtsProvider(package, instance, supervisor), Settings = settings, InstanceId = instanceId };
        return rig;
    }

    private static string? H(LoopbackHttpRequest req, string name)
        => req.Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static LoopbackHttpResponse Audio(byte[] bytes, string contentType = "audio/mpeg") => new(200, bytes, ContentType: contentType);

    private static async Task<byte[]> ReadAndRelease(AudioOutcome outcome)
    {
        var ready = Assert.IsType<AudioOutcome.Ready>(outcome);
        using var clip = ready.Clip;
        Assert.Equal("audio/mpeg", clip.Mime);
        return await File.ReadAllBytesAsync(clip.FilePath);
    }

    private static ProviderError Failure(AudioOutcome outcome) => Assert.IsType<AudioOutcome.Failure>(outcome).Error;

    // ---------------- P-S01 Microsoft ----------------

    [Fact] // B04 raw audio: SSML request with the key header; the audio reaches the host only as a handle, then as a leased clip
    public async Task Microsoft_raw_audio_plays_from_a_handle()
    {
        LoopbackHttpRequest? captured = null;
        using var server = new LoopbackHttpServer(req => { captured = req; return Audio(Mp3); });
        using var rig = Build(SpeechCatalog.MicrosoftTts, server);
        if (rig is null) return;
        var bytes = await ReadAndRelease(await rig.SpeakAsync(new SpeakRequest("Tom & <Jerry>", "zh-Hans", Rate: 1.5)));
        Assert.Equal(Mp3, bytes);
        Assert.NotNull(captured);
        Assert.Equal("POST", captured.Method);
        Assert.Equal("/cognitiveservices/v1", captured.Path);
        Assert.Equal(AzureKey, H(captured, "Ocp-Apim-Subscription-Key"));
        Assert.StartsWith("application/ssml+xml", H(captured, "Content-Type"));
        Assert.Equal("audio-24khz-48kbitrate-mono-mp3", H(captured, "X-Microsoft-OutputFormat"));
        string ssml = Encoding.UTF8.GetString(captured.Body);
        Assert.Contains("<voice name=\"zh-CN-XiaoxiaoNeural\">", ssml);
        Assert.Contains("xml:lang=\"zh-CN\"", ssml);
        Assert.Contains("<prosody rate=\"+50%\">Tom &amp; &lt;Jerry&gt;</prosody>", ssml);
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // the configured voice wins over the language default
    public async Task Microsoft_configured_voice_is_used()
    {
        LoopbackHttpRequest? captured = null;
        using var server = new LoopbackHttpServer(req => { captured = req; return Audio(Mp3); });
        using var rig = Build(SpeechCatalog.MicrosoftTts, server, new() { ["voice"] = "en-GB-RyanNeural" });
        if (rig is null) return;
        await ReadAndRelease(await rig.SpeakAsync(new SpeakRequest("hello", "en", Voice: "en-GB-RyanNeural", Rate: 0.5)));
        string ssml = Encoding.UTF8.GetString(captured!.Body);
        Assert.Contains("<voice name=\"en-GB-RyanNeural\">", ssml);
        Assert.Contains("rate=\"-50%\"", ssml);
    }

    [Theory] // B04: HTTP errors are classified, never played, and leave no file
    [InlineData(401, "{\"error\":\"unauthorized\"}", "application/json", ErrorKind.Auth)]
    [InlineData(403, "Forbidden", "text/plain", ErrorKind.Auth)]
    [InlineData(429, "{\"error\":\"too many\"}", "application/json", ErrorKind.RateLimited)]
    [InlineData(503, "busy", "text/plain", ErrorKind.Network)]
    [InlineData(500, "<html>oops</html>", "text/html", ErrorKind.Network)] // F10 test
    [InlineData(400, "{\"error\":\"bad ssml\"}", "application/json", ErrorKind.BadResponse)]
    public async Task Microsoft_http_errors_are_classified(int status, string body, string contentType, ErrorKind expected)
    {
        using var server = new LoopbackHttpServer(_ => new LoopbackHttpResponse(status, Encoding.UTF8.GetBytes(body),
            status == 429 ? new Dictionary<string, string> { ["Retry-After"] = "7" } : null, contentType));
        using var rig = Build(SpeechCatalog.MicrosoftTts, server);
        if (rig is null) return;
        var error = Failure(await rig.SpeakAsync(new SpeakRequest("hello", "en")));
        Assert.Equal(expected, error.Kind);
        if (status == 429) Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
        await rig.AssertNoFilesLeftAsync();
    }

    [Theory] // B04: a 2xx body that is not audio (JSON error, HTML, octet-stream that is not audio) is never treated as audio
    [InlineData("application/json", "{\"error\":{\"code\":\"InvalidVoice\"}}")]
    [InlineData("text/html", "<html>proxy login</html>")]
    [InlineData("application/octet-stream", "{\"error\":\"not audio\"}")]
    [InlineData("audio/mpeg", "{\"error\":{\"code\":\"Throttled\"}}")] // F10 test: a declared audio MIME over a JSON error body
    public async Task Microsoft_non_audio_success_body_is_refused(string contentType, string body)
    {
        using var server = new LoopbackHttpServer(_ => new LoopbackHttpResponse(200, Encoding.UTF8.GetBytes(body), ContentType: contentType));
        using var rig = Build(SpeechCatalog.MicrosoftTts, server);
        if (rig is null) return;
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.SpeakAsync(new SpeakRequest("hello", "en"))).Kind);
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // voices through the F07.2 options path; unsupported language without a voice is refused before any request
    public async Task Microsoft_voices_and_language_refusal()
    {
        using var server = new LoopbackHttpServer(req => req.Path == "/cognitiveservices/voices/list"
            ? LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(new[] { new { ShortName = "en-US-JennyNeural", LocalName = "Jenny", Locale = "en-US" }, new { ShortName = "zh-CN-XiaoxiaoNeural", LocalName = "晓晓", Locale = "zh-CN" } }))
            : Audio(Mp3));
        using var rig = Build(SpeechCatalog.MicrosoftTts, server);
        if (rig is null) return;
        var voices = await rig.VoicesAsync();
        Assert.True(voices.Ok, voices.ErrorDetail);
        Assert.Equal(["en-US-JennyNeural", "zh-CN-XiaoxiaoNeural"], voices.Result!.Items.Select(i => i.Value));
        Assert.Equal("晓晓 (zh-CN)", voices.Result.Items[1].Label);
        int before = server.RequestCount;
        Assert.Equal(ErrorKind.UnsupportedLanguage, Failure(await rig.SpeakAsync(new SpeakRequest("habari", "sw"))).Kind);
        Assert.Equal(before, server.RequestCount);
    }

    [Fact] // region config picks the endpoint origin (and with it the grant); a malformed region falls back to the default
    public void Microsoft_region_selects_the_origin()
    {
        var package = SpeechCatalog.Find(SpeechCatalog.MicrosoftTts)!;
        Assert.StartsWith("https://westeurope.tts.speech.microsoft.com", package.Origin(new Dictionary<string, string> { ["region"] = "westeurope" }));
        Assert.StartsWith("https://eastus.tts.speech.microsoft.com", package.Origin(new Dictionary<string, string> { ["region"] = "evil.com/x" }));
        Assert.StartsWith("https://westeurope.tts.speech.microsoft.com", package.RequiredGrants(new Dictionary<string, string> { ["region"] = "westeurope" }).Single().Origin);
        Assert.All(SpeechCatalog.Choices(SpeechSlot.Tts), p => Assert.True(p.Installed));
    }

    // ---------------- P-S02 Google ----------------

    [Fact] // B03: JSON Base64 audioContent is decoded by the host into a file; the plugin result carries only the handle
    public async Task Google_base64_audio_is_decoded_into_a_handle()
    {
        LoopbackHttpRequest? captured = null;
        string base64 = Convert.ToBase64String(Mp3);
        using var server = new LoopbackHttpServer(req => { captured = req; return LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(new { audioContent = base64 })); });
        using var rig = Build(SpeechCatalog.GoogleTts, server);
        if (rig is null) return;

        // Raw call first: what crosses IPC back from the plugin is the handle and metadata, never the Base64.
        var session = rig.Supervisor.Acquire()!;
        try
        {
            var (requestId, _, task) = session.Invoke("app.susu.google-tts", "tts", PluginTtsProvider.RequestJson(new SpeakRequest("你好", "zh-Hans", Rate: 1.25)), "job-g1",
                [server.Origin], secrets: ["apiKey"], configJson: JsonSerializer.Serialize(new { baseUrl = server.Origin }), instanceId: SpeechCatalog.GoogleTts,
                signer: "unsigned:app.susu.google-tts", adoptResultFiles: true);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            string raw = envelope.Payload!.Value.GetRawText();
            Assert.DoesNotContain(base64[..40], raw);
            var result = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!.Result!.Value.Deserialize(ContractsJson.Default.TtsResult)!;
            Assert.Equal("audio/mpeg", result.Audio.Mime);
            Assert.Equal(Mp3.Length, result.Audio.Bytes);
            var adopted = Assert.Single(session.TakeAdoptedFiles(requestId));
            Assert.Equal(Mp3, await File.ReadAllBytesAsync(rig.Leases.PathOf(adopted), Ct));
            rig.Leases.Release(adopted);
        }
        finally { rig.Supervisor.Release(); }

        using var body = JsonDocument.Parse(captured!.Body);
        Assert.Equal("/v1/text:synthesize", captured.Path);
        Assert.Equal(GoogleKey, H(captured, "X-Goog-Api-Key"));
        Assert.Equal("你好", body.RootElement.GetProperty("input").GetProperty("text").GetString());
        Assert.Equal("cmn-CN", body.RootElement.GetProperty("voice").GetProperty("languageCode").GetString());
        Assert.Equal("MP3", body.RootElement.GetProperty("audioConfig").GetProperty("audioEncoding").GetString());
        Assert.Equal(1.25, body.RootElement.GetProperty("audioConfig").GetProperty("speakingRate").GetDouble());

        Assert.Equal(Mp3, await ReadAndRelease(await rig.SpeakAsync(new SpeakRequest("hello", "en", Voice: "en-US-Neural2-C"))));
        using var second = JsonDocument.Parse(captured.Body);
        Assert.Equal("en-US-Neural2-C", second.RootElement.GetProperty("voice").GetProperty("name").GetString());
        Assert.Equal("en-US", second.RootElement.GetProperty("voice").GetProperty("languageCode").GetString());
        await rig.AssertNoFilesLeftAsync();
    }

    [Theory] // B04: Google error bodies classified; Retry-After kept for 429
    [InlineData(401, "UNAUTHENTICATED", "Request had invalid authentication credentials", ErrorKind.Auth)] // F10 test
    [InlineData(403, "PERMISSION_DENIED", "API key not valid", ErrorKind.Auth)]
    [InlineData(403, "PERMISSION_DENIED", "Quota exceeded / billing not enabled", ErrorKind.Quota)]
    [InlineData(429, "RESOURCE_EXHAUSTED", "Too many requests", ErrorKind.RateLimited)]
    [InlineData(500, "INTERNAL", "boom", ErrorKind.Network)]
    [InlineData(400, "INVALID_ARGUMENT", "Voice 'x' does not exist", ErrorKind.UnsupportedLanguage)]
    [InlineData(400, "INVALID_ARGUMENT", "Input too long", ErrorKind.BadResponse)]
    public async Task Google_errors_are_classified(int status, string code, string message, ErrorKind expected)
    {
        using var server = new LoopbackHttpServer(_ => new LoopbackHttpResponse(status, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = new { code = status, message, status = code } })),
            status == 429 ? new Dictionary<string, string> { ["Retry-After"] = "4" } : null));
        using var rig = Build(SpeechCatalog.GoogleTts, server);
        if (rig is null) return;
        var error = Failure(await rig.SpeakAsync(new SpeakRequest("hello", "en")));
        Assert.Equal(expected, error.Kind);
        if (status == 429) Assert.Equal(TimeSpan.FromSeconds(4), error.RetryAfter);
        await rig.AssertNoFilesLeftAsync();
    }

    [Theory] // 2xx without usable audio: missing field, invalid Base64, Base64 of a JSON error (not audio)
    [InlineData("{}")]
    [InlineData("{\"audioContent\":\"***not base64***\"}")]
    [InlineData("{\"audioContent\":\"eyJlcnJvciI6InguIn0=\"}")]
    public async Task Google_success_without_audio_is_bad_response(string body)
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, body));
        using var rig = Build(SpeechCatalog.GoogleTts, server);
        if (rig is null) return;
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.SpeakAsync(new SpeakRequest("hello", "en"))).Kind);
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact]
    public async Task Google_voices_list()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(new
        {
            voices = new[] { new { languageCodes = new[] { "en-US" }, name = "en-US-Neural2-C", ssmlGender = "FEMALE", naturalSampleRateHertz = 24000 } },
        })));
        using var rig = Build(SpeechCatalog.GoogleTts, server);
        if (rig is null) return;
        var voices = await rig.VoicesAsync();
        Assert.True(voices.Ok, voices.ErrorDetail);
        var item = Assert.Single(voices.Result!.Items);
        Assert.Equal("en-US-Neural2-C", item.Value);
        Assert.Equal("en-US-Neural2-C (female) (en-US)", item.Label);
    }

    // ---------------- P-S03 Tencent ----------------

    [Fact] // B03: TC3-signed TextToVoice; Response.Audio decoded into a handle; speed/voice/language mapping
    public async Task Tencent_signed_request_and_base64_audio()
    {
        LoopbackHttpRequest? captured = null;
        using var server = new LoopbackHttpServer(req =>
        {
            captured = req;
            return LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(new { Response = new { Audio = Convert.ToBase64String(Mp3), SessionId = "s", RequestId = "r" } }));
        });
        using var rig = Build(SpeechCatalog.TencentTts, server);
        if (rig is null) return;
        Assert.Equal(Mp3, await ReadAndRelease(await rig.SpeakAsync(new SpeakRequest("你好世界", "zh-Hans", Rate: 1.5))));
        Assert.Equal("TextToVoice", H(captured!, "X-TC-Action"));
        Assert.Equal("2019-08-23", H(captured!, "X-TC-Version"));
        Assert.Null(H(captured!, "X-TC-Region"));
        Assert.StartsWith($"TC3-HMAC-SHA256 Credential={SecretId}/", H(captured!, "Authorization"));
        Assert.Contains("/tts/tc3_request", H(captured!, "Authorization"));
        Assert.DoesNotContain(SecretKey, Encoding.UTF8.GetString(captured!.Body));
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("你好世界", body.RootElement.GetProperty("Text").GetString());
        Assert.Equal(101001, body.RootElement.GetProperty("VoiceType").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("PrimaryLanguage").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("Speed").GetDouble());
        Assert.Equal("mp3", body.RootElement.GetProperty("Codec").GetString());
        Assert.StartsWith("susu-", body.RootElement.GetProperty("SessionId").GetString());

        await ReadAndRelease(await rig.SpeakAsync(new SpeakRequest("hello", "en", Voice: "101050", Rate: 0.8)));
        using var english = JsonDocument.Parse(captured.Body);
        Assert.Equal(101050, english.RootElement.GetProperty("VoiceType").GetInt32());
        Assert.Equal(2, english.RootElement.GetProperty("PrimaryLanguage").GetInt32());
        Assert.Equal(-1, english.RootElement.GetProperty("Speed").GetDouble());
        await rig.AssertNoFilesLeftAsync();
    }

    [Theory] // B04: 2xx business errors (Response.Error handed back through errorPointer) and HTTP errors are classified
    [InlineData(200, "AuthFailure.SignatureFailure", ErrorKind.Auth)]
    [InlineData(200, "UnsupportedOperation.ServerNotOpen", ErrorKind.Auth)]
    [InlineData(200, "UnsupportedOperation.AccountArrears", ErrorKind.Quota)]
    [InlineData(200, "LimitExceeded.AccessLimit", ErrorKind.RateLimited)]
    [InlineData(200, "InternalError.ErrorGetRoute", ErrorKind.Network)]
    [InlineData(200, "InvalidParameterValue.VoiceType", ErrorKind.BadResponse)]
    [InlineData(401, "", ErrorKind.Auth)] // F10 test: HTTP-level auth failures
    [InlineData(403, "", ErrorKind.Auth)]
    [InlineData(429, "", ErrorKind.RateLimited)]
    [InlineData(502, "", ErrorKind.Network)]
    public async Task Tencent_errors_are_classified(int status, string code, ErrorKind expected)
    {
        using var server = new LoopbackHttpServer(_ => new LoopbackHttpResponse(status,
            Encoding.UTF8.GetBytes(status == 200 ? JsonSerializer.Serialize(new { Response = new { Error = new { Code = code, Message = "vendor says no" }, RequestId = "r" } }) : "{}"),
            status == 429 ? new Dictionary<string, string> { ["Retry-After"] = "2" } : null));
        using var rig = Build(SpeechCatalog.TencentTts, server);
        if (rig is null) return;
        var error = Failure(await rig.SpeakAsync(new SpeakRequest("hello", "en")));
        Assert.Equal(expected, error.Kind);
        if (status == 429) Assert.Equal(TimeSpan.FromSeconds(2), error.RetryAfter);
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // language and length limits are enforced before any request; voices are the static basic list
    public async Task Tencent_limits_and_voices()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, "{}"));
        using var rig = Build(SpeechCatalog.TencentTts, server);
        if (rig is null) return;
        Assert.Equal(ErrorKind.UnsupportedLanguage, Failure(await rig.SpeakAsync(new SpeakRequest("こんにちは", "ja"))).Kind);
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.SpeakAsync(new SpeakRequest(new string('字', 151), "zh-Hans"))).Kind);
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.SpeakAsync(new SpeakRequest(new string('a', 501), "en"))).Kind);
        Assert.Equal(0, server.RequestCount);
        var voices = await rig.VoicesAsync();
        Assert.True(voices.Ok, voices.ErrorDetail);
        Assert.Contains(voices.Result!.Items, i => i.Value == "101001");
        Assert.Contains(voices.Result.Items, i => i.Value == "101051");
    }

    // ---------------- B07 ----------------

    [Fact] // cancel during the download: the canceller's I/O stops and its files go; another caller's clip stays valid
    public async Task Cancel_during_download_cleans_up_and_other_leases_stay_valid()
    {
        var slow = new ManualResetEventSlim(false);
        int calls = 0;
        using var server = new LoopbackHttpServer(_ =>
        {
            if (Interlocked.Increment(ref calls) == 2) slow.Wait(TimeSpan.FromSeconds(10));
            return LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(new { audioContent = Convert.ToBase64String(Mp3) }));
        });
        using var rig = Build(SpeechCatalog.GoogleTts, server);
        if (rig is null) return;
        var kept = Assert.IsType<AudioOutcome.Ready>(await rig.SpeakAsync(new SpeakRequest("first", "en"))).Clip;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancelled = rig.SpeakAsync(new SpeakRequest("second", "en"), cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref calls) < 2 && DateTime.UtcNow < deadline) await Task.Delay(10, Ct);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        slow.Set(); // the server answers after the cancel: that late response must not leave a file behind

        await Task.Delay(300, Ct);
        Assert.True(File.Exists(kept.FilePath));
        Assert.Equal(Mp3, await File.ReadAllBytesAsync(kept.FilePath, Ct));
        Assert.Equal(1, rig.Leases.ActiveCount);
        kept.Dispose();
        await rig.AssertNoFilesLeftAsync();

        // The plugin host is still healthy for the next call.
        Assert.Equal(Mp3, await ReadAndRelease(await rig.SpeakAsync(new SpeakRequest("third", "en"))));
    }

    [Fact] // a timeout is classified and leaves nothing
    public async Task Timeout_is_classified_and_leaves_nothing()
    {
        var slow = new ManualResetEventSlim(false);
        using var server = new LoopbackHttpServer(_ => { slow.Wait(TimeSpan.FromSeconds(10)); return new LoopbackHttpResponse(200, Mp3, ContentType: "audio/mpeg"); });
        using var rig = Build(SpeechCatalog.MicrosoftTts, server);
        if (rig is null) return;
        Assert.Equal(ErrorKind.Timeout, Failure(await rig.SpeakAsync(new SpeakRequest("hello", "en"), timeout: TimeSpan.FromMilliseconds(800))).Kind);
        slow.Set();
        await rig.AssertNoFilesLeftAsync();
    }

    // ---------------- TTS01: native SAPI never depends on the plugin host (F10.3) ----------------

    private sealed class InstantSink : IAudioSink
    {
        public readonly List<string> Mimes = [];
        public AudioDeviceStatus Probe() => new(true);
        public Task PlayAsync(IAudioClip clip, CancellationToken cancellationToken) { lock (Mimes) Mimes.Add(clip.Mime); return Task.CompletedTask; }
    }

    [Fact] // real sandbox + real SAPI: a hung vendor call, then a killed plugin host with a call in flight, never block local SAPI
    public async Task Hung_or_crashed_plugin_host_never_blocks_native_sapi()
    {
        using var hold = new ManualResetEventSlim(false);
        using var server = new LoopbackHttpServer(_ => { hold.Wait(TimeSpan.FromSeconds(30)); return Audio(Mp3); });
        using var rig = Build(SpeechCatalog.MicrosoftTts, server);
        if (rig is null) return;
        try
        {
            if ((await Susu.Windows.Audio.SapiTtsProvider.VoicesAsync()).Count == 0) Assert.Skip("no SAPI voice is installed on this machine");
            var sapi = new Susu.Windows.Audio.SapiTtsProvider(new LeasedAudioFiles(rig.Leases));
            var sink = new InstantSink();
            var backend = new SpeechBackend(new SpeechPlayer(sink), (_, id) => id == BuiltInCatalog.NativeTts ? sapi : rig.Provider);

            var hung = backend.SpeakWithAsync(rig.Settings, rig.InstanceId, "hello", "en-US", TimeSpan.FromSeconds(30), Ct);
            Assert.True(await Eventually.WaitAsync(() => server.RequestCount >= 1));
            var local = await backend.SpeakWithAsync(rig.Settings, BuiltInCatalog.NativeTts, "hello", "en-US", TimeSpan.FromSeconds(45), Ct).WaitAsync(TimeSpan.FromSeconds(50), Ct);
            Assert.Equal(PlaybackStatus.Completed, local.Status);
            Assert.Equal(PlaybackStatus.Superseded, (await hung.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Status);

            // The plugin host dies while a cloud call is in flight: that call fails with a class; SAPI still speaks.
            var inflight = backend.SpeakWithAsync(rig.Settings, rig.InstanceId, "again", "en-US", TimeSpan.FromSeconds(15), Ct);
            Assert.True(await Eventually.WaitAsync(() => server.RequestCount >= 2));
            using (var host = System.Diagnostics.Process.GetProcessById(rig.Session!.ChildPid)) { host.Kill(); host.WaitForExit(5000); }
            var crashed = await inflight.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            Assert.Equal(PlaybackStatus.Failed, crashed.Status);
            Assert.Contains(crashed.Error!.Kind, new[] { ErrorKind.Unavailable, ErrorKind.Network, ErrorKind.Timeout });
            var afterCrash = await backend.SpeakWithAsync(rig.Settings, BuiltInCatalog.NativeTts, "still here", "en-US", TimeSpan.FromSeconds(45), Ct).WaitAsync(TimeSpan.FromSeconds(50), Ct);
            Assert.Equal(PlaybackStatus.Completed, afterCrash.Status);
            Assert.Equal(["audio/wav", "audio/wav"], sink.Mimes); // only SAPI audio ever played
            Assert.True(await Eventually.WaitAsync(() => rig.Leases.ActiveCount == 0));
        }
        finally { hold.Set(); }
    }

    // ---------------- F10 independent verification ----------------

    /// <summary>Records every clip that reaches the output (its bytes, read while "playing").</summary>
    private sealed class RecordingSink : IAudioSink
    {
        public readonly List<byte[]> Played = [];
        public AudioDeviceStatus Probe() => new(true);
        public async Task PlayAsync(IAudioClip clip, CancellationToken cancellationToken)
        {
            byte[] bytes = await File.ReadAllBytesAsync(clip.FilePath, cancellationToken);
            lock (Played) Played.Add(bytes);
        }
    }

    /// <summary>
    /// A copy of a shipped package whose tts method, instead of returning the audio handle, throws a PluginError carrying
    /// exactly what its <c>$http</c> call handed the plugin (the JSON of <c>r</c>) and whether any of the audio's Base64 is in it.
    /// </summary>
    private static string ProbeCopy(string packageDir)
    {
        string source = Path.Combine(staged.Value!, packageDir);
        string relative = packageDir + "-probe-" + Guid.NewGuid().ToString("N")[..8];
        string target = Path.Combine(staged.Value!, relative);
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        string main = File.ReadAllText(Path.Combine(target, "main.js"));
        int at = main.IndexOf("return { audio };", StringComparison.Ordinal);
        Assert.True(at > 0, "probe anchor not found");
        const string Probe = "{ const seen = JSON.stringify(r); throw new PluginError('bad_response', 'B64=' + /SUQzAwAAAAAA|VVVVVVVVVVVV/.test(seen) + ' PROBE ' + seen.slice(0, 1200)); }";
        File.WriteAllText(Path.Combine(target, "main.js"), main[..at] + Probe + main[at..]);
        return relative;
    }

    [Theory] // B03 (plugin side): what the plugin's $http call receives is a handle plus metadata; no Base64 or raw audio ever reaches the plugin
    [InlineData("app.susu.google-tts")]
    [InlineData("app.susu.tencent-tts")]
    [InlineData("app.susu.microsoft-tts")]
    public async Task Plugin_http_result_carries_only_the_handle_never_the_audio(string packageId)
    {
        var package = PluginTranslationProviders.WiredPackages.Single(p => p.PackageId == packageId);
        string base64 = Convert.ToBase64String(Mp3);
        using var server = new LoopbackHttpServer(_ => package.InstanceId switch
        {
            SpeechCatalog.GoogleTts => LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(new { audioContent = base64 })),
            SpeechCatalog.TencentTts => LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(new { Response = new { Audio = base64, RequestId = "r" } })),
            _ => Audio(Mp3),
        });
        if (staged.Value is null) return;
        using var rig = Build(package.InstanceId, server, directory: ProbeCopy(package.Directory));
        if (rig is null) return;
        var error = Failure(await rig.SpeakAsync(new SpeakRequest("hello", "en")));
        string detail = error.Detail ?? "";
        TestContext.Current.TestOutputHelper?.WriteLine(detail);
        Assert.Contains("PROBE", detail); // the probe ran: this is the plugin-visible $http result
        Assert.Contains("B64=false", detail);
        Assert.DoesNotContain(base64[..24], detail);
        Assert.Contains("\"id\":", detail); // a handle
        Assert.Contains("audio/mpeg", detail);
        Assert.Contains(Mp3.Length.ToString(), detail); // byte count metadata
        if (package.InstanceId == SpeechCatalog.GoogleTts) Assert.Contains("\"audioContent\":null", detail);
        if (package.InstanceId == SpeechCatalog.TencentTts) Assert.Contains("\"Audio\":null", detail);
        await rig.AssertNoFilesLeftAsync(); // the result file nobody adopted is released with the call
    }

    [Fact] // TTS01: the vendor is unreachable (connection refused): the cloud call fails as network, SAPI still speaks through the same player
    public async Task Unreachable_vendor_is_a_network_error_and_native_sapi_still_speaks()
    {
        var server = new LoopbackHttpServer(_ => Audio(Mp3));
        server.Dispose(); // nothing listens on this origin any more
        using var rig = Build(SpeechCatalog.MicrosoftTts, server);
        if (rig is null) return;
        if ((await Susu.Windows.Audio.SapiTtsProvider.VoicesAsync()).Count == 0) Assert.Skip("no SAPI voice is installed on this machine");
        var sapi = new Susu.Windows.Audio.SapiTtsProvider(new LeasedAudioFiles(rig.Leases));
        var sink = new InstantSink();
        var backend = new SpeechBackend(new SpeechPlayer(sink), (_, id) => id == BuiltInCatalog.NativeTts ? sapi : rig.Provider);

        var cloud = await backend.SpeakWithAsync(rig.Settings, rig.InstanceId, "hello", "en-US", TimeSpan.FromSeconds(20), Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(PlaybackStatus.Failed, cloud.Status);
        Assert.Equal(ErrorKind.Network, cloud.Error!.Kind);
        var local = await backend.SpeakWithAsync(rig.Settings, BuiltInCatalog.NativeTts, "offline", "en-US", TimeSpan.FromSeconds(45), Ct).WaitAsync(TimeSpan.FromSeconds(50), Ct);
        Assert.Equal(PlaybackStatus.Completed, local.Status);
        Assert.Equal(["audio/wav"], sink.Mimes);
        Assert.True(await Eventually.WaitAsync(() => rig.Leases.ActiveCount == 0));
    }

    [Fact] // TTS02: switching the voice while the first download is in flight supersedes it; the late first audio never plays; everything is released
    public async Task Voice_switch_mid_download_supersedes_and_the_late_audio_never_plays()
    {
        using var gate = new ManualResetEventSlim(false);
        byte[] first = [.. Mp3[..^1], 0x11], second = [.. Mp3[..^1], 0x22];
        using var server = new LoopbackHttpServer(req =>
        {
            bool slowVoice = Encoding.UTF8.GetString(req.Body).Contains("en-US-Neural2-A");
            if (slowVoice) gate.Wait(TimeSpan.FromSeconds(15));
            return LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(new { audioContent = Convert.ToBase64String(slowVoice ? first : second) }));
        });
        using var rig = Build(SpeechCatalog.GoogleTts, server);
        if (rig is null) return;
        var sink = new RecordingSink();
        var player = new SpeechPlayer(sink);
        try
        {
            var old = player.SpeakAsync(rig.Provider, new SpeakRequest("hello", "en", Voice: "en-US-Neural2-A"), TimeSpan.FromSeconds(30), Ct);
            Assert.True(await Eventually.WaitAsync(() => server.RequestCount >= 1));
            var next = await player.SpeakAsync(rig.Provider, new SpeakRequest("hello", "en", Voice: "en-US-Neural2-C"), TimeSpan.FromSeconds(30), Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal(PlaybackStatus.Completed, next.Status);
            Assert.Equal(PlaybackStatus.Superseded, (await old.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Status);
        }
        finally { gate.Set(); }
        await Task.Delay(500, Ct); // the vendor answers the old request now: that audio must not play or linger
        await player.IdleAsync();
        Assert.Equal([second], sink.Played);
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // B07: two calls in flight on one plugin host; cancelling one mid-download leaves the other's result intact; nothing is left afterwards
    public async Task Concurrent_calls_cancel_one_mid_download_and_the_other_completes()
    {
        using var gate = new ManualResetEventSlim(false);
        using var server = new LoopbackHttpServer(_ =>
        {
            gate.Wait(TimeSpan.FromSeconds(15));
            return LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(new { Response = new { Audio = Convert.ToBase64String(Mp3), RequestId = "r" } }));
        });
        using var rig = Build(SpeechCatalog.TencentTts, server);
        if (rig is null) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<AudioOutcome> cancelled, kept;
        try
        {
            cancelled = rig.SpeakAsync(new SpeakRequest("first", "en"), cts.Token);
            kept = rig.SpeakAsync(new SpeakRequest("second", "en"));
            Assert.True(await Eventually.WaitAsync(() => server.RequestCount >= 2));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        }
        finally { gate.Set(); }
        Assert.Equal(Mp3, await ReadAndRelease(await kept.WaitAsync(TimeSpan.FromSeconds(30), Ct)));
        await rig.AssertNoFilesLeftAsync();
    }
}
