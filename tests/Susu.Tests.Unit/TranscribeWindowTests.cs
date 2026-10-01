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
/// F14.4 Transcribe window with fakes (no desktop, no real media, no vendor): the entry greyed until a timecode-capable video ASR is
/// usable (A03), pick / drop and probe (name, duration, streams, unsupported encoding, no audio, not found), upload confirmation
/// (T06), stage progress with incremental cues, quota stop and service switch (T07), cancel, close and reopen, export through
/// the save-dialog port (VID04 host part), and the video translation selection (SetSpeechB). The page never gets a path or a token.
/// </summary>
public sealed class TranscribeWindowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly AsrLimits AsrLimit = new([AsrFormat.Wav16kMono], 24_000_000, 25_000_000, 300, AsrUpload.Multipart, 2048);

    private sealed class FakeAsr(Func<int, CancellationToken, Task<AsrOutcome>> answer, bool timecodes = true) : IAsrProvider
    {
        public int Calls;
        public string InstanceId => "openai-asr";
        public string Model => "whisper-1";
        public bool Timecodes => timecodes;
        public AsrLimits Limits => AsrLimit;
        public Task<AsrOutcome> TranscribeAsync(AsrCall call, CancellationToken ct) => answer(Interlocked.Increment(ref Calls) - 1, ct);
    }

    private static Task<AsrOutcome> Segments(int n, CancellationToken _) => Task.FromResult<AsrOutcome>(new AsrOutcome.Transcribed("segments", "x", [new AsrSegment(0.5, 2, "first"), new AsrSegment(3, 5, "second\nline")]));
    private static Task<AsrOutcome> AsrQuota(int n, CancellationToken ct) => Task.FromResult<AsrOutcome>(new AsrOutcome.Failure(new ProviderError(ErrorKind.Quota, "daily")));

    private sealed class ScriptDecoder(Func<MediaProbe> probe) : IMediaDecoder
    {
        public Func<MediaProbe> Probe { get; set; } = probe;
        public Exception? ProbeFailure;
        public int Opens;
        public Task<MediaProbe> ProbeAsync(string path, CancellationToken ct) => ProbeFailure is { } e ? Task.FromException<MediaProbe>(e) : Task.FromResult(Probe());
        public Task<IMediaAudioReader> OpenAsync(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref Opens);
            var probe = Probe();
            return Task.FromResult<IMediaAudioReader>(new VideoJobTests.FakeReader(probe, VideoJobTests.Blocks((int)probe.Duration.TotalSeconds, _ => true).GetEnumerator()));
        }
    }

    private sealed class FakePicker(Rig rig) : IMediaPicker
    {
        public string? Next;
        public Task<string?> PickAsync(CancellationToken ct) => Task.FromResult(Next is null ? null : rig.Tokens.Issue(Next));
    }

    private sealed class FakeSave : ISubtitleSavePicker
    {
        public string? Path;
        public readonly List<(string Name, SubtitleFormat Format)> Asked = [];
        public Task<string?> PickAsync(string name, SubtitleFormat format, CancellationToken ct) { Asked.Add((name, format)); return Task.FromResult(Path); }
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly SettingsStore Settings;
        public readonly ConfigService Config;
        public readonly ShellCoordinator Shell;
        public readonly FileLeases Leases = new(TestTemp.NewDir("susu-transcribe-leases"));
        public readonly MediaTokens Tokens = new();
        public readonly ScriptDecoder Decoder = new(() => new MediaProbe(TimeSpan.FromSeconds(30), true, [new MediaAudioStream(1, "aac", 44100, 2, true)]));
        public readonly FakePicker Picker;
        public readonly FakeSave Save = new();
        public readonly VideoJobs Jobs;
        public FakeAsr Asr = new(Segments);
        public Func<int, SubtitleBatchCall, SubtitleBatchOutcome>? Translate;
        public readonly List<FakeTranslatorBox> Translators = [];
        public string MediaFile;
        private long counter;
        private int translateCalls;

        public sealed class FakeTranslatorBox(VideoJobTests.FakeTranslator inner) { public VideoJobTests.FakeTranslator Inner = inner; }

        public Rig(bool videoReady = true)
        {
            Settings = new SettingsStore(Root.Paths, new ManualClock());
            Config = new ConfigService(Settings, new SecretStore(Root.Paths.Secrets, new XorProtector()));
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Transcription, FeatureState.Available, null, []));
            var translator = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Echo("T:"));
            var snapshot = new ConfigSnapshot(1, 1, 1, 1, TimeSpan.FromSeconds(30));
            Shell = new ShellCoordinator(Platform, Config, features, c => c == Capability.Translate, new ShellOptions(false, false),
                _ => new TranslationSession([translator], new TranslationSessionOptions(snapshot, 1), new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(), new RecordingUsage()));
            Picker = new FakePicker(this);
            Jobs = new VideoJobs(Tokens, Decoder, new LeasedFiles(Leases), () => Asr, () =>
            {
                string id = Config.State.Effective.Speech.VideoTranslator is { Length: > 0 } selected ? selected : "svc";
                var fake = new VideoJobTests.FakeTranslator(id, VideoJobTests.ItemsLimits, (call, _) => Translate?.Invoke(Interlocked.Increment(ref translateCalls) - 1, call) ?? VideoJobTests.Echo(call));
                Translators.Add(new FakeTranslatorBox(fake));
                return fake;
            });
            Shell.MediaTokens = Tokens;
            Shell.MediaPicker = Picker;
            Shell.SubtitleSavePicker = Save;
            Shell.VideoJobs = Jobs;
            MediaFile = Path.Combine(Root.Root, "movie.mp4");
            File.WriteAllText(MediaFile, "x");
            Shell.Start();
            if (videoReady) MakeVideoAsrUsable();
        }

        /// <summary>The default video selection (OpenAI whisper-1, timecodes) needs its key saved and the grants confirmed.</summary>
        public void MakeVideoAsrUsable()
        {
            Shell.Open(WindowKind.Settings);
            Ready(WindowKind.Settings);
            Assert.True(Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("openai", "apiKey", "sk-test-shared", ConfirmGrants: true)).Ok);
            Assert.True(Run(WindowKind.Settings, UiCommands.BindAccount, new BindAccountRequest("openai-asr", "openai", ConfirmGrants: true)).Ok);
        }

        public void Ready(WindowKind kind)
        {
            int before = Snapshots(kind);
            Shell.OnPageMessage(kind, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = Platform.Session(kind) }));
            for (var poll = System.Diagnostics.Stopwatch.StartNew(); Snapshots(kind) == before; Thread.Sleep(5))
                if (poll.Elapsed > Eventually.DefaultTimeout) throw new TimeoutException("no snapshot");
        }

        private int Snapshots(WindowKind kind) => Platform.Posted.Count(p => p.Kind == kind && p.Envelope.Kind == UiMessageKind.Snapshot);

        public CommandResult Run(WindowKind kind, string name, object? payload = null)
        {
            string id = $"c{++counter}";
            Shell.OnPageMessage(kind, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Command", windowSessionId = Platform.Session(kind), name, correlationId = id, payload }, Web));
            for (var poll = System.Diagnostics.Stopwatch.StartNew(); poll.Elapsed < Eventually.DefaultTimeout; Thread.Sleep(5))
            {
                var hit = Platform.Posted.FirstOrDefault(p => p.Envelope.CorrelationId == id);
                if (hit.Envelope is not null) return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CommandResult)!;
            }
            throw new TimeoutException($"no result for {name}");
        }

        public CommandResult T(string name, object? payload = null) => Run(WindowKind.Transcribe, name, payload);

        public UiSnapshot Snapshot(WindowKind kind) => Platform.Posted.Last(p => p.Kind == kind && p.Envelope.Kind == UiMessageKind.Snapshot).Envelope.Payload!.Value.Deserialize(ContractsJson.Default.UiSnapshot)!;

        public List<TranscribeView> ViewEvents() => [.. Platform.Posted.Where(p => p.Kind == WindowKind.Transcribe && p.Envelope.Name == "transcribe").Select(p => p.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.TranscribeView)!)];

        public List<TranscribeCueView> CueEvents() => [.. Platform.Posted.Where(p => p.Kind == WindowKind.Transcribe && p.Envelope.Name == "transcribe.cue").Select(p => p.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.TranscribeCueView)!)];

        public string? Phase => Shell.TranscribeWindowView?.Phase;
        public Task<bool> WaitPhase(string phase) => Eventually.WaitAsync(() => Shell.TranscribeWindowView?.Phase == phase);

        /// <summary>Opens the window through the hotkey and has its page ready.</summary>
        public void Open()
        {
            Shell.OnHotkey("videoTranscribe");
            Ready(WindowKind.Transcribe);
        }

        /// <summary>Picks the movie and starts the job; leaves it waiting for the confirmation.</summary>
        public async Task StartToConfirmAsync()
        {
            Open();
            Picker.Next = MediaFile;
            Assert.True(T(UiCommands.PickMedia).Ok);
            Assert.True(await WaitPhase("picked"));
            Assert.True(T(UiCommands.StartTranscription).Ok);
            Assert.True(await WaitPhase("confirm"));
        }

        public void Dispose() { Shell.VideoJobs?.CancelActive(); Shell.ReleaseAudio(); Leases.Dispose(); Settings.Dispose(); Root.Dispose(); }
    }

    // ---------- entry ----------

    [Fact] // A03: no usable timecode ASR greys the entry with the prerequisite; once usable, tray and hotkey open the window
    public void Entry_needs_a_usable_timecode_asr()
    {
        using var rig = new Rig(videoReady: false);
        var item = rig.Shell.TrayModel().Items.Single(i => i.Id == "transcription");
        Assert.False(item.Enabled);
        Assert.Equal("feature.noService.videoAsr", item.ReasonKey);
        rig.Shell.OnHotkey("videoTranscribe");
        Assert.DoesNotContain("show:Transcribe:True", rig.Platform.Calls);

        rig.MakeVideoAsrUsable();
        Assert.True(rig.Shell.TrayModel().Items.Single(i => i.Id == "transcription").Enabled);
        rig.Shell.OnHotkey("videoTranscribe");
        Assert.Contains("show:Transcribe:True", rig.Platform.Calls);
        rig.Ready(WindowKind.Transcribe);
        Assert.Equal("idle", rig.Snapshot(WindowKind.Transcribe).Transcribe!.Phase);
    }

    // ---------- pick and probe ----------

    [Fact] // pick: name, length and streams are shown; the page gets neither the path nor the token
    public async Task Pick_shows_file_duration_and_streams_without_path_or_token()
    {
        using var rig = new Rig();
        rig.Open();
        rig.Picker.Next = rig.MediaFile;
        Assert.True(rig.T(UiCommands.PickMedia).Ok);
        Assert.True(await rig.WaitPhase("picked"));
        var view = rig.Shell.TranscribeWindowView!;
        Assert.Equal(("movie.mp4", 30_000L, true), (view.FileName, view.DurationMs, view.HasVideo));
        var stream = Assert.Single(view.Streams);
        Assert.Equal(("aac", 44100, 2, true, true), (stream.Codec, stream.SampleRate, stream.Channels, stream.Decodable, stream.Selected));
        string all = rig.Platform.AllJson();
        Assert.DoesNotContain(rig.Root.Root.Replace("\\", "\\\\"), all);
        Assert.DoesNotContain("media-", all, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, rig.Asr.Calls);
    }

    [Fact] // a cancelled dialog changes nothing
    public async Task Cancelled_dialog_changes_nothing()
    {
        using var rig = new Rig();
        rig.Open();
        rig.Picker.Next = null;
        Assert.True(rig.T(UiCommands.PickMedia).Ok);
        await Task.Delay(30, Ct);
        Assert.Equal("idle", rig.Phase);
    }

    [Fact] // VID01 error part: no decodable audio stream, no audio track, missing file and a probe failure each say why, and nothing starts
    public async Task Unusable_files_show_their_error()
    {
        using var rig = new Rig();
        rig.Open();
        rig.Decoder.Probe = () => new MediaProbe(TimeSpan.FromSeconds(60), false, [new MediaAudioStream(1, "ac3", 48000, 6, false)]);
        rig.Picker.Next = rig.MediaFile;
        rig.T(UiCommands.PickMedia);
        Assert.True(await rig.WaitPhase("pickError"));
        Assert.Equal(MediaErrors.UnsupportedEncoding, rig.Shell.TranscribeWindowView!.ErrorCode);
        Assert.False(Assert.Single(rig.Shell.TranscribeWindowView.Streams).Decodable);
        Assert.False(rig.T(UiCommands.StartTranscription).Ok);

        rig.Decoder.Probe = () => new MediaProbe(TimeSpan.FromSeconds(60), true, []);
        rig.T(UiCommands.PickMedia);
        Assert.True(await rig.WaitPhase("pickError"));
        Assert.Equal(MediaErrors.NoAudio, rig.Shell.TranscribeWindowView!.ErrorCode);

        rig.Decoder.ProbeFailure = new MediaDecodeException(MediaErrors.Corrupt, "bad");
        rig.T(UiCommands.PickMedia);
        Assert.True(await rig.WaitPhase("pickError"));
        Assert.Equal(MediaErrors.Corrupt, rig.Shell.TranscribeWindowView!.ErrorCode);

        var dropped = await rig.Shell.OnMediaDroppedAsync(Path.Combine(rig.Root.Root, "missing.mp4"));
        Assert.True(dropped.Ok);
        Assert.Equal(MediaErrors.NotFound, rig.Shell.TranscribeWindowView!.ErrorCode);
        Assert.Equal(0, rig.Asr.Calls);
        Assert.Equal(0, rig.Decoder.Opens);
    }

    [Fact] // drag-in: the native layer hands over the path; same result as the dialog
    public async Task Dropped_file_is_picked_like_a_dialog_file()
    {
        using var rig = new Rig();
        rig.Open();
        Assert.True((await rig.Shell.OnMediaDroppedAsync(rig.MediaFile)).Ok);
        Assert.Equal("picked", rig.Phase);
        Assert.Equal("movie.mp4", rig.Shell.TranscribeWindowView!.FileName);
    }

    [Fact] // a text-only ASR cannot start a job: the refusal is shown and nothing is probed or sent
    public async Task Start_without_timecodes_is_refused_with_the_reason()
    {
        using var rig = new Rig();
        rig.Asr = new FakeAsr(Segments, timecodes: false);
        rig.Open();
        Assert.True((await rig.Shell.OnMediaDroppedAsync(rig.MediaFile)).Ok);
        Assert.True(rig.T(UiCommands.StartTranscription).Ok);
        Assert.Equal(("picked", VideoErrors.NoTimecodes), (rig.Phase, rig.Shell.TranscribeWindowView!.ErrorCode));
        Assert.Equal(0, rig.Asr.Calls);
    }

    // ---------- confirmation, progress, done ----------

    [Fact] // T06 UI: the notice shows services and limits; nothing is uploaded before yes; yes runs ASR then translation with incremental cues
    public async Task Confirm_then_progress_with_incremental_cues_to_done()
    {
        using var rig = new Rig();
        await rig.StartToConfirmAsync();
        var upload = rig.Shell.TranscribeWindowView!.Upload!;
        Assert.Equal(("movie.mp4", 30_000L, "openai-asr", "whisper-1"), (upload.FileName, upload.DurationMs, upload.AsrService, upload.AsrModel));
        Assert.Equal(("svc", VideoJobTests.ItemsLimits.MaxInput, VideoJobTests.ItemsLimits.MaxItems), (upload.TranslationService, upload.TranslationMaxInput, upload.TranslationMaxItems));
        Assert.Null(upload.EstimatedCharacters);
        Assert.Null(upload.EstimatedPrice);
        Assert.True(upload.UploadBytesEstimate > 0);
        Assert.Equal(0, rig.Asr.Calls); // no request while waiting

        Assert.True(rig.T(UiCommands.ConfirmTranscription, new TranscribeConfirmRequest(true)).Ok);
        Assert.True(await rig.WaitPhase("done"));
        var view = rig.Shell.TranscribeWindowView!;
        Assert.Equal((2, 2, 0, 1), (view.CueCount, view.Translated, view.FailedCues, rig.Asr.Calls));
        // Cues arrived as events, in order, first without then with translation.
        var cues = rig.CueEvents();
        Assert.Contains(cues, c => c.Original == "first" && c.Translation is null);
        Assert.Contains(cues, c => c.Original == "first" && c.Translation == "T:first");
        Assert.Equal([0.5, 3], cues.Select(c => c.Start).Distinct().Order());
        var stages = rig.ViewEvents().Select(v => v.Stage).Distinct().ToList();
        Assert.Contains("asr", stages);
        Assert.Contains("translate", stages);
        // Reopening later (a snapshot) carries the whole list.
        rig.Ready(WindowKind.Transcribe);
        var again = rig.Snapshot(WindowKind.Transcribe).Transcribe!;
        Assert.Equal(("done", 2), (again.Phase, again.Cues.Length));
        Assert.Equal("second\nline", again.Cues[1].Original);
        Assert.Equal(0, rig.Leases.ActiveCount);
    }

    [Fact] // declining the confirmation returns to the picked file, no request was made
    public async Task Declining_the_confirmation_keeps_the_file_picked()
    {
        using var rig = new Rig();
        await rig.StartToConfirmAsync();
        Assert.True(rig.T(UiCommands.ConfirmTranscription, new TranscribeConfirmRequest(false)).Ok);
        Assert.True(await rig.WaitPhase("picked"));
        Assert.Equal(0, rig.Asr.Calls);
        // The same file can be started again.
        Assert.True(rig.T(UiCommands.StartTranscription).Ok);
        Assert.True(await rig.WaitPhase("confirm"));
    }

    [Fact] // a second file cannot be picked while a job runs; explicit cancel stops it and keeps what exists
    public async Task Busy_pick_is_refused_and_cancel_ends_the_job()
    {
        using var rig = new Rig();
        await rig.StartToConfirmAsync();
        rig.Picker.Next = rig.MediaFile;
        Assert.Equal(VideoErrors.Busy, rig.T(UiCommands.PickMedia).Error);
        Assert.True(rig.T(UiCommands.CancelTranscription).Ok);
        Assert.True(await rig.WaitPhase("picked")); // cancelled at the confirmation is a "no": the file stays picked
        Assert.Equal(0, rig.Asr.Calls);
    }

    // ---------- quota ----------

    [Fact] // T07 UI: translation quota keeps the originals; switching the service saves the selection and only the missing cues are translated
    public async Task Translation_quota_then_switch_and_resume()
    {
        using var rig = new Rig();
        rig.Translate = (n, call) => n == 0 ? new SubtitleBatchOutcome.Failure(new ProviderError(ErrorKind.Quota)) : VideoJobTests.Echo(call);
        await rig.StartToConfirmAsync();
        rig.T(UiCommands.ConfirmTranscription, new TranscribeConfirmRequest(true));
        Assert.True(await rig.WaitPhase("quota"));
        var view = rig.Shell.TranscribeWindowView!;
        Assert.Equal(("translation", 2, 0), (view.QuotaSide, view.CueCount, view.Translated));
        Assert.Equal(1, rig.Asr.Calls);
        Assert.All(view.Choices, c => Assert.Equal("translation", c.Kind));
        var other = view.Choices.FirstOrDefault(c => !c.Current) ?? throw new Xunit.Sdk.XunitException("no other translation service offered");
        // The window can export what is there while stopped (originals).
        rig.Save.Path = Path.Combine(rig.Root.Root, "partial.srt");
        Assert.True(rig.T(UiCommands.Export, new TranscribeExportRequest("srt", "original")).Ok);
        Assert.Contains("first", File.ReadAllText(rig.Save.Path));

        Assert.True(rig.T(UiCommands.ChangeTranslator, new TranscribeSwitchRequest("translation", other.Id)).Ok);
        Assert.Equal(other.Id, rig.Config.State.Effective.Speech.VideoTranslator);
        Assert.True(rig.T(UiCommands.ResumeTranscription).Ok);
        Assert.True(await rig.WaitPhase("done"));
        Assert.Equal((2, 2), (rig.Shell.TranscribeWindowView!.CueCount, rig.Shell.TranscribeWindowView.Translated));
        Assert.Equal(1, rig.Asr.Calls); // ASR is not repeated
        Assert.Equal(other.Id, rig.Translators[^1].Inner.ServiceId);
    }

    [Fact] // ASR quota stops with the ASR choices; switching the ASR and resuming transcribes the slice again
    public async Task Asr_quota_then_switch_and_resume()
    {
        using var rig = new Rig();
        rig.Asr = new FakeAsr((n, ct) => n == 0 ? AsrQuota(n, ct) : Segments(n, ct));
        await rig.StartToConfirmAsync();
        rig.T(UiCommands.ConfirmTranscription, new TranscribeConfirmRequest(true));
        Assert.True(await rig.WaitPhase("quota"));
        var view = rig.Shell.TranscribeWindowView!;
        Assert.Equal(("asr", 0), (view.QuotaSide, view.CueCount));
        var current = Assert.Single(view.Choices, c => c.Current);
        Assert.Equal(("asr", "openai-asr", "whisper-1"), (current.Kind, current.Id, current.Model));
        Assert.True(rig.T(UiCommands.ChangeTranslator, new TranscribeSwitchRequest("asr", "openai-asr", "whisper-1")).Ok);
        Assert.True(rig.T(UiCommands.ResumeTranscription).Ok);
        Assert.True(await rig.WaitPhase("done"));
        Assert.Equal(2, rig.Shell.TranscribeWindowView!.CueCount);
    }

    [Fact] // a non-quota failure ends as failed with the error; the finished part stays exportable
    public async Task Failure_shows_code_and_keeps_cues()
    {
        using var rig = new Rig();
        rig.Translate = (n, call) => new SubtitleBatchOutcome.Failure(new ProviderError(ErrorKind.Auth));
        await rig.StartToConfirmAsync();
        rig.T(UiCommands.ConfirmTranscription, new TranscribeConfirmRequest(true));
        Assert.True(await rig.WaitPhase("failed"));
        var view = rig.Shell.TranscribeWindowView!;
        Assert.Equal(ErrorKind.Auth, view.ErrorKind);
        Assert.Equal(2, view.CueCount);
        rig.Save.Path = Path.Combine(rig.Root.Root, "kept.txt");
        Assert.True(rig.T(UiCommands.Export, new TranscribeExportRequest("txt", "original")).Ok);
        Assert.True(File.Exists(rig.Save.Path));
    }

    // ---------- close and reopen ----------

    [Fact] // close cancels a running job (no lease or upload left); the finished cues survive for reopen and export
    public async Task Close_cancels_the_job_and_reopen_keeps_finished_results()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Asr = new FakeAsr(async (n, ct) => { await gate.Task.WaitAsync(ct); return await Segments(n, ct); });
        await rig.StartToConfirmAsync();
        rig.T(UiCommands.ConfirmTranscription, new TranscribeConfirmRequest(true));
        Assert.True(await Eventually.WaitAsync(() => rig.Asr.Calls == 1)); // in flight
        Assert.True(rig.T(UiCommands.Close).Ok);
        Assert.True(await rig.WaitPhase("cancelled"));
        await Eventually.WaitAsync(() => rig.Leases.ActiveCount == 0);
        Assert.Equal(0, rig.Leases.ActiveCount);
        Assert.Null(rig.Jobs.Active);

        // Nothing came of it, so the reopened window is empty again.
        rig.Shell.OnHotkey("videoTranscribe");
        Assert.Equal("idle", rig.Phase);

        // A finished job: close, reopen, export - no ASR, translation or upload happens.
        rig.Asr = new FakeAsr(Segments);
        await rig.StartToConfirmAsync();
        rig.T(UiCommands.ConfirmTranscription, new TranscribeConfirmRequest(true));
        Assert.True(await rig.WaitPhase("done"));
        int asrCalls = rig.Asr.Calls, translateCalls = rig.Translators.Sum(t => t.Inner.Calls.Count);
        rig.T(UiCommands.Close);
        rig.Shell.OnHotkey("videoTranscribe");
        rig.Ready(WindowKind.Transcribe);
        var reopened = rig.Snapshot(WindowKind.Transcribe).Transcribe!;
        Assert.Equal(("done", 2), (reopened.Phase, reopened.Cues.Length));
        rig.Save.Path = Path.Combine(rig.Root.Root, "after-close.srt");
        Assert.True(rig.T(UiCommands.Export, new TranscribeExportRequest("srt", "bilingual")).Ok);
        Assert.Contains("T:first", File.ReadAllText(rig.Save.Path));
        Assert.Equal((asrCalls, translateCalls), (rig.Asr.Calls, rig.Translators.Sum(t => t.Inner.Calls.Count)));
    }

    // ---------- export ----------

    [Fact] // VID04 host part: formats and modes through the dialog port; bad cues are reported, not repaired; cancel writes nothing; running is refused
    public async Task Export_writes_chosen_format_reports_bad_cues_and_honours_cancel()
    {
        using var rig = new Rig();
        rig.Asr = new FakeAsr((n, ct) => Task.FromResult<AsrOutcome>(new AsrOutcome.Transcribed("segments", "x", [new AsrSegment(0.5, 2, "good"), new AsrSegment(4, 4, "zero length"), new AsrSegment(5, 6, "tail")])));
        await rig.StartToConfirmAsync();
        Assert.Equal("unavailable", rig.T(UiCommands.Export, new TranscribeExportRequest("srt", "original")).Error); // not while waiting
        rig.T(UiCommands.ConfirmTranscription, new TranscribeConfirmRequest(true));
        Assert.True(await rig.WaitPhase("done"));

        rig.Save.Path = null; // dialog cancelled: no file, no export record
        Assert.True(rig.T(UiCommands.Export, new TranscribeExportRequest("vtt", "original")).Ok);
        Assert.Null(rig.Shell.TranscribeWindowView!.Export);

        string path = Path.Combine(rig.Root.Root, "out.vtt");
        rig.Save.Path = path;
        Assert.True(rig.T(UiCommands.Export, new TranscribeExportRequest("vtt", "bilingualTranslationFirst")).Ok);
        var export = rig.Shell.TranscribeWindowView!.Export!;
        Assert.Equal((path, null, 2), (export.Path, export.Error, export.Exported));
        Assert.Contains(export.Issues, i => i.Code == SubtitleIssues.BadRange);
        string text = File.ReadAllText(path);
        Assert.StartsWith("WEBVTT", text);
        Assert.Contains("T:good", text);
        Assert.DoesNotContain("zero length", text);
        Assert.Equal((SubtitleFormat.Vtt, "movie.bilingual-translation-first.vtt"), (rig.Save.Asked[^1].Format, rig.Save.Asked[^1].Name));

        Assert.Equal("format", rig.T(UiCommands.Export, new TranscribeExportRequest("doc", "original")).Error);
    }

    // ---------- SetSpeechB video translation selection ----------

    [Fact] // the video translation service is its own selection: saved, shown in SetSpeechB, independent of the main window, refused when unknown
    public void Video_translation_selection_is_saved_and_validated()
    {
        using var rig = new Rig();
        rig.Shell.Open(WindowKind.Settings);
        rig.Ready(WindowKind.Settings);
        var speech = rig.Snapshot(WindowKind.Settings).Settings!.Speech!;
        Assert.Equal("", speech.VideoTranslator);
        Assert.NotEmpty(speech.VideoTranslatorChoices!);
        string id = speech.VideoTranslatorChoices![0];
        var before = rig.Config.State.Effective;
        var saved = rig.Run(WindowKind.Settings, UiCommands.SelectSpeech, new SpeechSelectRequest(rig.Config.State.Revision, rig.Config.State.FileHash, "videoTranslator", id, ""));
        Assert.True(saved.Ok);
        var after = rig.Config.State.Effective;
        Assert.Equal(id, after.Speech.VideoTranslator);
        Assert.Equal(before.TranslationOrder, after.TranslationOrder); // the main window's services and order are untouched
        Assert.Equal(before.Speech.Asr, after.Speech.Asr);
        Assert.Equal("range", rig.Run(WindowKind.Settings, UiCommands.SelectSpeech, new SpeechSelectRequest(rig.Config.State.Revision, rig.Config.State.FileHash, "videoTranslator", "no-such", "")).Error);
        // It survives a restart (settings file round trip).
        using var reload = new SettingsStore(rig.Root.Paths, new ManualClock());
        Assert.Equal(id, reload.State.Effective.Speech.VideoTranslator);
    }
}
