using System.Text;
using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Net;
using Susu.Plugins;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F12.2 P-R01 OpenAI ASR and P-R02 Gemini ASR: the real shipped packages through the real AppContainer sandbox + QuickJS + IPC +
/// Broker/$http and <see cref="PluginAsrProvider"/>/<see cref="AsrJob"/>, against a LoopbackHttpServer standing in for the vendor.
/// B02 (the plugin sees a handle, never the audio; the host builds the multipart part / the Base64 field; the real upload size is
/// verifiable and stays within the estimate and the cap), error classes, A04 request-size enforcement, A06 through the plugin,
/// B07 cancel and shared-lease cases. Needs susu.exe published (`dotnet publish src/Susu.Host -c Release -r win-x64`) and skips
/// itself otherwise; the plugin folders are copied from the source tree. Real vendor calls (A01): not executed (no account).
/// </summary>
public class AsrPluginTests
{
    private const string ApiKey = "asr-test-key-0123456789abcdef";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? FindRoot(Func<string, bool> test)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (test(dir.FullName)) return dir.FullName;
        return null;
    }

    private static readonly string[] Packages = [SpeechCatalog.OpenAiAsr, SpeechCatalog.GeminiAsr];

    private static readonly Lazy<string?> staged = new(() =>
    {
        string? output = FindRoot(d => File.Exists(Path.Combine(d, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish", "susu.exe")));
        if (output is null) return null;
        string publish = Path.Combine(output, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish");
        string source = Path.Combine(output, "src", "Susu.Host", "plugins");
        string dir = TestTemp.NewDir("susu-asr-it");
        foreach (string file in Directory.EnumerateFiles(publish))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        foreach (string package in Packages)
        {
            string target = Path.Combine(dir, "plugins", package);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(Path.Combine(source, package))) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
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
        public required PluginAsrProvider Provider { get; init; }
        public required WiredPackage Package { get; init; }
        public required string ConfigJson { get; init; }
        public required string Origin { get; init; }
        public HostSession? Session;
        public Broker Broker => Session!.Broker;
        public LeasedFiles Files => new(Leases);

        /// <summary>A chunk lease holding <paramref name="wav"/> (what the pipeline writes per chunk).</summary>
        public ILeasedFile Chunk(byte[] wav)
        {
            var file = Files.Create("asr", "audio/wav", "wav");
            File.WriteAllBytes(file.FilePath, wav);
            return file;
        }

        public AsrCall Call(ILeasedFile chunk, string output = "text", string? lang = null, long? cap = null, double? seconds = null)
            => new(chunk.LeaseId, chunk.Mime, chunk.Bytes, seconds ?? (chunk.Bytes - 44) / 32000.0, Provider.Model, output, lang, $"asr-{Guid.NewGuid():N}", TimeSpan.FromSeconds(60), cap ?? Provider.Limits.EffectiveRequestCap);

        public Task<AsrOutcome> TranscribeAsync(ILeasedFile chunk, string output = "text", string? lang = null, long? cap = null, double? seconds = null, CancellationToken? token = null)
            => Provider.TranscribeAsync(Call(chunk, output, lang, cap, seconds), token ?? Ct);

        public async Task<(CompletedPayload Completed, string Raw)> RawAsync(string requestJson, IEnumerable<string> handles)
        {
            var session = Supervisor.Acquire()!;
            try
            {
                var (_, _, task) = session.Invoke(Package.PackageId, "asr", requestJson, $"job-{Guid.NewGuid():N}", [Origin], secrets: Package.SecretNames,
                    configJson: ConfigJson, handles: handles, instanceId: Package.InstanceId, signer: Package.Signer);
                var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                return (envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!, envelope.Payload!.Value.GetRawText());
            }
            finally { Supervisor.Release(); }
        }

        public async Task AssertNoFilesLeftAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((Leases.ActiveCount > 0 || (Session is not null && (Broker.ActiveResponseFiles > 0 || Broker.ActiveGrants > 0))) && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
            Assert.Equal(0, Leases.ActiveCount);
            if (Session is not null) Assert.Equal(0, Broker.ActiveGrants); // no call (and so no upload) is still alive
        }

        public void Dispose() { Supervisor.Dispose(); Leases.Dispose(); }
    }

    private static Rig? Build(string instanceId, string model, LoopbackHttpServer server, string? directory = null)
    {
        if (staged.Value is not { } dir) return null;
        var package = PluginTranslationProviders.WiredPackages.Single(p => p.InstanceId == instanceId);
        var speech = (SpeechPackage)package.Credentials;
        var cfg = new Dictionary<string, string> { ["baseUrl"] = server.Origin };
        var secrets = new FakeSecretStore();
        secrets.Set("account", "apiKey", ApiKey);
        var account = new AccountSettings("account", "Shared account", ["apiKey"], [.. speech.RequiredGrants(cfg)]);
        var instance = new InstanceSettings(instanceId, package.PackageId, 1, cfg, new Dictionary<string, string> { ["apiKey"] = "account" });
        var accounts = new AccountAuthorization(() => ([account], [instance]));
        var leases = new FileLeases(TestTemp.NewDir("susu-asr-leases"));
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
        rig = new Rig
        {
            Leases = leases, Supervisor = supervisor, Package = package, ConfigJson = JsonSerializer.Serialize(cfg), Origin = package.Origin(cfg),
            Provider = new PluginAsrProvider(package, instance, speech.Models.Single(m => m.Id == model), supervisor),
        };
        return rig;
    }

    private static string? H(LoopbackHttpRequest req, string name)
        => req.Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static ProviderError Failure(AsrOutcome outcome) => Assert.IsType<AsrOutcome.Failure>(outcome).Error;

    private static byte[] ShortWav(double seconds = 0.4) => AsrPipelineTests.Wav(AsrPipelineTests.Tone(seconds));

    private static string OpenAiText(string text) => JsonSerializer.Serialize(new { text });
    private static string OpenAiSegments(params object[] segments) => JsonSerializer.Serialize(new { task = "transcribe", language = "english", duration = 0.4, text = "t", segments });
    private static object Seg(double start, double end, string text) => new { id = 0, start, end, text, tokens = new[] { 1 } };

    private static string GeminiOk(string text, string finish = "STOP")
        => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { role = "model", parts = new[] { new { text } } }, finishReason = finish } } });

    /// <summary>The multipart body split into named parts: (headers, body bytes as Latin-1 so binary survives).</summary>
    private static List<(string Headers, string Body)> Parts(LoopbackHttpRequest req)
    {
        string contentType = H(req, "Content-Type")!;
        string boundary = contentType[(contentType.IndexOf("boundary=", StringComparison.Ordinal) + 9)..].Trim('"');
        string body = Encoding.Latin1.GetString(req.Body);
        var parts = new List<(string, string)>();
        foreach (string raw in body.Split("--" + boundary).Skip(1))
        {
            if (raw.StartsWith("--", StringComparison.Ordinal)) break;
            int split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            parts.Add((raw[..split].Trim().Replace("\"", ""), raw[(split + 4)..^2]));
        }
        return parts;
    }

    // ---------------- P-R01 OpenAI ----------------

    [Fact] // A01-shape/B02: the host builds the multipart upload from the handle; real wire size is within the estimate and the cap
    public async Task OpenAi_text_upload_is_multipart_built_by_the_host()
    {
        LoopbackHttpRequest? captured = null;
        using var server = new LoopbackHttpServer(req => { captured = req; return LoopbackHttpResponse.Json(200, OpenAiText(" hello there ")); });
        using var rig = Build(SpeechCatalog.OpenAiAsr, "whisper-1", server);
        if (rig is null) return;
        byte[] wav = ShortWav();
        using var chunk = rig.Chunk(wav);

        var outcome = Assert.IsType<AsrOutcome.Transcribed>(await rig.TranscribeAsync(chunk, lang: "en-US"));
        Assert.Equal(("text", "hello there"), (outcome.Kind, outcome.Text));
        Assert.Null(outcome.Segments);

        Assert.NotNull(captured);
        Assert.Equal(("POST", "/v1/audio/transcriptions"), (captured.Method, captured.Path));
        Assert.Equal($"Bearer {ApiKey}", H(captured, "Authorization"));
        Assert.StartsWith("multipart/form-data", H(captured, "Content-Type"));
        var parts = Parts(captured);
        var file = parts.Single(p => p.Headers.Contains("name=file"));
        Assert.Contains("filename=audio.wav", file.Headers);
        Assert.Contains("audio/wav", file.Headers);
        Assert.Equal(Encoding.Latin1.GetString(wav), file.Body); // the leased chunk's real bytes, byte for byte
        Assert.Equal("whisper-1", parts.Single(p => p.Headers.Contains("name=model")).Body);
        Assert.Equal("json", parts.Single(p => p.Headers.Contains("name=response_format")).Body);
        Assert.Equal("en", parts.Single(p => p.Headers.Contains("name=language")).Body);
        Assert.DoesNotContain(parts, p => p.Headers.Contains("timestamp_granularities"));
        // The whole upload body is verifiable: bigger than the file, within the model's overhead estimate and its cap.
        Assert.InRange(captured.Body.Length, wav.Length + 1, rig.Provider.Limits.EstimateRequestBytes(wav.Length));
        Assert.True(captured.Body.Length <= rig.Provider.Limits.MaxRequestBytes);
        Assert.DoesNotContain(ApiKey, Encoding.Latin1.GetString(captured.Body));
        chunk.Dispose();
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // whisper-1 with timecodes: verbose_json + segment granularity; segments parsed and validated against the chunk length
    public async Task OpenAi_segments_request_verbose_json_and_parse_times()
    {
        LoopbackHttpRequest? captured = null;
        using var server = new LoopbackHttpServer(req => { captured = req; return LoopbackHttpResponse.Json(200, OpenAiSegments(Seg(0, 0.25, " one"), Seg(0.1, 0.4, " two"))); });
        using var rig = Build(SpeechCatalog.OpenAiAsr, "whisper-1", server);
        if (rig is null) return;
        using var chunk = rig.Chunk(ShortWav());
        var outcome = Assert.IsType<AsrOutcome.Transcribed>(await rig.TranscribeAsync(chunk, "segments"));
        Assert.Equal([(0.0, 0.25, " one"), (0.1, 0.4, " two")], outcome.Segments!.Select(s => (s.Start, s.End, s.Text)).ToArray()); // overlap kept as given
        var parts = Parts(captured!);
        Assert.Equal("verbose_json", parts.Single(p => p.Headers.Contains("name=response_format")).Body);
        Assert.Equal("segment", parts.Single(p => p.Headers.Contains("timestamp_granularities[]")).Body);
        Assert.DoesNotContain(parts, p => p.Headers.Contains("name=language"));
    }

    [Theory] // A06 through the real plugin: bad vendor segments become bad_response, never repaired
    [InlineData(0.0, 0.0)] // end <= start
    [InlineData(0.3, 0.2)]
    [InlineData(-0.1, 0.2)]
    [InlineData(0.0, 5.0)] // out of the 0.4 s chunk
    public async Task OpenAi_bad_segments_are_bad_response(double start, double end)
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, OpenAiSegments(Seg(start, end, "x"))));
        using var rig = Build(SpeechCatalog.OpenAiAsr, "whisper-1", server);
        if (rig is null) return;
        using var chunk = rig.Chunk(ShortWav());
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.TranscribeAsync(chunk, "segments")).Kind);
    }

    [Fact] // A06: unordered, missing-field and wrong-type segments, and a missing segments array, through the real plugin
    public async Task OpenAi_malformed_segment_lists_are_bad_response()
    {
        var bodies = new Queue<string>([
            OpenAiSegments(Seg(0.2, 0.3, "b"), Seg(0.0, 0.1, "a")), // unordered
            """{"segments":[{"start":0.0,"text":"no end"}]}""", // missing end
            """{"segments":[{"start":0.0,"end":0.1}]}""", // missing text
            """{"segments":[{"start":"0","end":0.1,"text":"x"}]}""", // string time
            """{"segments":[{"start":null,"end":0.1,"text":"x"}]}""", // NaN-like null
            """{"text":"only text"}""", // no segments
        ]);
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, bodies.Dequeue()));
        using var rig = Build(SpeechCatalog.OpenAiAsr, "whisper-1", server);
        if (rig is null) return;
        using var chunk = rig.Chunk(ShortWav());
        for (int i = 0; i < 6; i++) Assert.Equal(ErrorKind.BadResponse, Failure(await rig.TranscribeAsync(chunk, "segments")).Kind);
        Assert.Empty(bodies);
    }

    [Fact] // A06: an empty segment list is the valid answer for a chunk without speech
    public async Task OpenAi_empty_segments_mean_no_speech_in_the_chunk()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, OpenAiSegments()));
        using var rig = Build(SpeechCatalog.OpenAiAsr, "whisper-1", server);
        if (rig is null) return;
        using var chunk = rig.Chunk(ShortWav());
        var outcome = Assert.IsType<AsrOutcome.Transcribed>(await rig.TranscribeAsync(chunk, "segments"));
        Assert.Empty(outcome.Segments!);
        Assert.Equal("", outcome.Text);
    }

    [Fact] // A03: a text-only model is never asked for timecodes: refused at the host, nothing sent
    public async Task OpenAi_text_only_model_refuses_segments_without_a_request()
    {
        int hits = 0;
        using var server = new LoopbackHttpServer(_ => { Interlocked.Increment(ref hits); return LoopbackHttpResponse.Json(200, OpenAiText("x")); });
        using var rig = Build(SpeechCatalog.OpenAiAsr, "gpt-4o-transcribe", server);
        if (rig is null) return;
        using var chunk = rig.Chunk(ShortWav());
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.TranscribeAsync(chunk, "segments")).Kind);
        Assert.Equal(0, hits);
        var text = Assert.IsType<AsrOutcome.Transcribed>(await rig.TranscribeAsync(chunk));
        Assert.Equal("x", text.Text);
    }

    [Fact] // error classes: statuses, Retry-After, quota, bad bodies
    public async Task OpenAi_errors_are_classified()
    {
        var responses = new Queue<LoopbackHttpResponse>([
            LoopbackHttpResponse.Json(401, """{"error":{"message":"bad key","type":"invalid_request_error","code":"invalid_api_key"}}"""),
            new(429, """{"error":{"message":"slow down","type":"requests","code":"rate_limit_exceeded"}}"""u8.ToArray(), new Dictionary<string, string> { ["Retry-After"] = "7" }),
            LoopbackHttpResponse.Json(429, """{"error":{"message":"You exceeded your current quota","type":"insufficient_quota","code":"insufficient_quota"}}"""),
            LoopbackHttpResponse.Json(500, "{}"),
            LoopbackHttpResponse.Json(413, """{"error":{"message":"too big"}}"""),
            LoopbackHttpResponse.Text(200, "<html>gateway</html>"),
            LoopbackHttpResponse.Json(200, "{}"),
        ]);
        using var server = new LoopbackHttpServer(_ => responses.Dequeue());
        using var rig = Build(SpeechCatalog.OpenAiAsr, "whisper-1", server);
        if (rig is null) return;
        using var chunk = rig.Chunk(ShortWav());
        Assert.Equal(ErrorKind.Auth, Failure(await rig.TranscribeAsync(chunk)).Kind);
        var limited = Failure(await rig.TranscribeAsync(chunk));
        Assert.Equal((ErrorKind.RateLimited, TimeSpan.FromSeconds(7)), (limited.Kind, limited.RetryAfter));
        Assert.Equal(ErrorKind.Quota, Failure(await rig.TranscribeAsync(chunk)).Kind);
        Assert.Equal(ErrorKind.Network, Failure(await rig.TranscribeAsync(chunk)).Kind);
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.TranscribeAsync(chunk)).Kind);
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.TranscribeAsync(chunk)).Kind);
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.TranscribeAsync(chunk)).Kind);
    }

    // ---------------- P-R02 Gemini ----------------

    [Fact] // B02: the host writes the Base64 of the chunk into the reserved JSON null; size within estimate and cap; text only
    public async Task Gemini_inline_upload_is_base64_filled_by_the_host()
    {
        LoopbackHttpRequest? captured = null;
        using var server = new LoopbackHttpServer(req => { captured = req; return LoopbackHttpResponse.Json(200, GeminiOk("  transcript text \n")); });
        using var rig = Build(SpeechCatalog.GeminiAsr, "gemini-2.5-flash", server);
        if (rig is null) return;
        byte[] wav = ShortWav();
        using var chunk = rig.Chunk(wav);
        var outcome = Assert.IsType<AsrOutcome.Transcribed>(await rig.TranscribeAsync(chunk, lang: "zh-CN"));
        Assert.Equal(("text", "transcript text"), (outcome.Kind, outcome.Text));
        Assert.Null(outcome.Segments);

        Assert.NotNull(captured);
        Assert.Equal(("POST", "/v1beta/models/gemini-2.5-flash:generateContent"), (captured.Method, captured.Path));
        Assert.Equal(ApiKey, H(captured, "X-Goog-Api-Key"));
        using var body = JsonDocument.Parse(captured.Body);
        var parts = body.RootElement.GetProperty("contents")[0].GetProperty("parts");
        string prompt = parts[0].GetProperty("text").GetString()!;
        Assert.Contains("Transcribe", prompt);
        Assert.Contains("zh-CN", prompt);
        Assert.Equal("audio/wav", parts[1].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal(wav, Convert.FromBase64String(parts[1].GetProperty("inlineData").GetProperty("data").GetString()!)); // real bytes, host-filled
        Assert.InRange(captured.Body.Length, wav.Length * 4 / 3, rig.Provider.Limits.EstimateRequestBytes(wav.Length)); // Base64 expansion counted
        Assert.DoesNotContain(ApiKey, Encoding.UTF8.GetString(captured.Body));
        chunk.Dispose();
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // Gemini offers no timecodes: segments are refused (host and plugin), nothing is sent
    public async Task Gemini_refuses_segments_without_a_request()
    {
        int hits = 0;
        using var server = new LoopbackHttpServer(_ => { Interlocked.Increment(ref hits); return LoopbackHttpResponse.Json(200, GeminiOk("x")); });
        using var rig = Build(SpeechCatalog.GeminiAsr, "gemini-2.5-flash", server);
        if (rig is null) return;
        using var chunk = rig.Chunk(ShortWav());
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.TranscribeAsync(chunk, "segments")).Kind);
        // Even a hand-built call that skips the host's check is refused by the plugin itself.
        string request = JsonSerializer.Serialize(new AsrRequest(new FileHandleInfo(chunk.LeaseId, "audio/wav", chunk.Bytes, 400), "gemini-2.5-flash", "segments"), ContractsJson.Default.AsrRequest);
        var (completed, _) = await rig.RawAsync(request, [chunk.LeaseId]);
        Assert.False(completed.Ok);
        Assert.Equal(0, hits);
    }

    [Fact] // PLAN 4.7.2: refusals, truncation, blocked prompts and empty output are errors, never an empty success
    public async Task Gemini_refusal_truncation_and_empty_output_are_bad_response()
    {
        var bodies = new Queue<string>([
            GeminiOk("partial", "MAX_TOKENS"),
            GeminiOk("", "STOP"),
            GeminiOk("I cannot do that", "SAFETY"),
            GeminiOk("quoted", "RECITATION"),
            """{"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"}}""",
            """{"candidates":[]}""",
            "[]",
        ]);
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, bodies.Dequeue()));
        using var rig = Build(SpeechCatalog.GeminiAsr, "gemini-2.5-flash", server);
        if (rig is null) return;
        using var chunk = rig.Chunk(ShortWav());
        for (int i = 0; i < 7; i++) Assert.Equal(ErrorKind.BadResponse, Failure(await rig.TranscribeAsync(chunk)).Kind);
        Assert.Empty(bodies);
    }

    [Fact]
    public async Task Gemini_errors_are_classified()
    {
        var responses = new Queue<LoopbackHttpResponse>([
            LoopbackHttpResponse.Json(403, """{"error":{"code":403,"message":"denied","status":"PERMISSION_DENIED"}}"""),
            LoopbackHttpResponse.Json(400, """{"error":{"code":400,"message":"API key not valid","status":"INVALID_ARGUMENT"}}"""),
            new(429, """{"error":{"code":429,"message":"slow","status":"RESOURCE_EXHAUSTED"}}"""u8.ToArray(), new Dictionary<string, string> { ["Retry-After"] = "3" }),
            LoopbackHttpResponse.Json(429, """{"error":{"code":429,"message":"You exceeded your current quota, check billing","status":"RESOURCE_EXHAUSTED"}}"""),
            LoopbackHttpResponse.Json(503, """{"error":{"status":"UNAVAILABLE"}}"""),
        ]);
        using var server = new LoopbackHttpServer(_ => responses.Dequeue());
        using var rig = Build(SpeechCatalog.GeminiAsr, "gemini-2.5-flash", server);
        if (rig is null) return;
        using var chunk = rig.Chunk(ShortWav());
        Assert.Equal(ErrorKind.Auth, Failure(await rig.TranscribeAsync(chunk)).Kind);
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.TranscribeAsync(chunk)).Kind);
        var limited = Failure(await rig.TranscribeAsync(chunk));
        Assert.Equal((ErrorKind.RateLimited, TimeSpan.FromSeconds(3)), (limited.Kind, limited.RetryAfter));
        Assert.Equal(ErrorKind.Quota, Failure(await rig.TranscribeAsync(chunk)).Kind);
        Assert.Equal(ErrorKind.Network, Failure(await rig.TranscribeAsync(chunk)).Kind);
    }

    // ---------------- B02: what the plugin itself sees ----------------

    /// <summary>A copy of a shipped package whose code, right after its <c>$http</c> call, throws what it saw (req and r).</summary>
    private static string ProbeCopy(string packageDir, string marker)
    {
        string source = Path.Combine(staged.Value!, packageDir);
        string relative = packageDir + "-probe-" + Guid.NewGuid().ToString("N")[..8];
        string target = Path.Combine(staged.Value!, relative);
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        string main = File.ReadAllText(Path.Combine(target, "main.js"));
        int at = main.IndexOf("if (r.status < 200 || r.status >= 300)", StringComparison.Ordinal);
        Assert.True(at > 0, "probe anchor not found");
        string probe = "{ const seen = JSON.stringify({ req: req, r: r }); throw new PluginError('bad_response', 'B64=' + (seen.indexOf('" + marker +
            "') >= 0) + ' LEN=' + seen.length + ' PROBE ' + seen.slice(0, 1500)); }";
        File.WriteAllText(Path.Combine(target, "main.js"), main[..at] + probe + main[at..]);
        return relative;
    }

    [Theory] // B02 (plugin side): the plugin's input and its $http result hold the handle and metadata only, never the audio or its Base64
    [InlineData(SpeechCatalog.OpenAiAsr, "whisper-1")]
    [InlineData(SpeechCatalog.GeminiAsr, "gemini-2.5-flash")]
    public async Task Plugin_sees_only_the_handle_never_the_audio(string instanceId, string model)
    {
        if (staged.Value is null) return;
        byte[] wav = ShortWav(1.0); // 32 KB: its Base64 is ~43k chars
        string base64 = Convert.ToBase64String(wav);
        LoopbackHttpRequest? sent = null;
        using var server = new LoopbackHttpServer(req => { sent = req; return LoopbackHttpResponse.Json(200, instanceId == SpeechCatalog.OpenAiAsr ? OpenAiText("hi") : GeminiOk("hi")); });
        var package = PluginTranslationProviders.WiredPackages.Single(p => p.InstanceId == instanceId);
        using var rig = Build(instanceId, model, server, directory: ProbeCopy(package.Directory, base64[20..44]));
        if (rig is null) return;
        using var chunk = rig.Chunk(wav);
        string detail = Failure(await rig.TranscribeAsync(chunk)).Detail ?? "";
        TestContext.Current.TestOutputHelper?.WriteLine(detail);
        Assert.Contains("PROBE", detail); // the probe ran after the real $http call
        Assert.Contains("B64=false", detail);
        int len = int.Parse(System.Text.RegularExpressions.Regex.Match(detail, @"LEN=(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(len < 2000 && len < base64.Length, $"plugin-visible JSON is {len} chars; the audio's Base64 alone is {base64.Length}");
        Assert.Contains(chunk.LeaseId, detail); // the handle
        Assert.Contains("audio/wav", detail);
        Assert.NotNull(sent); // and the vendor still got the real audio
        if (instanceId == SpeechCatalog.GeminiAsr) Assert.Contains(base64, Encoding.UTF8.GetString(sent.Body));
        else Assert.Contains(Encoding.Latin1.GetString(wav), Encoding.Latin1.GetString(sent.Body));
        chunk.Dispose();
        await rig.AssertNoFilesLeftAsync();
    }

    [Theory] // B02 raw: neither the answer nor what crosses IPC holds audio bytes
    [InlineData(SpeechCatalog.OpenAiAsr, "whisper-1")]
    [InlineData(SpeechCatalog.GeminiAsr, "gemini-2.5-flash")]
    public async Task What_crosses_ipc_holds_no_audio(string instanceId, string model)
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, instanceId == SpeechCatalog.OpenAiAsr ? OpenAiText("hi") : GeminiOk("hi")));
        using var rig = Build(instanceId, model, server);
        if (rig is null) return;
        byte[] wav = ShortWav(1.0);
        string base64 = Convert.ToBase64String(wav);
        using var chunk = rig.Chunk(wav);
        string request = PluginAsrProvider.RequestJson(rig.Call(chunk));
        Assert.DoesNotContain(base64[..40], request);
        Assert.Contains(chunk.LeaseId, request);
        var (completed, raw) = await rig.RawAsync(request, [chunk.LeaseId]);
        Assert.True(completed.Ok, completed.Error?.Detail);
        Assert.DoesNotContain(base64[..40], raw);
        Assert.DoesNotContain(base64[^40..], raw);
        // A handle the call was not granted is refused by the host even when the plugin passes it on.
        using var other = rig.Chunk(wav);
        string forged = PluginAsrProvider.RequestJson(rig.Call(other));
        var (denied, _) = await rig.RawAsync(forged, [chunk.LeaseId]);
        Assert.False(denied.Ok);
    }

    // ---------------- A04: the whole request size is enforced ----------------

    [Theory] // maxRequestBytes is respected by the broker on the real body, multipart overhead and Base64 expansion included
    [InlineData(SpeechCatalog.OpenAiAsr, "whisper-1")]
    [InlineData(SpeechCatalog.GeminiAsr, "gemini-2.5-flash")]
    public async Task The_broker_refuses_a_body_over_the_request_cap_and_accepts_one_at_it(string instanceId, string model)
    {
        int hits = 0;
        long lastBody = 0;
        using var server = new LoopbackHttpServer(req => { Interlocked.Increment(ref hits); lastBody = req.Body.Length; return LoopbackHttpResponse.Json(200, instanceId == SpeechCatalog.OpenAiAsr ? OpenAiText("ok") : GeminiOk("ok")); });
        using var rig = Build(instanceId, model, server);
        if (rig is null) return;
        byte[] wav = ShortWav(1.0);
        using var chunk = rig.Chunk(wav);
        // The file alone is under this cap, but the real body (multipart framing / Base64) is not: nothing is sent.
        var refused = Failure(await rig.TranscribeAsync(chunk, cap: wav.Length));
        Assert.Equal(ErrorKind.BadResponse, refused.Kind);
        Assert.Contains("request limit", refused.Detail);
        Assert.Equal(0, hits);
        // One byte under the real size is refused, the exact size is accepted.
        Assert.IsType<AsrOutcome.Transcribed>(await rig.TranscribeAsync(chunk));
        long real = lastBody;
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.TranscribeAsync(chunk, cap: real - 1)).Kind);
        Assert.IsType<AsrOutcome.Transcribed>(await rig.TranscribeAsync(chunk, cap: real));
        Assert.Equal(2, hits);
        Assert.True(real <= rig.Provider.Limits.EstimateRequestBytes(wav.Length)); // the host's estimate is a safe upper bound
    }

    [Fact] // A04: a shrunken model limit cuts shorter chunks; every real request body fits the cap, end to end through the sandbox
    public async Task A_long_recording_goes_up_in_chunks_that_all_fit_the_request_cap()
    {
        var bodies = new List<int>();
        using var server = new LoopbackHttpServer(req => { lock (bodies) bodies.Add(req.Body.Length); return LoopbackHttpResponse.Json(200, GeminiOk("piece")); });
        using var rig = Build(SpeechCatalog.GeminiAsr, "gemini-2.5-flash", server);
        if (rig is null) return;
        // The same Gemini package and Base64 mechanism, with a model limit shrunk to 100 kB for the whole request.
        var small = new SpeechModel("gemini-2.5-flash", false, new AsrLimits([AsrFormat.Wav16kMono], 12_000_000, 100_000, 300, AsrUpload.Base64Json, 4096));
        var provider = new PluginAsrProvider(rig.Package, new InstanceSettings(rig.Package.InstanceId, rig.Package.PackageId, 1, JsonSerializer.Deserialize<Dictionary<string, string>>(rig.ConfigJson)!,
            new Dictionary<string, string> { ["apiKey"] = "account" }), small, rig.Supervisor);
        string? text = null;
        var job = new AsrJob(() => provider, rig.Files, t => { text = t; return Task.CompletedTask; }, () => true);
        var wav = AsrPipelineTests.Wav(AsrPipelineTests.Tone(10));
        var input = rig.Files.Create("asr-input", "audio/wav", "wav");
        File.WriteAllBytes(input.FilePath, wav);
        var state = await job.TranscribeAsync(new RecordedAudio(input, TimeSpan.FromSeconds(10), 16000, false));
        Assert.True(state.Phase == AsrPhase.Transcribed, $"{state.Phase} {state.Error}");
        Assert.True(bodies.Count >= 4, $"{bodies.Count} requests");
        Assert.All(bodies, b => Assert.True(b <= 100_000, $"{b} bytes"));
        Assert.Equal(string.Join(' ', Enumerable.Repeat("piece", bodies.Count)), text);
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // A08 (recording): 310 s needs two real requests with the shipped OpenAI limits (300 s per chunk); text merges in order
    public async Task A_recording_over_five_minutes_makes_two_real_requests()
    {
        var sizes = new List<int>();
        int n = 0;
        using var server = new LoopbackHttpServer(req => { lock (sizes) sizes.Add(req.Body.Length); return LoopbackHttpResponse.Json(200, OpenAiText($"part {Interlocked.Increment(ref n)}")); });
        using var rig = Build(SpeechCatalog.OpenAiAsr, "whisper-1", server);
        if (rig is null) return;
        string? text = null;
        var job = new AsrJob(() => rig.Provider, rig.Files, t => { text = t; return Task.CompletedTask; }, () => true);
        var input = rig.Files.Create("asr-input", "audio/wav", "wav");
        File.WriteAllBytes(input.FilePath, AsrPipelineTests.Wav(AsrPipelineTests.Tone(310)));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var state = await job.TranscribeAsync(new RecordedAudio(input, TimeSpan.FromSeconds(310), 16000, false));
        TestContext.Current.TestOutputHelper?.WriteLine($"A08 sandbox: 310 s, {sizes.Count} requests ({string.Join(", ", sizes)} bytes), {clock.ElapsedMilliseconds} ms");
        Assert.True(state.Phase == AsrPhase.Transcribed, $"{state.Phase} {state.Error}");
        Assert.Equal("part 1 part 2", text);
        Assert.Equal(2, sizes.Count);
        Assert.All(sizes, s => Assert.True(s <= 25_000_000));
        Assert.InRange(sizes.Max(), 9_600_000, 9_700_000); // 300 s of 16 kHz mono 16-bit plus multipart framing
        await rig.AssertNoFilesLeftAsync();
    }

    // ---------------- B07: cancel and shared leases ----------------

    [Fact] // A05/B07: cancelling mid-upload stops the call (no active grant), later chunks are never sent, no file is left
    public async Task Cancel_during_transcription_leaves_no_active_upload_and_no_files()
    {
        var gate = new ManualResetEventSlim(false);
        int hits = 0;
        using var server = new LoopbackHttpServer(_ =>
        {
            if (Interlocked.Increment(ref hits) == 1) gate.Wait(TimeSpan.FromSeconds(10));
            return LoopbackHttpResponse.Json(200, OpenAiText("late"));
        });
        using var rig = Build(SpeechCatalog.OpenAiAsr, "whisper-1", server);
        if (rig is null) return;
        int translated = 0;
        var small = new SpeechModel("whisper-1", true, new AsrLimits([AsrFormat.Wav16kMono], 24_000_000, 25_000_000, 6, AsrUpload.Multipart, 2048));
        var provider = new PluginAsrProvider(rig.Package, new InstanceSettings(rig.Package.InstanceId, rig.Package.PackageId, 1, JsonSerializer.Deserialize<Dictionary<string, string>>(rig.ConfigJson)!,
            new Dictionary<string, string> { ["apiKey"] = "account" }), small, rig.Supervisor);
        var job = new AsrJob(() => provider, rig.Files, _ => { Interlocked.Increment(ref translated); return Task.CompletedTask; }, () => true);
        var input = rig.Files.Create("asr-input", "audio/wav", "wav");
        File.WriteAllBytes(input.FilePath, AsrPipelineTests.Wav(AsrPipelineTests.Tone(20)));
        var running = job.TranscribeAsync(new RecordedAudio(input, TimeSpan.FromSeconds(20), 16000, false));
        for (var deadline = DateTime.UtcNow.AddSeconds(15); Volatile.Read(ref hits) < 1 && DateTime.UtcNow < deadline;) await Task.Delay(10, Ct);
        Assert.Equal(1, Volatile.Read(ref hits));
        job.Cancel();
        Assert.Equal(AsrPhase.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Phase);
        gate.Set();
        await rig.AssertNoFilesLeftAsync(); // no lease, no response file, no live grant (so no live upload)
        Assert.Equal(0, translated);
        await Task.Delay(200, Ct);
        Assert.Equal(1, Volatile.Read(ref hits)); // chunks 2..4 were never uploaded

        // The service is usable again after the cancel.
        var again = rig.Files.Create("asr-input", "audio/wav", "wav");
        File.WriteAllBytes(again.FilePath, ShortWav(2));
        Assert.Equal(AsrPhase.Transcribed, (await job.TranscribeAsync(new RecordedAudio(again, TimeSpan.FromSeconds(2), 16000, false))).Phase);
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // B07: two legal calls share one chunk lease; cancelling one keeps the other and the owner's lease and file intact
    public async Task Two_calls_share_one_chunk_and_cancelling_one_keeps_the_other()
    {
        var gate = new ManualResetEventSlim(false);
        int hits = 0;
        var bodies = new List<byte[]>();
        using var server = new LoopbackHttpServer(req =>
        {
            lock (bodies) bodies.Add(req.Body);
            if (Interlocked.Increment(ref hits) == 1) gate.Wait(TimeSpan.FromSeconds(10));
            return LoopbackHttpResponse.Json(200, OpenAiText("second"));
        });
        using var rig = Build(SpeechCatalog.OpenAiAsr, "whisper-1", server);
        if (rig is null) return;
        byte[] wav = ShortWav(0.5);
        using var chunk = rig.Chunk(wav);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var first = rig.TranscribeAsync(chunk, token: cts.Token);
        for (var deadline = DateTime.UtcNow.AddSeconds(15); Volatile.Read(ref hits) < 1 && DateTime.UtcNow < deadline;) await Task.Delay(10, Ct);
        Assert.Equal(1, Volatile.Read(ref hits));

        Assert.Equal("second", Assert.IsType<AsrOutcome.Transcribed>(await rig.TranscribeAsync(chunk)).Text);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        gate.Set();
        Assert.Equal(2, bodies.Count);
        Assert.All(bodies, b => Assert.Contains(Encoding.Latin1.GetString(wav), Encoding.Latin1.GetString(b))); // both carried the same real bytes
        var deadline2 = DateTime.UtcNow.AddSeconds(5);
        while (rig.Leases.ActiveCount > 1 && DateTime.UtcNow < deadline2) await Task.Delay(20, Ct);
        Assert.Equal(1, rig.Leases.ActiveCount);
        Assert.Equal(wav, File.ReadAllBytes(chunk.FilePath));
        Assert.IsType<AsrOutcome.Transcribed>(await rig.TranscribeAsync(chunk)); // still usable
        chunk.Dispose();
        await rig.AssertNoFilesLeftAsync();
    }

    [Theory] // the shipped manifests are valid, declare only asr, and call exactly the host the catalog grants credentials for
    [InlineData(SpeechCatalog.OpenAiAsr)]
    [InlineData(SpeechCatalog.GeminiAsr)]
    public void The_shipped_manifests_match_the_catalog(string instanceId)
    {
        string? root = FindRoot(d => Directory.Exists(Path.Combine(d, "src", "Susu.Host", "plugins", instanceId)));
        Assert.NotNull(root);
        var (manifest, issues) = PackageManifest.Parse(File.ReadAllText(Path.Combine(root, "src", "Susu.Host", "plugins", instanceId, "manifest.yaml")));
        Assert.Empty(issues);
        var package = SpeechCatalog.Find(instanceId)!;
        Assert.Equal(package.PackageId, manifest!.Id);
        Assert.Equal(["asr"], manifest.Capabilities.ToArray());
        Assert.Equal(["apiKey"], manifest.CredentialUse.ToArray());
        Assert.Equal(new Uri(package.DefaultOrigin).Host, Assert.Single(manifest.Hosts));
        Assert.True(package.Installed);
        Assert.Contains(PluginTranslationProviders.WiredPackages, p => p.InstanceId == instanceId && p.Directory == $"plugins/{instanceId}");
    }

    // ---------------- selection, A02 ----------------

    [Fact] // A02: the provider follows the ASR selection only; the AI translation model never changes it; credentials need the grant
    public void The_provider_follows_the_asr_selection_and_needs_its_grant()
    {
        var d = BuiltInCatalog.Defaults();
        var speech = (SpeechPackage)CredentialPackages.Find(SpeechCatalog.OpenAiAsr)!;
        var gemini = (SpeechPackage)CredentialPackages.Find(SpeechCatalog.GeminiAsr)!;
        var grants = new List<CredentialGrant>([.. speech.RequiredGrants(new Dictionary<string, string>()), .. gemini.RequiredGrants(new Dictionary<string, string>())]);
        var bindings = new Dictionary<string, string> { ["apiKey"] = "acct" };
        AppSettings With(IReadOnlyList<CredentialGrant> accountGrants, Func<InstanceSettings, InstanceSettings>? edit = null) => d with
        {
            Accounts = [new AccountSettings("acct", "Shared", ["apiKey"], accountGrants)],
            Instances = [.. d.Instances.Select(i => { var x = i.Id is SpeechCatalog.OpenAiAsr or SpeechCatalog.GeminiAsr or "openai" ? i with { AccountBindings = bindings } : i; return edit is null ? x : edit(x); })],
        };
        using var supervisor = new Supervisor<HostSession>(() => throw new InvalidOperationException("never started"), SystemClock.Instance, TimeSpan.FromMinutes(1));
        Func<string, string, bool> saved = (_, _) => true;

        Assert.Null(PluginAsrProviders.Create(With([]), SpeechSlot.Asr, saved, supervisor)); // no grant
        var ready = With(grants);
        var whisper = PluginAsrProviders.Create(ready, SpeechSlot.Asr, saved, supervisor)!;
        Assert.Equal((SpeechCatalog.OpenAiAsr, "whisper-1", true), (whisper.InstanceId, whisper.Model, whisper.Timecodes));
        Assert.Null(PluginAsrProviders.Create(ready, SpeechSlot.Asr, (_, _) => false, supervisor)); // secrets not saved
        Assert.Null(PluginAsrProviders.Create(ready, SpeechSlot.Tts, saved, supervisor));

        // Changing the AI translation model (the openai instance's config) leaves the transcription model alone.
        var editedAi = With(grants, i => i.Id == "openai" ? i with { Config = new Dictionary<string, string>(i.Config) { ["model"] = "gpt-4.1" } } : i);
        Assert.Equal("whisper-1", PluginAsrProviders.Create(editedAi, SpeechSlot.Asr, saved, supervisor)!.Model);

        // Voice (text) and video (timecodes) are selected apart: text-only Gemini serves voice but never video (A03).
        var split = ready with { Speech = ready.Speech.With(SpeechSlot.Asr, new SpeechSelection(SpeechCatalog.GeminiAsr, "gemini-2.5-flash")) };
        Assert.Equal(("gemini-2.5-flash", false), (PluginAsrProviders.Create(split, SpeechSlot.Asr, saved, supervisor)!.Model, PluginAsrProviders.Create(split, SpeechSlot.Asr, saved, supervisor)!.Timecodes));
        Assert.Equal("whisper-1", PluginAsrProviders.Create(split, SpeechSlot.VideoAsr, saved, supervisor)!.Model);
        var badVideo = ready with { Speech = ready.Speech.With(SpeechSlot.VideoAsr, new SpeechSelection(SpeechCatalog.GeminiAsr, "gemini-2.5-flash")) };
        Assert.Null(PluginAsrProviders.Create(badVideo, SpeechSlot.VideoAsr, saved, supervisor));
    }
}
