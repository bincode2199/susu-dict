using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Jobs;

/// <summary>Everything a video job is configured with; selections are independent of the microphone voice window (A02).</summary>
public sealed record VideoJobOptions(string From, string To, string? AsrLanguage = null, TimeSpan? AsrTimeout = null, TimeSpan? TranslateTimeout = null,
    TimeSpan? SliceLength = null, Func<string>? IdFactory = null);

/// <summary>
/// F14.2 video job (PLAN 6.2, 4.7.1, 4.7.2; TEST-PLAN T03–T07, A05–A07): media token → rolling slices (<see cref="MediaSession"/>) →
/// the video ASR service per slice, times moved to the media time axis → cues with stable ids → the video translation service
/// by id, in batches → an incremental, ordered cue list.
/// <list type="bullet">
/// <item>A timecode-less ASR is refused before anything is probed or sent; no time is ever made up (A03).</item>
/// <item>Nothing is uploaded until <see cref="IVideoUploadConfirmation"/> says yes (T06); cancel or no answer ends the job with no
/// upload.</item>
/// <item>Quota or rate exhaustion of either service parks the job in <see cref="VideoJobPhase.QuotaExhausted"/> with no new request.
/// <see cref="SwitchTranslator"/> / <see cref="SwitchAsr"/> then <see cref="Resume"/> do only what is missing: ASR slices already
/// transcribed are never re-sent, translated cues are never re-translated (T07).</item>
/// <item>One loop owns all the work. Pause takes effect between requests (a request in flight completes). Cancel stops the in-flight
/// call and decode, deletes every temp slice and releases every lease; the cues made so far stay readable.</item>
/// </list>
/// </summary>
public sealed class VideoJob : IDisposable
{
    private readonly object gate = new();
    private readonly string token;
    private readonly VideoJobOptions options;
    private readonly MediaSlicer slicer;
    private readonly IMediaTokens tokens;
    private readonly Func<IVideoUploadConfirmation?> confirmation;
    private readonly AsrTranscriber transcriber;
    private readonly CancellationTokenSource cancel = new();
    private readonly List<VideoCue> cues = [];
    private readonly List<AsrSegment> timeline = [];
    private readonly Dictionary<string, string> partial = new(StringComparer.Ordinal);
    private readonly TimeSpan translateTimeout;
    private IAsrProvider asr;
    private ISubtitleTranslationProvider translator;
    private TaskCompletionSource? resume;
    private bool pauseRequested;
    private int cueSeq, calls, started;
    private VideoJobState state;
    private Task completion = Task.CompletedTask;

    public VideoJob(string id, string token, VideoJobOptions options, MediaSlicer slicer, IMediaTokens tokens, ILeasedFileFactory files,
        Func<IVideoUploadConfirmation?> confirmation, IAsrProvider asr, ISubtitleTranslationProvider translator)
    {
        Id = id; this.token = token; this.options = options; this.slicer = slicer; this.tokens = tokens; this.confirmation = confirmation;
        this.asr = asr; this.translator = translator;
        transcriber = new AsrTranscriber(files, options.AsrTimeout ?? ConfigSnapshot.EffectiveTimeout(Capability.Asr, null), options.IdFactory);
        translateTimeout = options.TranslateTimeout ?? ConfigSnapshot.EffectiveTimeout(Capability.Translate, null);
        state = new VideoJobState(id, VideoJobPhase.Created, AsrService: asr.InstanceId, TranslationService: translator.ServiceId);
    }

    public string Id { get; }
    public string DisplayName => tokens.DisplayName(token) ?? "";
    public VideoJobState State { get { lock (gate) return state; } }
    public Task Completion => completion;

    /// <summary>The state changed (phase, counts, error). Cues travel in <see cref="CueChanged"/>.</summary>
    public event Action<VideoJobState>? StateChanged;
    /// <summary>A cue was added, translated, or marked failed (same id, newer content).</summary>
    public event Action<VideoCue>? CueChanged;

    public IReadOnlyList<VideoCue> Cues() { lock (gate) return [.. cues]; }
    public VideoJobResult Result() { lock (gate) return new VideoJobResult(Id, DisplayName, state, [.. cues]); }

    /// <summary>Starts the job once; later calls return the same task. The task ends when the job is done, failed or cancelled.</summary>
    public Task Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 0) completion = Task.Run(RunAsync);
        return completion;
    }

    // ---------------- user commands ----------------

    /// <summary>Asks the running job to stop before its next request (a request in flight finishes).</summary>
    public void Pause() { lock (gate) if (state.Phase == VideoJobPhase.Running) pauseRequested = true; }

    /// <summary>Continues a paused or quota-exhausted job (or a disk-full wait after space was freed). False when it is not waiting.</summary>
    public bool Resume()
    {
        TaskCompletionSource? wait;
        lock (gate) { pauseRequested = false; wait = resume; }
        return wait?.TrySetResult() ?? false;
    }

    /// <summary>Cancels in any phase; no active upload, temp slice or lease remains once <see cref="Completion"/> ends.</summary>
    public void Cancel() { try { cancel.Cancel(); } catch (ObjectDisposedException) { } }

    /// <summary>
    /// Replaces the translation service for the next requests (T07). Results of a half-translated cue are dropped because another
    /// service splits parts differently. Applies while paused or exhausted, or between requests while running.
    /// </summary>
    public bool SwitchTranslator(ISubtitleTranslationProvider next)
    {
        lock (gate)
        {
            if (state.IsTerminal) return false;
            translator = next; partial.Clear();
            state = state with { TranslationService = next.ServiceId };
        }
        Raise();
        return true;
    }

    /// <summary>Replaces the ASR service for the slices not yet transcribed. Refused (false) for a model without timecodes (A03).</summary>
    public bool SwitchAsr(IAsrProvider next)
    {
        if (!next.Timecodes) return false;
        lock (gate)
        {
            if (state.IsTerminal) return false;
            asr = next;
            state = state with { AsrService = next.InstanceId };
        }
        Raise();
        return true;
    }

    public void Dispose() { Cancel(); cancel.Dispose(); }

    // ---------------- the loop ----------------

    private async Task RunAsync()
    {
        var ct = cancel.Token;
        MediaSession? session = null;
        MediaSlice? held = null;
        try
        {
            if (!asr.Timecodes) { Fail(VideoErrors.NoTimecodes); return; }
            Set(s => s with { Phase = VideoJobPhase.Probing, Stage = VideoStage.Probe });
            try { session = await slicer.OpenAsync(token, ct); }
            catch (MediaDecodeException error) { Fail(error.Code); return; }
            var probe = session.Probe;
            if (probe.Selected is null) { Fail(probe.AudioStreams.Count == 0 ? MediaErrors.NoAudio : MediaErrors.UnsupportedEncoding); return; }
            TimeSpan sliceLength = options.SliceLength ?? MediaSliceOptions.Default.SliceLength;
            int estimate = Math.Max(1, (int)Math.Ceiling(probe.Duration / sliceLength));
            Set(s => s with { Phase = VideoJobPhase.AwaitingConfirm, SlicesTotal = estimate });

            var ui = confirmation();
            if (ui is null || !await ui.ConfirmAsync(Notice(probe), ct)) { Finish(VideoJobPhase.Cancelled, VideoErrors.NotConfirmed); return; }

            Set(s => s with { Phase = VideoJobPhase.Running, Stage = VideoStage.Asr });
            while (true)
            {
                await GateAsync(ct);
                var step = await session.NextAsync(ct);
                if (step is MediaStep.End) break;
                if (step is MediaStep.Failure { Retryable: true } disk)
                {
                    // Disk full: wait for the user to free space and resume, then the same slice is written again.
                    await ParkAsync(VideoJobPhase.Paused, VideoStage.Probe, VideoQuotaSide.None, disk.Code, null, ct);
                    session.Retry();
                    continue;
                }
                if (step is not MediaStep.Slice slice) { Fail(((MediaStep.Failure)step).Code); return; }
                held = slice.Value;
                if (!await TranscribeAsync(held, ct)) return;
                held.Dispose(); held = null;
                Set(s => s with { SlicesDone = s.SlicesDone + 1, SlicesTotal = Math.Max(s.SlicesTotal, s.SlicesDone + 1) });
                if (!await TranslatePendingAsync(ct)) return;
            }
            if (Cues().Count == 0) { Fail(VideoErrors.NoSpeech); return; }
            Finish(VideoJobPhase.Done, null);
        }
        catch (OperationCanceledException) { Finish(VideoJobPhase.Cancelled, null); }
        catch (Exception error) { Fail(VideoErrors.AsrFailed, new ProviderError(ErrorKind.Unavailable, error.GetType().Name)); }
        finally
        {
            held?.Dispose();
            session?.Dispose();
        }
    }

    private VideoUploadNotice Notice(MediaProbe probe)
    {
        var limits = asr.Limits;
        long chunkSeconds = Math.Min(limits.MaxSeconds, limits.MaxChunkSamples(MediaSession.Rate) / MediaSession.Rate);
        var service = translator;
        return new VideoUploadNotice(Id, DisplayName, probe.Duration, probe.HasVideo, asr.InstanceId, asr.Model,
            (long)(probe.Duration.TotalSeconds * MediaSession.Rate * 2) + Wav16.HeaderBytes, (int)chunkSeconds, limits.EffectiveFileCap,
            service.ServiceId, service.DisplayName, service.Limits, service.QuotaNoteKey, null, null);
    }

    // ---------------- ASR ----------------

    /// <summary>Transcribes one slice (once: a finished slice is never sent again). False when the job ended.</summary>
    private async Task<bool> TranscribeAsync(MediaSlice slice, CancellationToken ct)
    {
        while (true)
        {
            await GateAsync(ct);
            IAsrProvider provider;
            lock (gate) { provider = asr; }
            Set(s => s with { Stage = VideoStage.Asr, AsrService = provider.InstanceId });
            AsrRunOutcome outcome;
            try { outcome = await transcriber.RunAsync(provider, slice.Wav, "segments", options.AsrLanguage, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { outcome = new AsrRunOutcome.Failure(new ProviderError(ErrorKind.Unavailable, error.GetType().Name)); }
            switch (outcome)
            {
                case AsrRunOutcome.NoSpeech: return true;
                case AsrRunOutcome.Done d:
                    if (AddCues(d.Transcript.Segments ?? [], slice.Start.TotalSeconds) is { } bad) { Fail(VideoErrors.AsrFailed, bad); return false; }
                    return true;
                case AsrRunOutcome.Failure { Error.Kind: ErrorKind.Quota or ErrorKind.RateLimited } quota:
                    await ParkAsync(VideoJobPhase.QuotaExhausted, VideoStage.Asr, VideoQuotaSide.Asr, null, quota.Error, ct);
                    continue; // the same slice again, with whichever ASR is selected now
                case AsrRunOutcome.Failure f: Fail(VideoErrors.AsrFailed, f.Error); return false;
                default: Fail(VideoErrors.AsrFailed, new ProviderError(ErrorKind.BadResponse)); return false;
            }
        }
    }

    /// <summary>Moves a slice's segments onto the media time axis and appends them as cues; the error when the timeline would go backwards.</summary>
    private ProviderError? AddCues(IReadOnlyList<AsrSegment> segments, double sliceStart)
    {
        List<VideoCue> added = [];
        lock (gate)
        {
            var next = new List<AsrSegment>(timeline);
            if (AsrTimeline.Append(next, segments, sliceStart) is { } bad) return bad;
            for (int i = timeline.Count; i < next.Count; i++)
            {
                var s = next[i];
                var cue = new VideoCue($"c{++cueSeq}", s.Start, s.End, s.Text.Trim());
                cues.Add(cue); added.Add(cue);
            }
            timeline.Clear(); timeline.AddRange(next);
            state = state with { Cues = cues.Count };
        }
        foreach (var cue in added) CueChanged?.Invoke(cue);
        Raise();
        return null;
    }

    // ---------------- translation ----------------

    /// <summary>Translates every cue that has neither a translation nor an error. Quota parks and retries only what is missing. False when the job ended.</summary>
    private async Task<bool> TranslatePendingAsync(CancellationToken ct)
    {
        int lastCount = -1;
        ISubtitleTranslationProvider? lastService = null;
        while (true)
        {
            List<VideoCue> pending;
            ISubtitleTranslationProvider current;
            lock (gate) { pending = [.. cues.Where(c => c.Translation is null && c.TranslationError is null)]; current = translator; }
            if (pending.Count == 0) return true;
            // A round that handled nothing with the same service would repeat forever: mark what is left as failed instead.
            if (pending.Count == lastCount && ReferenceEquals(current, lastService)) { foreach (var cue in pending) MarkFailed(cue.Id, "stalled"); return true; }
            lastCount = pending.Count; lastService = current;
            Set(s => s with { Stage = VideoStage.Translate });
            ProviderError? stop = await TranslateRoundAsync(pending, ct);
            if (stop is null) continue; // re-check: nothing left means done
            if (stop.Kind is ErrorKind.Quota or ErrorKind.RateLimited)
            {
                await ParkAsync(VideoJobPhase.QuotaExhausted, VideoStage.Translate, VideoQuotaSide.Translation, null, stop, ct);
                lastCount = -1; // a resume (with or without a switch) is a fresh try, not a stalled round
                continue;
            }
            Fail(VideoErrors.TranslationFailed, stop);
            return false;
        }
    }

    /// <summary>One pass over the pending cues. Returns the provider error that stops it, null when every batch was handled.</summary>
    private async Task<ProviderError?> TranslateRoundAsync(List<VideoCue> pending, CancellationToken ct)
    {
        ISubtitleTranslationProvider service;
        lock (gate) service = translator;
        var limits = service.Limits;
        var parts = new List<SubtitlePart>();
        var byCue = new Dictionary<string, List<SubtitlePart>>(StringComparer.Ordinal);
        foreach (var cue in pending)
        {
            if (string.IsNullOrWhiteSpace(cue.Original)) { MarkFailed(cue.Id, "empty"); continue; }
            IReadOnlyList<SubtitlePart> own;
            try { own = SubtitleMapper.ToParts([new SubtitleSegment(cue.Id, cue.Start, cue.End, cue.Original)], limits); }
            catch (InvalidOperationException) { MarkFailed(cue.Id, VideoErrors.TextTooLong); continue; }
            parts.AddRange(own); byCue[cue.Id] = [.. own];
        }
        List<SubtitlePart> todo;
        lock (gate) todo = [.. parts.Where(p => !partial.ContainsKey(p.Id))];
        foreach (var batch in SubtitleMapper.Batches(todo, limits))
        {
            await GateAsync(ct);
            lock (gate) if (!ReferenceEquals(service, translator)) return null; // switched while waiting: start the round again with the new service
            if (await TranslateBatchAsync(service, batch, byCue, ct) is { } stop) return stop;
        }
        return null;
    }

    /// <summary>One batch; a malformed reply degrades once to one call per part (T04), then the cue is marked failed. No unbounded retry.</summary>
    private async Task<ProviderError?> TranslateBatchAsync(ISubtitleTranslationProvider service, IReadOnlyList<SubtitlePart> batch, Dictionary<string, List<SubtitlePart>> byCue, CancellationToken ct)
    {
        var reply = await CallAsync(service, batch, ct);
        if (reply.Error is { } error) return error;
        if (Valid(batch, reply.Items!)) { Commit(batch, reply.Items!, byCue); return null; }
        if (batch.Count == 1) { FailParts(batch, byCue); return null; }
        foreach (var part in batch)
        {
            await GateAsync(ct);
            var single = await CallAsync(service, [part], ct);
            if (single.Error is { } e) return e;
            if (Valid([part], single.Items!)) Commit([part], single.Items!, byCue); else FailParts([part], byCue);
        }
        return null;
    }

    private async Task<(IReadOnlyList<(string? Id, string? Text)>? Items, ProviderError? Error)> CallAsync(ISubtitleTranslationProvider service, IReadOnlyList<SubtitlePart> batch, CancellationToken ct)
    {
        ProviderError? last = null;
        for (int attempt = 0; attempt < 2; attempt++) // one more try for a transient failure, never more
        {
            ct.ThrowIfCancellationRequested();
            SubtitleBatchOutcome outcome;
            try
            {
                var call = new SubtitleBatchCall(batch, options.From, options.To, $"{Id}-t{Interlocked.Increment(ref calls)}", translateTimeout);
                outcome = await service.TranslateBatchAsync(call, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { outcome = new SubtitleBatchOutcome.Failure(new ProviderError(ErrorKind.Unavailable, error.GetType().Name)); }
            if (outcome is SubtitleBatchOutcome.Success ok) return (ok.Items, null);
            last = ((SubtitleBatchOutcome.Failure)outcome).Error;
            if (last.Kind is not (ErrorKind.Network or ErrorKind.Timeout or ErrorKind.Busy)) break;
        }
        // A reply the service made unusable is the same as a malformed reply: the caller degrades it.
        return last!.Kind == ErrorKind.BadResponse ? ([], null) : (null, last);
    }

    private static bool Valid(IReadOnlyList<SubtitlePart> requested, IReadOnlyList<(string? Id, string? Text)> returned)
    {
        if (SubtitleMapper.Validate(requested, returned) != BatchProblem.None) return false;
        var text = returned.ToDictionary(r => r.Id!, r => r.Text!, StringComparer.Ordinal);
        return requested.All(p => string.IsNullOrWhiteSpace(p.Text) || !string.IsNullOrWhiteSpace(text[p.Id]));
    }

    /// <summary>Stores validated part results by id; a cue is complete when all its parts are in (joined in part order, time from the cue).</summary>
    private void Commit(IReadOnlyList<SubtitlePart> batch, IReadOnlyList<(string? Id, string? Text)> returned, Dictionary<string, List<SubtitlePart>> byCue)
    {
        List<VideoCue> changed = [];
        lock (gate)
        {
            foreach (var (id, text) in returned) partial[id!] = text!;
            foreach (string cueId in batch.Select(p => p.SegmentId).Distinct(StringComparer.Ordinal))
            {
                var own = byCue[cueId];
                if (own.Any(p => !partial.ContainsKey(p.Id))) continue;
                string joined = string.Concat(own.OrderBy(p => p.PartIndex).Select(p => partial[p.Id]));
                foreach (var p in own) partial.Remove(p.Id);
                int index = cues.FindIndex(c => c.Id == cueId);
                if (index < 0) continue;
                cues[index] = cues[index] with { Translation = joined, TranslationError = null };
                changed.Add(cues[index]);
            }
            state = state with { Translated = cues.Count(c => c.Translation is not null) };
        }
        foreach (var cue in changed) CueChanged?.Invoke(cue);
        Raise();
    }

    private void FailParts(IReadOnlyList<SubtitlePart> batch, Dictionary<string, List<SubtitlePart>> byCue)
    {
        foreach (string id in batch.Select(p => p.SegmentId).Distinct(StringComparer.Ordinal))
        {
            lock (gate) foreach (var p in byCue[id]) partial.Remove(p.Id);
            MarkFailed(id, ErrorKind.BadResponse.ToString());
        }
    }

    private void MarkFailed(string cueId, string code)
    {
        VideoCue? changed = null;
        lock (gate)
        {
            int index = cues.FindIndex(c => c.Id == cueId);
            if (index < 0) return;
            cues[index] = changed = cues[index] with { TranslationError = code };
            state = state with { Failed = cues.Count(c => c.TranslationError is not null) };
        }
        CueChanged?.Invoke(changed);
        Raise();
    }

    // ---------------- parking, state ----------------

    private async Task GateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool pause;
        VideoStage stage;
        lock (gate) { pause = pauseRequested; stage = state.Stage; }
        if (pause) await ParkAsync(VideoJobPhase.Paused, stage, VideoQuotaSide.None, null, null, ct);
    }

    /// <summary>Publishes a waiting phase, makes no request until <see cref="Resume"/> (or cancel), then goes back to Running.</summary>
    private async Task ParkAsync(VideoJobPhase phase, VideoStage stage, VideoQuotaSide quota, string? code, ProviderError? error, CancellationToken ct)
    {
        var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) resume = wait;
        Set(s => s with { Phase = phase, Stage = stage, Quota = quota, ErrorCode = code, Error = error });
        using (ct.Register(() => wait.TrySetCanceled(ct))) await wait.Task;
        lock (gate) { resume = null; pauseRequested = false; }
        Set(s => s with { Phase = VideoJobPhase.Running, Quota = VideoQuotaSide.None, ErrorCode = null, Error = null });
    }

    private void Fail(string code, ProviderError? error = null) => Finish(VideoJobPhase.Failed, code, error);

    private void Finish(VideoJobPhase phase, string? code, ProviderError? error = null)
        => Set(s => s with { Phase = phase, Stage = VideoStage.None, Quota = VideoQuotaSide.None, ErrorCode = code, Error = error });

    private void Set(Func<VideoJobState, VideoJobState> change)
    {
        lock (gate) { if (state.IsTerminal) return; state = change(state); }
        Raise();
    }

    private void Raise() => StateChanged?.Invoke(State);
}

/// <summary>
/// Finished and partial video results kept in the process after the window closes, for reopen and export (F14.3). Memory only: a
/// restart forgets them. Holds the jobs themselves (their media resources are already released); the oldest finished one goes when
/// <see cref="Capacity"/> is exceeded.
/// </summary>
public sealed class VideoResultStore
{
    public const int Capacity = 8;
    private readonly object gate = new();
    private readonly List<VideoJob> jobs = [];

    public void Put(VideoJob job)
    {
        lock (gate)
        {
            jobs.RemoveAll(j => j.Id == job.Id);
            jobs.Add(job);
            while (jobs.Count > Capacity && jobs.FirstOrDefault(j => j.State.IsTerminal) is { } old) jobs.Remove(old);
        }
    }

    public VideoJobResult? Get(string jobId) { lock (gate) return jobs.FirstOrDefault(j => j.Id == jobId)?.Result(); }
    public VideoJob? Job(string jobId) { lock (gate) return jobs.FirstOrDefault(j => j.Id == jobId); }
    public IReadOnlyList<VideoJobResult> List() { lock (gate) return [.. jobs.Select(j => j.Result())]; }
    public bool Remove(string jobId) { lock (gate) return jobs.RemoveAll(j => j.Id == jobId) > 0; }
}

/// <summary>Outcome of <see cref="VideoJobs.Start"/>: a started job, or the code why not (nothing was probed, decoded or sent).</summary>
public sealed record VideoStart(VideoJob? Job, string? Refusal);

/// <summary>
/// The video job service (F14.2): picks the video ASR and the video translation service at the moment a job starts (their own
/// selections, never the microphone's), refuses a second job while one is active, and keeps every started job in the
/// <see cref="VideoResultStore"/>. The UI (F14.4) sets <see cref="Confirmation"/>; without it no job passes the upload gate.
/// </summary>
public sealed class VideoJobs(IMediaTokens tokens, IMediaDecoder decoder, ILeasedFileFactory files, Func<IAsrProvider?> asr,
    Func<ISubtitleTranslationProvider?> translator, VideoResultStore? results = null)
{
    private readonly object gate = new();
    private VideoJob? active;

    public VideoResultStore Results { get; } = results ?? new();
    public IVideoUploadConfirmation? Confirmation { get; set; }
    public event Action<VideoJob>? JobStarted;

    public VideoJob? Active { get { lock (gate) return active is { State.IsTerminal: false } ? active : null; } }

    public VideoStart Start(string mediaToken, string from, string to, string? asrLanguage = null, VideoJobOptions? template = null, MediaSliceOptions? slicing = null)
    {
        var service = asr();
        if (service is null) return new(null, VideoErrors.NoAsr);
        if (!service.Timecodes) return new(null, VideoErrors.NoTimecodes);
        var translate = translator();
        if (translate is null) return new(null, VideoErrors.NoTranslation);
        VideoJob job;
        lock (gate)
        {
            if (active is { State.IsTerminal: false }) return new(null, VideoErrors.Busy);
            var opts = (template ?? new VideoJobOptions(from, to, asrLanguage)) with { From = from, To = to, AsrLanguage = asrLanguage, SliceLength = slicing?.SliceLength ?? template?.SliceLength };
            job = new VideoJob($"video-{Guid.NewGuid():N}", mediaToken, opts, new MediaSlicer(tokens, decoder, files, slicing), tokens, files, () => Confirmation, service, translate);
            active = job;
        }
        Results.Put(job);
        JobStarted?.Invoke(job);
        _ = job.Start();
        return new(job, null);
    }

    /// <summary>Cancels the active job, if any (window close, exit).</summary>
    public void CancelActive() => Active?.Cancel();
}

/// <summary>
/// Adapts a plain translation provider (one text per call) to subtitle batches: one part per call, so the job's batches have one
/// item (<see cref="BatchMode.Single"/>) and every part is matched by id. An items-capable plugin API is not wired yet.
/// </summary>
public sealed class SingleItemSubtitleProvider(ITranslationProvider inner, Func<ConfigSnapshot> config, string? quotaNoteKey = null) : ISubtitleTranslationProvider
{
    public string ServiceId => inner.ServiceId;
    public string DisplayName => inner.DisplayName;
    public string? QuotaNoteKey => quotaNoteKey;
    public TranslationLimits Limits => inner.Limits with { Mode = BatchMode.Single, MaxItems = 1, MaxTotalInput = inner.Limits.MaxInput };

    public async Task<SubtitleBatchOutcome> TranslateBatchAsync(SubtitleBatchCall call, CancellationToken cancellationToken)
    {
        if (!inner.SupportsLanguagePair(call.From, call.To)) return new SubtitleBatchOutcome.Failure(new ProviderError(ErrorKind.UnsupportedLanguage));
        var items = new List<(string?, string?)>();
        foreach (var part in call.Parts)
        {
            var outcome = await inner.TranslateAsync(new TranslateCall(part.Text, call.From, call.To, $"{call.AttemptId}-{part.Id}", config(), call.Timeout), _ => ValueTask.CompletedTask, cancellationToken);
            if (outcome is ProviderOutcome.Failure f) return new SubtitleBatchOutcome.Failure(f.Error);
            items.Add((part.Id, ((ProviderOutcome.Success)outcome).Text));
        }
        return new SubtitleBatchOutcome.Success(items);
    }
}
