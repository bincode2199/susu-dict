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

/// <summary>F08.3 placement (UI01): the floating window and the failure bar across DPI, negative coordinates and small work areas.</summary>
public class SelectionPlacementTests
{
    private static readonly MonitorInfo Primary = new("m1", new PixelRect(0, 0, 1920, 1040), 96);
    private static readonly MonitorInfo Left150 = new("m2", new PixelRect(-2880, 0, 2880, 1560), 144);
    private static readonly MonitorInfo Hi200 = new("m3", new PixelRect(1920, 0, 3840, 2080), 192);
    private static readonly WindowSpec Float = WindowSpec.For(WindowKind.Selection);
    private static readonly WindowSpec Bar = WindowSpec.For(WindowKind.Error);

    [Theory] // UI01, PLAN 1.4.1: no remembered position → the float centers on the pointer's monitor at that monitor's DPI
    [InlineData(96, 380, 420)]
    [InlineData(144, 570, 630)]
    [InlineData(192, 760, 840)]
    public void Float_centers_with_the_monitor_dpi_when_nothing_is_remembered(int dpi, int width, int height)
    {
        var monitor = new MonitorInfo("m", new PixelRect(0, 0, 4000, 3000), dpi);
        var rect = PlacementPolicy.Compute(Float, [monitor], monitor, null);
        Assert.Equal(new PixelRect((4000 - width) / 2, (3000 - height) / 2, width, height), rect);
    }

    [Fact] // UI01: the remembered float position on a negative-coordinate 150 % monitor is reused with that DPI
    public void Float_reopens_at_its_remembered_position_on_a_left_monitor()
    {
        var rect = PlacementPolicy.Compute(Float, [Primary, Left150], Primary, (-1500, 200));
        Assert.Equal(new PixelRect(-1500, 200, 570, 630), rect);
    }

    [Fact] // UI01: remembered position no longer on any monitor (unplugged) → invalid → centered on the pointer's monitor
    public void Float_with_an_invalid_remembered_position_centers()
    {
        var rect = PlacementPolicy.Compute(Float, [Primary, Hi200], Hi200, (-1500, 200));
        Assert.Equal(new PixelRect(1920 + (3840 - 760) / 2, (2080 - 840) / 2, 760, 840), rect);
    }

    [Theory] // DESIGN 9: the failure bar sits just below-right of the pointer, sized for the pointer's monitor
    [InlineData(96, 460, 34, 8, 16)]
    [InlineData(144, 690, 51, 12, 24)]
    [InlineData(192, 920, 68, 16, 32)]
    public void Failure_bar_follows_the_pointer_at_each_dpi(int dpi, int width, int height, int dx, int dy)
    {
        var monitor = new MonitorInfo("m", new PixelRect(0, 0, 4000, 3000), dpi);
        var rect = PlacementPolicy.NearPointer(Bar, [monitor], monitor, (1000, 1000));
        Assert.Equal(new PixelRect(1000 + dx, 1000 + dy, width, height), rect);
    }

    [Fact] // UI01: a pointer on a negative-coordinate monitor keeps the bar on that monitor (clamped at its right edge)
    public void Failure_bar_on_a_negative_monitor_stays_inside_its_work_area()
    {
        var rect = PlacementPolicy.NearPointer(Bar, [Primary, Left150], Primary, (-100, 300));
        Assert.Equal(new PixelRect(-690, 324, 690, 51), rect);
        Assert.True(rect.Right <= Left150.WorkArea.Right);
    }

    [Fact] // the bar flips above the pointer near the bottom of the work area instead of covering the taskbar
    public void Failure_bar_flips_above_the_pointer_at_the_bottom()
    {
        var rect = PlacementPolicy.NearPointer(Bar, [Primary], Primary, (400, 1030));
        Assert.Equal(1030 - 8 - 34, rect.Y);
        Assert.True(rect.Bottom <= Primary.WorkArea.Bottom);
    }

    [Fact] // UI01: an invalid pointer position (outside every work area) centers the bar on the fallback monitor
    public void Failure_bar_with_an_invalid_pointer_centers()
    {
        var rect = PlacementPolicy.NearPointer(Bar, [Primary], Primary, (-5000, -5000));
        Assert.Equal(new PixelRect((1920 - 460) / 2, (1040 - 34) / 2, 460, 34), rect);
    }

    [Fact] // UI01: a work area narrower than the bar shrinks it to fit rather than overflowing
    public void Failure_bar_shrinks_on_a_small_work_area()
    {
        var small = new MonitorInfo("s", new PixelRect(0, 0, 400, 300), 96);
        var rect = PlacementPolicy.NearPointer(Bar, [small], small, (390, 100));
        Assert.Equal(new PixelRect(0, 116, 400, 34), rect);
    }

    [Theory] // UI01: auto height follows content up to work height − 32 DIP; taller content scrolls inside the page
    [InlineData(96, 300, 300)]
    [InlineData(96, 5000, 1040 - 32)]
    [InlineData(192, 300, 600)]
    [InlineData(192, 5000, 2080 - 64)]
    public void Float_height_fits_content_within_the_work_area(int dpi, int contentDip, int expectedHeight)
    {
        var monitor = new MonitorInfo("m", new PixelRect(0, 0, dpi == 96 ? 1920 : 3840, dpi == 96 ? 1040 : 2080), dpi);
        var current = new PixelRect(100, 50, Dip.ToPixels(380, dpi), Dip.ToPixels(420, dpi));
        var rect = PlacementPolicy.FitHeight(Float, current, monitor, contentDip);
        Assert.Equal(expectedHeight, rect.Height);
        Assert.True(rect.Bottom <= monitor.WorkArea.Bottom);
    }

    [Fact] // UI01: growing near the bottom moves the window up (still wholly reachable); left/top kept otherwise
    public void Float_that_grows_near_the_bottom_moves_up()
    {
        var rect = PlacementPolicy.FitHeight(Float, new PixelRect(-2000, 1200, 570, 630), Left150, 600);
        Assert.Equal(new PixelRect(-2000, 1560 - 900, 570, 900), rect);
    }

    [Fact] // the failure bar has no float margin: two lines are exactly 68 DIP
    public void Failure_bar_fits_two_lines()
        => Assert.Equal(68, PlacementPolicy.FitHeight(Bar, new PixelRect(10, 10, 460, 34), Primary, 68).Height);
}

/// <summary>F08.3 shell behaviour: capture → floating window / failure bar, tray entries, hotkey sharing (UI03, CFG05).</summary>
public sealed class SelectionWindowTests
{
    private sealed class GatedReader : ISelectionReader
    {
        public int Snapshots;
        public TaskCompletionSource<SelectionResult> Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ForegroundSnapshot Snapshot() { Snapshots++; return FakeClipboardPlatform.Target(); }
        public Task<SelectionResult> ReadAsync(ForegroundSnapshot snapshot, CancellationToken cancellationToken) => Next.Task.WaitAsync(cancellationToken);
        public void Prime(nint window) { }
        public static SelectionResult Selected(string text) => new(SelectionStatus.Selected, text, "uia", new ScreenRect(10, 10, 50, 30), 144, 1, 1);
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly SettingsStore Settings;
        public readonly ConfigService Config;
        public readonly GatedReader Reader = new();
        public readonly FakeClipboardPlatform Clipboard = new();
        public readonly CaptureCoordinator Capture;
        public readonly ShellCoordinator Shell;
        public bool Borrow;
        private long counter;
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        public Rig(Action<Dictionary<string, string>>? hotkeys = null)
        {
            Settings = new SettingsStore(Root.Paths, new ManualClock());
            Config = new ConfigService(Settings, new SecretStore(Root.Paths.Secrets, new XorProtector()));
            if (hotkeys is not null)
            {
                var s = Config.State.Effective;
                var chords = new Dictionary<string, string>(s.Hotkeys.Chords, StringComparer.Ordinal);
                hotkeys(chords);
                Assert.True(Config.Save(s with { Hotkeys = new HotkeySettings(chords) }, Config.State.Revision, Config.State.FileHash).Status == SaveStatus.Saved);
            }
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Selection, FeatureState.Available, null, [Capability.Translate]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Clipboard, FeatureState.Available, null, [Capability.Translate]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Ocr, FeatureState.Available, null, []));
            Capture = new CaptureCoordinator(Reader, new ClipboardBorrower(Clipboard), () => Borrow);
            var provider = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Echo("T:"));
            var snapshot = new ConfigSnapshot(1, 1, 1, 1, TimeSpan.FromSeconds(30));
            Shell = new ShellCoordinator(Platform, Config, features, c => c == Capability.Translate, new ShellOptions(false, false),
                _ => new TranslationSession([provider], new TranslationSessionOptions(snapshot, 1), new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(), new RecordingUsage()),
                capture: Capture);
            Shell.Start();
        }

        public Task<CaptureOutcome> NextPresentation()
        {
            var done = new TaskCompletionSource<CaptureOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<CaptureOutcome>? handler = null;
            handler = o => { Shell.CapturePresented -= handler; done.TrySetResult(o); };
            Shell.CapturePresented += handler;
            return done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

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

        public UiSnapshot Snapshot(WindowKind kind)
        {
            for (int i = 0; i < 500; i++)
            {
                var hit = Platform.Posted.LastOrDefault(p => p.Kind == kind && p.Envelope.Kind == UiMessageKind.Snapshot);
                if (hit.Envelope is not null) return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.UiSnapshot)!;
                Thread.Sleep(5);
            }
            throw new TimeoutException("no snapshot");
        }

        public IEnumerable<string> Shows => Platform.Calls.Where(c => c.StartsWith("show:", StringComparison.Ordinal));

        public void Dispose() { Settings.Dispose(); Root.Dispose(); }
    }

    [Fact] // UI03/SEL02: nothing is shown or activated while the capture runs; the float activates only after it finished
    public async Task No_window_is_shown_or_activated_before_the_capture_completes()
    {
        using var rig = new Rig();
        rig.Shell.OnHotkey("selectionTranslate");
        Assert.Equal(1, rig.Reader.Snapshots); // the foreground snapshot was taken synchronously in the hotkey handler
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(rig.Shows);
        var presented = rig.NextPresentation();
        rig.Reader.Next.SetResult(GatedReader.Selected("hello"));
        var outcome = await presented;
        Assert.Equal(CaptureStatus.Text, outcome.Status);
        Assert.Equal(["show:Selection:True"], rig.Shows);
        Assert.DoesNotContain(rig.Platform.Calls, c => c.StartsWith("show:Main", StringComparison.Ordinal));
    }

    [Fact] // the captured text is translated in the float's own session and reaches the float's page, not the main window
    public async Task Captured_text_is_translated_in_the_selection_window()
    {
        using var rig = new Rig();
        rig.Shell.OnHotkey("selectionTranslate");
        var presented = rig.NextPresentation();
        rig.Reader.Next.SetResult(GatedReader.Selected("hello"));
        await presented;
        rig.Ready(WindowKind.Selection);
        var snapshot = rig.Snapshot(WindowKind.Selection);
        Assert.Equal(new CaptureView(1, "selection", false), snapshot.Capture);
        Assert.Equal("hello", snapshot.Translation!.SourceText);
        // The result reaches the float either in its snapshot (fast provider) or as a card patch afterwards.
        bool Translated() => rig.Snapshot(WindowKind.Selection).Translation!.Cards.Any(c => c.Text == "T:hello")
            || rig.Platform.Posted.Any(p => p.Kind == WindowKind.Selection && p.Envelope.Kind == UiMessageKind.Patch && p.Envelope.Payload!.Value.GetProperty("card").GetProperty("text").GetString() == "T:hello");
        for (int i = 0; i < 200 && !Translated(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(Translated());
        Assert.DoesNotContain(rig.Platform.Posted, p => p.Kind == WindowKind.Main);
    }

    [Theory] // DESIGN 9/Error 01: the two failure texts are not mixed; a failure shows the bar, not a window, without activation
    [InlineData(false, "capture.notSupportedBorrowOff", "settings")]
    [InlineData(true, "capture.notSupported", null)]
    public async Task Unsupported_capture_shows_the_failure_bar(bool borrow, string key, string? link)
    {
        // Separate chords: a selection failure is a failure (the shared chord would fall back to the clipboard).
        using var rig = new Rig(h => h["clipboardTranslate"] = "Alt+Shift+D") { Borrow = borrow };
        rig.Clipboard.FailStart = true; // borrow on: the helper cannot start → the borrow fails too
        rig.Shell.OnHotkey("selectionTranslate");
        var presented = rig.NextPresentation();
        rig.Reader.Next.SetResult(SelectionResult.Failure(SelectionStatus.Unsupported));
        var outcome = await presented;
        Assert.Equal(CaptureStatus.Failed, outcome.Status);
        Assert.Equal(["show:Error:False"], rig.Shows);
        rig.Ready(WindowKind.Error);
        var bar = rig.Snapshot(WindowKind.Error).ErrorBar!;
        var line = Assert.Single(bar.Lines);
        Assert.Equal((key, link), (line.Key, line.Link));
        // 4 s later the bar hides itself.
        var (delay, hide) = rig.Platform.Timers.Last(t => t.Delay == ShellCoordinator.ErrorBarLifetime);
        hide();
        Assert.Contains("hide:Error", rig.Platform.Calls);
    }

    [Fact] // the user moved on (focus changed): no window and no bar
    public async Task Focus_change_shows_nothing()
    {
        using var rig = new Rig(h => h["clipboardTranslate"] = "Alt+Shift+D");
        rig.Shell.OnHotkey("selectionTranslate");
        var presented = rig.NextPresentation();
        rig.Reader.Next.SetResult(SelectionResult.Failure(SelectionStatus.FocusChanged));
        await presented;
        Assert.Empty(rig.Shows);
    }

    [Fact] // PLAN 6.1: the shared chord falls back to clipboard text; nothing there → the float opens empty for input
    public async Task Shared_chord_with_nothing_to_read_opens_an_empty_float()
    {
        using var rig = new Rig();
        rig.Clipboard.NextHelper = () => new FakeClipboardHelper { Text = _ => Task.FromResult(new ClipboardText(ClipboardTextStatus.NoText, "")) };
        rig.Shell.OnHotkey("selectionTranslate");
        var presented = rig.NextPresentation();
        rig.Reader.Next.SetResult(SelectionResult.Failure(SelectionStatus.Empty));
        var outcome = await presented;
        Assert.Equal(CaptureStatus.Empty, outcome.Status);
        Assert.Equal(["show:Selection:True"], rig.Shows);
        rig.Ready(WindowKind.Selection);
        var snapshot = rig.Snapshot(WindowKind.Selection);
        Assert.True(snapshot.Capture!.Empty);
        Assert.Equal("", snapshot.Translation!.SourceText);
    }

    [Fact] // PLAN 1.4 / D-51: the tray offers clipboard translation and never selection capture
    public void Tray_menu_offers_clipboard_but_never_selection()
    {
        using var rig = new Rig();
        var ids = rig.Shell.TrayModel().Items.Select(i => i.Id).ToList();
        Assert.Contains("clipboard", ids);
        Assert.DoesNotContain(ids, id => id.Contains("selection", StringComparison.OrdinalIgnoreCase));
        Assert.True(rig.Shell.TrayModel().Items.Single(i => i.Id == "clipboard").Enabled);
    }

    [Fact] // the tray's clipboard entry reads the clipboard: no foreground snapshot, no Ctrl+C, the float shows "clipboard"
    public async Task Tray_clipboard_entry_reads_the_clipboard_without_capturing_the_selection()
    {
        using var rig = new Rig();
        rig.Shell.OnTrayMenu();
        rig.Ready(WindowKind.Tray);
        rig.Snapshot(WindowKind.Tray);
        var presented = rig.NextPresentation();
        Assert.True(rig.Result(rig.Command(WindowKind.Tray, UiCommands.TrayOpen, new { id = "clipboard" })).Ok);
        var outcome = await presented;
        Assert.Equal((CaptureTrigger.Clipboard, CaptureStatus.Text, "copied"), (outcome.Trigger, outcome.Status, outcome.Text));
        Assert.Equal(0, rig.Reader.Snapshots);
        Assert.Equal(0, rig.Clipboard.Copies);
        Assert.Contains("hide:Tray", rig.Platform.Calls);
        rig.Ready(WindowKind.Selection);
        Assert.Equal("clipboard", rig.Snapshot(WindowKind.Selection).Capture!.Origin);
    }

    [Fact] // J01: a second hotkey before the first capture finishes supersedes it; only the newer one is presented
    public async Task Newer_capture_supersedes_the_pending_one()
    {
        using var rig = new Rig();
        var first = rig.Reader.Next;
        rig.Shell.OnHotkey("selectionTranslate");
        rig.Reader.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var presented = rig.NextPresentation();
        rig.Shell.OnHotkey("selectionTranslate");
        first.TrySetResult(GatedReader.Selected("old")); // cancelled already; must not show
        rig.Reader.Next.SetResult(GatedReader.Selected("new"));
        Assert.Equal("new", (await presented).Text);
        Assert.Single(rig.Shows);
    }

    [Fact] // restore failure (C06) is its own bar line even when the text was captured
    public async Task Restore_notice_shows_a_bar_next_to_the_window()
    {
        using var rig = new Rig(h => h["clipboardTranslate"] = "Alt+Shift+D") { Borrow = true };
        rig.Clipboard.NextHelper = () => new FakeClipboardHelper { Restore = (_, _, _) => Task.FromResult(new ClipboardRestoreResult(ClipboardRestoreStatus.Busy, 0)) };
        rig.Shell.OnHotkey("selectionTranslate");
        var presented = rig.NextPresentation();
        rig.Reader.Next.SetResult(SelectionResult.Failure(SelectionStatus.Unsupported));
        var outcome = await presented;
        Assert.Equal(CaptureStatus.Text, outcome.Status);
        Assert.NotNull(outcome.Notice);
        Assert.Contains("show:Error:False", rig.Shows);
        Assert.Contains("show:Selection:True", rig.Shows);
        rig.Ready(WindowKind.Error);
        Assert.Equal("capture.restoreFailed", Assert.Single(rig.Snapshot(WindowKind.Error).ErrorBar!.Lines).Key);
    }

    [Fact] // UI01: the page reports its content height; the host resizes (clamping is PlacementPolicy.FitHeight)
    public async Task Float_page_content_height_resizes_the_window()
    {
        using var rig = new Rig();
        rig.Shell.OnHotkey("selectionTranslate");
        var presented = rig.NextPresentation();
        rig.Reader.Next.SetResult(GatedReader.Selected("hello"));
        await presented;
        rig.Ready(WindowKind.Selection);
        rig.Snapshot(WindowKind.Selection);
        Assert.True(rig.Result(rig.Command(WindowKind.Selection, UiCommands.FitContent, new { heightDip = 512 })).Ok);
        Assert.Contains("fit:Selection:512", rig.Platform.Calls);
    }

    [Fact] // "open in main window" cancels the float's task and translates the text in the main window
    public async Task Open_in_main_hands_the_text_to_the_main_window()
    {
        using var rig = new Rig();
        rig.Shell.OnHotkey("selectionTranslate");
        var presented = rig.NextPresentation();
        rig.Reader.Next.SetResult(GatedReader.Selected("hello"));
        await presented;
        rig.Ready(WindowKind.Selection);
        rig.Snapshot(WindowKind.Selection);
        Assert.True(rig.Result(rig.Command(WindowKind.Selection, UiCommands.OpenInMain, new { text = "hello" })).Ok);
        Assert.Contains("hide:Selection", rig.Platform.Calls);
        Assert.Contains("show:Main:True", rig.Platform.Calls);
        rig.Ready(WindowKind.Main);
        Assert.Equal("hello", rig.Snapshot(WindowKind.Main).Translation!.SourceText);
    }

    [Theory] // ARCHITECTURE 6: the new window commands are whitelisted per window
    [InlineData(WindowKind.Selection, UiCommands.OpenInMain, true)]
    [InlineData(WindowKind.Main, UiCommands.OpenInMain, false)]
    [InlineData(WindowKind.Settings, UiCommands.FitContent, false)]
    [InlineData(WindowKind.Error, UiCommands.FitContent, true)]
    [InlineData(WindowKind.Error, UiCommands.OpenSettings, true)]
    [InlineData(WindowKind.Error, UiCommands.SubmitText, false)]
    public void New_window_commands_are_whitelisted(WindowKind window, string command, bool allowed)
        => Assert.Equal(allowed, UiCommands.IsAllowed(window, command));

    [Fact] // CFG05: only selection and clipboard may share a chord; they share one RegisterHotKey
    public void Selection_and_clipboard_share_one_registration()
    {
        using var rig = new Rig(h => h["clipboardTranslate"] = h["selectionTranslate"]);
        Assert.True(rig.Platform.Registered.ContainsKey("selectionTranslate"));
        Assert.False(rig.Platform.Registered.ContainsKey("clipboardTranslate"));
        var view = rig.Shell.ProjectSettings(rig.Config.State).Hotkeys;
        Assert.Equal("ok", view.Single(h => h.Action == "selectionTranslate").State);
        Assert.Equal("ok", view.Single(h => h.Action == "clipboardTranslate").State);
    }

    [Fact] // CFG05: any other shared chord is a conflict; the host refuses to save it and keeps the working registrations
    public void Other_shared_chords_conflict_and_are_refused()
    {
        var chords = new HotkeySettings(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["selectionTranslate"] = "Alt+D", ["clipboardTranslate"] = "Alt+D", ["inputTranslate"] = "Alt+A", ["ocrTranslate"] = "Alt+A",
        });
        var conflict = Assert.Single(chords.Conflicts());
        Assert.Equal(["inputTranslate", "ocrTranslate"], conflict.Actions);
        var three = new HotkeySettings(new Dictionary<string, string>(StringComparer.Ordinal) { ["selectionTranslate"] = "Alt+D", ["clipboardTranslate"] = "Alt+D", ["ocrTranslate"] = "Alt+D" });
        Assert.Equal(3, Assert.Single(three.Conflicts()).Actions.Length); // a third action breaks the allowed pair

        using var rig = new Rig();
        var s = rig.Config.State.Effective;
        var proposed = new Dictionary<string, string>(s.Hotkeys.Chords, StringComparer.Ordinal) { ["ocrTranslate"] = s.Hotkeys.Chords["inputTranslate"] };
        Assert.Equal(SaveStatus.Invalid, rig.Config.Save(s with { Hotkeys = new HotkeySettings(proposed) }, rig.Config.State.Revision, rig.Config.State.FileHash).Status);
        Assert.True(rig.Platform.Registered.ContainsKey("inputTranslate")); // the working registration is untouched
    }

    [Fact] // CFG05: a RegisterHotKey refusal is visible on the row, and on clipboard too when it shares selection's chord
    public void Register_failure_is_visible_on_both_shared_rows()
    {
        using var rig = new Rig();
        rig.Platform.HotkeyOutcome["selectionTranslate"] = false;
        var s = rig.Config.State.Effective;
        var chords = new Dictionary<string, string>(s.Hotkeys.Chords, StringComparer.Ordinal) { ["selectionTranslate"] = "Alt+F9", ["clipboardTranslate"] = "Alt+F9" };
        Assert.True(rig.Config.Save(s with { Hotkeys = new HotkeySettings(chords) }, rig.Config.State.Revision, rig.Config.State.FileHash).Status == SaveStatus.Saved);
        var view = rig.Shell.ProjectSettings(rig.Config.State).Hotkeys;
        Assert.Equal("failed", view.Single(h => h.Action == "selectionTranslate").State);
        Assert.Equal("failed", view.Single(h => h.Action == "clipboardTranslate").State);
    }
}
