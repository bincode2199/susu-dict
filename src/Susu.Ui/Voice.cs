using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;

namespace Susu.Ui;

/// <summary>
/// F12.3 voice window (DESIGN Voice artboard, PLAN 6.2, REC01-REC03): record, then transcribe, then translate. The window
/// follows one task at a time: the F12.1 recording session (level meter and captured time, pause, stop, cancel), then the F12.2
/// <see cref="AsrJob"/> (transcribing, transcribed, no speech, failed, unavailable), then the window's own translation session
/// (T02). Nothing is transcribed while recording: the audio goes to the ASR service only after the recording ended.
/// <list type="bullet">
/// <item>Close or Esc cancels the task: recording is discarded and the device released, a running transcription is cancelled, kept
/// audio is deleted. Minimize hides the window and keeps recording; the tray shows the recording state (DESIGN 窗口关闭).</item>
/// <item>A device that went away (unplugged, default changed, sleep) leaves the captured part kept and the window in
/// <c>interrupted</c> with the reason and two choices: transcribe what was recorded, or record again (DESIGN 录制中断, REC03).</item>
/// <item>The page sees numbers and phases only; the audio stays in a host lease owned by the job (B02).</item>
/// </list>
/// </summary>
public sealed partial class ShellCoordinator
{
    private AsrJob? asr;
    private VoiceView? voiceView;
    private long voiceViews, voiceEpoch;
    private IRecordingSession? recording;
    private bool recordingStarting;
    private RecordedAudio? heldAudio;
    private long voiceGeneration = -1, voiceTranscribeStartedAt, voiceLevelSentAt;
    private int trayTipSecond = -1;
    private AudioSourceKind voiceSource = AudioSourceKind.Microphone; // F13.2: the one window records the microphone or system audio

    private bool SystemAudioSource => voiceSource == AudioSourceKind.SystemLoopback;
    private static string SourceName(AudioSourceKind kind) => kind == AudioSourceKind.SystemLoopback ? "systemAudio" : "microphone";
    private string VoiceFeatureId => SystemAudioSource ? FeatureRegistry.Ids.SystemAudio : FeatureRegistry.Ids.Voice;
    private string DefaultStartError => SystemAudioSource ? "loopback.failed" : "mic.failed";

    /// <summary>Longest recording the view reports (the recorder enforces the real limit; DESIGN 10 minutes).</summary>
    public TimeSpan RecordingLimit { get; set; } = AudioCaptureCoordinator.MaxDuration;

    /// <summary>
    /// F12.2/F12.3: the ASR job. Once set, the shell owns every recording: it hands the finished audio to the job, which releases
    /// the lease however transcription ends; close, Esc and <see cref="ReleaseAudio"/> cancel it.
    /// </summary>
    public AsrJob? Asr
    {
        get => asr;
        set
        {
            if (asr is not null) asr.StateChanged -= OnAsrStateFromJob;
            asr = value;
            if (asr is not null) asr.StateChanged += OnAsrStateFromJob;
        }
    }

    /// <summary>The voice window's current view (null before it was first opened); for tests and diagnostics.</summary>
    public VoiceView? VoiceWindowView => voiceView;

    /// <summary>A recording is running or paused (the tray shows it while the window is minimized).</summary>
    public bool IsRecording => recording is not null;

    /// <summary>
    /// The recording state for the notification-area icon (DESIGN 窗口关闭: minimized recording stays visible): the phase
    /// (<c>recording</c> or <c>paused</c>, null when no recording runs) and the captured time. Raised on the message thread,
    /// at most once per second while the time advances.
    /// </summary>
    public event Action<string?, TimeSpan>? RecordingStatusChanged;

    private string VoiceHotkey() => config.State.Effective.Hotkeys.Chords.TryGetValue(SystemAudioSource ? "audioTranslate" : "voiceTranslate", out var chord) ? chord : "";

    private VoiceView IdleVoice() => new(++voiceViews, "idle", 0, (long)RecordingLimit.TotalMilliseconds, 0, false, null, null, null, null, null, false, VoiceHotkey(), false, null, null, null, null, SourceName(voiceSource), SystemAudioSource);

    private VoiceView WithPhase(string phase, Func<VoiceView, VoiceView>? edit = null)
    {
        var current = voiceView ?? IdleVoice();
        var next = current with { Phase = phase, Level = 0, Silent = false, Reason = null, ErrorCode = null, ErrorKind = null, CanTranscribe = false, Notice = null, Chunk = null, Chunks = null, Hotkey = VoiceHotkey() };
        return edit is null ? next : edit(next);
    }

    /// <summary>
    /// The voice or system-audio hotkey or tray item: shows the window (it keeps whatever task is running, e.g. a minimized
    /// recording). With no recording running and another source asked for, the window is emptied and switches source (F13.2).
    /// </summary>
    private void OpenVoice(AudioSourceKind source = AudioSourceKind.Microphone)
    {
        if (recording is null && !recordingStarting && (voiceSource != source || voiceView is null))
        {
            if (voiceSource != source) AbandonVoiceTask();
            voiceSource = source;
            SetVoiceView(IdleVoice());
        }
        voiceView ??= IdleVoice();
        _ = OpenAsync(WindowKind.Voice, activate: true);
    }

    private void SetVoiceView(VoiceView view, bool quiet = false)
    {
        voiceView = view;
        if (quiet) return;
        if (windows.TryGetValue(WindowKind.Voice, out var session) && session.Ready && lifecycle.Visible.Contains(WindowKind.Voice))
            Send(WindowKind.Voice, session, UiMessageKind.Event, "voice", null, JsonSerializer.SerializeToElement(view, ContractsJson.Default.VoiceView));
    }

    private void NotifyRecordingStatus()
    {
        if (recording is { } session)
            RecordingStatusChanged?.Invoke(session.Phase == RecordingPhase.Paused ? "paused" : "recording", session.Captured);
        else
            RecordingStatusChanged?.Invoke(null, TimeSpan.Zero);
    }

    // ---------- recording ----------

    private async Task<CommandResult> StartRecordingAsync()
    {
        var capture = SystemAudioSource ? SystemAudioCapture : AudioCapture;
        if (capture is null || Resolve(VoiceFeatureId).State != FeatureState.Available) return new CommandResult(false, "unavailable");
        if (recordingStarting) return new CommandResult(false, "busy");
        // Record again: whatever the window held (a recording, a running transcription, kept audio, an old result) is dropped (J01).
        AbandonVoiceTask();
        ReattachTranslation(WindowKind.Voice);
        if (windows.TryGetValue(WindowKind.Voice, out var page) && page.Ready && TranslationOf(WindowKind.Voice) is { } fresh)
            Send(WindowKind.Voice, page, UiMessageKind.Event, "translation", null, JsonSerializer.SerializeToElement(await fresh.SnapshotAsync(), ContractsJson.Default.TranslationSnapshot));
        long epoch = voiceEpoch;
        recordingStarting = true;
        RecordingStartResult start;
        try { start = await capture.StartAsync(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Diagnostic?.Invoke($"voice.start.exception {e.GetType().Name}");
            start = RecordingStartResult.Failed(MicFailure.Failed, voiceSource);
        }
        finally { recordingStarting = false; }
        if (start.Session is not { } session)
        {
            if (epoch == voiceEpoch) SetVoiceView(IdleVoice() with { Phase = "error", ErrorCode = start.ErrorCode ?? DefaultStartError });
            return Ok();
        }
        if (epoch != voiceEpoch) { session.Cancel(); session.Dispose(); return new CommandResult(false, "cancelled"); } // closed while the device was opening
        recording = session;
        session.LevelChanged += level => platform.StartTimer(TimeSpan.Zero, () => OnRecordingLevel(session, level));
        session.PhaseChanged += phase => platform.StartTimer(TimeSpan.Zero, () => OnRecordingPhase(session, phase));
        _ = WatchRecordingAsync(session);
        trayTipSecond = -1;
        SetVoiceView(IdleVoice() with { Phase = "recording" });
        NotifyRecordingStatus();
        return Ok();
    }

    private async Task WatchRecordingAsync(IRecordingSession session)
    {
        RecordingResult result;
        try { result = await session.Completion; }
        catch (Exception e) when (e is not OutOfMemoryException) { result = new RecordingResult(RecordingStatus.Failed, null, null, session.Source == AudioSourceKind.SystemLoopback ? "loopback.failed" : "mic.failed", session.Source); }
        platform.StartTimer(TimeSpan.Zero, () => OnRecordingEnded(session, result));
    }

    private void OnRecordingLevel(IRecordingSession session, RecordingLevel level)
    {
        if (!ReferenceEquals(recording, session) || voiceView is not { } view || view.Phase != "recording") return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool changed = view.Silent != level.Silent;
        var next = view with { ElapsedMs = (long)level.Captured.TotalMilliseconds, Level = Math.Clamp(level.Peak, 0, 1), Silent = level.Silent };
        // The meter is projected about ten times a second; a change of the no-voice notice goes out at once.
        bool send = changed || System.Diagnostics.Stopwatch.GetElapsedTime(voiceLevelSentAt, now).TotalMilliseconds >= 100;
        if (send) voiceLevelSentAt = now;
        SetVoiceView(next, quiet: !send);
        int second = (int)level.Captured.TotalSeconds;
        if (second != trayTipSecond) { trayTipSecond = second; NotifyRecordingStatus(); }
    }

    private void OnRecordingPhase(IRecordingSession session, RecordingPhase phase)
    {
        if (!ReferenceEquals(recording, session) || voiceView is not { } view) return;
        if (phase == RecordingPhase.Paused) SetVoiceView(view with { Phase = "paused", Level = 0, ElapsedMs = (long)session.Captured.TotalMilliseconds });
        else if (phase == RecordingPhase.Recording) SetVoiceView(view with { Phase = "recording" });
        NotifyRecordingStatus();
    }

    /// <summary>The recording ended (message thread). A session the user already cancelled or replaced is ignored; its audio goes.</summary>
    private void OnRecordingEnded(IRecordingSession session, RecordingResult result)
    {
        if (!ReferenceEquals(recording, session)) { result.Audio?.Dispose(); return; }
        recording = null;
        NotifyRecordingStatus();
        var view = voiceView ?? IdleVoice();
        long elapsed = (long)(result.Audio?.Duration ?? session.Captured).TotalMilliseconds;
        switch (result.Status)
        {
            case RecordingStatus.Cancelled:
                result.Audio?.Dispose();
                SetVoiceView(IdleVoice());
                return;
            case RecordingStatus.Failed:
                result.Audio?.Dispose();
                SetVoiceView(WithPhase("error", v => v with { ErrorCode = result.ErrorCode ?? DefaultStartError, ElapsedMs = elapsed }));
                return;
            case RecordingStatus.Interrupted:
            {
                // What was captured is kept; the user decides (REC03). Audio that is only silence is not worth transcribing.
                bool usable = result.Audio is { Silent: false };
                if (!usable) result.Audio?.Dispose();
                else heldAudio = result.Audio;
                SetVoiceView(WithPhase("interrupted", v => v with { Reason = ReasonName(result.Reason), CanTranscribe = usable, ElapsedMs = elapsed }));
                return;
            }
        }
        // Stopped by the user or cut at the limit: transcription starts only now.
        if (result.Audio is not { } audio) { SetVoiceView(WithPhase("noSpeech", v => v with { ElapsedMs = elapsed })); return; }
        BeginTranscription(audio, result.Status == RecordingStatus.LimitReached ? "limit" : null, elapsed);
    }

    private static string ReasonName(MicFailure? reason) => reason switch
    {
        MicFailure.DeviceRemoved => "deviceRemoved",
        MicFailure.DefaultChanged => "defaultChanged",
        MicFailure.Sleep => "sleep",
        _ => "failed",
    };

    private async Task<CommandResult> PauseRecordingAsync()
    {
        if (recording is not { } session) return new CommandResult(false, "unavailable");
        if (session.Phase == RecordingPhase.Paused) session.Resume(); else session.Pause();
        await Task.CompletedTask;
        return Ok();
    }

    private async Task<CommandResult> StopRecordingAsync()
    {
        if (recording is not { } session) return new CommandResult(false, "unavailable");
        await session.StopAsync();
        return Ok();
    }

    /// <summary>Audio.CancelRecording: drop whatever the window is doing (recording, transcription, kept audio) and show it empty again.</summary>
    private CommandResult CancelVoiceCommand()
    {
        AbandonVoiceTask();
        SetVoiceView(IdleVoice());
        return Ok();
    }

    /// <summary>Audio.TranscribeRecorded: transcribe the part of an interrupted recording that was kept.</summary>
    private CommandResult TranscribeRecorded()
    {
        if (heldAudio is not { } audio || voiceView is not { Phase: "interrupted" } view) return new CommandResult(false, "unavailable");
        heldAudio = null;
        BeginTranscription(audio, null, view.ElapsedMs);
        return Ok();
    }

    /// <summary>
    /// Stops everything the voice window does and releases it: the recording (the device is released and the file goes), the running
    /// transcription (its upload stops, no chunk file stays) and kept audio. Later results of the dropped task are ignored.
    /// </summary>
    private void AbandonVoiceTask()
    {
        voiceEpoch++;
        var session = recording;
        recording = null;
        if (session is not null)
        {
            session.Cancel();
            session.Dispose();
            NotifyRecordingStatus();
        }
        asr?.Cancel();
        voiceGeneration = long.MaxValue; // states of the cancelled job are not projected
        heldAudio?.Dispose();
        heldAudio = null;
    }

    /// <summary>Close or Esc on the voice window, and application exit: cancel the task and release the microphone.</summary>
    private void CancelVoice()
    {
        AbandonVoiceTask();
        if (voiceView is { Phase: not "idle" }) SetVoiceView(IdleVoice(), quiet: true);
    }

    /// <summary>Application exit: stops a recording, cancels transcription and releases the device and every audio lease (REC02).</summary>
    public void ReleaseAudio()
    {
        AbandonVoiceTask();
        voiceView = null;
    }

    // ---------- transcription ----------

    private void BeginTranscription(RecordedAudio audio, string? notice, long elapsed)
    {
        if (asr is not { } job) { audio.Dispose(); SetVoiceView(WithPhase("unavailable", v => v with { ElapsedMs = elapsed })); return; }
        voiceGeneration = job.State.Generation + 1; // states queued by an older job are not projected
        voiceTranscribeStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        SetVoiceView(WithPhase("transcribing", v => v with { ElapsedMs = elapsed, Notice = notice, ServiceId = config.State.Effective.Speech.Asr.Instance }));
        _ = TranscribeAsync(job, audio);
    }

    private async Task TranscribeAsync(AsrJob job, RecordedAudio audio)
    {
        try { await job.TranscribeAsync(audio); }
        catch (Exception e) { Diagnostic?.Invoke($"voice.exception {e.GetType().Name}"); }
    }

    private void OnAsrStateFromJob(AsrState state) => platform.StartTimer(TimeSpan.Zero, () => OnAsrState(state));

    /// <summary>Projects a job state onto the voice window (message thread). A state of an older job never replaces a newer one.</summary>
    private void OnAsrState(AsrState state)
    {
        if (voiceView is not { } view || state.Generation < voiceGeneration || state.Phase is AsrPhase.Idle or AsrPhase.Cancelled) return;
        if (view.Phase is not ("transcribing" or "transcribed" or "noSpeech" or "failed" or "unavailable")) return; // a new recording is on
        voiceGeneration = state.Generation;
        string phase = state.Phase switch
        {
            AsrPhase.Transcribing => "transcribing",
            AsrPhase.Transcribed => "transcribed",
            AsrPhase.NoSpeech => "noSpeech",
            AsrPhase.Failed => "failed",
            _ => "unavailable",
        };
        long? elapsed = state.Phase == AsrPhase.Transcribing ? null : (long)System.Diagnostics.Stopwatch.GetElapsedTime(voiceTranscribeStartedAt).TotalMilliseconds;
        SetVoiceView(view with
        {
            Phase = phase, ServiceId = state.ServiceId ?? view.ServiceId, Text = state.Text, Translated = state.Translated,
            ErrorKind = state.Phase == AsrPhase.Failed ? state.Error?.Kind ?? ErrorKind.Unavailable : null,
            Chunk = state.Progress is { } p ? p.Index : null, Chunks = state.Progress is { } q ? q.Total : null, TranscribeMs = elapsed,
            Hotkey = VoiceHotkey(),
        });
    }

    /// <summary>The voice hotkey arrived while no ASR service is usable (e.g. its key was removed since registration): say why.</summary>
    private void ReportVoiceUnavailable(string? reasonKey)
        => ShowErrorBar([new ErrorLineView(reasonKey ?? "feature.noService.asr", "settings")]);
}
