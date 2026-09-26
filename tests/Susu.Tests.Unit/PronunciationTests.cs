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

/// <summary>
/// F10.2 bar placement (PLAN 6.5 "浮条定位优先用 UIA 的 BoundingRectangle，拿不到回落鼠标位置", DESIGN 9, SEL03): next to
/// the selection with the DPI of its monitor, flipped and clamped inside the work area; the pointer otherwise.
/// </summary>
public class SpeechBarPlacementTests
{
    private static readonly MonitorInfo Primary = new("m1", new PixelRect(0, 0, 1920, 1040), 96);
    private static readonly MonitorInfo Left150 = new("m2", new PixelRect(-2880, 0, 2880, 1560), 144);
    private static readonly WindowSpec Bar = WindowSpec.For(WindowKind.Speech);

    [Fact]
    public void Bar_width_follows_the_design_squares()
    {
        Assert.Equal(71, WindowSpec.SpeechBarWidthDip(1)); // 5 + 26 status + 9 divider + 26 + 5
        Assert.Equal(161, WindowSpec.SpeechBarWidthDip(4));
        Assert.Equal((34, false, false), (Bar.HeightDip, Bar.Activates, Bar.RemembersPosition));
    }

    [Fact]
    public void Bar_sits_left_aligned_just_below_the_selection()
        => Assert.Equal(new PixelRect(400, 236, 101, 34),
            PlacementPolicy.NearSelection(Bar, [Primary], Primary, new PixelRect(400, 200, 300, 30), (5, 5), 101));

    [Fact] // SEL03: a selection on a 150 % monitor left of the primary sizes the bar with that monitor's DPI
    public void Bar_uses_the_dpi_of_the_selection_monitor()
    {
        var rect = PlacementPolicy.NearSelection(Bar, [Primary, Left150], Primary, new PixelRect(-2000, 300, 450, 45), (100, 100), 71);
        Assert.Equal(new PixelRect(-2000, 345 + 9, 107, 51), rect); // 71 and 34 DIP at 144 DPI, 6 DIP gap = 9 px
    }

    [Fact]
    public void Bar_flips_above_a_selection_at_the_bottom_and_stays_inside_the_work_area()
    {
        var rect = PlacementPolicy.NearSelection(Bar, [Primary], Primary, new PixelRect(1880, 1000, 30, 20), (5, 5), 101);
        Assert.Equal(new PixelRect(1920 - 101, 1000 - 6 - 34, 101, 34), rect);
    }

    [Fact]
    public void Without_a_usable_selection_rect_the_bar_goes_to_the_pointer()
    {
        var pointer = PlacementPolicy.NearPointer(Bar with { WidthDip = 71 }, [Primary], Primary, (500, 500));
        Assert.Equal(pointer, PlacementPolicy.NearSelection(Bar, [Primary], Primary, null, (500, 500), 71));
        Assert.Equal(pointer, PlacementPolicy.NearSelection(Bar, [Primary], Primary, new PixelRect(10, 10, 0, 0), (500, 500), 71));
        Assert.Equal(pointer, PlacementPolicy.NearSelection(Bar, [Primary], Primary, new PixelRect(-5000, 10, 50, 20), (500, 500), 71)); // unplugged monitor
    }

    [Fact]
    public void Fractional_uia_bounds_cover_the_whole_selection()
    {
        Assert.Equal(new PixelRect(10, 20, 91, 11), ShellCoordinator.PixelsOf(new ScreenRect(10.4, 20.6, 100.2, 30.1)));
        Assert.Null(ShellCoordinator.PixelsOf(null));
        Assert.Null(ShellCoordinator.PixelsOf(new ScreenRect(5, 5, 5, 9)));
    }

    [Fact] // SetHotkeys (F08.3): only selection and clipboard translation may share a chord; pronunciation never does
    public void Pronunciation_chord_conflicts_with_any_other_action()
    {
        var shared = new HotkeySettings(new Dictionary<string, string> { ["selectionTranslate"] = "Alt+D", ["clipboardTranslate"] = "Alt+D", ["pronounce"] = "Alt+R" });
        Assert.Empty(shared.Conflicts());
        var clash = new HotkeySettings(new Dictionary<string, string> { ["selectionTranslate"] = "Alt+D", ["clipboardTranslate"] = "Alt+D", ["pronounce"] = "Alt+D" });
        Assert.Contains("pronounce", clash.Conflicts().Single().Actions);
        Assert.Equal(CaptureTrigger.Pronounce, CaptureCoordinator.TriggerFor("pronounce", shared));
    }

    [Theory]
    [InlineData(WindowKind.Main, UiCommands.SpeakCard, true)]
    [InlineData(WindowKind.Selection, UiCommands.SpeechStop, true)]
    [InlineData(WindowKind.Speech, UiCommands.SpeechPlay, true)]
    [InlineData(WindowKind.Speech, UiCommands.SpeechStop, true)]
    [InlineData(WindowKind.Speech, UiCommands.SpeakCard, false)]
    [InlineData(WindowKind.Main, UiCommands.SpeechPlay, false)]
    [InlineData(WindowKind.Settings, UiCommands.SpeakCard, false)]
    public void Speech_commands_are_whitelisted_per_window(WindowKind window, string command, bool allowed)
        => Assert.Equal(allowed, UiCommands.IsAllowed(window, command));
}

/// <summary>
/// F10.2 shell flow with fakes: the pronunciation hotkey (capture first, then the default service, the bar without
/// activation), per-service playback and stop through the one player, card read-aloud, and TTS03 (dictionary audio only
/// from a result already shown; a collapsed, never-queried dictionary card triggers no lookup).
/// </summary>
public class PronunciationFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Clip : IAudioClip
    {
        public string Mime => "audio/wav";
        public string FilePath => "fake://clip";
        public long Bytes => 10;
        public void Dispose() { }
    }

    /// <summary>Plays until <see cref="Release"/> (or instantly when <see cref="Instant"/>), or until cancelled.</summary>
    private sealed class Sink : IAudioSink
    {
        public bool Instant = true;
        public int Played;
        private TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AudioDeviceStatus Probe() => new(true);
        public async Task PlayAsync(IAudioClip clip, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Played);
            if (Instant) return;
            await release.Task.WaitAsync(cancellationToken);
        }
        public void Release() { var done = release; release = new(TaskCreationOptions.RunContinuationsAsynchronously); done.TrySetResult(); }
    }

    private sealed class Tts(string instance, ConcurrentQueue<(string Instance, SpeakRequest Request)> log) : ITtsProvider
    {
        public string InstanceId => instance;
        public bool Native => instance == "native-sapi";
        public Task<AudioOutcome> SynthesizeAsync(SpeakCall call, CancellationToken cancellationToken)
        {
            log.Enqueue((instance, call.Request));
            return Task.FromResult<AudioOutcome>(new AudioOutcome.Ready(new Clip()));
        }
    }

    private sealed class Reader(SelectionResult result) : ISelectionReader
    {
        public int Snapshots;
        public ForegroundSnapshot Snapshot() { Snapshots++; return FakeClipboardPlatform.Target(); }
        public Task<SelectionResult> ReadAsync(ForegroundSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(result);
        public void Prime(nint window) { }
    }

    /// <summary>A service with translate and dictionary: the lookup returns <see cref="Entry"/> and is counted.</summary>
    private sealed class DictionaryService(string serviceId, DictionaryResult entry) : ITranslationProvider, IDictionaryProvider
    {
        public int Lookups;
        public string ServiceId => serviceId;
        public string DisplayName => serviceId;
        public string LimiterKey => serviceId;
        public TranslationLimits Limits => ScriptedProvider.Generous;
        public bool SupportsLanguagePair(string from, string to) => true;
        public bool DictionaryEnabled => true;
        public Task<ProviderOutcome> TranslateAsync(TranslateCall call, Func<string, ValueTask> onChunk, CancellationToken cancellationToken)
            => new ScriptedProvider(serviceId, ScriptedProvider.Generous, new Step.Echo("D:")).TranslateAsync(call, onChunk, cancellationToken);
        public Task<DictionaryOutcome> LookupAsync(DictionaryCall call, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Lookups);
            return Task.FromResult<DictionaryOutcome>(new DictionaryOutcome.Entry(entry));
        }
    }

    private static readonly DictionaryResult Good = new("good",
        [new Phonetic("us", "ɡʊd", "https://dict.example/good-us.mp3"), new Phonetic("uk", "ɡʊd")], [new PartOfSpeech("adj.", ["好的", "优良的"])]);

    private sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly SettingsStore Settings;
        public readonly ConfigService Config;
        public readonly ShellCoordinator Shell;
        public readonly Sink Sink = new();
        public readonly SpeechBackend Speech;
        public readonly ConcurrentQueue<(string Instance, SpeakRequest Request)> Spoken = new();
        public readonly ConcurrentQueue<string> AudioIds = new();
        public readonly ConcurrentQueue<(string Target, PlaybackResult Result)> Ended = new();
        private long counter;
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        public Rig(ISelectionReader? reader = null, IReadOnlyList<ITranslationProvider>? providers = null, int expanded = 2, bool pronunciationReady = true)
        {
            Settings = new SettingsStore(Root.Paths, new ManualClock());
            Config = new ConfigService(Settings, new SecretStore(Root.Paths.Secrets, new XorProtector()));
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Selection, FeatureState.Available, null, []));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Clipboard, FeatureState.Available, null, []));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Pronunciation, FeatureState.Available, null, [Capability.Tts]));
            Speech = new SpeechBackend(new SpeechPlayer(Sink),
                (_, id) => id is "native-sapi" or "microsoft-tts" ? new Tts(id, Spoken) : null,
                (id, _) => { AudioIds.Enqueue(id); return Task.FromResult<AudioOutcome>(new AudioOutcome.Ready(new Clip())); });
            var capture = new CaptureCoordinator(reader ?? new Reader(new SelectionResult(SelectionStatus.Selected, "hello world", "uia", null, 96, 1, 1)),
                new ClipboardBorrower(new FakeClipboardPlatform()), () => false);
            var config = new ConfigSnapshot(1, 1, 1, 2, TimeSpan.FromSeconds(30));
            int ids = 0;
            Func<AppSettings, TranslationSession?> sessions = _ => providers is null ? null : new TranslationSession(providers,
                new TranslationSessionOptions(config, expanded, () => $"a{Interlocked.Increment(ref ids)}"), new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(0), new RecordingUsage());
            Shell = new ShellCoordinator(Platform, Config, features, c => c == Capability.Translate || (c == Capability.Tts && pronunciationReady), new ShellOptions(false, false),
                sessions, null, new TranslationBackend(true, (_, _) => null, Speech: Speech), capture);
            Shell.SpeechEnded += (target, result) => Ended.Enqueue((target, result));
            Shell.Start();
        }

        public void Ready(WindowKind kind) => Shell.OnPageMessage(kind, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = Platform.Session(kind) }));

        public CommandResult Run(WindowKind kind, string name, object payload)
        {
            string id = $"c{++counter}";
            Shell.OnPageMessage(kind, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Command", windowSessionId = Platform.Session(kind), name, correlationId = id, payload }, Web));
            for (var poll = System.Diagnostics.Stopwatch.StartNew(); poll.Elapsed < Eventually.DefaultTimeout;)
            {
                var hit = Platform.Posted.FirstOrDefault(p => p.Envelope.CorrelationId == id);
                if (hit.Envelope is not null) return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CommandResult)!;
                Thread.Sleep(5);
            }
            throw new TimeoutException(id);
        }

        public async Task<CardSnapshot> ShowAsync(string text, string serviceId)
        {
            Shell.Open(WindowKind.Main);
            Ready(WindowKind.Main);
            Assert.True(Run(WindowKind.Main, UiCommands.SubmitText, new { text }).Ok);
            CardSnapshot? card = null;
            Assert.True(await Eventually.WaitAsync(() => (card = Cards().LastOrDefault(c => c.ServiceId == serviceId && c.State == CardState.Ready)) is not null));
            return card!;
        }

        public IEnumerable<CardSnapshot> Cards() => Platform.Posted.ToArray()
            .Where(p => p.Kind == WindowKind.Main && p.Envelope.Kind == UiMessageKind.Patch)
            .Select(p => p.Envelope.Payload!.Value.GetProperty("card").Deserialize(ContractsJson.Default.CardSnapshot)!);

        public SpeechStateView[] States(WindowKind kind) => [.. Platform.Posted.ToArray()
            .Where(p => p.Kind == kind && p.Envelope.Name == "speech").Select(p => p.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.SpeechStateView)!)];

        public Task<bool> EndedAsync(int count) => Eventually.WaitAsync(() => Ended.Count >= count);

        public void Dispose() { Settings.Dispose(); Root.Dispose(); }
    }

    [Fact] // PLAN 6.5: capture first (snapshot before any window), then the default service speaks; the bar opens without activation
    public async Task Pronounce_hotkey_captures_then_speaks_with_the_default_service_and_anchors_the_bar_to_the_selection()
    {
        var reader = new Reader(new SelectionResult(SelectionStatus.Selected, "hello world", "uia", new ScreenRect(100, 200, 400, 230), 96, 1, 1));
        using var rig = new Rig(reader);
        rig.Shell.OnHotkey("pronounce");
        Assert.Equal(1, reader.Snapshots); // taken synchronously on the hotkey thread (F08 rule)
        Assert.True(await rig.EndedAsync(1));
        Assert.Equal(("bar", PlaybackStatus.Completed), (rig.Ended.Single().Target, rig.Ended.Single().Result.Status));
        var (instance, request) = rig.Spoken.Single();
        Assert.Equal(("native-sapi", "hello world", "en"), (instance, request.Text, request.Lang));
        Assert.Contains("show:Speech:False", rig.Platform.Calls);
        Assert.DoesNotContain(rig.Platform.Calls, c => c.StartsWith("show:Selection", StringComparison.Ordinal) || c.EndsWith(":True", StringComparison.Ordinal));
        var bar = rig.Shell.SpeechBar!;
        Assert.Equal([("native-sapi", true), ("microsoft-tts", false)], bar.Services.Select(s => (s.Instance, s.Default)));
        Assert.Equal((WindowKind.Speech, (PixelRect?)new PixelRect(100, 200, 300, 30), WindowSpec.SpeechBarWidthDip(2)), rig.Platform.Anchors.Single());
        Assert.Equal("stopped", rig.Shell.SpeechState.Phase);
    }

    [Fact] // SEL03: a capture failure shows the failure bar; nothing is spoken and no bar opens
    public async Task Pronounce_capture_failure_shows_the_failure_bar_and_speaks_nothing()
    {
        var reader = new Reader(new SelectionResult(SelectionStatus.Unsupported, "", "none", null, 96, 0, 1));
        using var rig = new Rig(reader);
        var presented = new TaskCompletionSource<CaptureOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Shell.CapturePresented += o => presented.TrySetResult(o);
        rig.Shell.OnHotkey("pronounce");
        var outcome = await presented.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal((CaptureTrigger.Pronounce, CaptureStatus.Failed), (outcome.Trigger, outcome.Status));
        Assert.Contains("show:Error:False", rig.Platform.Calls);
        Assert.DoesNotContain("show:Speech:False", rig.Platform.Calls);
        Assert.Empty(rig.Spoken);
    }

    [Fact]
    public void Pronounce_hotkey_does_nothing_while_pronunciation_is_unavailable()
    {
        var reader = new Reader(new SelectionResult(SelectionStatus.Selected, "x", "uia", null, 96, 1, 1));
        using var rig = new Rig(reader, pronunciationReady: false);
        rig.Shell.OnHotkey("pronounce");
        Assert.Equal(0, reader.Snapshots);
    }

    [Fact] // per-service playback, supersede and stop all go through the one player
    public async Task Bar_replays_with_another_service_and_stops_through_the_player()
    {
        using var rig = new Rig();
        rig.Sink.Instant = false;
        rig.Shell.OnHotkey("pronounce");
        Assert.True(await Eventually.WaitAsync(() => rig.Shell.SpeechState.Phase == "playing"));
        rig.Ready(WindowKind.Speech);
        Assert.Equal("unknown-service", rig.Run(WindowKind.Speech, UiCommands.SpeechPlay, new SpeechPlayRequest("google-tts")).Error);
        Assert.True(rig.Run(WindowKind.Speech, UiCommands.SpeechPlay, new SpeechPlayRequest("microsoft-tts")).Ok);
        Assert.True(await rig.EndedAsync(1));
        Assert.Equal(PlaybackStatus.Superseded, rig.Ended.First().Result.Status);
        Assert.Equal("microsoft-tts", rig.Shell.SpeechBar!.Active);
        Assert.Equal(["native-sapi", "microsoft-tts"], rig.Spoken.Select(s => s.Instance));
        Assert.Equal("hello world", rig.Spoken.Last().Request.Text);
        Assert.True(await Eventually.WaitAsync(() => rig.Shell.SpeechState.Phase == "playing"));
        Assert.True(rig.Run(WindowKind.Speech, UiCommands.SpeechStop, new { }).Ok);
        Assert.True(await rig.EndedAsync(2));
        Assert.Equal(PlaybackStatus.Stopped, rig.Ended.Last().Result.Status);
        Assert.True(await Eventually.WaitAsync(() => rig.Shell.SpeechState.Phase == "stopped"));
        Assert.Contains(rig.States(WindowKind.Speech), s => s.Phase == "playing" && s.Target == "bar");
        // The bar hides a moment after its playback ended.
        (TimeSpan Delay, Action Action)[] timers;
        lock (rig.Platform.Timers) timers = [.. rig.Platform.Timers.Where(t => t.Delay == ShellCoordinator.SpeechBarLinger)];
        Assert.NotEmpty(timers);
        timers[^1].Action();
        Assert.Contains("hide:Speech", rig.Platform.Calls);
    }

    [Fact] // DESIGN 8: the card key reads the shown translation with the default service
    public async Task Card_read_aloud_speaks_the_shown_translation()
    {
        using var rig = new Rig(providers: [new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Echo("T:"))]);
        await rig.ShowAsync("hello there.", "svc");
        Assert.True(rig.Run(WindowKind.Main, UiCommands.SpeakCard, new SpeakCardRequest("svc")).Ok);
        Assert.True(await rig.EndedAsync(1));
        var (instance, request) = rig.Spoken.Single();
        Assert.Equal(("native-sapi", "T:hello there.", "zh-Hans"), (instance, request.Text, request.Lang));
        Assert.Contains(rig.States(WindowKind.Main), s => s.Target == "card:svc" && s.Phase == "stopped");
        Assert.Equal("not-ready", rig.Run(WindowKind.Main, UiCommands.SpeakCard, new SpeakCardRequest("nope")).Error);
    }

    [Fact] // TTS03: the phonetic key plays the entry's own audio (no second lookup); without audio the word is spoken
    public async Task Dictionary_phonetic_plays_the_shown_entry_audio_without_another_lookup()
    {
        var youdao = new DictionaryService("youdao/translate", Good);
        using var rig = new Rig(providers: [youdao]);
        var card = await rig.ShowAsync("good", "youdao/translate");
        Assert.Equal(1, youdao.Lookups);
        string audioId = card.Entry!.Phonetics[0].AudioId!;
        Assert.True(rig.Run(WindowKind.Main, UiCommands.SpeakCard, new SpeakCardRequest("youdao/translate", 0)).Ok);
        Assert.True(await rig.EndedAsync(1));
        Assert.Equal([audioId], rig.AudioIds);
        Assert.Empty(rig.Spoken);
        Assert.True(rig.Run(WindowKind.Main, UiCommands.SpeakCard, new SpeakCardRequest("youdao/translate", 1)).Ok);
        Assert.True(await rig.EndedAsync(2));
        Assert.Equal(("good", "en-GB"), (rig.Spoken.Single().Request.Text, rig.Spoken.Single().Request.Lang));
        Assert.True(rig.Run(WindowKind.Main, UiCommands.SpeakCard, new SpeakCardRequest("youdao/translate")).Ok); // card key: the meanings
        Assert.True(await rig.EndedAsync(3));
        Assert.Equal("好的；优良的", rig.Spoken.Last().Request.Text);
        Assert.Equal("not-ready", rig.Run(WindowKind.Main, UiCommands.SpeakCard, new SpeakCardRequest("youdao/translate", 5)).Error);
        Assert.Equal(1, youdao.Lookups); // TTS03: no invisible extra query
        Assert.Contains(rig.States(WindowKind.Main), s => s.Target == "card:youdao/translate:0");
    }

    [Fact] // TTS03: a collapsed dictionary card that was never queried is refused and nothing is looked up
    public async Task Collapsed_never_queried_dictionary_card_never_triggers_a_lookup()
    {
        var youdao = new DictionaryService("youdao/translate", Good);
        using var rig = new Rig(providers: [new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Echo("T:")), youdao], expanded: 1);
        await rig.ShowAsync("good", "svc");
        Assert.Equal("not-ready", rig.Run(WindowKind.Main, UiCommands.SpeakCard, new SpeakCardRequest("youdao/translate", 0)).Error);
        Assert.Equal("not-ready", rig.Run(WindowKind.Main, UiCommands.SpeakCard, new SpeakCardRequest("youdao/translate")).Error);
        await Task.Delay(50, Ct);
        Assert.Equal(0, youdao.Lookups);
        Assert.Empty(rig.AudioIds);
        Assert.Empty(rig.Spoken);
        // The card was there, collapsed and idle: expanding it (a user action) is what looks the word up.
        Assert.True(rig.Run(WindowKind.Main, UiCommands.ToggleCard, new ToggleCardRequest("youdao/translate")).Ok);
        Assert.True(await Eventually.WaitAsync(() => youdao.Lookups == 1));
    }

    [Fact] // the card TTS path needs the pronunciation feature; the entry's own audio still plays without it
    public async Task Without_a_pronunciation_service_only_existing_dictionary_audio_plays()
    {
        var youdao = new DictionaryService("youdao/translate", Good);
        using var rig = new Rig(providers: [youdao], pronunciationReady: false);
        await rig.ShowAsync("good", "youdao/translate");
        Assert.Equal("unavailable", rig.Run(WindowKind.Main, UiCommands.SpeakCard, new SpeakCardRequest("youdao/translate", 1)).Error);
        Assert.True(rig.Run(WindowKind.Main, UiCommands.SpeakCard, new SpeakCardRequest("youdao/translate", 0)).Ok);
        Assert.True(await rig.EndedAsync(1));
        Assert.Single(rig.AudioIds);
        Assert.Empty(rig.Spoken);
    }
}
