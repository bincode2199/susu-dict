using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Storage;
using Susu.Testing;
using Susu.Ui;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F11.3 OCR window and SetOcr with fakes (no plugin host, no real capture): the SetOcr fields in settings.yaml and
/// Settings.SaveOcr; the OCR entry points greyed with a reason until an OCR service is usable (tray, SetHotkeys, SetOcr, the
/// hotkey registration and a failure bar for a stale chord); a capture opening the OCR window and following the job states;
/// capture failures on the failure bar; recapture, copy and close; and the preview reaching the page only as a small PNG
/// data URL, never as a path (OCR02 UI, OCR03, UI01).
/// </summary>
public sealed class OcrWindowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class FakeOcr(Func<OcrCall, CancellationToken, Task<OcrOutcome>> run, string id = OcrCatalog.TencentOcr) : IOcrProvider
    {
        public int Calls;
        public string InstanceId => id;
        public Task<OcrOutcome> RecognizeAsync(OcrCall call, CancellationToken cancellationToken) { Interlocked.Increment(ref Calls); return run(call, cancellationToken); }
    }

    private sealed class QueuedCapture : IScreenCapture
    {
        public readonly Queue<ScreenCaptureResult> Next = new();
        public int Calls;
        public Task<ScreenCaptureResult> CaptureRegionAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Next.Count > 0 ? Next.Dequeue() : ScreenCaptureResult.Cancelled("escape"));
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly SettingsStore Settings;
        public readonly ConfigService Config;
        public readonly ShellCoordinator Shell;
        public readonly QueuedCapture Capture = new();
        public readonly FileLeases Leases = new(TestTemp.NewDir("susu-ocrwin-leases"));
        public readonly OcrJob Job;
        public FakeOcr Provider;
        public bool ProviderReady = true;
        public readonly List<string> Translated = [];
        private long counter;

        public Rig(bool ocrReady = true, bool dev = false)
        {
            Settings = new SettingsStore(Root.Paths, new ManualClock());
            Config = new ConfigService(Settings, new SecretStore(Root.Paths.Secrets, new XorProtector()));
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Ocr, FeatureState.Available, null, [Capability.Ocr]));
            OcrReady = ocrReady;
            var translator = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Echo("T:"));
            var snapshot = new ConfigSnapshot(1, 1, 1, 1, TimeSpan.FromSeconds(30));
            Shell = new ShellCoordinator(Platform, Config, features, c => c == Capability.Translate || (c == Capability.Ocr && OcrReady), new ShellOptions(false, dev),
                _ => new TranslationSession([translator], new TranslationSessionOptions(snapshot, 1), new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(), new RecordingUsage()));
            Provider = new FakeOcr((_, _) => Task.FromResult<OcrOutcome>(new OcrOutcome.Recognized([new OcrBlock("Hello world"), new OcrBlock(@"\frac{a}{b}", Kind: "formula")])));
            Job = new OcrJob(() => ProviderReady ? Provider : null, async text => { Translated.Add(text); await Shell.SubmitRecognizedTextAsync(text); },
                () => Config.State.Effective.Ocr.AutoTranslate);
            Shell.ScreenCapture = Capture;
            Shell.Ocr = Job;
            Shell.Start();
        }

        public bool OcrReady;

        public ScreenshotImage Image(byte[]? preview = null, int width = 320, int height = 80)
        {
            var file = new LeasedFiles(Leases).Create("ocr", "image/png", "png");
            File.WriteAllBytes(file.FilePath, [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3]);
            return new ScreenshotImage(file, new PixelRect(-200, 40, width, height), width, height, 144, 1) { Preview = preview };
        }

        public Task<ScreenCaptureResult> NextPresentation()
        {
            var done = new TaskCompletionSource<ScreenCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<ScreenCaptureResult>? handler = null;
            handler = r => { Shell.ScreenCapturePresented -= handler; done.TrySetResult(r); };
            Shell.ScreenCapturePresented += handler;
            return done.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }

        public async Task<ScreenCaptureResult> CaptureAsync(ScreenCaptureResult result)
        {
            Capture.Next.Enqueue(result);
            var presented = NextPresentation();
            Shell.OnHotkey("ocrTranslate");
            return await presented;
        }

        /// <summary>Sends Ready and waits for the snapshot (it is built asynchronously), so later events reach the page.</summary>
        public void Ready(WindowKind kind)
        {
            int before = Snapshots(kind);
            Shell.OnPageMessage(kind, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = Platform.Session(kind) }));
            for (var poll = System.Diagnostics.Stopwatch.StartNew(); Snapshots(kind) == before; Thread.Sleep(5))
                if (poll.Elapsed > Eventually.DefaultTimeout) throw new TimeoutException("no snapshot");
        }

        private int Snapshots(WindowKind kind) => Platform.Posted.Count(p => p.Kind == kind && p.Envelope.Kind == UiMessageKind.Snapshot);

        public string Command(WindowKind kind, string name, object? payload = null)
        {
            string id = $"c{++counter}";
            Shell.OnPageMessage(kind, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Command", windowSessionId = Platform.Session(kind), name, correlationId = id, payload }, Web));
            return id;
        }

        public CommandResult Result(string correlationId)
        {
            for (var poll = System.Diagnostics.Stopwatch.StartNew(); poll.Elapsed < Eventually.DefaultTimeout;)
            {
                var hit = Platform.Posted.FirstOrDefault(p => p.Envelope.CorrelationId == correlationId);
                if (hit.Envelope is not null) return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CommandResult)!;
                Thread.Sleep(5);
            }
            throw new TimeoutException($"no result for {correlationId}");
        }

        public List<OcrView> OcrEvents() => [.. Platform.Posted.Where(p => p.Kind == WindowKind.Ocr && p.Envelope.Name == "ocr").Select(p => p.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.OcrView)!)];

        public UiSnapshot Snapshot(WindowKind kind)
            => Platform.Posted.Last(p => p.Kind == kind && p.Envelope.Kind == UiMessageKind.Snapshot).Envelope.Payload!.Value.Deserialize(ContractsJson.Default.UiSnapshot)!;

        public ErrorBarView? ErrorBar()
        {
            Ready(WindowKind.Error);
            return Snapshot(WindowKind.Error).ErrorBar;
        }

        public void SaveOcr(OcrSettings ocr)
        {
            var s = Config.State.Effective;
            Assert.Equal(SaveStatus.Saved, Config.Save(s with { Ocr = ocr }, Config.State.Revision, Config.State.FileHash).Status);
        }

        public void Dispose() { Leases.Dispose(); Settings.Dispose(); Root.Dispose(); }
    }

    private static ScreenCaptureResult Captured(ScreenshotImage image, string? copyError = null) => new(ScreenCaptureStatus.Captured, image, null, copyError);

    // ---------- settings ----------

    [Fact] // SetOcr fields round-trip through settings.yaml; keep-screenshots is off and auto-translate on by default (OCR03)
    public void Ocr_settings_round_trip_with_safe_defaults()
    {
        var defaults = BuiltInCatalog.Defaults().Ocr;
        Assert.Equal((OcrCatalog.TencentOcr, true, false, 7), (defaults.Service, defaults.AutoTranslate, defaults.KeepScreenshots, defaults.RetentionDays));
        var s = BuiltInCatalog.Defaults() with { Ocr = new OcrSettings(OcrCatalog.SimpleLatex, false, true, 30) };
        string text = SettingsYaml.Write(s).Replace("\r\n", "\n");
        Assert.Contains("ocr:\n  service: \"simple-latex\"\n  autoTranslate: false\n  keepScreenshots: true\n  retentionDays: 30\n", text);
        var (back, issues) = SettingsYaml.Read(text);
        Assert.Empty(issues);
        Assert.Equal(s.Ocr, back!.Ocr);
        Assert.True(back.ContentEquals(s));
        // An F11.2 file (service only) keeps the defaults for the new fields.
        var (old, oldIssues) = SettingsYaml.Read(text.Replace("  autoTranslate: false\n  keepScreenshots: true\n  retentionDays: 30\n", ""));
        Assert.Empty(oldIssues);
        Assert.Equal(new OcrSettings(OcrCatalog.SimpleLatex), old!.Ocr);
    }

    [Theory] // retention is 1-365 days and the switches are real booleans: anything else is a located issue, the file is refused
    [InlineData("  retentionDays: 30\n", "  retentionDays: 0\n", "ocr.retentionDays", "range")]
    [InlineData("  retentionDays: 30\n", "  retentionDays: 366\n", "ocr.retentionDays", "range")]
    [InlineData("  keepScreenshots: true\n", "  keepScreenshots: \"yes\"\n", "ocr.keepScreenshots", "type")]
    [InlineData("  retentionDays: 30\n", "  retentionDays: 30\n  extra: 1\n", "ocr.extra", "unknown")]
    public void Invalid_ocr_settings_are_located_issues(string from, string to, string path, string code)
    {
        var s = BuiltInCatalog.Defaults() with { Ocr = new OcrSettings(OcrCatalog.SimpleLatex, false, true, 30) };
        string text = SettingsYaml.Write(s).Replace("\r\n", "\n").Replace(from, to);
        var (settings, issues) = SettingsYaml.Read(text);
        Assert.Null(settings);
        Assert.Contains(issues, i => i.Path == path && (i.Code == code || code == "unknown") && i.Line > 0);
    }

    [Fact] // Settings.SaveOcr changes only the SetOcr fields; bad values and other windows are refused
    public void Save_ocr_writes_only_the_ocr_fields_and_validates()
    {
        using var rig = new Rig();
        rig.Shell.Open(WindowKind.Settings);
        rig.Ready(WindowKind.Settings);
        var before = rig.Config.State.Effective;
        var state = rig.Config.State;
        var ok = rig.Result(rig.Command(WindowKind.Settings, UiCommands.SaveOcr, new { expectedRevision = state.Revision, expectedFileHash = state.FileHash, service = OcrCatalog.SimpleLatex, autoTranslate = false, keepScreenshots = true, retentionDays = 14 }));
        Assert.True(ok.Ok, ok.Error);
        var after = rig.Config.State.Effective;
        Assert.Equal(new OcrSettings(OcrCatalog.SimpleLatex, false, true, 14), after.Ocr);
        Assert.Equal(before.Hotkeys.Chords, after.Hotkeys.Chords);
        Assert.Equal(before.Services, after.Services);
        var view = ok.Value!.Value.Deserialize(ContractsJson.Default.SettingsView)!.Ocr!;
        Assert.Equal((OcrCatalog.SimpleLatex, false, true, 14, 1, 365), (view.Service, view.AutoTranslate, view.KeepScreenshots, view.RetentionDays, view.MinRetentionDays, view.MaxRetentionDays));

        state = rig.Config.State;
        Assert.Equal("unknown-service", rig.Result(rig.Command(WindowKind.Settings, UiCommands.SaveOcr, new { expectedRevision = state.Revision, expectedFileHash = state.FileHash, service = "evil", autoTranslate = true, keepScreenshots = false, retentionDays = 7 })).Error);
        Assert.Equal("retention", rig.Result(rig.Command(WindowKind.Settings, UiCommands.SaveOcr, new { expectedRevision = state.Revision, expectedFileHash = state.FileHash, service = OcrCatalog.TencentOcr, autoTranslate = true, keepScreenshots = true, retentionDays = 0 })).Error);
        Assert.Equal("conflict", rig.Result(rig.Command(WindowKind.Settings, UiCommands.SaveOcr, new { expectedRevision = state.Revision - 1, expectedFileHash = state.FileHash, service = OcrCatalog.TencentOcr, autoTranslate = true, keepScreenshots = false, retentionDays = 7 })).Error);
        Assert.Equal(new OcrSettings(OcrCatalog.SimpleLatex, false, true, 14), rig.Config.State.Effective.Ocr);
        Assert.False(UiCommands.IsAllowed(WindowKind.Ocr, UiCommands.SaveOcr));
        Assert.False(UiCommands.IsAllowed(WindowKind.Main, UiCommands.BeginCapture));
        Assert.True(UiCommands.IsAllowed(WindowKind.Ocr, UiCommands.BeginCapture));
        Assert.True(UiCommands.IsAllowed(WindowKind.Ocr, UiCommands.CopyText));
    }

    // ---------- availability and greying ----------

    [Fact] // PLAN 1.2/UI06: with no usable OCR service every entry point is greyed with the same reason, nothing registers
    public void Without_a_usable_service_the_entries_are_greyed_with_a_reason()
    {
        using var rig = new Rig(ocrReady: false);
        var tray = rig.Shell.TrayModel().Items.Single(i => i.Id == "ocr");
        Assert.False(tray.Enabled);
        Assert.Equal("feature.noService.ocr", tray.ReasonKey);
        Assert.Equal("Alt+S", tray.Chord);
        var settings = rig.Shell.ProjectSettings(rig.Config.State);
        var hotkey = settings.Hotkeys.Single(h => h.Action == "ocrTranslate");
        Assert.Equal(("unavailable", "feature.noService.ocr"), (hotkey.State, hotkey.ReasonKey));
        Assert.False(settings.Ocr!.Ready);
        Assert.Equal("feature.noService.ocr", settings.Ocr.ReasonKey);
        Assert.Equal("Alt+S", settings.Ocr.Hotkey);
        Assert.DoesNotContain("ocrTranslate", rig.Platform.Registered.Keys);
        Assert.DoesNotContain("ocr", WindowFeatures(rig));
        // The tray command is refused, and the capture port is never reached.
        rig.Shell.OnTrayMenu();
        rig.Ready(WindowKind.Tray);
        Assert.Equal("unavailable", rig.Result(rig.Command(WindowKind.Tray, UiCommands.TrayOpen, new { id = "ocr" })).Error);
        Assert.Equal(0, rig.Capture.Calls);
    }

    private static string[] WindowFeatures(Rig rig)
    {
        rig.Shell.Open(WindowKind.Main);
        rig.Ready(WindowKind.Main);
        return rig.Snapshot(WindowKind.Main).Window.Features;
    }

    [Fact] // once a service is usable the feature is Available: tray enabled, chord registered, SetOcr ready
    public void With_a_usable_service_the_entries_are_available()
    {
        using var rig = new Rig(ocrReady: true);
        Assert.True(rig.Shell.TrayModel().Items.Single(i => i.Id == "ocr").Enabled);
        Assert.Equal("Alt+S", rig.Platform.Registered["ocrTranslate"]);
        var settings = rig.Shell.ProjectSettings(rig.Config.State);
        Assert.Equal("ok", settings.Hotkeys.Single(h => h.Action == "ocrTranslate").State);
        Assert.True(settings.Ocr!.Ready);
        Assert.Null(settings.Ocr.ReasonKey);
        Assert.Contains("ocr", WindowFeatures(rig));
        rig.Shell.OnTrayMenu();
        rig.Ready(WindowKind.Tray);
        Assert.True(rig.Result(rig.Command(WindowKind.Tray, UiCommands.TrayOpen, new { id = "ocr" })).Ok);
        Assert.Equal(1, rig.Capture.Calls);
    }

    [Fact] // a chord still registered when the service became unusable says why on the failure bar instead of doing nothing
    public void A_stale_ocr_chord_shows_the_reason_instead_of_failing_silently()
    {
        using var rig = new Rig(ocrReady: true);
        rig.OcrReady = false;
        rig.Shell.OnHotkey("ocrTranslate");
        Assert.Equal(0, rig.Capture.Calls);
        var bar = rig.ErrorBar();
        Assert.Equal(new ErrorLineView("feature.noService.ocr", "settings"), Assert.Single(bar!.Lines));
    }

    [Fact] // SetOcr: the shared Tencent Cloud account is not enough; the OCR package needs its own saved and granted targets
    public void SetOcr_projects_credential_targets_and_readiness_per_service()
    {
        using var rig = new Rig();
        var s = rig.Config.State.Effective;
        Assert.Equal(SaveStatus.Saved, rig.Config.Save(s with { Services = [.. s.Services.Select(x => x.Instance == OcrCatalog.TencentOcr ? x with { Enabled = true } : x)] }, rig.Config.State.Revision, rig.Config.State.FileHash).Status);
        rig.Shell.Open(WindowKind.Settings);
        rig.Ready(WindowKind.Settings);
        ServiceView Tencent(SettingsView view) => view.Services.Single(v => v.InstanceId == OcrCatalog.TencentOcr && v.Capability == "ocr");
        var initial = rig.Shell.ProjectSettings(rig.Config.State);
        Assert.Equal("ocr", Tencent(initial).Page);
        Assert.Equal("MissingCredential", Tencent(initial).Availability);
        Assert.All(Tencent(initial).CredentialTargets!, t => Assert.Equal(("https://ocr.tencentcloudapi.com:443", false, false), (t.Origin, t.Saved, t.Granted)));
        Assert.Equal(["Disabled", "MissingCredential"], initial.Ocr!.Choices.OrderBy(c => c.InstanceId).Select(c => c.Availability));
        // Saved without confirming the grant: still not usable.
        Assert.True(rig.Result(rig.Command(WindowKind.Settings, UiCommands.SecretWriteNew, new { instanceId = OcrCatalog.TencentOcr, secretName = "secretId", value = "AKIDtest" })).Ok);
        Assert.True(rig.Result(rig.Command(WindowKind.Settings, UiCommands.SecretWriteNew, new { instanceId = OcrCatalog.TencentOcr, secretName = "secretKey", value = "secret-test" })).Ok);
        var saved = rig.Shell.ProjectSettings(rig.Config.State);
        Assert.All(Tencent(saved).CredentialTargets!, t => Assert.True(t.Saved && !t.Granted));
        Assert.False(saved.Ocr!.Choices.Single(c => c.InstanceId == OcrCatalog.TencentOcr).Usable);
        // Confirming the OCR origin grant makes it usable.
        Assert.True(rig.Result(rig.Command(WindowKind.Settings, UiCommands.BindAccount, new { instanceId = OcrCatalog.TencentOcr, confirmGrants = true })).Ok);
        var granted = rig.Shell.ProjectSettings(rig.Config.State);
        Assert.Equal("Ready", Tencent(granted).Availability);
        var choice = granted.Ocr!.Choices.Single(c => c.InstanceId == OcrCatalog.TencentOcr);
        Assert.True(choice.Usable);
        Assert.Equal("tencent-ocr/ocr", choice.ServiceId);
        Assert.DoesNotContain("AKIDtest", rig.Platform.AllJson());
        Assert.DoesNotContain("secret-test", rig.Platform.AllJson());
    }

    // ---------- the OCR window ----------

    [Fact] // PLAN 6.2: a capture opens the OCR window (activated) and recognized text reaches its source card and its own session
    public async Task A_capture_opens_the_ocr_window_and_shows_the_recognized_text()
    {
        using var rig = new Rig();
        var image = rig.Image(preview: Png.EncodeBgra(new byte[4 * 4 * 4], 4, 4));
        string path = image.File.FilePath;
        await rig.CaptureAsync(Captured(image));
        Assert.Contains("show:Ocr:True", rig.Platform.Calls);
        Assert.True(await Eventually.WaitAsync(() => rig.Shell.OcrWindowView?.Phase == "recognized"));
        rig.Ready(WindowKind.Ocr);
        var view = rig.Snapshot(WindowKind.Ocr).Ocr!;
        Assert.Equal(("recognized", OcrCatalog.TencentOcr, "Hello world\n\\frac{a}{b}"), (view.Phase, view.ServiceId, view.Text));
        Assert.Equal([new OcrBlockView("Hello world", "text"), new OcrBlockView(@"\frac{a}{b}", "formula")], view.Blocks);
        Assert.Equal((320, 80, true, true, "Alt+S"), (view.Width, view.Height, view.Translated, view.AutoTranslate, view.Hotkey));
        Assert.NotNull(view.ElapsedMs);
        Assert.StartsWith("data:image/png;base64,", view.Preview);
        Assert.Equal(["Hello world\n\\frac{a}{b}"], rig.Translated);
        Assert.Equal("Hello world\n\\frac{a}{b}", (await rig.Shell.OcrTranslation!.SnapshotAsync()).SourceText);
        Assert.True(image.File.Released);
        Assert.DoesNotContain(Path.GetFileName(path), rig.Platform.AllJson());
        Assert.False(File.Exists(path));
        Assert.DoesNotContain("file:", rig.Platform.AllJson());
    }

    [Fact] // the page follows the states: recognizing (two skeleton lines) then the result, as events on a warm window
    public async Task The_window_follows_recognizing_then_the_result()
    {
        using var rig = new Rig();
        await rig.CaptureAsync(Captured(rig.Image()));
        Assert.True(await Eventually.WaitAsync(() => rig.Shell.OcrWindowView?.Phase == "recognized"));
        rig.Ready(WindowKind.Ocr);
        var gate = new TaskCompletionSource<OcrOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider = new FakeOcr((_, _) => gate.Task);
        await rig.CaptureAsync(Captured(rig.Image()));
        Assert.True(await Eventually.WaitAsync(() => rig.OcrEvents().Any(e => e.Phase == "recognizing" && e.Id == 2)));
        Assert.Contains(rig.Platform.Posted, p => p.Kind == WindowKind.Ocr && p.Envelope.Name == "translation"); // the old cards are dropped
        gate.SetResult(new OcrOutcome.NoText());
        Assert.True(await Eventually.WaitAsync(() => rig.OcrEvents().LastOrDefault()?.Phase == "noText"));
        var last = rig.OcrEvents().Last();
        Assert.Equal((2L, null as string, false), (last.Id, last.Text, last.Translated));
        Assert.Single(rig.Translated); // "no text" is never translated
    }

    [Fact] // auto-translate off: the text only fills the source card; Translate (SubmitText) sends the edited text
    public async Task Auto_translate_off_waits_for_translate()
    {
        using var rig = new Rig();
        rig.SaveOcr(new OcrSettings(OcrCatalog.TencentOcr, AutoTranslate: false));
        await rig.CaptureAsync(Captured(rig.Image()));
        Assert.True(await Eventually.WaitAsync(() => rig.Shell.OcrWindowView?.Phase == "recognized"));
        Assert.False(rig.Shell.OcrWindowView!.Translated);
        Assert.False(rig.Shell.OcrWindowView.AutoTranslate);
        Assert.Empty(rig.Translated);
        rig.Ready(WindowKind.Ocr);
        Assert.True(rig.Result(rig.Command(WindowKind.Ocr, UiCommands.SubmitText, new { text = "Hello, edited" })).Ok);
        Assert.Equal("Hello, edited", (await rig.Shell.OcrTranslation!.SnapshotAsync()).SourceText);
    }

    [Theory] // OCR02 failures keep the window and say what failed with a host text key; no service is Unavailable
    [InlineData(ErrorKind.Auth)]
    [InlineData(ErrorKind.Network)]
    [InlineData(ErrorKind.Quota)]
    public async Task A_vendor_failure_is_shown_by_its_class(ErrorKind kind)
    {
        using var rig = new Rig();
        rig.Provider = new FakeOcr((_, _) => Task.FromResult<OcrOutcome>(new OcrOutcome.Failure(new ProviderError(kind, "vendor said: SecretId AKID... is bad"))));
        await rig.CaptureAsync(Captured(rig.Image()));
        Assert.True(await Eventually.WaitAsync(() => rig.Shell.OcrWindowView?.Phase == "failed"));
        rig.Ready(WindowKind.Ocr);
        var view = rig.Snapshot(WindowKind.Ocr).Ocr!;
        Assert.Equal(kind, view.ErrorKind);
        Assert.DoesNotContain("vendor said", rig.Platform.AllJson()); // the diagnostic detail never reaches the page
        Assert.Empty(rig.Translated);
    }

    [Fact]
    public async Task No_service_at_recognition_time_is_unavailable()
    {
        using var rig = new Rig();
        rig.ProviderReady = false;
        var image = rig.Image();
        await rig.CaptureAsync(Captured(image));
        Assert.True(await Eventually.WaitAsync(() => rig.Shell.OcrWindowView?.Phase == "unavailable"));
        Assert.True(image.File.Released);
    }

    [Theory] // OCR03/F11.1 codes: a failed capture has no window yet, so it shows the failure bar; never a false success
    [InlineData("capture.failed", "ocr.capture.failed")]
    [InlineData("capture.diskFull", "ocr.capture.diskFull")]
    [InlineData("capture.writeFailed", "ocr.capture.writeFailed")]
    public async Task A_failed_capture_shows_the_failure_bar(string code, string key)
    {
        using var rig = new Rig();
        await rig.CaptureAsync(ScreenCaptureResult.Failed(code));
        Assert.DoesNotContain(rig.Platform.Calls, c => c.StartsWith("show:Ocr", StringComparison.Ordinal));
        Assert.Equal(new ErrorLineView(key), Assert.Single(rig.ErrorBar()!.Lines));
        Assert.Equal(0, rig.Provider.Calls);
    }

    [Fact] // a kept copy that could not be written is reported in the window; the recognition still runs
    public async Task A_failed_kept_copy_is_a_notice_in_the_window()
    {
        using var rig = new Rig();
        await rig.CaptureAsync(Captured(rig.Image(), copyError: "capture.diskFull"));
        Assert.True(await Eventually.WaitAsync(() => rig.Shell.OcrWindowView?.Phase == "recognized"));
        Assert.Equal("ocr.keep.diskFull", rig.Shell.OcrWindowView!.Notice);
    }

    [Fact] // Esc on the overlay: nothing is shown and the previous result stays
    public async Task A_cancelled_capture_keeps_the_previous_result()
    {
        using var rig = new Rig();
        await rig.CaptureAsync(Captured(rig.Image()));
        Assert.True(await Eventually.WaitAsync(() => rig.Shell.OcrWindowView?.Phase == "recognized"));
        var before = rig.Shell.OcrWindowView;
        int shows = rig.Platform.Calls.Count(c => c.StartsWith("show:", StringComparison.Ordinal));
        await rig.CaptureAsync(ScreenCaptureResult.Cancelled("escape"));
        Assert.Same(before, rig.Shell.OcrWindowView);
        Assert.Equal(shows, rig.Platform.Calls.Count(c => c.StartsWith("show:", StringComparison.Ordinal)));
    }

    [Fact] // "重新截图" and copy from the OCR window; recapture is refused while OCR is unavailable
    public async Task Recapture_and_copy_from_the_window()
    {
        using var rig = new Rig();
        await rig.CaptureAsync(Captured(rig.Image()));
        Assert.True(await Eventually.WaitAsync(() => rig.Shell.OcrWindowView?.Phase == "recognized"));
        rig.Ready(WindowKind.Ocr);
        Assert.True(rig.Result(rig.Command(WindowKind.Ocr, UiCommands.CopyText, new { text = @"\frac{a}{b}" })).Ok);
        Assert.Equal(@"\frac{a}{b}", rig.Platform.Clipboard);
        rig.Capture.Next.Enqueue(Captured(rig.Image()));
        var presented = rig.NextPresentation();
        Assert.True(rig.Result(rig.Command(WindowKind.Ocr, UiCommands.BeginCapture)).Ok);
        await presented;
        Assert.Equal(2, rig.Capture.Calls);
        Assert.True(await Eventually.WaitAsync(() => rig.Shell.OcrWindowView?.Id == 2 && rig.Shell.OcrWindowView.Phase == "recognized"));
        rig.OcrReady = false;
        Assert.Equal("unavailable", rig.Result(rig.Command(WindowKind.Ocr, UiCommands.BeginCapture)).Error);
        Assert.Equal(2, rig.Capture.Calls);
    }

    [Fact] // closing the window (Esc) cancels the recognition: the lease goes and nothing is translated
    public async Task Closing_the_window_cancels_recognition()
    {
        using var rig = new Rig();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider = new FakeOcr(async (_, cancel) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancel); return new OcrOutcome.NoText(); });
        var image = rig.Image();
        await rig.CaptureAsync(Captured(image));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        rig.Ready(WindowKind.Ocr);
        Assert.True(rig.Result(rig.Command(WindowKind.Ocr, UiCommands.Close)).Ok);
        Assert.Contains("hide:Ocr", rig.Platform.Calls);
        Assert.True(await Eventually.WaitAsync(() => image.File.Released));
        Assert.Empty(rig.Translated);
        Assert.Equal(0, rig.Leases.ActiveCount);
    }

    // ---------- preview safety ----------

    [Fact] // the page gets only a PNG data URL of bounded size: never a path, a file URL or arbitrary bytes
    public void Preview_url_accepts_only_small_png_bytes()
    {
        var png = Png.EncodeBgra(new byte[8 * 8 * 4], 8, 8);
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(png), ShellCoordinator.PreviewUrl(png));
        Assert.Null(ShellCoordinator.PreviewUrl(null));
        Assert.Null(ShellCoordinator.PreviewUrl("<svg onload=alert(1)>"u8.ToArray()));
        Assert.Null(ShellCoordinator.PreviewUrl(System.Text.Encoding.UTF8.GetBytes(@"C:\Users\x\AppData\Local\Temp\a.png")));
        var huge = new byte[OcrView.PreviewMaxBytes + 1];
        png.CopyTo(huge, 0);
        Assert.Null(ShellCoordinator.PreviewUrl(huge));
    }

    [Fact] // UI01: a large high-DPI selection gets a shrunk thumbnail within the size limits; a small one is sent as is
    public void Thumbnails_fit_the_limits()
    {
        var rnd = new Random(7);
        int w = 3000, h = 1400;
        var noisy = new byte[w * h * 4];
        rnd.NextBytes(noisy);
        var thumb = Png.Thumbnail(noisy, w, h);
        if (thumb is not null)
        {
            var (tw, th) = Png.SizeOf(thumb);
            Assert.True(tw <= Png.ThumbnailMaxWidth && th <= Png.ThumbnailMaxHeight, $"{tw}x{th}");
            Assert.True(thumb.Length <= OcrView.PreviewMaxBytes);
        }
        var flat = new byte[w * h * 4];
        var shrunk = Png.Thumbnail(flat, w, h)!;
        var (fw, fh) = Png.SizeOf(shrunk);
        Assert.Equal((750, 350), (fw, fh)); // factor 4: 3000/760 and 1400/360 both round up to 4
        var small = Png.EncodeBgra(new byte[40 * 10 * 4], 40, 10);
        Assert.Same(small, Png.Thumbnail(new byte[40 * 10 * 4], 40, 10, small));
        var shrink = Png.Shrink([10, 20, 30, 255, 30, 40, 50, 255, 50, 60, 70, 255], 3, 1, 2, out int sw, out int sh);
        Assert.Equal((2, 1), (sw, sh));
        Assert.Equal(new byte[] { 20, 30, 40, 255, 50, 60, 70, 255 }, shrink);
    }

    [Fact] // the capture coordinator attaches the thumbnail made from the captured pixels
    public async Task Captured_images_carry_a_thumbnail()
    {
        using var root = new TempRoot();
        using var leases = new FileLeases(TestTemp.NewDir("susu-ocrwin-thumb"));
        var frame = new ScreenCaptureTestsFrame(new PixelRect(0, 0, 200, 100));
        var coordinator = new ScreenCaptureCoordinator(new FrameGrabber(frame), new FixedSelector(new PixelRect(10, 10, 120, 40)), new NoHider(), new LeasedFiles(leases), new ManualClock());
        var result = await coordinator.CaptureRegionAsync(Ct);
        using var image = result.Image!;
        Assert.NotNull(image.Preview);
        Assert.Equal((120, 40), Png.SizeOf(image.Preview));
    }

    private sealed class ScreenCaptureTestsFrame(PixelRect bounds) : IScreenFrame
    {
        public PixelRect Bounds => bounds;
        public IReadOnlyList<CaptureMonitor> Monitors { get; } = [new CaptureMonitor("m", bounds, 96, true)];
        public byte[] CopyBgra(PixelRect region) => new byte[region.Width * region.Height * 4];
        public void Dispose() { }
    }

    private sealed class FrameGrabber(IScreenFrame frame) : IScreenGrabber { public IScreenFrame CaptureVirtualScreen() => frame; }

    private sealed class FixedSelector(PixelRect rect) : IRegionSelector
    {
        public Task<PixelRect?> SelectAsync(IScreenFrame frame, RegionSelectOptions options, CancellationToken cancellationToken) => Task.FromResult<PixelRect?>(rect);
    }

    private sealed class NoHider : ICaptureWindowHider
    {
        public Task<IDisposable> HideAllAsync(CancellationToken cancellationToken) => Task.FromResult<IDisposable>(new Nothing());
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
