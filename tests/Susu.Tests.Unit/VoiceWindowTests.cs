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
/// F12.3 voice window with fakes (no microphone, no plugin host): the Voice entry greyed with a reason until an ASR service is
/// usable (tray, SetHotkeys, hotkey, A03: text-only ASR leaves voice available and video greyed with the timecode reason); record
/// then transcribe then translate with a fake session (REC01: no ASR request while recording, level meter, no-voice notice, start
/// errors); pause, cancel, re-record and close (REC02: minimize keeps recording visible in the tray, close and exit release the
/// device); interruption keeps the captured part with "transcribe recorded / re-record" (REC03); transcription outcomes.
/// </summary>
public sealed class VoiceWindowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class FakeAsrProvider(Func<AsrCall, CancellationToken, Task<AsrOutcome>> run) : IAsrProvider
    {
        public int Calls;
        public string InstanceId => "openai-asr";
        public string Model => "whisper-1";
        public bool Timecodes => false;
        public AsrLimits Limits { get; } = new([AsrFormat.Wav16kMono], 24L << 20, 25L << 20, 300, AsrUpload.Multipart, 2048);
        public Task<AsrOutcome> TranscribeAsync(AsrCall call, CancellationToken cancellationToken) { Interlocked.Increment(ref Calls); return run(call, cancellationToken); }
    }

    /// <summary>A recording the test drives: levels, pause/resume, interruption, stop with audio.</summary>
    private sealed class FakeSession(Func<RecordedAudio?> audio, AudioSourceKind source = AudioSourceKind.Microphone) : IRecordingSession
    {
        private readonly TaskCompletionSource<RecordingResult> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AudioSourceKind Source => source;
        public RecordingPhase Phase { get; private set; } = RecordingPhase.Recording;
        public TimeSpan Captured { get; set; }
        public bool CancelRequested, Disposed;
        public event Action<RecordingLevel>? LevelChanged;
        public event Action<RecordingPhase>? PhaseChanged;
        public void Level(double peak, double seconds, bool silent = false) { Captured = TimeSpan.FromSeconds(seconds); LevelChanged?.Invoke(new RecordingLevel(peak, silent, Captured)); }
        public void Pause() { Phase = RecordingPhase.Paused; PhaseChanged?.Invoke(Phase); }
        public void Resume() { Phase = RecordingPhase.Recording; PhaseChanged?.Invoke(Phase); }
        public Task<RecordingResult> StopAsync() { done.TrySetResult(new RecordingResult(RecordingStatus.Stopped, audio())); return done.Task; }
        public void Cancel() { CancelRequested = true; done.TrySetResult(RecordingResult.Cancelled()); }
        public void Dispose() { Disposed = true; Cancel(); }
        public Task<RecordingResult> Completion => done.Task;
        public void End(RecordingStatus status, MicFailure? reason = null, string? error = null) => done.TrySetResult(new RecordingResult(status, status == RecordingStatus.Failed ? null : audio(), reason, error, source));
    }

    private sealed class FakeCapture(Rig rig, AudioSourceKind source = AudioSourceKind.Microphone) : IAudioCapture
    {
        public readonly Queue<RecordingStartResult> Next = new();
        public readonly List<FakeSession> Sessions = [];
        public int Starts;
        public bool HasDevice() => true;
        public Task<RecordingStartResult> StartAsync(CancellationToken cancellationToken = default)
        {
            Starts++;
            if (Next.Count > 0) return Task.FromResult(Next.Dequeue());
            var session = new FakeSession(rig.Audio, source);
            Sessions.Add(session);
            return Task.FromResult(RecordingStartResult.Started(session));
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly SettingsStore Settings;
        public readonly ConfigService Config;
        public readonly ShellCoordinator Shell;
        public readonly FakeCapture Capture;
        public readonly FakeCapture SystemCapture;
        public readonly FileLeases Leases = new(TestTemp.NewDir("susu-voicewin-leases"));
        public readonly AsrJob Job;
        public FakeAsrProvider Provider;
        public bool AsrReady = true, AudioSilent;
        public double AudioSeconds = 1.5;
        public readonly List<string> Translated = [];
        public readonly List<(string? Phase, TimeSpan Captured)> TrayStatus = [];
        public readonly List<RecordedAudio> Audios = [];
        private long counter;

        public Rig(bool asrReady = true)
        {
            Settings = new SettingsStore(Root.Paths, new ManualClock());
            Config = new ConfigService(Settings, new SecretStore(Root.Paths.Secrets, new XorProtector()));
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Voice, FeatureState.Available, null, [Capability.Asr]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.SystemAudio, FeatureState.Available, null, [Capability.Asr]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Transcription, FeatureState.InDevelopment, "feature.inDevelopment", []));
            AsrReady = asrReady;
            var translator = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Echo("T:"));
            var snapshot = new ConfigSnapshot(1, 1, 1, 1, TimeSpan.FromSeconds(30));
            Shell = new ShellCoordinator(Platform, Config, features, c => c == Capability.Translate || (c == Capability.Asr && AsrReady), new ShellOptions(false, false),
                _ => new TranslationSession([translator], new TranslationSessionOptions(snapshot, 1), new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(), new RecordingUsage()));
            Provider = new FakeAsrProvider((call, _) => Task.FromResult<AsrOutcome>(new AsrOutcome.Transcribed("text", "Hello from the microphone", null)));
            Job = new AsrJob(() => AsrReady ? Provider : null, new LeasedFiles(Leases), async text => { Translated.Add(text); await Shell.SubmitRecognizedTextAsync(text, WindowKind.Voice); }, () => true);
            Capture = new FakeCapture(this);
            Shell.AudioCapture = Capture;
            SystemCapture = new FakeCapture(this, AudioSourceKind.SystemLoopback);
            Shell.SystemAudioCapture = SystemCapture;
            Shell.Asr = Job;
            Shell.RecordingStatusChanged += (phase, captured) => TrayStatus.Add((phase, captured));
            Shell.Start();
        }

        /// <summary>A WAV lease like the recorder's: 16 kHz mono, loud unless <see cref="AudioSilent"/> is set.</summary>
        public RecordedAudio Audio()
        {
            var file = new LeasedFiles(Leases).Create("record", "audio/wav", "wav");
            File.WriteAllBytes(file.FilePath, AsrPipelineTests.Wav((!AudioSilent, AudioSeconds)));
            var audio = new RecordedAudio(file, TimeSpan.FromSeconds(AudioSeconds), 16000, AudioSilent);
            Audios.Add(audio);
            return audio;
        }

        public FakeSession Session => Capture.Sessions[^1];
        public FakeSession SystemSession => SystemCapture.Sessions[^1];

        public void OpenSystemAudio()
        {
            Shell.OnHotkey("audioTranslate");
            Ready(WindowKind.Voice);
        }

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

        public CommandResult Run(WindowKind kind, string name, object? payload = null) => Result(Command(kind, name, payload));

        public List<VoiceView> VoiceEvents() => [.. Platform.Posted.Where(p => p.Kind == WindowKind.Voice && p.Envelope.Name == "voice").Select(p => p.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.VoiceView)!)];

        public UiSnapshot Snapshot(WindowKind kind)
            => Platform.Posted.Last(p => p.Kind == kind && p.Envelope.Kind == UiMessageKind.Snapshot).Envelope.Payload!.Value.Deserialize(ContractsJson.Default.UiSnapshot)!;

        public ErrorBarView? ErrorBar()
        {
            Ready(WindowKind.Error);
            return Snapshot(WindowKind.Error).ErrorBar;
        }

        /// <summary>Opens the voice window through the hotkey and has its page ready.</summary>
        public void OpenVoice()
        {
            Shell.OnHotkey("voiceTranslate");
            Ready(WindowKind.Voice);
        }


        /// <summary>Tray.Open from the menu window: "ok" or the refusal.</summary>
        public string TrayOpen(string id)
        {
            Shell.OnTrayMenu();
            var result = Run(WindowKind.Tray, UiCommands.TrayOpen, new { id });
            return result.Ok ? "ok" : result.Error ?? "failed";
        }
        public string? Phase => Shell.VoiceWindowView?.Phase;
        public Task<bool> WaitPhase(string phase) => Eventually.WaitAsync(() => Shell.VoiceWindowView?.Phase == phase);

        public void Dispose() { Shell.ReleaseAudio(); Leases.Dispose(); Settings.Dispose(); Root.Dispose(); }
    }

    // ---------- entry points ----------

    [Fact] // the Voice entry is greyed with the reason until an ASR service can run (tray, SetHotkeys, hotkey)
    public void Voice_entry_needs_a_usable_asr_service()
    {
        using var rig = new Rig(asrReady: false);
        var item = rig.Shell.TrayModel().Items.Single(i => i.Id == "voice");
        Assert.False(item.Enabled);
        Assert.Equal("feature.noService.asr", item.ReasonKey);
        Assert.False(rig.Platform.Registered.ContainsKey("voiceTranslate"));
        rig.Shell.OnHotkey("voiceTranslate");
        Assert.DoesNotContain("show:Voice:True", rig.Platform.Calls);
        // The chord is not registered while unavailable; a stale one says why on the failure bar.
        Assert.Equal(new ErrorLineView("feature.noService.asr", "settings"), Assert.Single(rig.ErrorBar()!.Lines));
        Assert.Equal("unavailable", rig.TrayOpen("voice"));
        rig.AsrReady = true;
        Assert.True(rig.Shell.TrayModel().Items.Single(i => i.Id == "voice").Enabled);
        rig.Shell.OnHotkey("voiceTranslate");
        Assert.Contains("show:Voice:True", rig.Platform.Calls);
    }

    [Fact] // A03: with only a text-only ASR, voice is available and video stays greyed with the timecode reason; no segments are made up
    public void A03_text_only_asr_keeps_voice_and_greys_video()
    {
        using var rig = new Rig();
        var s = rig.Config.State.Effective;
        Assert.Equal(SaveStatus.Saved, rig.Config.Save(s with { Speech = s.Speech.With(SpeechSlot.Asr, new SpeechSelection("gemini-asr", "gemini-2.5-flash")) }, rig.Config.State.Revision, rig.Config.State.FileHash).Status);
        var tray = rig.Shell.TrayModel().Items;
        Assert.True(tray.Single(i => i.Id == "voice").Enabled);
        var video = tray.Single(i => i.Id == "transcription");
        Assert.False(video.Enabled);
        Assert.Equal("feature.noService.videoAsr", video.ReasonKey);
        rig.Shell.Open(WindowKind.Settings);
        rig.Ready(WindowKind.Settings);
        var speech = rig.Snapshot(WindowKind.Settings).Settings!.Speech!;
        Assert.Equal("gemini-asr", speech.Asr.Instance);
        Assert.False(speech.VideoAsr.Ready);
        Assert.True(speech.VideoAsr.Choices.Single(c => c.InstanceId == "gemini-asr").Selectable == false);
        Assert.Equal("needs-timecodes", speech.VideoAsr.Choices.Single(c => c.InstanceId == "gemini-asr").ReasonKey);
        // The video selection can never become the text-only model.
        var refused = rig.Run(WindowKind.Settings, UiCommands.SelectSpeech, new SpeechSelectRequest(rig.Config.State.Revision, rig.Config.State.FileHash, "videoAsr", "gemini-asr", "gemini-2.5-flash"));
        Assert.Equal("needs-timecodes", refused.Error);
    }

    // ---------- record, transcribe, translate ----------

    [Fact] // REC01 normal: nothing is sent while recording; the window shows level and time only; stop then transcribes and translates
    public async Task Record_then_transcribe_then_translate()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        Assert.Equal("idle", rig.Snapshot(WindowKind.Voice).Voice!.Phase);
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        Assert.Equal("recording", rig.Phase);
        Assert.Equal(("recording", TimeSpan.Zero), rig.TrayStatus[^1]);

        rig.Session.Level(0.42, 1.0);
        Assert.Equal((0.42, 1000L, false), (rig.Shell.VoiceWindowView!.Level, rig.Shell.VoiceWindowView.ElapsedMs, rig.Shell.VoiceWindowView.Silent));
        Assert.Equal(0, rig.Provider.Calls); // no live transcription, no request before the recording ends
        Assert.Null(rig.Shell.VoiceWindowView.Text);

        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StopRecording).Ok);
        Assert.True(await rig.WaitPhase("transcribed"));
        var view = rig.Shell.VoiceWindowView!;
        Assert.Equal(("Hello from the microphone", "openai-asr", true), (view.Text, view.ServiceId, view.Translated));
        Assert.Equal(["Hello from the microphone"], rig.Translated);
        Assert.Equal(1, rig.Provider.Calls);
        Assert.Equal((null, TimeSpan.Zero), rig.TrayStatus[^1]);
        Assert.True(rig.Session.Disposed || rig.Session.Completion.IsCompleted);
        // The translation session of the window produced the cards (T02).
        var snapshot = rig.Platform.Posted.Where(p => p.Kind == WindowKind.Voice && p.Envelope.Name == "translation").Last().Envelope.Payload!.Value.Deserialize(ContractsJson.Default.TranslationSnapshot)!;
        Assert.Equal("Hello from the microphone", snapshot.SourceText);
        // The audio lease is released once transcription ended, and the page never saw a path.
        Assert.True(await Eventually.WaitAsync(() => rig.Audios[0].File.Released && rig.Leases.ActiveCount == 0));
        string json = string.Join('\n', rig.Platform.Posted.Where(p => p.Kind == WindowKind.Voice).Select(p => JsonSerializer.Serialize(p.Envelope, ContractsJson.Default.UiEnvelope)));
        Assert.DoesNotContain(".wav", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("susu-voicewin", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // the meter is projected a few times a second, never one event per device block; the no-voice notice goes out at once
    public void Level_events_are_throttled_and_silence_is_shown()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        int before = rig.VoiceEvents().Count;
        for (int i = 1; i <= 50; i++) rig.Session.Level(0.2, i * 0.05);
        int sent = rig.VoiceEvents().Count - before;
        Assert.InRange(sent, 1, 15);
        Assert.Equal(2500, rig.Shell.VoiceWindowView!.ElapsedMs); // the stored view is current even where no event was sent
        rig.Session.Level(0.0, 3.0, silent: true);
        Assert.True(rig.VoiceEvents().Last().Silent);
        rig.Session.Level(0.3, 3.1);
        Assert.False(rig.Shell.VoiceWindowView.Silent);
    }

    [Fact] // REC02: pause shows paused and does not count; resume continues; the tray state follows
    public void Pause_and_resume()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        rig.Session.Level(0.3, 2.0);
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.PauseRecording).Ok);
        Assert.Equal(("paused", 2000L), (rig.Phase, rig.Shell.VoiceWindowView!.ElapsedMs));
        Assert.Equal("paused", rig.TrayStatus[^1].Phase);
        Assert.Equal("paused", rig.Shell.TrayModel().Items.Single(i => i.Id == "voice").Status);
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.PauseRecording).Ok); // the same command resumes
        Assert.Equal("recording", rig.Phase);
        Assert.Equal("recording", rig.Shell.TrayModel().Items.Single(i => i.Id == "voice").Status);
        Assert.Equal(0, rig.Provider.Calls);
    }

    [Theory] // REC01 start errors: denied, no device, busy, a broken device: a clear phase with the code, no session, and retry works
    [InlineData(MicFailure.Denied, "mic.denied")]
    [InlineData(MicFailure.NoDevice, "mic.noDevice")]
    [InlineData(MicFailure.Failed, "mic.failed")]
    public void Start_errors_show_their_reason_and_can_be_retried(MicFailure failure, string code)
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Capture.Next.Enqueue(RecordingStartResult.Failed(failure));
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        Assert.Equal(("error", code), (rig.Phase, rig.Shell.VoiceWindowView!.ErrorCode));
        Assert.False(rig.Shell.IsRecording);
        Assert.Empty(rig.Capture.Sessions);
        Assert.Equal(0, rig.Provider.Calls);
        rig.Capture.Next.Enqueue(RecordingStartResult.Busy());
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        Assert.Equal("mic.busy", rig.Shell.VoiceWindowView.ErrorCode);
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok); // the next attempt records
        Assert.Equal("recording", rig.Phase);
    }

    [Fact] // a disk error while saving the recording ends it with the code and keeps nothing
    public async Task Save_failure_is_shown()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        rig.Session.End(RecordingStatus.Failed, error: "record.diskFull");
        Assert.True(await rig.WaitPhase("error"));
        Assert.Equal(("error", "record.diskFull"), (rig.Phase, rig.Shell.VoiceWindowView!.ErrorCode));
        Assert.False(rig.Shell.IsRecording);
        Assert.Equal(0, rig.Provider.Calls);
    }

    [Fact] // a recording with no audible sound is never sent: no speech, no request (REC01 silence)
    public async Task A_silent_recording_makes_no_request()
    {
        using var rig = new Rig();
        rig.AudioSilent = true;
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        rig.Run(WindowKind.Voice, UiCommands.StopRecording);
        Assert.True(await rig.WaitPhase("noSpeech"));
        Assert.Equal(0, rig.Provider.Calls);
        Assert.Empty(rig.Translated);
        Assert.True(await Eventually.WaitAsync(() => rig.Audios[0].File.Released));
    }

    [Fact] // REC01 limit: the recorder cut the audio at the limit; the window says so and transcribes after it
    public async Task The_limit_ends_the_recording_and_transcribes()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        rig.Session.End(RecordingStatus.LimitReached);
        Assert.True(await rig.WaitPhase("transcribed"));
        Assert.Equal("limit", rig.Shell.VoiceWindowView!.Notice);
        Assert.Equal(600_000, rig.Shell.VoiceWindowView.LimitMs);
    }

    // ---------- transcription outcomes ----------

    [Fact] // a service failure shows its class; re-record drops it
    public async Task A_service_failure_is_shown_and_re_record_starts_over()
    {
        using var rig = new Rig();
        rig.Provider = new FakeAsrProvider((_, _) => Task.FromResult<AsrOutcome>(new AsrOutcome.Failure(new ProviderError(ErrorKind.Auth))));
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        rig.Run(WindowKind.Voice, UiCommands.StopRecording);
        Assert.True(await rig.WaitPhase("failed"));
        Assert.Equal(ErrorKind.Auth, rig.Shell.VoiceWindowView!.ErrorKind);
        Assert.Empty(rig.Translated);
        Assert.True(await Eventually.WaitAsync(() => rig.Audios[0].File.Released));
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        Assert.Equal("recording", rig.Phase);
        Assert.Null(rig.Shell.VoiceWindowView!.ErrorKind);
    }

    [Fact] // the service lost its key between registration and use: unavailable, nothing called, audio released
    public async Task Unavailable_service_is_shown()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        var job = rig.Job;
        rig.Shell.Asr = new AsrJob(() => null, new LeasedFiles(rig.Leases), _ => Task.CompletedTask, () => true);
        rig.Run(WindowKind.Voice, UiCommands.StopRecording);
        Assert.True(await rig.WaitPhase("unavailable"));
        Assert.True(await Eventually.WaitAsync(() => rig.Audios[0].File.Released));
        rig.Shell.Asr = job;
    }

    [Fact] // edited text is translated from the window like typed text
    public async Task The_transcript_can_be_edited_and_translated_again()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        rig.Run(WindowKind.Voice, UiCommands.StopRecording);
        Assert.True(await rig.WaitPhase("transcribed"));
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.SubmitText, new { text = "Edited by hand" }).Ok);
        var snapshot = rig.Platform.Posted.Where(p => p.Kind == WindowKind.Voice && p.Envelope.Name == "translation").Last().Envelope.Payload!.Value.Deserialize(ContractsJson.Default.TranslationSnapshot)!;
        Assert.Equal("Edited by hand", snapshot.SourceText);
    }

    // ---------- cancel, re-record, close, minimize, exit ----------

    [Fact] // cancel discards the recording and releases the device; nothing is sent
    public void Cancel_discards_the_recording()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        var session = rig.Session;
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.CancelRecording).Ok);
        Assert.True(session.CancelRequested);
        Assert.Equal("idle", rig.Phase);
        Assert.False(rig.Shell.IsRecording);
        Assert.Equal((null, TimeSpan.Zero), rig.TrayStatus[^1]);
        Assert.Equal(0, rig.Provider.Calls);
        Assert.Empty(rig.Translated);
    }

    [Fact] // re-record while recording cancels the first take and opens a second device session
    public void Re_record_replaces_the_running_take()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        var first = rig.Session;
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        Assert.True(first.CancelRequested);
        Assert.Equal(2, rig.Capture.Sessions.Count);
        Assert.Equal("recording", rig.Phase);
        Assert.False(rig.Capture.Sessions[1].CancelRequested);
        Assert.Equal(0, rig.Provider.Calls);
    }

    [Fact] // cancelling while transcribing stops the upload and leaves no audio
    public async Task Cancel_during_transcription()
    {
        using var rig = new Rig();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider = new FakeAsrProvider(async (_, cancel) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancel); return new AsrOutcome.Transcribed("text", "never", null); });
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        rig.Run(WindowKind.Voice, UiCommands.StopRecording);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("transcribing", rig.Phase);
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.CancelRecording).Ok);
        Assert.Equal("idle", rig.Phase);
        Assert.True(await Eventually.WaitAsync(() => rig.Audios[0].File.Released && rig.Leases.ActiveCount == 0));
        await Task.Delay(50, Ct);
        Assert.Equal("idle", rig.Phase); // the cancelled job's late state never replaces the empty window
        Assert.Empty(rig.Translated);
    }

    [Fact] // REC02 close: the recording is discarded and the device released, the window hides
    public void Closing_the_window_releases_the_microphone()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        var session = rig.Session;
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.Close).Ok);
        Assert.Contains("hide:Voice", rig.Platform.Calls);
        Assert.True(session.CancelRequested && session.Disposed);
        Assert.False(rig.Shell.IsRecording);
        Assert.Equal("idle", rig.Shell.VoiceWindowView!.Phase);
        Assert.Equal(0, rig.Provider.Calls);
    }

    [Fact] // kept audio of an interrupted take is deleted when the window closes
    public async Task Closing_deletes_kept_audio_of_an_interrupted_take()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        rig.Session.End(RecordingStatus.Interrupted, MicFailure.DeviceRemoved);
        Assert.True(await rig.WaitPhase("interrupted"));
        Assert.Equal("interrupted", rig.Phase);
        var kept = rig.Audios[0];
        Assert.False(kept.File.Released);
        rig.Run(WindowKind.Voice, UiCommands.Close);
        Assert.True(kept.File.Released);
        Assert.Equal(0, rig.Leases.ActiveCount);
    }

    [Fact] // REC02 minimize: the window hides, the recording goes on, the tray shows it, and the tray entry brings the window back
    public async Task Minimize_keeps_recording_visible_in_the_tray()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        rig.Session.Level(0.3, 4.0);
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.Minimize).Ok);
        Assert.Contains("hide:Voice", rig.Platform.Calls);
        Assert.False(rig.Session.CancelRequested);
        Assert.True(rig.Shell.IsRecording);
        rig.Session.Level(0.3, 5.0);
        Assert.Equal(("recording", TimeSpan.FromSeconds(5)), rig.TrayStatus[^1]);
        var item = rig.Shell.TrayModel().Items.Single(i => i.Id == "voice");
        Assert.True(item.Enabled);
        Assert.Equal("recording", item.Status);
        // Even if the ASR key vanished meanwhile, the running recording stays reachable.
        rig.AsrReady = false;
        Assert.True(rig.Shell.TrayModel().Items.Single(i => i.Id == "voice").Enabled);
        Assert.Equal("ok", rig.TrayOpen("voice"));
        Assert.True(await Eventually.WaitAsync(() => rig.Snapshot(WindowKind.Voice).Voice?.Phase == "recording" && rig.Snapshot(WindowKind.Voice).Voice!.ElapsedMs == 5000));
        // The hidden window is not fed meter events, but the state is current when it comes back.
        int hiddenEvents = rig.VoiceEvents().Count;
        rig.Run(WindowKind.Voice, UiCommands.Minimize);
        for (int i = 0; i < 20; i++) rig.Session.Level(0.5, 6 + i * 0.05);
        Assert.Equal(hiddenEvents, rig.VoiceEvents().Count);
        Assert.Equal(6950, rig.Shell.VoiceWindowView!.ElapsedMs);
    }

    [Fact] // REC02 exit: the running recording is stopped and the device released
    public void Exit_releases_the_device()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        var session = rig.Session;
        rig.Shell.ReleaseAudio();
        Assert.True(session.CancelRequested && session.Disposed);
        Assert.False(rig.Shell.IsRecording);
        Assert.Equal((null, TimeSpan.Zero), rig.TrayStatus[^1]);
    }

    [Fact] // after close the device is free again: a later open records without a busy error
    public void The_device_is_free_after_close()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        Assert.Single(rig.Capture.Sessions);
        rig.Run(WindowKind.Voice, UiCommands.Close);
        Assert.False(rig.Shell.IsRecording);
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok); // a later open can record again (device free)
        Assert.True(rig.Shell.IsRecording);
    }

    // ---------- interruption (REC03) ----------

    [Theory] // unplug, default change and sleep each keep the captured part and say why; the user transcribes it
    [InlineData(MicFailure.DeviceRemoved, "deviceRemoved")]
    [InlineData(MicFailure.DefaultChanged, "defaultChanged")]
    [InlineData(MicFailure.Sleep, "sleep")]
    public async Task Interruption_keeps_the_recorded_part_and_can_be_transcribed(MicFailure failure, string reason)
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        rig.Session.Level(0.3, 1.5);
        rig.Session.End(RecordingStatus.Interrupted, failure);
        Assert.True(await rig.WaitPhase("interrupted"));
        var view = rig.Shell.VoiceWindowView!;
        Assert.Equal(("interrupted", reason, true), (view.Phase, view.Reason, view.CanTranscribe));
        Assert.False(rig.Shell.IsRecording);
        Assert.Equal(1, rig.Capture.Starts); // never silently switches device or restarts
        Assert.Equal(0, rig.Provider.Calls);
        Assert.False(rig.Audios[0].File.Released);
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.TranscribeRecorded).Ok);
        Assert.True(await rig.WaitPhase("transcribed"));
        Assert.Equal(["Hello from the microphone"], rig.Translated);
        Assert.Equal(1, rig.Capture.Starts);
        Assert.False(rig.Run(WindowKind.Voice, UiCommands.TranscribeRecorded).Ok); // nothing left to transcribe
    }

    [Fact] // re-record after an interruption drops the kept part and records on the (new) default device
    public async Task Re_record_after_interruption_deletes_the_kept_part()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        rig.Session.End(RecordingStatus.Interrupted, MicFailure.DeviceRemoved);
        Assert.True(await rig.WaitPhase("interrupted"));
        var kept = rig.Audios[0];
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        Assert.True(kept.File.Released);
        Assert.Equal("recording", rig.Phase);
        Assert.Equal(2, rig.Capture.Starts);
    }

    [Fact] // an interruption that kept only silence offers re-record only
    public async Task Interruption_with_only_silence_keeps_nothing()
    {
        using var rig = new Rig();
        rig.AudioSilent = true;
        rig.OpenVoice();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        rig.Session.End(RecordingStatus.Interrupted, MicFailure.Sleep);
        Assert.True(await rig.WaitPhase("interrupted"));
        Assert.Equal(("interrupted", false), (rig.Phase, rig.Shell.VoiceWindowView!.CanTranscribe));
        Assert.True(rig.Audios[0].File.Released);
    }

    // ---------- command scoping ----------

    [Fact] // the audio commands belong to the voice window only
    public void Audio_commands_are_voice_window_only()
    {
        foreach (var name in new[] { UiCommands.StartRecording, UiCommands.PauseRecording, UiCommands.StopRecording, UiCommands.CancelRecording, UiCommands.TranscribeRecorded })
        {
            Assert.True(UiCommands.IsAllowed(WindowKind.Voice, name), name);
            foreach (var other in Enum.GetValues<WindowKind>().Where(k => k != WindowKind.Voice)) Assert.False(UiCommands.IsAllowed(other, name), $"{other} {name}");
        }
        Assert.True(UiCommands.IsAllowed(WindowKind.Voice, UiCommands.Minimize));
    }

    // ---------- F13.2 system audio in the same window ----------

    [Fact] // the system-audio entry needs the same ASR service as Voice: greyed with feature.noService.asr until it can run
    public void System_audio_entry_needs_a_usable_asr_service()
    {
        using var rig = new Rig(asrReady: false);
        var item = rig.Shell.TrayModel().Items.Single(i => i.Id == "system-audio");
        Assert.False(item.Enabled);
        Assert.Equal("feature.noService.asr", item.ReasonKey);
        Assert.False(rig.Platform.Registered.ContainsKey("audioTranslate"));
        rig.Shell.OnHotkey("audioTranslate");
        Assert.DoesNotContain("show:Voice:True", rig.Platform.Calls);
        Assert.Equal(new ErrorLineView("feature.noService.asr", "settings"), Assert.Single(rig.ErrorBar()!.Lines));
        Assert.Equal("unavailable", rig.TrayOpen("system-audio"));
        Assert.Equal(0, rig.SystemCapture.Starts);
        rig.AsrReady = true;
        Assert.True(rig.Shell.TrayModel().Items.Single(i => i.Id == "system-audio").Enabled);
        Assert.Equal("ok", rig.TrayOpen("system-audio"));
        Assert.Contains("show:Voice:True", rig.Platform.Calls);
    }

    [Fact] // REC01/REC04 UI: loopback source, never the microphone, the own-sound notice, same ASR job and translation session
    public async Task System_audio_records_the_output_and_transcribes_through_the_same_job()
    {
        using var rig = new Rig();
        rig.OpenSystemAudio();
        var idle = rig.Snapshot(WindowKind.Voice).Voice!;
        Assert.Equal(("idle", "systemAudio", true), (idle.Phase, idle.Source, idle.OwnPlayback));
        Assert.Equal("Alt+B", idle.Hotkey);
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        Assert.Equal((1, 0), (rig.SystemCapture.Starts, rig.Capture.Starts));
        rig.SystemSession.Level(0.5, 2.0);
        var recording = rig.Shell.VoiceWindowView!;
        Assert.Equal(("recording", "systemAudio", true, 2000L), (recording.Phase, recording.Source, recording.OwnPlayback, recording.ElapsedMs));
        Assert.Equal(0, rig.Provider.Calls); // nothing is sent while recording
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StopRecording).Ok);
        Assert.True(await rig.WaitPhase("transcribed"));
        var view = rig.Shell.VoiceWindowView!;
        Assert.Equal(("systemAudio", true, true), (view.Source, view.OwnPlayback, view.Translated));
        Assert.Equal(["Hello from the microphone"], rig.Translated);
        Assert.Equal(1, rig.Provider.Calls);
        Assert.True(await Eventually.WaitAsync(() => rig.Audios[0].File.Released && rig.Leases.ActiveCount == 0));
        Assert.Equal("recording", rig.TrayStatus.First(s => s.Phase is not null).Phase);
    }

    [Fact] // the one window serves both sources: an idle or finished window switches, a running recording is only brought forward
    public async Task Hotkeys_switch_the_source_when_idle_and_keep_a_running_recording()
    {
        using var rig = new Rig();
        rig.OpenVoice();
        Assert.Equal(("microphone", false), (rig.Shell.VoiceWindowView!.Source, rig.Shell.VoiceWindowView.OwnPlayback));
        rig.OpenSystemAudio();
        Assert.Equal(("systemAudio", true), (rig.Shell.VoiceWindowView!.Source, rig.Shell.VoiceWindowView.OwnPlayback));
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        Assert.Equal((1, 0), (rig.SystemCapture.Starts, rig.Capture.Starts));
        rig.Shell.OnHotkey("voiceTranslate"); // a different source while recording: the task stays
        Assert.Equal(("recording", "systemAudio"), (rig.Phase, rig.Shell.VoiceWindowView!.Source));
        Assert.True(rig.Shell.IsRecording);
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StopRecording).Ok);
        Assert.True(await rig.WaitPhase("transcribed"));
        rig.Shell.OnHotkey("voiceTranslate"); // finished: switch back and start empty
        Assert.Equal(("idle", "microphone"), (rig.Phase, rig.Shell.VoiceWindowView!.Source));
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        Assert.Equal((1, 1), (rig.SystemCapture.Starts, rig.Capture.Starts));
    }

    [Theory] // loopback start errors keep their own codes
    [InlineData(MicFailure.NoDevice, "loopback.noDevice")]
    [InlineData(MicFailure.Denied, "loopback.denied")]
    [InlineData(MicFailure.Failed, "loopback.failed")]
    public void System_audio_start_errors_use_loopback_codes(MicFailure failure, string code)
    {
        using var rig = new Rig();
        rig.OpenSystemAudio();
        rig.SystemCapture.Next.Enqueue(RecordingStartResult.Failed(failure, AudioSourceKind.SystemLoopback));
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        var view = rig.Shell.VoiceWindowView!;
        Assert.Equal(("error", code, "systemAudio"), (view.Phase, view.ErrorCode, view.Source));
        Assert.Equal(0, rig.Capture.Starts);
    }

    [Fact] // a loopback failure after start with no code reports loopback.failed, not a microphone error
    public async Task System_audio_failure_without_code_is_a_loopback_failure()
    {
        using var rig = new Rig();
        rig.OpenSystemAudio();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        rig.SystemSession.End(RecordingStatus.Failed);
        Assert.True(await rig.WaitPhase("error"));
        Assert.Equal("loopback.failed", rig.Shell.VoiceWindowView!.ErrorCode);
    }

    [Fact] // REC03 with the output device: a switch keeps the part, offers transcribe/re-record, never follows the new device
    public async Task System_audio_device_switch_keeps_the_part_and_rerecords_on_loopback()
    {
        using var rig = new Rig();
        rig.OpenSystemAudio();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        rig.SystemSession.Level(0.3, 3.0);
        rig.SystemSession.End(RecordingStatus.Interrupted, MicFailure.DefaultChanged);
        Assert.True(await rig.WaitPhase("interrupted"));
        var view = rig.Shell.VoiceWindowView!;
        Assert.Equal(("defaultChanged", true, true, "systemAudio"), (view.Reason, view.CanTranscribe, view.OwnPlayback, view.Source));
        Assert.Equal(1, rig.SystemCapture.Starts);
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.TranscribeRecorded).Ok);
        Assert.True(await rig.WaitPhase("transcribed"));
        Assert.Equal("systemAudio", rig.Shell.VoiceWindowView!.Source);
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok); // record again: loopback again, never the microphone
        Assert.Equal((2, 0), (rig.SystemCapture.Starts, rig.Capture.Starts));
    }

    [Fact] // the tray marks the entry of the source that is recording, not the other one; close releases the loopback
    public void Tray_marks_the_recording_entry_and_close_releases_the_loopback()
    {
        using var rig = new Rig();
        rig.OpenSystemAudio();
        rig.Run(WindowKind.Voice, UiCommands.StartRecording);
        var items = rig.Shell.TrayModel().Items;
        Assert.Equal("recording", items.Single(i => i.Id == "system-audio").Status);
        Assert.Null(items.Single(i => i.Id == "voice").Status);
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.Close).Ok);
        Assert.False(rig.Shell.IsRecording);
        Assert.True(rig.SystemSession.CancelRequested && rig.SystemSession.Disposed);
    }
}
