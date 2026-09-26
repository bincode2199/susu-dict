using System.Collections.Concurrent;
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

public class PlacementTests
{
    private static readonly MonitorInfo Primary = new("m1", new PixelRect(0, 0, 1920, 1040), 96);
    private static readonly MonitorInfo Left150 = new("m2", new PixelRect(-2880, 0, 2880, 1560), 144);
    private static readonly MonitorInfo Hi200 = new("m3", new PixelRect(1920, 0, 3840, 2080), 192);

    [Theory] // UI01: design DIPs become physical pixels on the target monitor
    [InlineData(96, 520, 700)]
    [InlineData(144, 780, 1050)]
    [InlineData(192, 1040, 1400)]
    public void Design_size_scales_with_the_monitor_dpi(int dpi, int width, int height)
    {
        var monitor = new MonitorInfo("m", new PixelRect(0, 0, 4000, 3000), dpi);
        var rect = PlacementPolicy.Compute(WindowSpec.For(WindowKind.Main), [monitor], monitor, null);
        Assert.Equal((width, height), (rect.Width, rect.Height));
        Assert.Equal(((4000 - width) / 2, (3000 - height) / 2), (rect.X, rect.Y)); // no remembered position: centered
    }

    [Fact] // UI01: a remembered position on a negative-coordinate secondary monitor is reused with that monitor's DPI
    public void Remembered_position_on_a_left_monitor_is_kept()
    {
        var rect = PlacementPolicy.Compute(WindowSpec.For(WindowKind.Main), [Primary, Left150], Primary, (-2000, 100));
        Assert.Equal(new PixelRect(-2000, 100, 780, 1050), rect);
    }

    [Fact] // UI01: unplugged monitor → the title bar is no longer in any work area → center on the cursor's monitor
    public void Position_on_a_missing_monitor_falls_back_to_center()
    {
        var rect = PlacementPolicy.Compute(WindowSpec.For(WindowKind.Main), [Primary], Primary, (-2000, 100));
        Assert.Equal(new PixelRect(700, 170, 520, 700), rect);
    }

    [Fact] // UI01: only a sliver visible → the whole window is clamped into the work area
    public void Partially_visible_window_is_clamped()
    {
        var rect = PlacementPolicy.Compute(WindowSpec.For(WindowKind.Main), [Primary], Primary, (1650, 900));
        Assert.Equal((1920 - 520, 1040 - 700), (rect.X, rect.Y));
    }

    [Fact] // UI02: settings always centers on the cursor's monitor and ignores any remembered position
    public void Settings_always_centers_on_the_cursor_monitor()
    {
        var rect = PlacementPolicy.Compute(WindowSpec.For(WindowKind.Settings), [Primary, Hi200], Hi200, (10, 10));
        Assert.Equal(new PixelRect(1920 + (3840 - 1800) / 2, (2080 - 1400) / 2, 1800, 1400), rect);
    }

    [Fact] // UI01: small work area → window shrinks to fit; floats never exceed work height minus 32 DIP
    public void Small_work_area_shrinks_and_floats_keep_a_margin()
    {
        var small = new MonitorInfo("s", new PixelRect(0, 0, 800, 600), 96);
        var main = PlacementPolicy.Compute(WindowSpec.For(WindowKind.Settings), [small], small, null);
        Assert.Equal(new PixelRect(0, 0, 800, 600), main);
        var floating = PlacementPolicy.Compute(WindowSpec.For(WindowKind.Selection), [small], small, null, contentHeightDip: 5000);
        Assert.Equal(600 - 32, floating.Height);
    }
}

public class WindowPolicyTests
{
    [Fact] // PER04 logic: release 10 min after the last window hides; showing again cancels; stale timers do nothing
    public void Keep_warm_release_and_cancel()
    {
        var life = new WebViewLifecycle();
        Assert.Equal(WebViewAction.None, life.Shown(WindowKind.Main));
        Assert.Equal(WebViewAction.None, life.Shown(WindowKind.Settings));
        Assert.Equal(WebViewAction.None, life.Hidden(WindowKind.Main));
        Assert.Equal(WebViewAction.StartReleaseTimer, life.Hidden(WindowKind.Settings));
        long first = life.TimerGeneration;
        Assert.Equal(WebViewAction.CancelReleaseTimer, life.Shown(WindowKind.Main));
        Assert.Equal(WebViewAction.None, life.TimerFired(first));
        Assert.Equal(WebViewAction.StartReleaseTimer, life.Hidden(WindowKind.Main));
        Assert.Equal(WebViewAction.Release, life.TimerFired(life.TimerGeneration));
        Assert.Equal(WebViewAction.None, life.TimerFired(life.TimerGeneration)); // no duplicate release
        Assert.Equal(TimeSpan.FromMinutes(10), WebViewLifecycle.ReleaseAfter);
    }

    [Theory] // J03 window part: minimize keeps the task, close cancels, "close exits" quits from main/settings
    [InlineData(WindowKind.Main, WindowRequest.Minimize, CloseAction.Exit, WindowOutcome.HideKeepTask)]
    [InlineData(WindowKind.Main, WindowRequest.Close, CloseAction.Hide, WindowOutcome.HideCancelTask)]
    [InlineData(WindowKind.Main, WindowRequest.Close, CloseAction.Exit, WindowOutcome.ExitApp)]
    [InlineData(WindowKind.Tray, WindowRequest.Close, CloseAction.Exit, WindowOutcome.HideCancelTask)]
    [InlineData(WindowKind.Selection, WindowRequest.Escape, CloseAction.Exit, WindowOutcome.HideCancelTask)]
    public void Close_and_minimize_semantics(WindowKind kind, WindowRequest request, CloseAction action, WindowOutcome expected)
        => Assert.Equal(expected, WindowSemantics.Decide(kind, request, action));

    [Theory]
    [InlineData("Alt+A", Chords.Modifiers.Alt, 0x41u)]
    [InlineData("Ctrl+Alt+Shift+F13", Chords.Modifiers.Ctrl | Chords.Modifiers.Alt | Chords.Modifiers.Shift, 0x7Cu)]
    [InlineData("Win+Space", Chords.Modifiers.Win, 0x20u)]
    [InlineData("Ctrl+Oem3", Chords.Modifiers.Ctrl, 0xC0u)]
    public void Chords_parse(string chord, Chords.Modifiers modifiers, uint key)
    {
        Assert.True(Chords.TryParse(chord, out var m, out uint k));
        Assert.Equal((modifiers, key), (m, k));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("Alt+Alt+A")]
    [InlineData("Hyper+A")]
    [InlineData("Alt+F25")]
    public void Bad_chords_are_rejected(string chord) => Assert.False(Chords.TryParse(chord, out _, out _));
}

public class UiInboundTests
{
    private static string Json(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public void Ready_and_whitelisted_commands_from_the_right_session_pass()
    {
        Assert.NotNull(UiInbound.Decode(Json(new { uiVersion = 1, kind = "Ready", windowSessionId = "s" }), WindowKind.Main, "s", out _));
        Assert.NotNull(UiInbound.Decode(Json(new { uiVersion = 1, kind = "Command", windowSessionId = "s", name = "Secret.WriteNew", correlationId = "c1", payload = new { } }), WindowKind.Settings, "s", out _));
    }

    [Theory] // S08 and protocol hygiene
    [InlineData("""{"uiVersion":1,"kind":"Command","windowSessionId":"s","name":"Secret.WriteNew","correlationId":"c"}""", "Main", "not-allowed")]
    [InlineData("""{"uiVersion":1,"kind":"Command","windowSessionId":"s","name":"Http.Fetch","correlationId":"c"}""", "Settings", "not-allowed")]
    [InlineData("""{"uiVersion":1,"kind":"Command","windowSessionId":"old","name":"Window.Close","correlationId":"c"}""", "Main", "session")]
    [InlineData("""{"uiVersion":2,"kind":"Ready","windowSessionId":"s"}""", "Main", "version")]
    [InlineData("""{"uiVersion":1,"kind":"Ready","windowSessionId":"s","grant":"x"}""", "Main", "malformed")]
    [InlineData("""{"uiVersion":1,"kind":"Snapshot","windowSessionId":"s"}""", "Main", "kind")]
    [InlineData("""{"uiVersion":1,"kind":"Command","windowSessionId":"s","name":"Window.Close"}""", "Main", "correlation")]
    [InlineData("""not json""", "Main", "malformed")]
    public void Hostile_or_stale_messages_are_rejected(string json, string window, string reason)
    {
        Assert.Null(UiInbound.Decode(json, Enum.Parse<WindowKind>(window), "s", out var why));
        Assert.Equal(reason, why);
    }

    [Fact]
    public void Oversized_messages_are_rejected_before_parsing()
        => Assert.Null(UiInbound.Decode(new string(' ', UiInbound.MaxMessageChars + 1), WindowKind.Main, "s", out _));
}

/// <summary>Records what the coordinator asks of Win32/WebView; timers are fired by the test.</summary>
public sealed class FakePlatform : IWindowPlatform
{
    public ConcurrentQueue<(WindowKind Kind, UiEnvelope Envelope)> Posted { get; } = new();
    public List<string> Calls { get; } = [];
    public List<(TimeSpan Delay, Action Action)> Timers { get; } = [];
    public Dictionary<string, bool> HotkeyOutcome { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Registered { get; private set; } = [];
    private readonly Dictionary<WindowKind, string> sessions = [];
    public bool Exited;
    public string Clipboard = "";

    public string Session(WindowKind kind) => sessions[kind];
    public string Show(WindowKind kind, bool activate)
    {
        Calls.Add($"show:{kind}:{activate}");
        return sessions.TryGetValue(kind, out var s) ? s : sessions[kind] = Guid.NewGuid().ToString("N");
    }
    public void Hide(WindowKind kind) => Calls.Add($"hide:{kind}");
    public void ToggleMaximize(WindowKind kind) => Calls.Add($"max:{kind}");
    public void SetPinned(WindowKind kind, bool pinned) => Calls.Add($"pin:{kind}:{pinned}");
    public void Post(WindowKind kind, string json) => Posted.Enqueue((kind, JsonSerializer.Deserialize(json, ContractsJson.Default.UiEnvelope)!));
    public void Suspend(WindowKind kind) => Calls.Add($"suspend:{kind}");
    public void ReleaseAll() { Calls.Add("release"); sessions.Clear(); }
    public IReadOnlyDictionary<string, bool> RegisterHotkeys(IReadOnlyDictionary<string, string> chords)
    {
        Registered = new(chords);
        return chords.ToDictionary(c => c.Key, c => HotkeyOutcome.GetValueOrDefault(c.Key, true));
    }
    public void SetClipboardText(string text) => Clipboard = text;
    public void StartTimer(TimeSpan delay, Action elapsed)
    {
        if (delay == TimeSpan.Zero) { elapsed(); return; }
        lock (Timers) Timers.Add((delay, elapsed));
    }
    public void Exit() => Exited = true;
    public string AllJson() => string.Join('\n', Posted.Select(p => JsonSerializer.Serialize(p.Envelope, ContractsJson.Default.UiEnvelope)));
}

public class ShellCoordinatorTests
{
    private sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly SettingsStore Settings;
        public readonly SecretStore Secrets;
        public readonly ConfigService Config;
        public readonly FeatureRegistry Features = new();
        public readonly ShellCoordinator Shell;
        private long counter;
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        public Rig(bool translateReady = false, Func<AppSettings, TranslationSession?>? sessions = null, ILanguageDetector? languageDetector = null, CaptureCoordinator? capture = null)
        {
            Settings = new SettingsStore(Root.Paths, new ManualClock());
            Secrets = new SecretStore(Root.Paths.Secrets, new XorProtector());
            Config = new ConfigService(Settings, Secrets);
            Features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            Features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Ocr, FeatureState.InDevelopment, "feature.inDevelopment", []));
            if (capture is not null)
            {
                Features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Selection, FeatureState.Available, null, []));
                Features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Clipboard, FeatureState.Available, null, []));
            }
            Shell = new ShellCoordinator(Platform, Config, Features, c => translateReady && c == Capability.Translate, new ShellOptions(false, false), sessions, languageDetector, capture: capture);
            Shell.Start();
        }

        public TranslationSnapshot TranslationEvent(int index = 0)
            => Platform.Posted.Where(p => p.Envelope.Name == "translation").ElementAt(index).Envelope.Payload!.Value.Deserialize(ContractsJson.Default.TranslationSnapshot)!;

        public void Ready(WindowKind kind) => Shell.OnPageMessage(kind, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = Platform.Session(kind) }));

        public string Command(WindowKind kind, string name, object? payload = null)
        {
            string id = $"c{++counter}";
            Shell.OnPageMessage(kind, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Command", windowSessionId = Platform.Session(kind), name, correlationId = id, payload }, Web));
            return id;
        }

        public CommandResult Result(string correlationId)
        {
            for (int i = 0; i < 500; i++)
            {
                var hit = Platform.Posted.FirstOrDefault(p => p.Envelope.CorrelationId == correlationId);
                if (hit.Envelope is not null) return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CommandResult)!;
                Thread.Sleep(5);
            }
            throw new TimeoutException($"no result for {correlationId}");
        }

        public UiSnapshot LastSnapshot(WindowKind kind)
        {
            for (int i = 0; i < 500; i++)
            {
                var hit = Platform.Posted.LastOrDefault(p => p.Kind == kind && p.Envelope.Kind == UiMessageKind.Snapshot);
                if (hit.Envelope is not null) return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.UiSnapshot)!;
                Thread.Sleep(5);
            }
            throw new TimeoutException("no snapshot");
        }

        public void Dispose() { Settings.Dispose(); Root.Dispose(); }
    }

    [Fact] // S07: credentials go in one way; no page message, settings file or secrets file contains them
    public void Secret_write_is_one_way_and_only_presence_comes_back()
    {
        using var rig = new Rig();
        rig.Shell.Open(WindowKind.Settings);
        rig.Ready(WindowKind.Settings);
        var result = rig.Result(rig.Command(WindowKind.Settings, UiCommands.SecretWriteNew, new { instanceId = "deepl", secretName = "apiKey", value = "dl-SECRET-4711" }));
        Assert.True(result.Ok, result.Error);
        var view = result.Value!.Value.Deserialize(ContractsJson.Default.SettingsView)!;
        Assert.Equal("Disabled", view.Services.Single(s => s.ServiceId == "deepl/translate").Availability); // saved key, service still off
        Assert.True(view.Accounts.Single(a => a.Id == "deepl").Secrets.Single(s => s.Name == "apiKey").Saved);
        Assert.True(rig.Secrets.TryRead("deepl", "apiKey", out var stored) && stored == "dl-SECRET-4711");
        Assert.DoesNotContain("SECRET-4711", rig.Platform.AllJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-4711", File.ReadAllText(rig.Root.Paths.Settings), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-4711", File.ReadAllText(rig.Root.Paths.Secrets), StringComparison.Ordinal);
        Assert.Equal("deepl", rig.Settings.State.Effective.Instances.Single(i => i.Id == "deepl").AccountBindings["apiKey"]);

        // Proxy password uses the same path and is reported only as saved.
        Assert.True(rig.Result(rig.Command(WindowKind.Settings, UiCommands.SecretWriteNew, new { instanceId = ShellCoordinator.ProxySecretInstance, secretName = "password", value = "proxy-PASS-99" })).Ok);
        Assert.True(rig.Shell.ProjectSettings(rig.Settings.State).Network.ProxyPasswordSaved);
        Assert.DoesNotContain("PASS-99", rig.Platform.AllJson(), StringComparison.Ordinal);
    }

    [Fact] // S08: a result window cannot reach credential commands; nothing answers it
    public void Result_windows_cannot_touch_secrets()
    {
        using var rig = new Rig();
        rig.Shell.Open(WindowKind.Main);
        string id = rig.Command(WindowKind.Main, UiCommands.SecretWriteNew, new { instanceId = "deepl", secretName = "apiKey", value = "x" });
        Thread.Sleep(50);
        Assert.DoesNotContain(rig.Platform.Posted, p => p.Envelope.CorrelationId == id);
        Assert.False(rig.Secrets.Has("deepl", "apiKey"));
    }

    [Fact] // DATA01 through the UI: a stale view is a conflict; an invalid proposal returns exact issues
    public void Settings_save_reports_conflicts_and_invalid_input()
    {
        using var rig = new Rig();
        rig.Shell.Open(WindowKind.Settings);
        rig.Ready(WindowKind.Settings);
        var view = rig.LastSnapshot(WindowKind.Settings).Settings!;
        var general = view.General with { DefaultExpandedCards = 3 };
        var ok = rig.Result(rig.Command(WindowKind.Settings, UiCommands.SettingsSave, new SettingsSaveRequest(view.Revision, view.FileHash, general, view.Hotkeys, view.Network, [])));
        Assert.True(ok.Ok, ok.Error);
        Assert.Equal(3, rig.Settings.State.Effective.General.DefaultExpandedCards);
        var stale = rig.Result(rig.Command(WindowKind.Settings, UiCommands.SettingsSave, new SettingsSaveRequest(view.Revision, view.FileHash, general with { DefaultExpandedCards = 4 }, view.Hotkeys, view.Network, [])));
        Assert.Equal("conflict", stale.Error);
        var current = rig.Shell.ProjectSettings(rig.Settings.State);
        var clash = current.Hotkeys.Select(h => h.Action == "ocrTranslate" ? h with { Chord = "Alt+A" } : h).ToArray();
        var invalid = rig.Result(rig.Command(WindowKind.Settings, UiCommands.SettingsSave, new SettingsSaveRequest(current.Revision, current.FileHash, current.General, clash, current.Network, [])));
        Assert.Equal("invalid", invalid.Error);
        Assert.Contains(invalid.Value!.Value.Deserialize(ContractsJson.Default.SettingsIssueViewArray)!, i => i.Code == "conflict");
    }

    [Fact] // UI06: unimplemented features are listed disabled with a reason and cannot be opened
    public void Tray_offers_only_available_entries()
    {
        using var rig = new Rig();
        var tray = rig.Shell.TrayModel();
        Assert.False(tray.Items.Single(i => i.Id == "ocr").Enabled);
        Assert.Equal("feature.inDevelopment", tray.Items.Single(i => i.Id == "ocr").ReasonKey);
        Assert.Equal("feature.noService.translate", tray.Items.Single(i => i.Id == "input-translation").ReasonKey);
        Assert.True(tray.Items.Single(i => i.Id == "settings").Enabled);
        rig.Shell.Open(WindowKind.Tray);
        Assert.Equal("unavailable", rig.Result(rig.Command(WindowKind.Tray, UiCommands.TrayOpen, new { id = "ocr" })).Error);
        Assert.True(rig.Result(rig.Command(WindowKind.Tray, UiCommands.TrayOpen, new { id = "settings" })).Ok);
        Assert.Contains("hide:Tray", rig.Platform.Calls);
        Assert.Contains("show:Settings:True", rig.Platform.Calls);
        rig.Result(rig.Command(WindowKind.Tray, UiCommands.TrayExit));
        Assert.True(rig.Platform.Exited);
    }

    [Fact] // CFG05 / PLAN 1.2: only available features register hotkeys; a refused registration is visible
    public void Hotkeys_register_for_available_features_and_failures_are_shown()
    {
        using var rig = new Rig(translateReady: true);
        Assert.Equal(["inputTranslate"], rig.Platform.Registered.Keys);
        rig.Platform.HotkeyOutcome["inputTranslate"] = false;
        var view = rig.Shell.ProjectSettings(rig.Settings.State);
        rig.Shell.Open(WindowKind.Settings);
        rig.Ready(WindowKind.Settings);
        rig.Result(rig.Command(WindowKind.Settings, UiCommands.SettingsSave, new SettingsSaveRequest(view.Revision, view.FileHash, view.General with { LaunchAtStartup = true }, view.Hotkeys, view.Network, [])));
        var after = rig.Shell.ProjectSettings(rig.Settings.State).Hotkeys;
        Assert.Equal("failed", after.Single(h => h.Action == "inputTranslate").State);
        Assert.Equal("unavailable", after.Single(h => h.Action == "ocrTranslate").State);
        Assert.Equal("unassigned", after.Single(h => h.Action == "pronounce").State);
        rig.Shell.OnHotkey("ocrTranslate");
        Assert.DoesNotContain(rig.Platform.Calls, c => c.StartsWith("show:Ocr", StringComparison.Ordinal)); // no action for unavailable features
    }

    [Fact] // PER04 logic end to end: hide → hidden notice → suspend → release timer → release; reopen gets a fresh session
    public void Close_hides_suspends_and_releases_after_the_timer()
    {
        using var rig = new Rig();
        rig.Shell.Open(WindowKind.Settings);
        rig.Ready(WindowKind.Settings);
        string before = rig.Platform.Session(WindowKind.Settings);
        rig.Result(rig.Command(WindowKind.Settings, UiCommands.Close));
        Assert.Contains(rig.Platform.Posted, p => p.Envelope.Name == "window.hidden");
        Assert.Equal(["show:Settings:True", "hide:Settings", "suspend:Settings"], rig.Platform.Calls);
        var (delay, fire) = Assert.Single(rig.Platform.Timers);
        Assert.Equal(TimeSpan.FromMinutes(10), delay);
        fire();
        Assert.Contains("release", rig.Platform.Calls);
        rig.Shell.Open(WindowKind.Settings);
        Assert.NotEqual(before, rig.Platform.Session(WindowKind.Settings));
    }

    [Fact] // PER02 measure sequence: three windows open at once, closed in turn → one release timer that releases
    public void Closing_several_open_windows_starts_one_release_timer()
    {
        var provider = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Echo("T:"));
        var config = new ConfigSnapshot(1, 1, 1, 1, TimeSpan.FromSeconds(30));
        using var rig = new Rig(translateReady: true, sessions: _ => new TranslationSession([provider], new TranslationSessionOptions(config, 1), new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(), new RecordingUsage()));
        foreach (var kind in new[] { WindowKind.Settings, WindowKind.Main, WindowKind.Tray }) { rig.Shell.Open(kind); rig.Ready(kind); rig.LastSnapshot(kind); }
        rig.Shell.OnWindowRequest(WindowKind.Settings, WindowRequest.Close);
        rig.Shell.OnWindowRequest(WindowKind.Main, WindowRequest.Close);
        rig.Shell.OnWindowRequest(WindowKind.Tray, WindowRequest.Escape);
        Assert.Empty(rig.Shell.VisibleWindows);
        var releaseTimers = rig.Platform.Timers.Where(t => t.Delay == WebViewLifecycle.ReleaseAfter).ToList();
        var (_, fire) = Assert.Single(releaseTimers);
        fire();
        Assert.Contains("release", rig.Platform.Calls);
    }

    [Fact] // J03 window part: "close exits" quits from the main window; minimize only hides
    public void Close_action_exit_quits_and_minimize_hides()
    {
        using var rig = new Rig();
        rig.Shell.Open(WindowKind.Settings);
        rig.Shell.OnWindowRequest(WindowKind.Settings, WindowRequest.Minimize);
        Assert.False(rig.Platform.Exited);
        var s = rig.Settings.State;
        rig.Settings.Save(s.Effective with { General = s.Effective.General with { CloseAction = CloseAction.Exit } }, s.Revision, s.FileHash);
        rig.Shell.Open(WindowKind.Settings);
        rig.Shell.OnWindowRequest(WindowKind.Settings, WindowRequest.Close);
        Assert.True(rig.Platform.Exited);
    }

    [Fact] // ARCHITECTURE 6: streaming updates are coalesced to ≤30/s, terminal states go out at once
    public void Streaming_patches_are_coalesced()
    {
        using var rig = new Rig();
        rig.Shell.Open(WindowKind.Main);
        rig.Ready(WindowKind.Main);
        rig.LastSnapshot(WindowKind.Main);
        var card = new CardSnapshot("svc", "svc", CardState.Streaming, false, "");
        for (int i = 1; i <= 10; i++) rig.Shell.OnCardPatch(new CardPatch(i, 1, card with { Text = new string('x', i) }));
        Assert.DoesNotContain(rig.Platform.Posted, p => p.Envelope.Kind == UiMessageKind.Patch);
        var (delay, flush) = Assert.Single(rig.Platform.Timers);
        Assert.Equal(ShellCoordinator.PatchInterval, delay);
        flush();
        var patches = rig.Platform.Posted.Where(p => p.Envelope.Kind == UiMessageKind.Patch).Select(p => p.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CardPatch)!).ToList();
        Assert.Equal(10, Assert.Single(patches).Card.Text.Length);
        rig.Shell.OnCardPatch(new CardPatch(11, 1, card with { State = CardState.Ready, Text = "done" }));
        Assert.Equal("done", rig.Platform.Posted.Last().Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CardPatch)!.Card.Text);
        var sequences = rig.Platform.Posted.Where(p => p.Kind == WindowKind.Main).Select(p => p.Envelope.Sequence).ToList();
        Assert.Equal(sequences.Order(), sequences); // strictly increasing per window
        Assert.Equal(sequences.Count, sequences.Distinct().Count());
    }

    [Fact] // UI04: with a working translation session the main window submits and receives results; copy uses the host clipboard
    public async Task Main_window_submits_and_copies_through_the_host()
    {
        var provider = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Echo("T:"));
        var config = new ConfigSnapshot(1, 1, 1, 1, TimeSpan.FromSeconds(30));
        using var rig = new Rig(translateReady: true, sessions: _ => new TranslationSession([provider], new TranslationSessionOptions(config, 1), new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(), new RecordingUsage()));
        rig.Shell.Open(WindowKind.Main);
        rig.Ready(WindowKind.Main);
        rig.LastSnapshot(WindowKind.Main);
        Assert.True(rig.Result(rig.Command(WindowKind.Main, UiCommands.SubmitText, new { text = "hello" })).Ok);
        Assert.Contains(rig.Platform.Posted, p => p.Envelope.Name == "translation");
        for (int i = 0; i < 200 && !rig.Platform.Posted.Any(p => p.Envelope.Kind == UiMessageKind.Patch && p.Envelope.Payload!.Value.GetProperty("card").GetProperty("text").GetString() == "T:hello"); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Contains(rig.Platform.Posted, p => p.Envelope.Kind == UiMessageKind.Patch && p.Envelope.Payload!.Value.GetProperty("card").GetProperty("text").GetString() == "T:hello");
        Assert.True(rig.Result(rig.Command(WindowKind.Main, UiCommands.CopyText, new { text = "T:hello" })).Ok);
        Assert.Equal("T:hello", rig.Platform.Clipboard);
        Assert.Equal("text-length", rig.Result(rig.Command(WindowKind.Main, UiCommands.SubmitText, new { text = "   " })).Error);
    }

    private sealed class SelectingReader : ISelectionReader
    {
        public int Snapshots;
        public ForegroundSnapshot Snapshot() { Snapshots++; return FakeClipboardPlatform.Target(); }
        public Task<SelectionResult> ReadAsync(ForegroundSnapshot snapshot, CancellationToken cancellationToken)
            => Task.FromResult(new SelectionResult(SelectionStatus.Selected, "picked", "uia", null, 96, 1, 1));
        public void Prime(nint window) { }
    }

    [Fact] // F08.2: the default shared Alt+D chord registers once and captures with the shared arbitration
    public async Task Selection_hotkey_snapshots_synchronously_and_raises_the_current_capture()
    {
        var reader = new SelectingReader();
        var capture = new CaptureCoordinator(reader, new ClipboardBorrower(new FakeClipboardPlatform()), () => false);
        using var rig = new Rig(capture: capture);
        var captured = new TaskCompletionSource<CaptureOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Shell.Captured += o => captured.TrySetResult(o);
        Assert.True(rig.Shell.HotkeyResults.ContainsKey("selectionTranslate"));
        Assert.False(rig.Shell.HotkeyResults.ContainsKey("clipboardTranslate")); // shared chord: one registration
        rig.Shell.OnHotkey("selectionTranslate");
        Assert.Equal(1, reader.Snapshots);
        var outcome = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal((CaptureTrigger.Shared, CaptureStatus.Text, "picked"), (outcome.Trigger, outcome.Status, outcome.Text));
    }

    private sealed class FakeDetector(IReadOnlyList<string> candidates) : ILanguageDetector
    {
        public int Calls;
        public Task<IReadOnlyList<string>> DetectAsync(string text, CancellationToken cancellationToken) { Calls++; return Task.FromResult(candidates); }
    }

    [Fact] // ARCHITECTURE 7: pure Han/Latin text takes the fast Unicode path and never calls the native detector
    public void Pure_script_text_skips_the_language_detector()
    {
        var provider = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Echo("T:"));
        var config = new ConfigSnapshot(1, 1, 1, 1, TimeSpan.FromSeconds(30));
        var detector = new FakeDetector(["zh-Hans"]);
        using var rig = new Rig(translateReady: true, sessions: _ => new TranslationSession([provider], new TranslationSessionOptions(config, 1), new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(), new RecordingUsage()), languageDetector: detector);
        rig.Shell.Open(WindowKind.Main);
        rig.Ready(WindowKind.Main);
        rig.LastSnapshot(WindowKind.Main);
        Assert.True(rig.Result(rig.Command(WindowKind.Main, UiCommands.SubmitText, new { text = "hello world" })).Ok);
        var snapshot = rig.TranslationEvent();
        Assert.Equal(("en", "zh-Hans"), (snapshot.From, snapshot.To));
        Assert.Equal(0, detector.Calls);
    }

    [Fact] // ARCHITECTURE 7: mixed-script text falls to the native detector; a confident candidate picks direction
    public void Mixed_script_text_uses_the_language_detector_result()
    {
        var provider = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Echo("T:"));
        var config = new ConfigSnapshot(1, 1, 1, 1, TimeSpan.FromSeconds(30));
        var detector = new FakeDetector(["zh-Hans"]);
        using var rig = new Rig(translateReady: true, sessions: _ => new TranslationSession([provider], new TranslationSessionOptions(config, 1), new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(), new RecordingUsage()), languageDetector: detector);
        rig.Shell.Open(WindowKind.Main);
        rig.Ready(WindowKind.Main);
        rig.LastSnapshot(WindowKind.Main);
        Assert.True(rig.Result(rig.Command(WindowKind.Main, UiCommands.SubmitText, new { text = "混合 mixed 文本" })).Ok);
        var snapshot = rig.TranslationEvent();
        Assert.Equal(("zh-Hans", "en"), (snapshot.From, snapshot.To));
        Assert.Equal(1, detector.Calls);
    }

    [Fact] // ARCHITECTURE 7: detection failure/no confident candidate falls back to the configured default, never blocks
    public void Detector_failure_falls_back_to_the_default_source_language()
    {
        var provider = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Echo("T:"));
        var config = new ConfigSnapshot(1, 1, 1, 1, TimeSpan.FromSeconds(30));
        var detector = new FakeDetector([]); // no candidate the host can map (e.g. unsupported language)
        using var rig = new Rig(translateReady: true, sessions: _ => new TranslationSession([provider], new TranslationSessionOptions(config, 1), new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(), new RecordingUsage()), languageDetector: detector);
        rig.Shell.Open(WindowKind.Main);
        rig.Ready(WindowKind.Main);
        rig.LastSnapshot(WindowKind.Main);
        Assert.True(rig.Result(rig.Command(WindowKind.Main, UiCommands.SubmitText, new { text = "混合 mixed 文本" })).Ok);
        var snapshot = rig.TranslationEvent();
        Assert.Equal((rig.Config.State.Effective.General.SourceLanguage, rig.Config.State.Effective.General.TargetLanguage), (snapshot.From, snapshot.To));
    }
}

public class Win32ShellTests
{
    [Fact] // F03.1: a second instance is refused and wakes only the instance with the same name
    public void Single_instance_wakes_only_its_own_instance()
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { Body(); } catch (Exception e) { failure = e; } });
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;

        static void Body()
        {
            string mine = $"Su-Su.Test.{Guid.NewGuid():N}", other = $"Su-Su.Test.{Guid.NewGuid():N}";
            using var first = Susu.Windows.Shell.SingleInstance.TryAcquire(mine);
            Assert.NotNull(first);
            using var dispatcher = new Susu.Windows.Shell.UiDispatcher(mine);
            using var otherDispatcher = new Susu.Windows.Shell.UiDispatcher(other);
            int woken = 0, wrong = 0;
            dispatcher.ActivateRequested += () => woken++;
            otherDispatcher.ActivateRequested += () => wrong++;
            var sender = new Thread(() => Assert.Null(Susu.Windows.Shell.SingleInstance.TryAcquire(mine))); // second launch
            sender.Start();
            while (sender.IsAlive) { PumpOnce(); Thread.Sleep(5); }
            for (int i = 0; i < 20; i++) { PumpOnce(); Thread.Sleep(5); }
            Assert.Equal((1, 0), (woken, wrong));
        }
    }

    private static void PumpOnce()
    {
        while (PeekMessage(out var message, 0, 0, 0, 1)) { TranslateMessage(message); DispatchMessage(message); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Msg { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public int X, Y, Private; }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out Msg message, nint hwnd, uint min, uint max, uint remove);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool TranslateMessage(in Msg message);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(in Msg message);

    [Fact] // CFG05: RegisterHotKey success and a visible failure when the chord is already taken
    public void Hotkey_registration_reports_conflicts_with_other_owners()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Body(); } catch (Exception e) { failure = e; }
        });
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;

        static void Body()
        {
            using var dispatcher = new Susu.Windows.Shell.UiDispatcher();
            using var db = Database.Open(Path.Combine(Path.GetTempPath(), "susu-tests", Guid.NewGuid().ToString("N"), "h.db"));
            using var first = new Susu.Windows.Shell.WindowPlatform(dispatcher, new Susu.Windows.Shell.UiHosting("ui", "wv", false), new WindowStateRepository(db), () => "en");
            using var second = new Susu.Windows.Shell.WindowPlatform(dispatcher, new Susu.Windows.Shell.UiHosting("ui", "wv", false), new WindowStateRepository(db), () => "en");
            var chord = new Dictionary<string, string> { ["inputTranslate"] = "Ctrl+Alt+Shift+F13" };
            Assert.True(first.RegisterHotkeys(chord)["inputTranslate"]);
            Assert.False(second.RegisterHotkeys(chord)["inputTranslate"]);
            Assert.False(first.RegisterHotkeys(new Dictionary<string, string> { ["x"] = "NotAChord" })["x"]);
        }
    }
}
