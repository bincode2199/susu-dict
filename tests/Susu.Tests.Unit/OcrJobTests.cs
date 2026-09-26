using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Plugins;
using Susu.Storage;
using Susu.Testing;
using Susu.Ui;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F11.2 OCR job, result contract and selection with fakes (no plugin host): lease ownership on every path (OCR01/ARCHITECTURE
/// 8.4), recognized text into the shared translation pipeline (T02/OCR02), classified failures instead of empty success, the
/// optional block fields, the OCR service selection and the shared Tencent account's separate OCR grant.
/// </summary>
public class OcrJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeOcr(Func<OcrCall, CancellationToken, Task<OcrOutcome>> run, string id = "tencent-ocr") : IOcrProvider
    {
        public List<OcrCall> Calls { get; } = [];
        public string InstanceId => id;
        public Task<OcrOutcome> RecognizeAsync(OcrCall call, CancellationToken cancellationToken) { Calls.Add(call); return run(call, cancellationToken); }
    }

    private sealed class Images : IDisposable
    {
        public readonly FileLeases Leases = new(TestTemp.NewDir("susu-ocrjob-leases"));
        public ScreenshotImage New(int width = 200, int height = 100)
        {
            var file = new LeasedFiles(Leases).Create("ocr", "image/png", "png");
            File.WriteAllBytes(file.FilePath, [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3]);
            return new ScreenshotImage(file, new PixelRect(10, 20, width, height), width, height, 144, 1);
        }
        public void Dispose() => Leases.Dispose();
    }

    private static OcrOutcome Recognized(params OcrBlock[] blocks) => new OcrOutcome.Recognized(blocks);

    [Fact] // OCR02: recognized text enters the translation pipeline once; the image lease is gone afterwards
    public async Task Recognized_text_is_translated_and_the_image_is_released()
    {
        using var images = new Images();
        var image = images.New();
        bool leaseAliveDuringCall = false;
        var provider = new FakeOcr((call, _) =>
        {
            leaseAliveDuringCall = images.Leases.ActiveCount == 1 && File.Exists(image.File.FilePath);
            return Task.FromResult(Recognized(new OcrBlock("Hello", [0.1, 0.1, 0.5, 0.2], "text", 0.98), new OcrBlock("world  ")));
        });
        var translated = new List<string>();
        var job = new OcrJob(() => provider, t => { translated.Add(t); return Task.CompletedTask; }, () => true);
        var states = new List<OcrPhase>();
        job.StateChanged += s => states.Add(s.Phase);

        var final = await job.RecognizeAsync(image);

        Assert.Equal(OcrPhase.Recognized, final.Phase);
        Assert.True(final.Translated);
        Assert.Equal("Hello\nworld", final.Text);
        Assert.Equal(2, final.Blocks!.Count);
        Assert.Equal(["Hello\nworld"], translated);
        Assert.Equal([OcrPhase.Recognizing, OcrPhase.Recognized], states);
        Assert.True(leaseAliveDuringCall);
        Assert.True(image.File.Released);
        Assert.Equal(0, images.Leases.ActiveCount);

        // The call names the lease, never a path, with the host's own metadata and the OCR default timeout (PLAN 4.5: 60 s).
        var call = Assert.Single(provider.Calls);
        Assert.Equal(image.File.LeaseId, call.Image);
        Assert.Equal(("image/png", 7L, 200, 100), (call.Mime, call.Bytes, call.Width, call.Height));
        Assert.Equal(TimeSpan.FromSeconds(60), call.Timeout);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, call.Image);
    }

    [Fact] // auto-translate off: the text stays in the source card until the user asks
    public async Task Auto_translate_off_only_fills_the_source()
    {
        using var images = new Images();
        var translated = new List<string>();
        var job = new OcrJob(() => new FakeOcr((_, _) => Task.FromResult(Recognized(new OcrBlock("abc")))), t => { translated.Add(t); return Task.CompletedTask; }, () => false);
        var final = await job.RecognizeAsync(images.New());
        Assert.Equal(OcrPhase.Recognized, final.Phase);
        Assert.False(final.Translated);
        Assert.Empty(translated);
        await job.TranslateAsync("abc edited");
        Assert.Equal(["abc edited"], translated);
    }

    [Fact] // formulas are kept as LaTeX source, verbatim, one block per line
    public async Task Formula_blocks_keep_their_latex_source()
    {
        using var images = new Images();
        const string latex = @"\frac{a}{b}+\sqrt{x^{2}}";
        var job = new OcrJob(() => new FakeOcr((_, _) => Task.FromResult(Recognized(new OcrBlock("Let", Kind: "text"), new OcrBlock($"  {latex} ", Kind: "formula")))), _ => Task.CompletedTask, () => false);
        var final = await job.RecognizeAsync(images.New());
        Assert.Equal($"Let\n{latex}", final.Text);
        Assert.Equal("formula", final.Blocks![1].Kind);
    }

    [Theory] // recognition that finds nothing is a classified outcome, never an empty success, and nothing is translated
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_text_is_reported_not_translated(bool blankBlocks)
    {
        using var images = new Images();
        var translated = 0;
        OcrOutcome outcome = blankBlocks ? Recognized(new OcrBlock("  "), new OcrBlock("\n")) : new OcrOutcome.NoText();
        var job = new OcrJob(() => new FakeOcr((_, _) => Task.FromResult(outcome)), _ => { translated++; return Task.CompletedTask; }, () => true);
        var final = await job.RecognizeAsync(images.New());
        Assert.Equal(OcrPhase.NoText, final.Phase);
        Assert.Null(final.Text);
        Assert.Equal(0, translated);
        Assert.Equal(0, images.Leases.ActiveCount);
    }

    [Fact] // a provider failure keeps its class and Retry-After; the image goes
    public async Task Failure_is_classified_and_the_image_is_released()
    {
        using var images = new Images();
        var error = new ProviderError(ErrorKind.RateLimited, "RequestLimitExceeded", TimeSpan.FromSeconds(3));
        var job = new OcrJob(() => new FakeOcr((_, _) => Task.FromResult<OcrOutcome>(new OcrOutcome.Failure(error))), _ => Task.CompletedTask, () => true);
        var final = await job.RecognizeAsync(images.New());
        Assert.Equal(OcrPhase.Failed, final.Phase);
        Assert.Equal(error, final.Error);
        Assert.Equal(0, images.Leases.ActiveCount);
    }

    [Fact] // a provider that throws is a failure too, and still releases the image
    public async Task A_throwing_provider_fails_and_releases()
    {
        using var images = new Images();
        var job = new OcrJob(() => new FakeOcr((_, _) => throw new InvalidOperationException("boom")), _ => Task.CompletedTask, () => true);
        var final = await job.RecognizeAsync(images.New());
        Assert.Equal(OcrPhase.Failed, final.Phase);
        Assert.Equal(ErrorKind.Unavailable, final.Error!.Kind);
        Assert.Equal(0, images.Leases.ActiveCount);
    }

    [Fact] // PLAN 1.2: no usable OCR service - nothing is called, the image is released at once
    public async Task No_service_is_unavailable_and_releases()
    {
        using var images = new Images();
        var job = new OcrJob(() => null, _ => Task.CompletedTask, () => true);
        var final = await job.RecognizeAsync(images.New());
        Assert.Equal(OcrPhase.Unavailable, final.Phase);
        Assert.Equal(0, images.Leases.ActiveCount);
    }

    [Fact] // cancel (Esc/close) stops the recognition, releases the lease and translates nothing
    public async Task Cancel_releases_the_lease_and_translates_nothing()
    {
        using var images = new Images();
        var started = new TaskCompletionSource();
        bool sawCancel = false;
        var provider = new FakeOcr(async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { sawCancel = true; throw; }
            return new OcrOutcome.NoText();
        });
        int translated = 0;
        var job = new OcrJob(() => provider, _ => { translated++; return Task.CompletedTask; }, () => true);
        var image = images.New();
        var running = job.RecognizeAsync(image);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(1, images.Leases.ActiveCount);
        job.Cancel();
        var final = await running.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(OcrPhase.Cancelled, final.Phase);
        Assert.Equal(OcrPhase.Cancelled, job.State.Phase);
        Assert.True(sawCancel);
        Assert.Equal(0, translated);
        Assert.True(image.File.Released);
        Assert.Equal(0, images.Leases.ActiveCount);
    }

    [Fact] // a new capture supersedes the running job: the old one is cancelled and never published or translated
    public async Task A_new_capture_supersedes_the_running_job()
    {
        using var images = new Images();
        var firstStarted = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        int call = 0;
        var provider = new FakeOcr(async (_, token) =>
        {
            if (Interlocked.Increment(ref call) == 1)
            {
                firstStarted.SetResult();
                await release.Task; // ignores the token: a late answer from the superseded call
                return Recognized(new OcrBlock("old"));
            }
            return Recognized(new OcrBlock("new"));
        });
        var translated = new List<string>();
        var job = new OcrJob(() => provider, t => { translated.Add(t); return Task.CompletedTask; }, () => true);
        var first = job.RecognizeAsync(images.New());
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var second = await job.RecognizeAsync(images.New());
        release.SetResult();
        var firstFinal = await first.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(OcrPhase.Recognized, second.Phase);
        Assert.Equal(OcrPhase.Cancelled, firstFinal.Phase);
        Assert.Equal(["new"], translated);
        Assert.Equal("new", job.State.Text);
        Assert.Equal(0, images.Leases.ActiveCount);
    }

    // ---------- result contract ----------

    [Theory]
    [InlineData("""{"blocks":[{"text":"a","box":[0.1,0.2,0.3,0.4],"kind":"text","confidence":0.5}]}""", null)]
    [InlineData("""{"blocks":[{"text":"a","box":[0.8,0,0.3,0.1]}]}""", "box outside")]
    [InlineData("""{"blocks":[{"text":"a","box":[0.1,0.2,0.3]}]}""", "box is not")]
    [InlineData("""{"blocks":[{"text":"a","box":[-0.1,0.2,0.3,0.1]}]}""", "box is not")]
    [InlineData("""{"blocks":[{"text":"a","kind":"table"}]}""", "unknown block kind")]
    [InlineData("""{"blocks":[{"text":"a","confidence":1.5}]}""", "confidence")]
    public void Ocr_results_are_validated(string json, string? problem)
    {
        var error = PluginResultValidation.ValidateOcr(JsonSerializer.Deserialize(json, ContractsJson.Default.OcrResult));
        if (problem is null) Assert.Null(error);
        else { Assert.Equal(ErrorKind.BadResponse, error!.Kind); Assert.Contains(problem, error.Detail); }
    }

    [Fact] // bounded: too many blocks or too much text is bad_response, not a partial success
    public void Ocr_result_size_is_bounded()
    {
        var many = new OcrResult([.. Enumerable.Range(0, PluginResultValidation.MaxOcrBlocks + 1).Select(_ => new OcrBlock("x"))]);
        Assert.Contains("too many blocks", PluginResultValidation.ValidateOcr(many)!.Detail);
        var huge = new OcrResult([new OcrBlock(new string('x', PluginResultValidation.MaxOcrChars + 1))]);
        Assert.Contains("too long", PluginResultValidation.ValidateOcr(huge)!.Detail);
        Assert.NotNull(PluginResultValidation.ValidateOcr(null));
    }

    private static IpcEnvelope Completed(bool ok, string? result = null, PluginErrorInfo? error = null)
        => new(ProtocolVersions.Ipc, IpcMessageType.Completed, "r1", "j1", PluginId: "p",
            Payload: JsonSerializer.SerializeToElement(new CompletedPayload(1, ok, result is null ? null : JsonDocument.Parse(result).RootElement, error), ContractsJson.Default.CompletedPayload));

    [Fact] // the provider's mapping of a Completed payload: plugin errors keep their class, unknown fields and blank results are refused
    public void Completed_payloads_map_to_outcomes()
    {
        Assert.IsType<OcrOutcome.NoText>(PluginOcrProvider.Interpret(Completed(true, """{"blocks":[]}""")));
        Assert.IsType<OcrOutcome.NoText>(PluginOcrProvider.Interpret(Completed(true, """{"blocks":[{"text":" "}]}""")));
        var ok = Assert.IsType<OcrOutcome.Recognized>(PluginOcrProvider.Interpret(Completed(true, """{"blocks":[{"text":" "},{"text":"x","kind":"formula"}]}""")));
        Assert.Equal("x", Assert.Single(ok.Blocks).Text);
        Assert.Equal(ErrorKind.BadResponse, Assert.IsType<OcrOutcome.Failure>(PluginOcrProvider.Interpret(Completed(true, """{"blocks":[{"text":"x","raw":"image-bytes"}]}"""))).Error.Kind);
        Assert.Equal(ErrorKind.BadResponse, Assert.IsType<OcrOutcome.Failure>(PluginOcrProvider.Interpret(Completed(true, """{"text":"x"}"""))).Error.Kind);
        var limited = Assert.IsType<OcrOutcome.Failure>(PluginOcrProvider.Interpret(Completed(false, error: new PluginErrorInfo("rate_limited", "slow down", "7")))).Error;
        Assert.Equal((ErrorKind.RateLimited, TimeSpan.FromSeconds(7)), (limited.Kind, limited.RetryAfter));
        Assert.Equal(ErrorKind.BadResponse, Assert.IsType<OcrOutcome.Failure>(PluginOcrProvider.Interpret(Completed(false, error: new PluginErrorInfo("weird", null, null)))).Error.Kind);
    }

    [Fact] // B01: the request the plugin gets is the handle and host metadata; no bytes, no path
    public void The_plugin_request_is_handle_and_metadata_only()
    {
        string json = PluginOcrProvider.RequestJson(new OcrCall("lease-123", "image/png", 4096, 640, 480, "ja", "a1", TimeSpan.FromSeconds(60)));
        using var doc = JsonDocument.Parse(json);
        var image = doc.RootElement.GetProperty("image");
        Assert.Equal(["id", "mime", "bytes", "width", "height"], image.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("lease-123", image.GetProperty("id").GetString());
        Assert.Equal("ja", doc.RootElement.GetProperty("lang").GetString());
        Assert.Equal(2, doc.RootElement.EnumerateObject().Count());
    }

    // ---------- selection and credentials ----------

    [Fact] // SetOcr default service first, then other enabled OCR services; disabled ones are never candidates
    public void Candidates_put_the_selected_service_first_and_skip_disabled_ones()
    {
        var d = BuiltInCatalog.Defaults();
        Assert.Equal(OcrCatalog.TencentOcr, d.Ocr.Service);
        Assert.Empty(OcrCatalog.Candidates(d)); // OCR services start disabled
        AppSettings Enable(AppSettings s, params string[] ids) => s with { Services = [.. s.Services.Select(x => x.Capability == Capability.Ocr ? x with { Enabled = ids.Contains(x.Instance) } : x)] };
        Assert.Equal([OcrCatalog.TencentOcr, OcrCatalog.SimpleLatex], OcrCatalog.Candidates(Enable(d, OcrCatalog.TencentOcr, OcrCatalog.SimpleLatex)));
        Assert.Equal([OcrCatalog.SimpleLatex, OcrCatalog.TencentOcr], OcrCatalog.Candidates(Enable(d, OcrCatalog.TencentOcr, OcrCatalog.SimpleLatex) with { Ocr = new OcrSettings(OcrCatalog.SimpleLatex) }));
        Assert.Equal([OcrCatalog.SimpleLatex], OcrCatalog.Candidates(Enable(d, OcrCatalog.SimpleLatex)));
    }

    [Fact] // settings.yaml keeps the OCR selection; an unknown id is a located issue
    public void Ocr_selection_round_trips_through_settings_yaml()
    {
        var s = BuiltInCatalog.Defaults() with { Ocr = new OcrSettings(OcrCatalog.SimpleLatex) };
        string text = SettingsYaml.Write(s);
        Assert.Contains("ocr:\n  service: \"simple-latex\"", text.Replace("\r\n", "\n"));
        var (back, issues) = SettingsYaml.Read(text);
        Assert.Empty(issues);
        Assert.Equal(OcrCatalog.SimpleLatex, back!.Ocr.Service);
        Assert.True(back.ContentEquals(s));

        var (bad, badIssues) = SettingsYaml.Read(text.Replace("\"simple-latex\"\n", "\"evil-ocr\"\n").Replace("\"simple-latex\"\r\n", "\"evil-ocr\"\r\n"));
        Assert.Contains(badIssues, i => i.Path == "ocr.service" && i.Code == "range" && i.Line > 0);
        Assert.Null(bad); // an invalid file is rejected as a whole, like every other settings error
        // A file written before F11.2 has no ocr section: the default service applies.
        var (old, oldIssues) = SettingsYaml.Read(SettingsYaml.Write(BuiltInCatalog.Defaults()).Split("\nocr:")[0]);
        Assert.Empty(oldIssues);
        Assert.Equal(OcrCatalog.TencentOcr, old!.Ocr.Service);
    }

    [Fact] // PLAN 1.3/4.5.4: the shared Tencent Cloud account reaches OCR only through its own grant for the OCR origin
    public void Tencent_ocr_needs_its_own_grant_on_the_shared_account()
    {
        var package = Assert.IsType<OcrPackage>(CredentialPackages.Find(OcrCatalog.TencentOcr));
        var grants = package.RequiredGrants(new Dictionary<string, string>());
        Assert.All(grants, g => Assert.Equal(("https://ocr.tencentcloudapi.com:443", "signer:tencent-tc3", "unsigned:app.susu.tencent-ocr"), (g.Origin, g.Use, g.Signer)));
        Assert.Equal(["secretId", "secretKey"], grants.Select(g => g.Secret).ToArray());
        var latex = Assert.IsType<OcrPackage>(CredentialPackages.Find(OcrCatalog.SimpleLatex));
        Assert.Equal(("apiKey", "header:token", "https://server.simpletex.cn:443"), (latex.RequiredGrants(new Dictionary<string, string>()).Single().Secret, latex.Credentials.Single().Use, latex.Origin(new Dictionary<string, string>())));
        Assert.Contains(PluginTranslationProviders.WiredPackages, p => p.InstanceId == OcrCatalog.TencentOcr && p.Directory == "plugins/tencent-ocr");

        var tmt = TranslationPackages.Find("tencent-translate")!;
        var tmtGrants = tmt.RequiredGrants(new Dictionary<string, string>());
        var bindings = new Dictionary<string, string> { ["secretId"] = "acct", ["secretKey"] = "acct" };
        var d = BuiltInCatalog.Defaults();
        AppSettings With(IReadOnlyList<CredentialGrant> accountGrants) => d with
        {
            Accounts = [new AccountSettings("acct", "Tencent Cloud", ["secretId", "secretKey"], accountGrants)],
            Instances = [.. d.Instances.Select(i => i.Id is OcrCatalog.TencentOcr or "tencent-translate" ? i with { AccountBindings = bindings } : i)],
            Services = [.. d.Services.Select(x => x.Instance == OcrCatalog.TencentOcr ? x with { Enabled = true } : x)],
        };
        using var supervisor = new Supervisor<HostSession>(() => throw new InvalidOperationException("never started"), SystemClock.Instance, TimeSpan.FromMinutes(1));
        Func<string, string, bool> saved = (_, _) => true;
        var tmtOnly = With([.. tmtGrants]);
        Assert.Null(PluginOcrProviders.Create(tmtOnly, OcrCatalog.TencentOcr, saved, supervisor));
        Assert.Null(PluginOcrProviders.Resolve(tmtOnly, saved, supervisor));
        var both = With([.. tmtGrants, .. grants]);
        Assert.Equal(OcrCatalog.TencentOcr, PluginOcrProviders.Resolve(both, saved, supervisor)!.InstanceId);
        Assert.Null(PluginOcrProviders.Resolve(both, (_, _) => false, supervisor)); // secrets not saved
    }

    // ---------- into the shared translation pipeline ----------

    [Fact] // T02/OCR02: OCR text enters the OCR window's own session: same chunking, one card per service
    public async Task Recognized_text_enters_the_ocr_translation_session_with_shared_chunking()
    {
        using var temp = new TempRoot();
        var settings = new SettingsStore(temp.Paths, new ManualClock());
        var config = new ConfigService(settings, new SecretStore(temp.Paths.Secrets, new XorProtector()));
        var features = new FeatureRegistry();
        var provider = new ScriptedProvider("mymemory", ScriptedProvider.MyMemory, new Step.Echo("T:"));
        var snapshot = new ConfigSnapshot(1, 1, 1, 1, TimeSpan.FromSeconds(30));
        var platform = new FakePlatform();
        var shell = new ShellCoordinator(platform, config, features, c => c == Capability.Translate, new ShellOptions(false, false),
            _ => new TranslationSession([provider], new TranslationSessionOptions(snapshot, 1), new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(), new RecordingUsage()));
        shell.Start();
        using var images = new Images();
        string longText = string.Join(" ", Enumerable.Range(0, 120).Select(i => $"word{i}"));
        var job = new OcrJob(() => new FakeOcr((_, _) => Task.FromResult(Recognized(new OcrBlock(longText)))), async t => Assert.True((await shell.SubmitRecognizedTextAsync(t)).Ok), () => true);

        var final = await job.RecognizeAsync(images.New());
        Assert.True(final.Translated);
        var session = shell.OcrTranslation;
        Assert.NotNull(session);
        TranslationSnapshot view = await session.SnapshotAsync();
        for (var deadline = DateTime.UtcNow.AddSeconds(5); view.Cards.Any(c => c.State != CardState.Ready) && DateTime.UtcNow < deadline; view = await session.SnapshotAsync()) await Task.Delay(20, Ct);
        Assert.Equal(longText, view.SourceText);
        var card = Assert.Single(view.Cards);
        Assert.Equal(CardState.Ready, card.State);
        Assert.True(card.Chunked); // > 500 UTF-8 bytes: split by the MyMemory limit, still one card
        Assert.True(provider.Calls.Count > 1);
        Assert.All(provider.Calls, c => Assert.True(System.Text.Encoding.UTF8.GetByteCount(c.Text) <= 500));
        Assert.Equal(0, images.Leases.ActiveCount);
    }
}
