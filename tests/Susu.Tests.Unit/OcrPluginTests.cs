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
/// F11.2 P-O01 Tencent OCR and P-O02 Simple LaTeX: the real shipped packages through the real AppContainer sandbox + QuickJS +
/// IPC + Broker/$http (+ the host tc3 signer for Tencent) and <see cref="PluginOcrProvider"/>/<see cref="OcrJob"/>, against a
/// LoopbackHttpServer standing in for the vendor. B01 (host-side Base64 and signature over the final bytes, no image bytes or
/// Base64 in what the plugin gets or returns), error classes, long text, B06/B08 handle and size rules, cancel cleanup. Needs
/// susu.exe published (`dotnet publish src/Susu.Host -c Release -r win-x64`); skips itself otherwise. Real vendor calls: not
/// executed (no account).
/// </summary>
public class OcrPluginTests
{
    private const string SecretId = "AKIDocrTestSecretId0123456";
    private const string SecretKey = "ocrTestSecretKey0123456789ab";
    private const string Uat = "simpletex-uat-0123456789abcdef";
    // A PNG signature plus distinctive filler: its Base64 is long enough to search for.
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 % 251))];

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
        string[] packages = [OcrCatalog.TencentOcr, OcrCatalog.SimpleLatex];
        if (packages.Any(p => !File.Exists(Path.Combine(output, "plugins", p, "main.js")))) return null;
        string dir = TestTemp.NewDir("susu-ocr-it");
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

    /// <summary>The staged host folder (susu.exe + both packages), or null when the host is not published.</summary>
    internal static string? StagedDir => staged.Value;

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

    internal sealed class Rig : IDisposable
    {
        public required FileLeases Leases { get; init; }
        public required Supervisor<HostSession> Supervisor { get; init; }
        public required PluginOcrProvider Provider { get; init; }
        public required WiredPackage Package { get; init; }
        public required string ConfigJson { get; init; }
        public required string Origin { get; init; }
        public HostSession? Session;
        public Broker Broker => Session!.Broker;

        public ScreenshotImage Image(byte[]? bytes = null, int width = 200, int height = 100)
        {
            var file = new LeasedFiles(Leases).Create("ocr", "image/png", "png");
            File.WriteAllBytes(file.FilePath, bytes ?? Png);
            return new ScreenshotImage(file, new PixelRect(0, 0, width, height), width, height, 96, 1);
        }

        public async Task<OcrOutcome> RecognizeAsync(ScreenshotImage image, string? lang = null, CancellationToken? token = null)
            => await Provider.RecognizeAsync(new OcrCall(image.File.LeaseId, image.File.Mime, image.File.Bytes, image.Width, image.Height, lang, $"ocr-{Guid.NewGuid():N}", TimeSpan.FromSeconds(30)), token ?? Ct);

        /// <summary>A raw call with a hand-built request: returns the Completed payload and its raw JSON (what the plugin returned).</summary>
        public async Task<(CompletedPayload Completed, string Raw)> RawAsync(string requestJson, IEnumerable<string> handles)
        {
            var session = Supervisor.Acquire()!;
            try
            {
                var (_, _, task) = session.Invoke(Package.PackageId, "ocr", requestJson, $"job-{Guid.NewGuid():N}", [Origin], secrets: Package.SecretNames,
                    configJson: ConfigJson, handles: handles, instanceId: Package.InstanceId, signer: Package.Signer);
                var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                return (envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!, envelope.Payload!.Value.GetRawText());
            }
            finally { Supervisor.Release(); }
        }

        /// <summary>B07: nothing of any call is left once the caller released what it holds.</summary>
        public async Task AssertNoFilesLeftAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((Leases.ActiveCount > 0 || (Session is not null && Broker.ActiveResponseFiles > 0)) && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
            Assert.Equal(0, Leases.ActiveCount);
        }

        public void Dispose() { Supervisor.Dispose(); Leases.Dispose(); }
    }

    /// <summary>
    /// The instance with its secrets on one account. For Tencent the account is the shared Tencent Cloud account (PLAN 1.3): it
    /// always carries the TMT grants, and the OCR grant for the OCR origin only when <paramref name="grantOcr"/>.
    /// </summary>
    internal static Rig? Build(string instanceId, LoopbackHttpServer server, Dictionary<string, string>? config = null, bool grantOcr = true, string? directory = null)
    {
        if (staged.Value is not { } dir) return null;
        var package = PluginTranslationProviders.WiredPackages.Single(p => p.InstanceId == instanceId);
        var ocr = OcrCatalog.Find(instanceId)!;
        var cfg = new Dictionary<string, string>(config ?? []) { ["baseUrl"] = server.Origin };
        var secrets = new FakeSecretStore();
        var bindings = new Dictionary<string, string>();
        var grants = new List<CredentialGrant>();
        foreach (var target in ocr.Credentials)
        {
            secrets.Set("account", target.Secret, target.Secret switch { "secretId" => SecretId, "secretKey" => SecretKey, _ => Uat });
            bindings[target.Secret] = "account";
        }
        if (grantOcr) grants.AddRange(ocr.RequiredGrants(cfg));
        if (instanceId == OcrCatalog.TencentOcr) grants.AddRange(TranslationPackages.Find("tencent-translate")!.RequiredGrants(new Dictionary<string, string>()));
        var account = new AccountSettings("account", "Shared account", [.. ocr.SecretNames], grants);
        var instance = new InstanceSettings(instanceId, package.PackageId, 1, cfg, bindings);
        var accounts = new AccountAuthorization(() => ([account], [instance]));
        var leases = new FileLeases(TestTemp.NewDir("susu-ocr-leases"));
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
            Leases = leases, Supervisor = supervisor, Provider = new PluginOcrProvider(package, instance, supervisor), Package = package,
            ConfigJson = JsonSerializer.Serialize(cfg), Origin = package.Origin(cfg),
        };
        return rig;
    }

    private static string? H(LoopbackHttpRequest req, string name)
        => req.Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static ProviderError Failure(OcrOutcome outcome) => Assert.IsType<OcrOutcome.Failure>(outcome).Error;

    private static string RequestFor(string handle, long bytes = 3008, string mime = "image/png", string? lang = null)
        => PluginOcrProvider.RequestJson(new OcrCall(handle, mime, bytes, 200, 100, lang, "a", TimeSpan.FromSeconds(30)));

    private static string TencentOk(params object[] detections)
        => JsonSerializer.Serialize(new { Response = new { TextDetections = detections, Language = "zh", RequestId = "req-1" } });

    private static object Detection(string text, int confidence = 99, int x = 20, int y = 10, int w = 100, int h = 20)
        => new { DetectedText = text, Confidence = confidence, ItemPolygon = new { X = x, Y = y, Width = w, Height = h }, Polygon = Array.Empty<object>() };

    private static string TencentError(string code)
        => JsonSerializer.Serialize(new { Response = new { Error = new { Code = code, Message = "vendor says no" }, RequestId = "req-1" } });

    // ---------------- P-O01 Tencent OCR ----------------

    [Fact] // B01: the host writes ImageBase64 and TC3-signs the final bytes; the plugin only ever holds the handle
    public async Task Tencent_host_encodes_the_image_and_signs_the_final_bytes()
    {
        LoopbackHttpRequest? captured = null;
        using var server = new LoopbackHttpServer(req => { captured = req; return LoopbackHttpResponse.Json(200, TencentOk(Detection("你好 world"), Detection("第二行", 87, 0, 50, 300, 60))); });
        using var rig = Build(OcrCatalog.TencentOcr, server);
        if (rig is null) return;
        string base64 = Convert.ToBase64String(Png);
        using var image = rig.Image();

        // What crosses IPC: the request is handle + metadata, the plugin's answer is blocks; neither holds the image.
        string request = RequestFor(image.File.LeaseId);
        Assert.DoesNotContain(base64[..40], request);
        var (completed, raw) = await rig.RawAsync(request, [image.File.LeaseId]);
        Assert.True(completed.Ok, completed.Error?.Detail);
        Assert.DoesNotContain(base64[..40], raw);
        Assert.DoesNotContain(base64[^40..], raw);

        var recognized = Assert.IsType<OcrOutcome.Recognized>(await rig.RecognizeAsync(image));
        Assert.Equal(["你好 world", "第二行"], recognized.Blocks.Select(b => b.Text).ToArray());
        Assert.Equal([0.1, 0.1, 0.5, 0.2], recognized.Blocks[0].Box!);
        Assert.Equal([0.0, 0.5, 1.0, 0.5], recognized.Blocks[1].Box!); // 300 px wide on a 200 px image: clamped into the image
        Assert.Equal((0.99, "text"), (recognized.Blocks[0].Confidence!.Value, recognized.Blocks[0].Kind));

        Assert.NotNull(captured);
        Assert.Equal(("POST", "/"), (captured.Method, captured.Path));
        Assert.Equal(("GeneralBasicOCR", "2018-11-19", "ap-guangzhou"), (H(captured, "X-TC-Action"), H(captured, "X-TC-Version"), H(captured, "X-TC-Region")));
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal(base64, body.RootElement.GetProperty("ImageBase64").GetString()); // the real bytes, host-filled
        Assert.Equal(Png, Convert.FromBase64String(body.RootElement.GetProperty("ImageBase64").GetString()!));
        Assert.Equal("auto", body.RootElement.GetProperty("LanguageType").GetString());
        string wire = Encoding.UTF8.GetString(captured.Body);
        Assert.DoesNotContain(SecretKey, wire);

        // The Authorization header is what the tc3 signer computes over the exact bytes on the wire (service "ocr"), recomputed here.
        long timestamp = long.Parse(H(captured, "X-TC-Timestamp")!, System.Globalization.CultureInfo.InvariantCulture);
        var signedHeaders = new[] { "Content-Type", "X-TC-Action", "X-TC-Version", "X-TC-Region" }.Select(n => new KeyValuePair<string, string>(n, H(captured, n)!)).ToList();
        var expected = TencentTc3Signer.Sign(new SignableRequest("POST", new Uri(server.Origin + "/"), signedHeaders, captured.Body), "ocr", SecretId, SecretKey,
            DateTimeOffset.FromUnixTimeSeconds(timestamp));
        Assert.Equal(expected.Headers.Single(h => h.Key == "Authorization").Value, H(captured, "Authorization"));
        Assert.Contains("/ocr/tc3_request", H(captured, "Authorization"));

        image.Dispose();
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // PLAN 1.3/4.5.4: the shared Tencent account holding only the TMT grant does not reach OCR; nothing is sent
    public async Task Tencent_without_the_ocr_grant_is_refused_before_sending()
    {
        int hits = 0;
        using var server = new LoopbackHttpServer(_ => { Interlocked.Increment(ref hits); return LoopbackHttpResponse.Json(200, TencentOk(Detection("x"))); });
        using var rig = Build(OcrCatalog.TencentOcr, server, grantOcr: false);
        if (rig is null) return;
        using var image = rig.Image();
        Assert.Equal(ErrorKind.Auth, Failure(await rig.RecognizeAsync(image)).Kind);
        Assert.Equal(0, hits);
    }

    [Theory] // vendor Response.Error codes map to host error classes; "no text in the image" is NoText, never an empty success
    [InlineData("AuthFailure.SignatureFailure", ErrorKind.Auth)]
    [InlineData("AuthFailure.SecretIdNotFound", ErrorKind.Auth)]
    [InlineData("FailedOperation.UnOpenError", ErrorKind.Auth)]
    [InlineData("ResourceUnavailable.InArrears", ErrorKind.Quota)]
    [InlineData("ResourceUnavailable.ResourcePackageRunOut", ErrorKind.Quota)]
    [InlineData("ResourcesSoldOut.ChargeStatusException", ErrorKind.Quota)]
    [InlineData("RequestLimitExceeded", ErrorKind.RateLimited)]
    [InlineData("LimitExceeded.TooLargeFileError", ErrorKind.BadResponse)]
    [InlineData("FailedOperation.LanguageNotSupport", ErrorKind.UnsupportedLanguage)]
    [InlineData("FailedOperation.EngineRecognizeTimeout", ErrorKind.Timeout)]
    [InlineData("InternalError", ErrorKind.Network)]
    [InlineData("FailedOperation.ImageDecodeFailed", ErrorKind.BadResponse)]
    [InlineData("FailedOperation.OcrFailed", ErrorKind.BadResponse)]
    [InlineData("FailedOperation.ImageNoText", null)]
    public async Task Tencent_vendor_errors_are_classified(string code, ErrorKind? expected)
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, TencentError(code)));
        using var rig = Build(OcrCatalog.TencentOcr, server);
        if (rig is null) return;
        using var image = rig.Image();
        var outcome = await rig.RecognizeAsync(image);
        if (expected is null) { Assert.IsType<OcrOutcome.NoText>(outcome); return; }
        var error = Failure(outcome);
        Assert.Equal(expected, error.Kind);
        Assert.Contains(code, error.Detail);
    }

    [Fact] // HTTP status classes, Retry-After on 429, and a non-JSON 200 is bad_response
    public async Task Tencent_http_errors_are_classified()
    {
        var responses = new Queue<LoopbackHttpResponse>([
            new(429, "{}"u8.ToArray(), new Dictionary<string, string> { ["Retry-After"] = "5" }),
            LoopbackHttpResponse.Json(401, "{}"),
            LoopbackHttpResponse.Json(503, "{}"),
            LoopbackHttpResponse.Text(200, "<html>gateway</html>"),
            LoopbackHttpResponse.Json(200, """{"Response":{"RequestId":"r"}}"""),
        ]);
        using var server = new LoopbackHttpServer(_ => responses.Dequeue());
        using var rig = Build(OcrCatalog.TencentOcr, server);
        if (rig is null) return;
        using var image = rig.Image();
        var limited = Failure(await rig.RecognizeAsync(image));
        Assert.Equal((ErrorKind.RateLimited, TimeSpan.FromSeconds(5)), (limited.Kind, limited.RetryAfter));
        Assert.Equal(ErrorKind.Auth, Failure(await rig.RecognizeAsync(image)).Kind);
        Assert.Equal(ErrorKind.Network, Failure(await rig.RecognizeAsync(image)).Kind);
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.RecognizeAsync(image)).Kind);
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.RecognizeAsync(image)).Kind);
    }

    [Fact] // a canonical language hint maps to LanguageType; the configured language applies otherwise; unknown ones send nothing
    public async Task Tencent_language_hint_and_config()
    {
        var bodies = new List<string>();
        using var server = new LoopbackHttpServer(req => { lock (bodies) bodies.Add(Encoding.UTF8.GetString(req.Body)); return LoopbackHttpResponse.Json(200, TencentOk(Detection("x"))); });
        using var rig = Build(OcrCatalog.TencentOcr, server, new Dictionary<string, string> { ["lang"] = "kor" });
        if (rig is null) return;
        using var image = rig.Image();
        Assert.IsType<OcrOutcome.Recognized>(await rig.RecognizeAsync(image, "ja"));
        Assert.IsType<OcrOutcome.Recognized>(await rig.RecognizeAsync(image));
        Assert.Equal(ErrorKind.UnsupportedLanguage, Failure(await rig.RecognizeAsync(image, "xx")).Kind);
        Assert.Equal(2, bodies.Count);
        Assert.Equal("jap", JsonDocument.Parse(bodies[0]).RootElement.GetProperty("LanguageType").GetString());
        Assert.Equal("kor", JsonDocument.Parse(bodies[1]).RootElement.GetProperty("LanguageType").GetString());
    }

    [Fact] // OCR02 long text: every detection survives in order and the job joins them for the translation pipeline
    public async Task Tencent_long_text_is_kept_whole()
    {
        var detections = Enumerable.Range(0, 400).Select(i => Detection($"line {i:D3} " + new string('x', 40), 90, 0, i % 90, 150, 1)).ToArray();
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, TencentOk(detections)));
        using var rig = Build(OcrCatalog.TencentOcr, server);
        if (rig is null) return;
        string? translated = null;
        var job = new OcrJob(() => rig.Provider, t => { translated = t; return Task.CompletedTask; }, () => true);
        var final = await job.RecognizeAsync(rig.Image());
        Assert.Equal(OcrPhase.Recognized, final.Phase);
        Assert.Equal(400, final.Blocks!.Count);
        var lines = translated!.Split('\n');
        Assert.Equal(400, lines.Length);
        Assert.StartsWith("line 000 ", lines[0]);
        Assert.StartsWith("line 399 ", lines[^1]);
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // the vendor's lower size limit applies before sending (PLAN 4.5.1)
    public async Task Tencent_oversized_image_is_refused_without_a_request()
    {
        int hits = 0;
        using var server = new LoopbackHttpServer(_ => { Interlocked.Increment(ref hits); return LoopbackHttpResponse.Json(200, TencentOk(Detection("x"))); });
        using var rig = Build(OcrCatalog.TencentOcr, server);
        if (rig is null) return;
        using var image = rig.Image(new byte[8 * 1024 * 1024]);
        var error = Failure(await rig.RecognizeAsync(image));
        Assert.Equal(ErrorKind.BadResponse, error.Kind);
        Assert.Contains("too large", error.Detail);
        Assert.Equal(0, hits);
    }

    [Fact] // B06: forged, foreign and revoked handles are refused by the host; self-reported mime/bytes change nothing
    public async Task Tencent_handles_are_checked_by_the_host()
    {
        var bodies = new List<byte[]>();
        using var server = new LoopbackHttpServer(req => { lock (bodies) bodies.Add(req.Body); return LoopbackHttpResponse.Json(200, TencentOk(Detection("x"))); });
        using var rig = Build(OcrCatalog.TencentOcr, server);
        if (rig is null) return;
        using var granted = rig.Image();
        using var foreign = rig.Image([1, 2, 3, 4]);

        // Forged id: never issued by any lease.
        var (forged, _) = await rig.RawAsync(RequestFor("f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0"), ["f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0"]);
        Assert.False(forged.Ok);
        // Foreign: a live lease of this host, but not granted to this call (the call's grant names another handle).
        var (other, _) = await rig.RawAsync(RequestFor(foreign.File.LeaseId), [granted.File.LeaseId]);
        Assert.False(other.Ok);
        Assert.Contains("not granted", other.Error!.Detail);
        Assert.Empty(bodies);

        // Fake metadata: the request claims 1 byte of text; the broker still sends the real leased bytes.
        var (lied, _) = await rig.RawAsync(RequestFor(granted.File.LeaseId, bytes: 1, mime: "text/plain"), [granted.File.LeaseId]);
        Assert.True(lied.Ok, lied.Error?.Detail);
        Assert.Equal(Png, Convert.FromBase64String(JsonDocument.Parse(Assert.Single(bodies)).RootElement.GetProperty("ImageBase64").GetString()!));

        // Revoked: the lease was released (job cancelled) before the call.
        var revoked = rig.Image();
        string id = revoked.File.LeaseId;
        revoked.Dispose();
        var (gone, _) = await rig.RawAsync(RequestFor(id), [id]);
        Assert.False(gone.Ok);
        Assert.Single(bodies);
        Assert.Equal(2, rig.Leases.ActiveCount); // granted + foreign are untouched by the refused calls
    }

    [Fact] // B08: the host's own 32 MiB input cap holds even when the metadata claims a small file; nothing is sent
    public async Task Host_binary_cap_holds_whatever_the_metadata_says()
    {
        int hits = 0;
        using var server = new LoopbackHttpServer(_ => { Interlocked.Increment(ref hits); return LoopbackHttpResponse.Json(200, TencentOk(Detection("x"))); });
        using var rig = Build(OcrCatalog.TencentOcr, server);
        if (rig is null) return;
        using var huge = rig.Image(new byte[ProtocolLimits.MaxBinaryBytes + 1]);
        var (completed, _) = await rig.RawAsync(RequestFor(huge.File.LeaseId, bytes: 100), [huge.File.LeaseId]);
        Assert.False(completed.Ok);
        Assert.Contains("size limit", completed.Error!.Detail);
        Assert.Equal(0, hits);
        huge.Dispose();
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // cancel during the upload/recognition: the job cancels the plugin call, the image lease goes, the host stays usable
    public async Task Cancel_during_recognition_releases_the_image()
    {
        var gate = new ManualResetEventSlim(false);
        int calls = 0;
        using var server = new LoopbackHttpServer(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) gate.Wait(TimeSpan.FromSeconds(10));
            return LoopbackHttpResponse.Json(200, TencentOk(Detection("after")));
        });
        using var rig = Build(OcrCatalog.TencentOcr, server);
        if (rig is null) return;
        int translated = 0;
        var job = new OcrJob(() => rig.Provider, _ => { Interlocked.Increment(ref translated); return Task.CompletedTask; }, () => true);
        var running = job.RecognizeAsync(rig.Image());
        for (var deadline = DateTime.UtcNow.AddSeconds(15); Volatile.Read(ref calls) < 1 && DateTime.UtcNow < deadline;) await Task.Delay(10, Ct);
        job.Cancel();
        Assert.Equal(OcrPhase.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Phase);
        gate.Set();
        await rig.AssertNoFilesLeftAsync();
        Assert.Equal(0, translated);

        Assert.Equal(OcrPhase.Recognized, (await job.RecognizeAsync(rig.Image())).Phase);
        Assert.Equal(1, translated);
        await rig.AssertNoFilesLeftAsync();
    }

    // ---------------- P-O02 Simple LaTeX ----------------

    [Fact] // multipart upload built by the host from the handle, UAT in the token header; a formula comes back as LaTeX
    public async Task SimpleLatex_uploads_the_image_as_multipart_and_returns_latex()
    {
        LoopbackHttpRequest? captured = null;
        const string latex = @"\int_0^1 x^2\,dx=\frac{1}{3}";
        using var server = new LoopbackHttpServer(req =>
        {
            captured = req;
            return LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(new { status = true, res = new { type = "formula", info = latex, conf = 0.93 }, request_id = "r1" }));
        });
        using var rig = Build(OcrCatalog.SimpleLatex, server);
        if (rig is null) return;
        using var image = rig.Image();
        var (completed, raw) = await rig.RawAsync(RequestFor(image.File.LeaseId), [image.File.LeaseId]);
        Assert.True(completed.Ok, completed.Error?.Detail);
        string base64 = Convert.ToBase64String(Png);
        Assert.DoesNotContain(base64[..40], raw);

        var recognized = Assert.IsType<OcrOutcome.Recognized>(await rig.RecognizeAsync(image));
        var block = Assert.Single(recognized.Blocks);
        Assert.Equal((latex, "formula", 0.93), (block.Text, block.Kind, block.Confidence!.Value));

        Assert.Equal(("POST", "/api/simpletex_ocr"), (captured!.Method, captured.Path));
        Assert.Equal(Uat, H(captured, "token"));
        Assert.StartsWith("multipart/form-data", H(captured, "Content-Type"));
        string wire = Encoding.Latin1.GetString(captured.Body);
        Assert.Matches("name=\"?file\"?[;\r]", wire);
        Assert.Matches("name=\"?rec_mode\"?[;\r]", wire);
        Assert.Contains(Encoding.Latin1.GetString(Png), wire); // the raw image bytes, as a file part
        Assert.DoesNotContain(base64[..40], wire);

        // The job keeps the formula verbatim for copying.
        var job = new OcrJob(() => rig.Provider, _ => Task.CompletedTask, () => false);
        Assert.Equal(latex, (await job.RecognizeAsync(rig.Image())).Text);
        image.Dispose();
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // latex_ocr returns res.latex; a document result from simpletex_ocr is a text block (Markdown with inline $...$)
    public async Task SimpleLatex_models_map_to_blocks()
    {
        using var server = new LoopbackHttpServer(req => LoopbackHttpResponse.Json(200, req.Path == "/api/latex_ocr"
            ? """{"status":true,"res":{"latex":"E=mc^2","conf":0.99}}"""
            : """{"status":true,"res":{"type":"doc","info":{"markdown":"Energy: $E=mc^2$\n\nSecond paragraph"},"conf":0.8}}"""));
        using (var formula = Build(OcrCatalog.SimpleLatex, server, new Dictionary<string, string> { ["mode"] = "latex_ocr" }))
        {
            if (formula is null) return;
            using var image = formula.Image();
            var block = Assert.Single(Assert.IsType<OcrOutcome.Recognized>(await formula.RecognizeAsync(image)).Blocks);
            Assert.Equal(("E=mc^2", "formula"), (block.Text, block.Kind));
        }
        using var doc = Build(OcrCatalog.SimpleLatex, server);
        using var docImage = doc!.Image();
        var text = Assert.Single(Assert.IsType<OcrOutcome.Recognized>(await doc.RecognizeAsync(docImage)).Blocks);
        Assert.Equal(("Energy: $E=mc^2$\n\nSecond paragraph", "text"), (text.Text, text.Kind));
    }

    [Theory] // SimpleTex failures: HTTP status or status=false with err_info, classified; an empty result is NoText
    [InlineData(401, """{"status":false,"err_info":{"err_type":"req_unauthorized"}}""", ErrorKind.Auth)]
    [InlineData(200, """{"status":false,"err_info":{"err_type":"req_unauthorized"}}""", ErrorKind.Auth)]
    [InlineData(200, """{"status":false,"err_info":{"err_type":"resource_not_enough"}}""", ErrorKind.Quota)]
    [InlineData(200, """{"status":false,"err_info":{"err_type":"exceed_max_qps"}}""", ErrorKind.RateLimited)]
    [InlineData(500, """{"status":false,"err_info":{"err_type":"server_inner_error"}}""", ErrorKind.Network)]
    [InlineData(200, """{"status":false,"err_info":{"err_type":"image_missing"}}""", ErrorKind.BadResponse)]
    [InlineData(200, """{"status":true}""", ErrorKind.BadResponse)]
    [InlineData(200, """{"status":true,"res":{"type":"doc","info":"   "}}""", null)]
    public async Task SimpleLatex_errors_are_classified(int status, string body, ErrorKind? expected)
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(status, body));
        using var rig = Build(OcrCatalog.SimpleLatex, server);
        if (rig is null) return;
        using var image = rig.Image();
        var outcome = await rig.RecognizeAsync(image);
        if (expected is null) Assert.IsType<OcrOutcome.NoText>(outcome);
        else Assert.Equal(expected, Failure(outcome).Kind);
    }

    [Fact] // 429 keeps Retry-After; an image over the vendor limit is refused before sending
    public async Task SimpleLatex_rate_limit_and_size_limit()
    {
        int hits = 0;
        using var server = new LoopbackHttpServer(_ => { Interlocked.Increment(ref hits); return new LoopbackHttpResponse(429, "{}"u8.ToArray(), new Dictionary<string, string> { ["Retry-After"] = "9" }); });
        using var rig = Build(OcrCatalog.SimpleLatex, server);
        if (rig is null) return;
        using var image = rig.Image();
        var limited = Failure(await rig.RecognizeAsync(image));
        Assert.Equal((ErrorKind.RateLimited, TimeSpan.FromSeconds(9)), (limited.Kind, limited.RetryAfter));
        using var big = rig.Image(new byte[10 * 1024 * 1024 + 1]);
        Assert.Equal(ErrorKind.BadResponse, Failure(await rig.RecognizeAsync(big)).Kind);
        Assert.Equal(1, hits);
    }
}
