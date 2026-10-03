using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;

namespace Susu.Ui;

/// <summary>
/// F14.4 Transcribe window (DESIGN Transcribe artboard, PLAN 6.2, VID04): pick or drop a file (host token, probed: name, duration,
/// streams, unsupported-encoding error), start, confirm the upload, watch stage progress with an incremental cue list, pause or
/// resume, switch a service after a quota stop, then export. The page sees names, numbers and cues only: no path and no media token.
/// <list type="bullet">
/// <item>Close or Esc cancels a running job and releases the decode and upload; the finished or partial cues stay in
/// <see cref="VideoResultStore"/> for the process, so reopening shows them and export still works. Nothing resumes after a restart.</item>
/// <item>The upload confirmation (T06) is answered by the page (<c>Transcription.Confirm</c>); closing the window answers no.</item>
/// <item>Export goes through <see cref="SubtitleExporter"/> and the save dialog; bad cues are reported, never repaired.</item>
/// </list>
/// </summary>
public sealed partial class ShellCoordinator
{
    private VideoJobs? videoJobs;
    private TranscribeView? transcribeView;
    private long transcribeViews;
    private string? pickedToken, pickedName;
    private MediaProbe? pickedProbe;
    private VideoJob? videoJob;
    private TaskCompletionSource<bool>? uploadAnswer;
    private TranscribeUploadView? uploadView;
    private TranscribeExportView? lastExport;
    private long transcribeEpoch;

    /// <summary>F14.4: the file open dialog (a token for the chosen file). Null: no picker, the pick command answers unavailable.</summary>
    public IMediaPicker? MediaPicker { get; set; }

    /// <summary>F14.4: the token table the picker and a dropped file use. The page never sees a path.</summary>
    public IMediaTokens? MediaTokens { get; set; }

    /// <summary>F14.4: the save-file dialog for subtitle export.</summary>
    public ISubtitleSavePicker? SubtitleSavePicker { get; set; }

    /// <summary>F14.2/F14.4: the video job service (own ASR and translation selection, results kept for the process). The window answers its upload confirmation.</summary>
    public VideoJobs? VideoJobs
    {
        get => videoJobs;
        set
        {
            if (videoJobs is not null) videoJobs.JobStarted -= OnVideoJobStarted;
            videoJobs = value;
            if (videoJobs is null) return;
            videoJobs.Confirmation = new UploadConfirmation(this);
            videoJobs.JobStarted += OnVideoJobStarted;
        }
    }

    /// <summary>The Transcribe window's current view without cues (null before it was first opened); for tests and diagnostics.</summary>
    public TranscribeView? TranscribeWindowView => transcribeView;

    private string TranscribeHotkey() => config.State.Effective.Hotkeys.Chords.TryGetValue("videoTranscribe", out var chord) ? chord : "";

    private TranscribeView TranscribeBase(string phase) => new(++transcribeViews, phase, null, pickedName, pickedProbe is null ? null : (long)pickedProbe.Duration.TotalMilliseconds,
        pickedProbe?.HasVideo ?? false, StreamsOf(pickedProbe), null, null, "none", 0, 0, 0, 0, 0, null, null, null, null, [], [], null, TranscribeHotkey());

    private static TranscribeStreamView[] StreamsOf(MediaProbe? probe)
        => probe is null ? [] : [.. probe.AudioStreams.Select(s => new TranscribeStreamView(s.Codec, s.SampleRate, s.Channels, s.Decodable, ReferenceEquals(s, probe.Selected)))];

    /// <summary>The hotkey or tray item: shows the window; whatever it holds (a running job, a finished result) stays.</summary>
    private void OpenTranscribe()
    {
        if (transcribeView is null || transcribeView is { Phase: "cancelled", CueCount: 0 }) ResetTranscribe();
        else if (videoJob is { } job) SetTranscribeView(JobView(job), quiet: true);
        _ = OpenAsync(WindowKind.Transcribe, activate: true);
    }

    private void SetTranscribeView(TranscribeView view, bool quiet = false)
    {
        transcribeView = view;
        if (quiet) return;
        if (windows.TryGetValue(WindowKind.Transcribe, out var session) && session.Ready && lifecycle.Visible.Contains(WindowKind.Transcribe))
            Send(WindowKind.Transcribe, session, UiMessageKind.Event, "transcribe", null, JsonSerializer.SerializeToElement(view, ContractsJson.Default.TranscribeView));
    }

    private TranscribeView? TranscribeForSnapshot()
    {
        if (transcribeView is not { } view) return null;
        return videoJob is { } job && videoJobs?.Results.Get(job.Id) is { } result ? view with { Cues = [.. result.Cues.Select(CueOf)] } : view;
    }

    private static TranscribeCueView CueOf(VideoCue c) => new(c.Id, c.Start, c.End, c.Original, c.Translation, c.TranslationError);

    /// <summary>Drops whatever the window holds (a picked file, an old result view) and shows it empty.</summary>
    private void ResetTranscribe()
    {
        transcribeEpoch++;
        if (pickedToken is not null && videoJob is null or { State.IsTerminal: true }) MediaTokens?.Revoke(pickedToken);
        pickedToken = null; pickedName = null; pickedProbe = null; videoJob = null; uploadView = null; lastExport = null;
        SetTranscribeView(TranscribeBase("idle"));
    }

    // ---------- picking ----------

    private async Task<CommandResult> PickMediaAsync()
    {
        if (MediaPicker is not { } picker) return new CommandResult(false, "unavailable");
        if (videoJob is { State.IsTerminal: false }) return new CommandResult(false, VideoErrors.Busy);
        string? token = await picker.PickAsync(CancellationToken.None);
        if (token is null) return Ok(); // cancelled dialog: nothing changes
        await AcceptMediaAsync(token);
        return Ok();
    }

    /// <summary>
    /// A file dropped on the Transcribe window (the native layer calls this with the dropped path; the page never does). Refused while
    /// a job runs; a non-file or missing path shows the not-found error.
    /// </summary>
    public async Task<CommandResult> OnMediaDroppedAsync(string path)
    {
        if (MediaTokens is not { } tokens) return new CommandResult(false, "unavailable");
        if (videoJob is { State.IsTerminal: false }) return new CommandResult(false, VideoErrors.Busy);
        string? token = tokens.Issue(path);
        if (token is null)
        {
            ResetTranscribe();
            SetTranscribeView(TranscribeBase("pickError") with { ErrorCode = MediaErrors.NotFound });
            return Ok();
        }
        await AcceptMediaAsync(token);
        return Ok();
    }

    private async Task AcceptMediaAsync(string token)
    {
        ResetTranscribe();
        long epoch = transcribeEpoch;
        pickedToken = token;
        pickedName = MediaTokens?.DisplayName(token);
        if (videoJobs is null) { SetTranscribeView(TranscribeBase("pickError") with { ErrorCode = "unavailable" }); return; }
        MediaProbe probe;
        try { probe = await videoJobs.ProbeAsync(token); }
        catch (MediaDecodeException e)
        {
            if (epoch == transcribeEpoch) SetTranscribeView(TranscribeBase("pickError") with { ErrorCode = e.Code });
            return;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Diagnostic?.Invoke($"transcribe.probe.exception {e.GetType().Name}");
            if (epoch == transcribeEpoch) SetTranscribeView(TranscribeBase("pickError") with { ErrorCode = MediaErrors.OpenFailed });
            return;
        }
        if (epoch != transcribeEpoch) return; // closed or replaced while probing
        pickedProbe = probe;
        string? error = probe.AudioStreams.Count == 0 ? MediaErrors.NoAudio : probe.Selected is null ? MediaErrors.UnsupportedEncoding : null;
        SetTranscribeView(TranscribeBase(error is null ? "picked" : "pickError") with { ErrorCode = error });
    }

    // ---------- job ----------

    private CommandResult StartTranscription()
    {
        if (videoJobs is null || pickedToken is null || transcribeView is not { Phase: "picked" }) return new CommandResult(false, "unavailable");
        var general = config.State.Effective.General;
        var start = videoJobs.Start(pickedToken, general.SourceLanguage, general.TargetLanguage);
        if (start.Job is null)
        {
            // Nothing was probed or sent: the file stays picked, with the reason (no ASR, no timecodes, no translation service, busy).
            SetTranscribeView(TranscribeBase("picked") with { ErrorCode = start.Refusal });
            return Ok();
        }
        return Ok();
    }

    private void OnVideoJobStarted(VideoJob job)
    {
        videoJob = job;
        lastExport = null;
        uploadView = null;
        job.StateChanged += _ => platform.StartTimer(TimeSpan.Zero, () => OnVideoState(job));
        job.CueChanged += cue => platform.StartTimer(TimeSpan.Zero, () => OnVideoCue(job, cue));
        SetTranscribeView(JobView(job));
    }

    private void OnVideoState(VideoJob job)
    {
        if (!ReferenceEquals(job, videoJob)) return;
        // The job raises StateChanged from several threads outside its own lock. Read the state and publish the view under one lock so a
        // callback holding an older state cannot overwrite the view built from a newer (e.g. terminal) one.
        lock (videoStateGate)
        {
            if (!ReferenceEquals(job, videoJob)) return;
            if (job.State.Phase != VideoJobPhase.AwaitingConfirm) uploadView = null;
            SetTranscribeView(JobView(job));
        }
    }

    private readonly object videoStateGate = new();

    private void OnVideoCue(VideoJob job, VideoCue cue)
    {
        if (!ReferenceEquals(job, videoJob)) return;
        if (windows.TryGetValue(WindowKind.Transcribe, out var session) && session.Ready && lifecycle.Visible.Contains(WindowKind.Transcribe))
            Send(WindowKind.Transcribe, session, UiMessageKind.Event, "transcribe.cue", null, JsonSerializer.SerializeToElement(CueOf(cue), ContractsJson.Default.TranscribeCueView));
    }

    private TranscribeView JobView(VideoJob job)
    {
        var st = job.State;
        if (st.Phase == VideoJobPhase.Cancelled && st.ErrorCode == VideoErrors.NotConfirmed) return TranscribeBase("picked"); // declined: the file stays picked
        string phase = st.Phase switch
        {
            VideoJobPhase.AwaitingConfirm => "confirm",
            VideoJobPhase.Paused => "paused",
            VideoJobPhase.QuotaExhausted => "quota",
            VideoJobPhase.Done => "done",
            VideoJobPhase.Failed => "failed",
            VideoJobPhase.Cancelled => "cancelled",
            _ => "running",
        };
        string stage = st.Phase is VideoJobPhase.Created or VideoJobPhase.Probing ? "probe" : st.Stage switch { VideoStage.Probe => "probe", VideoStage.Asr => "asr", VideoStage.Translate => "translate", _ => "none" };
        var view = TranscribeBase(phase);
        return view with
        {
            JobId = job.Id, FileName = job.DisplayName, ErrorCode = st.ErrorCode, ErrorKind = st.Error?.Kind, Stage = stage,
            SlicesDone = st.SlicesDone, SlicesTotal = st.SlicesTotal, CueCount = st.Cues, Translated = st.Translated, FailedCues = st.Failed,
            AsrService = st.AsrService, TranslationService = st.TranslationService,
            QuotaSide = phase == "quota" ? st.Quota == VideoQuotaSide.Asr ? "asr" : "translation" : null,
            Upload = phase == "confirm" ? uploadView : null,
            Choices = phase == "quota" ? SwitchChoices(st) : [],
            Export = lastExport,
        };
    }

    private TranscribeChoiceView[] SwitchChoices(VideoJobState st)
    {
        var s = config.State.Effective;
        if (st.Quota == VideoQuotaSide.Asr)
        {
            var slot = SpeechSlotOf(s, SpeechSlot.VideoAsr);
            return [.. slot.Choices.Where(c => c.Selectable && (c.Native || c.Availability == nameof(Availability.Ready)))
                .SelectMany(c => c.Models.Where(m => m.Selectable).Select(m => new TranscribeChoiceView("asr", c.InstanceId, m.Id, c.InstanceId == st.AsrService && m.Id == slot.Model)))];
        }
        return [.. VideoTranslatorChoices(s).Select(id => new TranscribeChoiceView("translation", id, "", id == st.TranslationService))];
    }

    /// <summary>Enabled translation services in the user's order: what the video translation selection can name (SetSpeechB).</summary>
    private static string[] VideoTranslatorChoices(AppSettings s)
    {
        var enabled = s.Services.Where(x => x.Capability == Capability.Translate && x.Enabled).Select(x => x.ServiceId).ToList();
        return [.. s.TranslationOrder.Where(enabled.Contains), .. enabled.Where(id => !s.TranslationOrder.Contains(id))];
    }

    private CommandResult ConfirmTranscription(TranscribeConfirmRequest request)
    {
        if (uploadAnswer is not { } answer) return new CommandResult(false, "unavailable");
        uploadAnswer = null;
        answer.TrySetResult(request.Accept);
        return Ok();
    }

    private CommandResult PauseTranscription()
    {
        if (videoJob is not { State.Phase: VideoJobPhase.Running } job) return new CommandResult(false, "unavailable");
        job.Pause();
        return Ok();
    }

    private CommandResult ResumeTranscription()
    {
        if (videoJob is not { } job || !job.Resume()) return new CommandResult(false, "unavailable");
        return Ok();
    }

    private CommandResult CancelTranscription()
    {
        if (videoJob is not { State.IsTerminal: false } job) return new CommandResult(false, "unavailable");
        AnswerUpload(false);
        job.Cancel();
        return Ok();
    }

    private void AnswerUpload(bool accept)
    {
        var answer = uploadAnswer;
        uploadAnswer = null;
        answer?.TrySetResult(accept);
    }

    /// <summary>Transcription.ChangeTranslator: saves the video selection (translation service, or ASR service and model) and applies it to the stopped job.</summary>
    private CommandResult ChangeTranscribeService(TranscribeSwitchRequest request)
    {
        if (videoJob is not { State.IsTerminal: false } job || videoJobs is null) return new CommandResult(false, "unavailable");
        var s = config.State.Effective;
        var state = config.State;
        if (request.Side == "translation")
        {
            if (!VideoTranslatorChoices(s).Contains(request.Id)) return new CommandResult(false, "range");
            var saved = Outcome(config.Save(s with { Speech = s.Speech with { VideoTranslator = request.Id } }, state.Revision, state.FileHash));
            if (!saved.Ok) return saved;
            return videoJobs.CurrentTranslator() is { } next && next.ServiceId == request.Id && job.SwitchTranslator(next) ? Ok() : new CommandResult(false, "unavailable");
        }
        if (request.Side != "asr") throw new ArgumentException("side");
        var selection = new SpeechSelection(request.Id, request.Model);
        if (SpeechCatalog.Check(SpeechSlot.VideoAsr, selection) is { } problem) return new CommandResult(false, problem);
        var done = Outcome(config.Save(s with { Speech = s.Speech.With(SpeechSlot.VideoAsr, selection) }, state.Revision, state.FileHash));
        if (!done.Ok) return done;
        return videoJobs.CurrentAsr() is { } asrNext && job.SwitchAsr(asrNext) ? Ok() : new CommandResult(false, SpeechCatalog.NeedsTimecodes);
    }

    // ---------- export ----------

    private async Task<CommandResult> ExportTranscriptAsync(TranscribeExportRequest request)
    {
        if (videoJob is not { } job || videoJobs is null) return new CommandResult(false, "unavailable");
        var st = job.State;
        if (st.Phase is VideoJobPhase.Created or VideoJobPhase.Probing or VideoJobPhase.AwaitingConfirm or VideoJobPhase.Running) return new CommandResult(false, "unavailable");
        SubtitleFormat format = request.Format switch { "srt" => SubtitleFormat.Srt, "vtt" => SubtitleFormat.Vtt, "txt" => SubtitleFormat.Txt, _ => throw new ArgumentException("format") };
        SubtitleMode mode = request.Mode switch
        {
            "original" => SubtitleMode.Original, "translation" => SubtitleMode.Translation, "bilingual" => SubtitleMode.BilingualOriginalFirst,
            "bilingualTranslationFirst" => SubtitleMode.BilingualTranslationFirst, _ => throw new ArgumentException("mode"),
        };
        var result = await new SubtitleExporter(videoJobs.Results, SubtitleSavePicker).ExportAsync(job.Id, mode, format, CancellationToken.None);
        if (result.Cancelled) return Ok(); // the dialog was cancelled: nothing written, nothing to report
        if (!ReferenceEquals(job, videoJob)) return Ok();
        lastExport = new TranscribeExportView(request.Format, request.Mode, result.Ok ? result.Path : null, result.Ok ? null : result.Error, result.Retryable, result.Exported, result.Overlaps,
            [.. result.Issues.Select(i => new TranscribeIssueView(i.CueId, i.Code))]);
        SetTranscribeView((transcribeView ?? TranscribeBase("done")) with { Export = lastExport });
        return Ok();
    }

    // ---------- window close and exit ----------

    /// <summary>
    /// Close or Esc: a running job is cancelled (decode, upload and slices go; the cues so far stay for reopen and export), a pending
    /// confirmation is answered no, and a picked file is dropped.
    /// </summary>
    private void CancelTranscribeWindow()
    {
        AnswerUpload(false);
        if (videoJob is { State.IsTerminal: false } job) { job.Cancel(); return; }
        if (videoJob is null && transcribeView is { Phase: not "idle" }) ResetTranscribe();
    }

    private sealed class UploadConfirmation(ShellCoordinator shell) : IVideoUploadConfirmation
    {
        public Task<bool> ConfirmAsync(VideoUploadNotice notice, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.Register(() => answer.TrySetResult(false));
            shell.platform.StartTimer(TimeSpan.Zero, () => shell.OnUploadRequested(notice, answer));
            return AwaitAsync(answer, registration);
        }

        private static async Task<bool> AwaitAsync(TaskCompletionSource<bool> answer, CancellationTokenRegistration registration)
        {
            try { return await answer.Task.ConfigureAwait(false); }
            finally { registration.Dispose(); }
        }
    }

    private void OnUploadRequested(VideoUploadNotice notice, TaskCompletionSource<bool> answer)
    {
        if (videoJob is not { } job || job.Id != notice.JobId || answer.Task.IsCompleted) { answer.TrySetResult(false); return; }
        uploadAnswer = answer;
        uploadView = new TranscribeUploadView(notice.DisplayName, (long)notice.Duration.TotalMilliseconds, notice.HasVideo, notice.AsrService, notice.AsrModel, notice.UploadBytesEstimate,
            notice.ChunkSecondsLimit, notice.ChunkBytesLimit, notice.TranslationService, notice.TranslationServiceName, notice.TranslationLimits.MaxInput, notice.TranslationLimits.MaxItems,
            notice.TranslationQuotaNoteKey, notice.EstimatedCharacters, notice.EstimatedPrice);
        SetTranscribeView(JobView(job));
    }
}
