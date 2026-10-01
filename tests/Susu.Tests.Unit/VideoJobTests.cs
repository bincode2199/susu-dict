using System.Buffers.Binary;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Storage;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F14.2 video job (TEST-PLAN T03–T07, A05–A07 job part, A02/A03). Real <see cref="MediaSlicer"/>, real <see cref="AsrTranscriber"/> and
/// real subtitle id mapping; fake media reader, ASR and translation providers. The real-sandbox run is in
/// VideoJobSandboxTests.
/// </summary>
public class VideoJobTests : IDisposable
{
    private const int Rate = 16000;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly AsrLimits Limits = new([AsrFormat.Wav16kMono], 24_000_000, 25_000_000, 300, AsrUpload.Multipart, 2048);
    internal static readonly TranslationLimits ItemsLimits = new(InputUnit.UnicodeScalars, 500, BatchMode.Items, 20, 4000);

    private readonly TempRoot root = new();
    private readonly FileLeases leases;
    private readonly LeasedFiles files;

    public VideoJobTests() { leases = new FileLeases(root.Paths.Cache); files = new LeasedFiles(leases); }
    public void Dispose() { leases.Dispose(); root.Dispose(); }

    // ---------------- fakes ----------------

    internal sealed class FakeReader(MediaProbe probe, IEnumerator<MediaPcmBlock> blocks) : IMediaAudioReader
    {
        public MediaProbe Probe { get; } = probe;
        public Task<MediaPcmBlock?> ReadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(blocks.MoveNext() ? blocks.Current : (MediaPcmBlock?)null);
        }
        public void Dispose() { }
    }

    internal sealed class FakeDecoder(Func<IMediaAudioReader> open) : IMediaDecoder
    {
        public int Opens;
        public Task<MediaProbe> ProbeAsync(string path, CancellationToken ct) => Task.FromResult(open().Probe);
        public Task<IMediaAudioReader> OpenAsync(string path, CancellationToken ct) { Interlocked.Increment(ref Opens); return Task.FromResult(open()); }
    }

    internal static IEnumerable<MediaPcmBlock> Blocks(int seconds, Func<int, bool> loud)
    {
        for (int i = 0; i < seconds; i++)
        {
            var pcm = new byte[Rate * 2];
            if (loud(i)) for (int s = 0; s < Rate; s++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(s * 2), 1000);
            yield return new MediaPcmBlock(pcm, TimeSpan.FromSeconds(i));
        }
    }

    private sealed class FakeAsr(string id, bool timecodes, Func<int, AsrCall, CancellationToken, Task<AsrOutcome>>? answer = null) : IAsrProvider
    {
        public int Calls;
        public string InstanceId => id;
        public string Model => "fake";
        public bool Timecodes => timecodes;
        public AsrLimits Limits => VideoJobTests.Limits;
        public Task<AsrOutcome> TranscribeAsync(AsrCall call, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref Calls) - 1;
            return answer is null ? Task.FromResult(Two(n)) : answer(n, call, ct);
        }
    }

    /// <summary>Two cues inside the chunk: one with a line break.</summary>
    private static AsrOutcome Two(int n) => new AsrOutcome.Transcribed("segments", "x", [new AsrSegment(0.5, 2, $"s{n}a"), new AsrSegment(3, 5, $"s{n}b\nline two")]);
    private static AsrOutcome Quota() => new AsrOutcome.Failure(new ProviderError(ErrorKind.Quota, "daily limit"));

    internal sealed class FakeTranslator(string id, TranslationLimits limits, Func<SubtitleBatchCall, int, SubtitleBatchOutcome>? answer = null, string? note = null) : ISubtitleTranslationProvider
    {
        public List<SubtitleBatchCall> Calls { get; } = [];
        public string ServiceId => id;
        public string DisplayName => "Service " + id;
        public TranslationLimits Limits => limits;
        public string? QuotaNoteKey => note;
        public Task<SubtitleBatchOutcome> TranslateBatchAsync(SubtitleBatchCall call, CancellationToken ct)
        {
            int n; lock (Calls) { n = Calls.Count; Calls.Add(call); }
            return Task.FromResult(answer?.Invoke(call, n) ?? Echo(call));
        }
    }

    internal static SubtitleBatchOutcome Echo(SubtitleBatchCall call) => new SubtitleBatchOutcome.Success([.. call.Parts.Select(p => ((string?)p.Id, (string?)("T:" + p.Text)))]);
    private static SubtitleBatchOutcome QuotaReply() => new SubtitleBatchOutcome.Failure(new ProviderError(ErrorKind.Quota));

    internal sealed class Confirm(Func<VideoUploadNotice, CancellationToken, Task<bool>>? answer = null) : IVideoUploadConfirmation
    {
        public List<VideoUploadNotice> Notices { get; } = [];
        public Task<bool> ConfirmAsync(VideoUploadNotice notice, CancellationToken ct) { lock (Notices) Notices.Add(notice); return answer?.Invoke(notice, ct) ?? Task.FromResult(true); }
    }

    private sealed class Rig
    {
        public required FakeDecoder Decoder { get; init; }
        public required MediaTokens Tokens { get; init; }
        public required string Token { get; init; }
        public required MediaSlicer Slicer { get; init; }
    }

    private Rig NewRig(int seconds = 30, Func<int, bool>? loud = null)
    {
        loud ??= _ => true;
        var probe = new MediaProbe(TimeSpan.FromSeconds(seconds), true, [new MediaAudioStream(1, "aac", 44100, 2, true)]);
        var decoder = new FakeDecoder(() => new FakeReader(probe, Blocks(seconds, loud).GetEnumerator()));
        var tokens = new MediaTokens();
        string file = Path.Combine(root.Root, "movie.mp4"); File.WriteAllText(file, "x");
        return new Rig { Decoder = decoder, Tokens = tokens, Token = tokens.Issue(file)!, Slicer = new MediaSlicer(tokens, decoder, files, new MediaSliceOptions(TimeSpan.FromSeconds(10))) };
    }

    private VideoJob NewJob(Rig rig, IAsrProvider asr, ISubtitleTranslationProvider translator, IVideoUploadConfirmation? confirm = null)
    {
        confirm ??= new Confirm();
        return new VideoJob("job1", rig.Token, new VideoJobOptions("en", "zh", SliceLength: TimeSpan.FromSeconds(10), IdFactory: () => "att"), rig.Slicer, rig.Tokens, files, () => confirm, asr, translator);
    }

    private static async Task<VideoJobState> WaitFor(VideoJob job, Func<VideoJobState, bool> predicate)
    {
        var tcs = new TaskCompletionSource<VideoJobState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(VideoJobState s) { if (predicate(s)) tcs.TrySetResult(s); }
        job.StateChanged += On;
        try
        {
            On(job.State);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
        finally { job.StateChanged -= On; }
    }

    private static async Task<VideoJobState> Run(VideoJob job)
    {
        _ = job.Start();
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        return job.State;
    }

    private void AssertNothingLeft()
    {
        Assert.Equal(0, leases.ActiveCount);
        Assert.Empty(Directory.Exists(root.Paths.Cache) ? Directory.EnumerateFiles(root.Paths.Cache, "*", SearchOption.AllDirectories) : []);
    }

    // ---------------- happy path: absolute times, order, events ----------------

    [Fact]
    public async Task Slices_are_transcribed_in_order_on_the_media_time_axis_and_translated_incrementally()
    {
        var rig = NewRig();
        var asr = new FakeAsr("asr", true);
        var tr = new FakeTranslator("tr", ItemsLimits);
        var job = NewJob(rig, asr, tr);
        var seen = new List<VideoCue>();
        var states = new List<VideoJobState>();
        job.CueChanged += seen.Add; job.StateChanged += states.Add;

        var state = await Run(job);

        Assert.Equal(VideoJobPhase.Done, state.Phase);
        Assert.Equal(3, asr.Calls);
        var cues = job.Cues();
        Assert.Equal(6, cues.Count);
        Assert.Equal([0.5, 3, 10.5, 13, 20.5, 23], cues.Select(c => c.Start));
        Assert.Equal([2, 5, 12, 15, 22, 25], cues.Select(c => c.End));
        Assert.Equal("s1b\nline two", cues[3].Original);
        Assert.All(cues, c => Assert.Equal("T:" + c.Original, c.Translation));
        Assert.Equal(cues.Select(c => c.Id).Distinct().Count(), cues.Count);
        Assert.Equal((6, 6, 0, 3), (state.Cues, state.Translated, state.Failed, state.SlicesDone));
        // Incremental: the first cues were announced and translated before the later slices were transcribed (3 batches, one per slice).
        Assert.Equal(3, tr.Calls.Count);
        Assert.Equal(6 + 6, seen.Count); // each cue once added, once translated
        Assert.Contains(states, s => s.Phase == VideoJobPhase.AwaitingConfirm);
        AssertNothingLeft();
    }

    // ---------------- T03 / T04 / T05 ----------------

    [Fact] // T03: 20 cues with line breaks, the service answers in another order: placed by id
    public async Task T03_shuffled_reply_is_placed_by_id_not_by_position_or_line_breaks()
    {
        var rig = NewRig(10);
        var segments = Enumerable.Range(0, 20).Select(i => new AsrSegment(i * 0.45, i * 0.45 + 0.4, $"first {i}\nsecond {i}")).ToArray();
        var asr = new FakeAsr("asr", true, (_, _, _) => Task.FromResult<AsrOutcome>(new AsrOutcome.Transcribed("segments", "x", segments)));
        var tr = new FakeTranslator("tr", ItemsLimits, (call, _) =>
        {
            var items = call.Parts.Select(p => ((string?)p.Id, (string?)("T:" + p.Text))).ToList();
            items.Reverse(); items.Insert(3, items[^1]); items.RemoveAt(items.Count - 1); // not in request order
            return new SubtitleBatchOutcome.Success(items);
        });
        var job = NewJob(rig, asr, tr);
        Assert.Equal(VideoJobPhase.Done, (await Run(job)).Phase);
        var cues = job.Cues();
        Assert.Equal(20, cues.Count);
        Assert.Single(tr.Calls);
        for (int i = 0; i < 20; i++) Assert.Equal($"T:first {i}\nsecond {i}", cues[i].Translation);
        Assert.Equal(segments.Select(s => s.Start), cues.Select(c => c.Start));
    }

    [Theory] // T04: missing / duplicate / extra id, wrong type of text: the batch is not used, each part is asked once alone
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("nullText")]
    public async Task T04_malformed_batch_degrades_once_to_single_items(string shape)
    {
        var rig = NewRig(10);
        var asr = new FakeAsr("asr", true, (_, _, _) => Task.FromResult<AsrOutcome>(new AsrOutcome.Transcribed("segments", "x", [.. Enumerable.Range(0, 4).Select(i => new AsrSegment(i, i + 0.5, $"cue {i}"))])));
        var tr = new FakeTranslator("tr", ItemsLimits, (call, _) => call.Parts.Count > 1 ? Malformed(shape, call) : Echo(call));
        var job = NewJob(rig, asr, tr);
        Assert.Equal(VideoJobPhase.Done, (await Run(job)).Phase);
        Assert.Equal(5, tr.Calls.Count); // 1 batch + 4 singles, nothing more
        Assert.All(tr.Calls.Skip(1), c => Assert.Single(c.Parts));
        Assert.All(job.Cues(), c => Assert.Equal("T:" + c.Original, c.Translation));
    }

    [Theory] // T04: a service that is always malformed: each cue marked failed after its one single call, no unbounded retry, job still ends
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("nullText")]
    public async Task T04_still_malformed_singles_mark_the_cues_failed_without_looping(string shape)
    {
        var rig = NewRig(10);
        var asr = new FakeAsr("asr", true, (_, _, _) => Task.FromResult<AsrOutcome>(new AsrOutcome.Transcribed("segments", "x", [.. Enumerable.Range(0, 4).Select(i => new AsrSegment(i, i + 0.5, $"cue {i}"))])));
        var tr = new FakeTranslator("tr", ItemsLimits, (call, _) => Malformed(shape, call));
        var job = NewJob(rig, asr, tr);
        var state = await Run(job);
        Assert.Equal(VideoJobPhase.Done, state.Phase);
        Assert.Equal(5, tr.Calls.Count);
        Assert.All(job.Cues(), c => { Assert.Null(c.Translation); Assert.Equal("BadResponse", c.TranslationError); Assert.StartsWith("cue ", c.Original); });
        Assert.Equal((4, 0, 4), (state.Cues, state.Translated, state.Failed));
    }

    private static SubtitleBatchOutcome Malformed(string shape, SubtitleBatchCall call)
    {
        var ok = call.Parts.Select(p => ((string?)p.Id, (string?)("T:" + p.Text))).ToList();
        switch (shape)
        {
            case "missing": ok.RemoveAt(0); break;
            case "duplicate": ok.Add(ok[0]); break;
            case "extra": ok.Add(("zzz#0", "x")); break;
            default: ok[0] = (ok[0].Item1, null); break;
        }
        return new SubtitleBatchOutcome.Success(ok);
    }

    private sealed class SingleOnly(TranslationLimits limits, List<string> texts) : ITranslationProvider
    {
        public string ServiceId => "single";
        public string DisplayName => "Single";
        public string LimiterKey => "single";
        public TranslationLimits Limits => limits;
        public bool SupportsLanguagePair(string from, string to) => true;
        public Task<ProviderOutcome> TranslateAsync(TranslateCall call, Func<string, ValueTask> onChunk, CancellationToken ct)
        {
            lock (texts) texts.Add(call.Text);
            return Task.FromResult<ProviderOutcome>(new ProviderOutcome.Success(call.Text.ToUpperInvariant()));
        }
    }

    private const string Long = "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima";

    [Theory] // T05: a cue over the service limit: parts merged back, time unchanged, every hard limit holds; single and items engines
    [InlineData(true)]
    [InlineData(false)]
    public async Task T05_long_cue_is_split_by_limit_and_merged_back(bool singleEngine)
    {
        var rig = NewRig(10);
        var asr = new FakeAsr("asr", true, (_, _, _) => Task.FromResult<AsrOutcome>(new AsrOutcome.Transcribed("segments", "x", [new AsrSegment(1.25, 4.5, Long), new AsrSegment(5, 6, "short one")])));
        var tight = new TranslationLimits(InputUnit.UnicodeScalars, 20, singleEngine ? BatchMode.Single : BatchMode.Items, singleEngine ? 1 : 5, singleEngine ? 20 : 60);
        List<int> sizes = [];
        ISubtitleTranslationProvider tr;
        List<string> singleTexts = [];
        FakeTranslator? items = null;
        if (singleEngine) tr = new SingleItemSubtitleProvider(new SingleOnly(tight, singleTexts), () => new ConfigSnapshot(1, 1, 1, 3, TimeSpan.FromSeconds(30)));
        else tr = items = new FakeTranslator("items", tight, (call, _) =>
        {
            Assert.InRange(call.Parts.Count, 1, 5);
            Assert.InRange(call.Parts.Sum(p => p.Text.Length), 1, 60);
            return new SubtitleBatchOutcome.Success([.. call.Parts.Select(p => ((string?)p.Id, (string?)p.Text.ToUpperInvariant()))]);
        });
        var job = NewJob(rig, asr, tr);
        Assert.Equal(VideoJobPhase.Done, (await Run(job)).Phase);
        var cues = job.Cues();
        Assert.Equal(Long.ToUpperInvariant(), cues[0].Translation);
        Assert.Equal((1.25, 4.5), (cues[0].Start, cues[0].End));
        Assert.Equal("SHORT ONE", cues[1].Translation);
        var sent = singleEngine ? singleTexts : [.. items!.Calls.SelectMany(c => c.Parts).Select(p => p.Text)];
        Assert.All(sent, t => Assert.True(t.Length <= 20, t));
        Assert.True(sent.Count > 2);
        if (singleEngine) Assert.Equal(Long, string.Concat(sent.Take(sent.Count - 1)));
    }

    // ---------------- T06: confirmation before upload ----------------

    [Fact]
    public async Task T06_notice_shows_service_limits_and_size_and_nothing_is_sent_before_the_answer()
    {
        var rig = NewRig();
        var asr = new FakeAsr("asr", true);
        var tr = new FakeTranslator("mymemory", ItemsLimits, note: "video.quota.sharedDaily");
        var answer = new TaskCompletionSource<bool>();
        var confirm = new Confirm((_, _) => answer.Task);
        var job = NewJob(rig, asr, tr, confirm);
        _ = job.Start();
        await WaitFor(job, s => s.Phase == VideoJobPhase.AwaitingConfirm);
        await Task.Delay(150, Ct);
        Assert.Equal(0, asr.Calls); Assert.Empty(tr.Calls);
        var notice = Assert.Single(confirm.Notices);
        Assert.Equal(("movie.mp4", "asr", "mymemory", "Service mymemory"), (notice.DisplayName, notice.AsrService, notice.TranslationService, notice.TranslationServiceName));
        Assert.Equal(TimeSpan.FromSeconds(30), notice.Duration);
        Assert.Equal(30L * Rate * 2 + 44, notice.UploadBytesEstimate);
        Assert.Equal(300, notice.ChunkSecondsLimit);
        Assert.Equal(24_000_000, notice.ChunkBytesLimit);
        Assert.Equal(ItemsLimits, notice.TranslationLimits);
        Assert.Equal("video.quota.sharedDaily", notice.TranslationQuotaNoteKey);
        Assert.Null(notice.EstimatedCharacters); // not known before ASR: never made up
        Assert.Null(notice.EstimatedPrice);
        answer.SetResult(true);
        Assert.Equal(VideoJobPhase.Done, (await WaitFor(job, s => s.IsTerminal)).Phase);
        Assert.Equal(3, asr.Calls);
    }

    [Fact]
    public async Task T06_a_service_without_a_known_quota_note_shows_none()
    {
        var rig = NewRig(10);
        var confirm = new Confirm();
        var job = NewJob(rig, new FakeAsr("asr", true), new FakeTranslator("other", ItemsLimits), confirm);
        await Run(job);
        Assert.Null(Assert.Single(confirm.Notices).TranslationQuotaNoteKey);
    }

    [Fact]
    public async Task T06_declining_cancels_with_no_upload_and_no_file()
    {
        var rig = NewRig();
        var asr = new FakeAsr("asr", true);
        var job = NewJob(rig, asr, new FakeTranslator("tr", ItemsLimits), new Confirm((_, _) => Task.FromResult(false)));
        var state = await Run(job);
        Assert.Equal((VideoJobPhase.Cancelled, VideoErrors.NotConfirmed), (state.Phase, state.ErrorCode));
        Assert.Equal(0, asr.Calls);
        AssertNothingLeft();
    }

    [Fact]
    public async Task T06_no_confirmation_port_means_no_upload()
    {
        var rig = NewRig(10);
        var asr = new FakeAsr("asr", true);
        var jobs = new VideoJobs(rig.Tokens, rig.Decoder, files, () => asr, () => new FakeTranslator("tr", ItemsLimits));
        var start = jobs.Start(rig.Token, "en", "zh");
        await start.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(VideoErrors.NotConfirmed, start.Job.State.ErrorCode);
        Assert.Equal(0, asr.Calls);
        AssertNothingLeft();
    }

    [Fact]
    public async Task Cancel_while_waiting_for_the_answer_ends_the_wait_and_leaves_nothing()
    {
        var rig = NewRig();
        var asr = new FakeAsr("asr", true);
        var job = NewJob(rig, asr, new FakeTranslator("tr", ItemsLimits), new Confirm(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return true; }));
        _ = job.Start();
        await WaitFor(job, s => s.Phase == VideoJobPhase.AwaitingConfirm);
        job.Cancel();
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(VideoJobPhase.Cancelled, job.State.Phase);
        Assert.Equal(0, asr.Calls);
        AssertNothingLeft();
    }

    // ---------------- T07: quota, switch, only the missing ----------------

    [Fact]
    public async Task T07_translation_quota_parks_then_another_engine_translates_only_the_missing_cues()
    {
        var rig = NewRig();
        var asr = new FakeAsr("asr", true);
        var a = new FakeTranslator("a", ItemsLimits, (call, n) => n == 0 ? Echo(call) : QuotaReply());
        var b = new FakeTranslator("b", ItemsLimits, (call, _) => new SubtitleBatchOutcome.Success([.. call.Parts.Select(p => ((string?)p.Id, (string?)("B:" + p.Text)))]));
        var jobs = new VideoJobs(rig.Tokens, rig.Decoder, files, () => asr, () => a) { Confirmation = new Confirm() };
        var job = jobs.Start(rig.Token, "en", "zh", slicing: new MediaSliceOptions(TimeSpan.FromSeconds(10))).Job!;

        var parked = await WaitFor(job, s => s.Phase == VideoJobPhase.QuotaExhausted);
        Assert.Equal((VideoQuotaSide.Translation, ErrorKind.Quota), (parked.Quota, parked.Error!.Kind));
        int asrBefore = asr.Calls;
        await Task.Delay(200, Ct);
        Assert.Equal(2, a.Calls.Count); // after the quota answer the old engine gets no new request
        Assert.Equal(asrBefore, asr.Calls);
        Assert.Equal(VideoJobPhase.QuotaExhausted, job.State.Phase);
        // The original text is exportable while exhausted: the store has the cues, translated and not.
        var kept = jobs.Results.Get(job.Id)!;
        Assert.Equal(4, kept.Cues.Count);
        Assert.Equal(2, kept.Cues.Count(c => c.Translation is not null));
        Assert.All(kept.Cues, c => Assert.False(string.IsNullOrEmpty(c.Original)));

        Assert.True(job.SwitchTranslator(b));
        Assert.True(job.Resume());
        var done = await WaitFor(job, s => s.IsTerminal);
        Assert.Equal(VideoJobPhase.Done, done.Phase);
        Assert.Equal(3, asr.Calls); // slices 0 and 1 were not sent again
        Assert.Equal(2, a.Calls.Count);
        Assert.Equal(2, b.Calls.Count);
        var firstB = b.Calls[0].Parts.Select(p => p.SegmentId).ToArray();
        var cues = job.Cues();
        Assert.Equal([cues[2].Id, cues[3].Id], firstB); // only the two cues the first engine missed
        Assert.Equal(["T:", "T:", "B:", "B:", "B:", "B:"], cues.Select(c => c.Translation![..2]));
        Assert.Equal(6, job.State.Translated);
    }

    [Fact]
    public async Task T07_asr_quota_parks_and_another_asr_only_does_the_slices_not_yet_done()
    {
        var rig = NewRig();
        var a = new FakeAsr("a", true, (n, _, _) => Task.FromResult(n == 1 ? Quota() : Two(n)));
        var b = new FakeAsr("b", true);
        var tr = new FakeTranslator("tr", ItemsLimits);
        var job = NewJob(rig, a, tr);
        _ = job.Start();
        var parked = await WaitFor(job, s => s.Phase == VideoJobPhase.QuotaExhausted);
        Assert.Equal(VideoQuotaSide.Asr, parked.Quota);
        await Task.Delay(150, Ct);
        Assert.Equal(2, a.Calls);
        Assert.Single(tr.Calls); // slice 0's cues were translated, nothing else was asked
        Assert.False(job.SwitchAsr(new FakeAsr("text-only", false))); // A03: no timecodes, refused
        Assert.True(job.SwitchAsr(b));
        Assert.True(job.Resume());
        Assert.Equal(VideoJobPhase.Done, (await WaitFor(job, s => s.IsTerminal)).Phase);
        Assert.Equal((2, 2), (a.Calls, b.Calls)); // a: slice 0 and the refused slice 1; b: slices 1 and 2
        Assert.Equal([0.5, 3, 10.5, 13, 20.5, 23], job.Cues().Select(c => c.Start));
        Assert.Equal(3, tr.Calls.Count);
        AssertNothingLeft();
    }

    [Fact]
    public async Task Rate_limited_is_treated_like_quota()
    {
        var rig = NewRig(10);
        var a = new FakeTranslator("a", ItemsLimits, (_, _) => new SubtitleBatchOutcome.Failure(new ProviderError(ErrorKind.RateLimited)));
        var job = NewJob(rig, new FakeAsr("asr", true), a);
        _ = job.Start();
        Assert.Equal(VideoQuotaSide.Translation, (await WaitFor(job, s => s.Phase == VideoJobPhase.QuotaExhausted)).Quota);
        job.Cancel();
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(VideoJobPhase.Cancelled, job.State.Phase);
        AssertNothingLeft();
    }

    [Fact]
    public async Task Resume_without_switching_retries_the_same_engine_for_the_missing_cues_only()
    {
        var rig = NewRig(10);
        var tr = new FakeTranslator("a", ItemsLimits, (call, n) => n == 0 ? QuotaReply() : Echo(call));
        var job = NewJob(rig, new FakeAsr("asr", true), tr);
        _ = job.Start();
        await WaitFor(job, s => s.Phase == VideoJobPhase.QuotaExhausted);
        Assert.True(job.Resume());
        Assert.Equal(VideoJobPhase.Done, (await WaitFor(job, s => s.IsTerminal)).Phase);
        Assert.Equal(2, tr.Calls.Count);
    }

    // ---------------- A02 / A03 ----------------

    [Fact] // A03: a text-only ASR is refused before anything is opened or sent
    public async Task A03_text_only_asr_is_refused_before_any_media_or_request()
    {
        var rig = NewRig();
        var asr = new FakeAsr("gemini", false);
        var jobs = new VideoJobs(rig.Tokens, rig.Decoder, files, () => asr, () => new FakeTranslator("tr", ItemsLimits));
        var start = jobs.Start(rig.Token, "en", "zh");
        Assert.Equal((null, VideoErrors.NoTimecodes), (start.Job, start.Refusal));
        Assert.Equal(0, rig.Decoder.Opens); Assert.Equal(0, asr.Calls);
        // A job built directly with such a provider fails the same way.
        var job = NewJob(rig, asr, new FakeTranslator("tr", ItemsLimits));
        var state = await Run(job);
        Assert.Equal((VideoJobPhase.Failed, VideoErrors.NoTimecodes), (state.Phase, state.ErrorCode));
        Assert.Equal(0, rig.Decoder.Opens); Assert.Equal(0, asr.Calls);
        Assert.Empty(job.Cues());
    }

    [Fact] // A02: the video job uses only the factories it was given (its own selections), looked up when a job starts
    public async Task A02_services_are_resolved_per_job_from_the_video_selection()
    {
        var rig = NewRig(10);
        IAsrProvider? current = null;
        var jobs = new VideoJobs(rig.Tokens, rig.Decoder, files, () => current, () => new FakeTranslator("tr", ItemsLimits));
        Assert.Equal(VideoErrors.NoAsr, jobs.Start(rig.Token, "en", "zh").Refusal);
        Assert.Equal(VideoErrors.NoTranslation, new VideoJobs(rig.Tokens, rig.Decoder, files, () => new FakeAsr("a", true), () => null).Start(rig.Token, "en", "zh").Refusal);
        var first = new FakeAsr("video-asr-1", true);
        current = first; jobs.Confirmation = new Confirm();
        var job = jobs.Start(rig.Token, "en", "zh", slicing: new MediaSliceOptions(TimeSpan.FromSeconds(10))).Job!;
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(("video-asr-1", "tr"), (job.State.AsrService, job.State.TranslationService));
        Assert.Equal(1, first.Calls);
    }

    [Fact]
    public async Task Only_one_job_runs_at_a_time()
    {
        var rig = NewRig(10);
        var gate = new TaskCompletionSource<bool>();
        var jobs = new VideoJobs(rig.Tokens, rig.Decoder, files, () => new FakeAsr("a", true), () => new FakeTranslator("tr", ItemsLimits)) { Confirmation = new Confirm((_, _) => gate.Task) };
        var first = jobs.Start(rig.Token, "en", "zh").Job!;
        Assert.Equal(VideoErrors.Busy, jobs.Start(rig.Token, "en", "zh").Refusal);
        Assert.Same(first, jobs.Active);
        gate.SetResult(true);
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Null(jobs.Active);
        var second = jobs.Start(rig.Token, "en", "zh");
        Assert.NotNull(second.Job);
        await second.Job.Completion.WaitAsync(TimeSpan.FromSeconds(20), Ct);
    }

    // ---------------- A05 / A06 job part ----------------

    [Fact] // A05: a silent slice makes no request and the later cues keep their real time
    public async Task A05_silent_slice_is_not_uploaded_and_later_cues_do_not_move_earlier()
    {
        var rig = NewRig(30, sec => sec < 10 || sec >= 20);
        var asr = new FakeAsr("asr", true);
        var job = NewJob(rig, asr, new FakeTranslator("tr", ItemsLimits));
        Assert.Equal(VideoJobPhase.Done, (await Run(job)).Phase);
        Assert.Equal(2, asr.Calls);
        Assert.Equal([0.5, 3, 20.5, 23], job.Cues().Select(c => c.Start));
        AssertNothingLeft();
    }

    [Fact] // A06 (job part): a result that would move back on the timeline ends the job, its cues never enter the list
    public async Task A06_a_backwards_slice_result_fails_the_job_and_adds_nothing_from_that_slice()
    {
        var rig = NewRig();
        var asr = new FakeAsr("asr", true, (n, _, _) => Task.FromResult(n == 1
            ? new AsrOutcome.Transcribed("segments", "x", [new AsrSegment(5, 6, "late"), new AsrSegment(2, 3, "early")])
            : Two(n)));
        var job = NewJob(rig, asr, new FakeTranslator("tr", ItemsLimits));
        var state = await Run(job);
        Assert.Equal((VideoJobPhase.Failed, VideoErrors.AsrFailed, ErrorKind.BadResponse), (state.Phase, state.ErrorCode, state.Error!.Kind));
        Assert.Equal(2, asr.Calls); // slice 2 is never sent
        Assert.Equal(["s0a", "s0b\nline two"], job.Cues().Select(c => c.Original));
        AssertNothingLeft();
    }

    [Fact]
    public async Task A06_a_failed_asr_slice_fails_the_job_and_keeps_the_earlier_cues()
    {
        var rig = NewRig();
        var asr = new FakeAsr("asr", true, (n, _, _) => Task.FromResult(n == 1 ? new AsrOutcome.Failure(new ProviderError(ErrorKind.BadResponse, "x")) : Two(n)));
        var job = NewJob(rig, asr, new FakeTranslator("tr", ItemsLimits));
        var state = await Run(job);
        Assert.Equal((VideoJobPhase.Failed, ErrorKind.BadResponse), (state.Phase, state.Error!.Kind));
        Assert.Equal(2, job.Cues().Count);
        Assert.Equal(2, asr.Calls);
    }

    [Fact]
    public async Task A05_cancel_in_flight_stops_the_upload_and_leaves_no_slice_lease_or_file()
    {
        var rig = NewRig();
        var entered = new TaskCompletionSource();
        bool cancelled = false;
        var asr = new FakeAsr("asr", true, async (n, _, ct) =>
        {
            if (n == 0) return Two(n);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { cancelled = true; throw; }
            return Two(n);
        });
        var job = NewJob(rig, asr, new FakeTranslator("tr", ItemsLimits));
        _ = job.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        job.Cancel();
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(cancelled);
        Assert.Equal(VideoJobPhase.Cancelled, job.State.Phase);
        Assert.Equal(2, job.Cues().Count); // slice 0's work stays readable
        AssertNothingLeft();
    }

    [Fact]
    public async Task Cancel_while_asr_is_exhausted_releases_the_held_slice()
    {
        var rig = NewRig();
        var job = NewJob(rig, new FakeAsr("asr", true, (n, _, _) => Task.FromResult(n == 0 ? Quota() : Two(n))), new FakeTranslator("tr", ItemsLimits));
        _ = job.Start();
        await WaitFor(job, s => s.Phase == VideoJobPhase.QuotaExhausted);
        Assert.True(leases.ActiveCount > 0);
        job.Cancel();
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(VideoJobPhase.Cancelled, job.State.Phase);
        AssertNothingLeft();
    }

    // ---------------- pause / resume ----------------

    [Fact]
    public async Task Pause_waits_for_the_request_in_flight_then_makes_no_request_until_resume()
    {
        var rig = NewRig();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var asr = new FakeAsr("asr", true, async (n, _, _) => { if (n == 0) { entered.SetResult(); await release.Task; } return Two(n); });
        var tr = new FakeTranslator("tr", ItemsLimits);
        var job = NewJob(rig, asr, tr);
        _ = job.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        job.Pause();
        release.SetResult();
        await WaitFor(job, s => s.Phase == VideoJobPhase.Paused);
        await Task.Delay(200, Ct);
        Assert.Equal(1, asr.Calls); Assert.Empty(tr.Calls);
        Assert.Equal(2, job.Cues().Count); // the finished request's result is kept
        Assert.True(job.Resume());
        Assert.Equal(VideoJobPhase.Done, (await WaitFor(job, s => s.IsTerminal)).Phase);
        Assert.Equal(3, asr.Calls);
        Assert.False(job.Resume()); // nothing to resume
    }

    // ---------------- media errors, results kept ----------------

    [Fact]
    public async Task No_audio_and_decode_errors_fail_with_the_media_code_before_the_notice()
    {
        var rig = NewRig();
        var noAudio = new FakeDecoder(() => new FakeReader(new MediaProbe(TimeSpan.FromSeconds(5), true, []), Blocks(0, _ => true).GetEnumerator()));
        var slicer = new MediaSlicer(rig.Tokens, noAudio, files, new MediaSliceOptions(TimeSpan.FromSeconds(10)));
        var confirm = new Confirm();
        var job = new VideoJob("j", rig.Token, new VideoJobOptions("en", "zh"), slicer, rig.Tokens, files, () => confirm, new FakeAsr("asr", true), new FakeTranslator("tr", ItemsLimits));
        var state = await Run(job);
        Assert.Equal((VideoJobPhase.Failed, MediaErrors.NoAudio), (state.Phase, state.ErrorCode));
        Assert.Empty(confirm.Notices);

        var broken = new FakeDecoder(() => throw new MediaDecodeException(MediaErrors.UnsupportedEncoding, "x"));
        var job2 = new VideoJob("j2", rig.Token, new VideoJobOptions("en", "zh"), new MediaSlicer(rig.Tokens, broken, files), rig.Tokens, files, () => confirm, new FakeAsr("asr", true), new FakeTranslator("tr", ItemsLimits));
        Assert.Equal(MediaErrors.UnsupportedEncoding, (await Run(job2)).ErrorCode);
        AssertNothingLeft();
    }

    [Fact]
    public async Task Everything_silent_is_no_speech_not_an_empty_success()
    {
        var rig = NewRig(10, _ => false);
        var job = NewJob(rig, new FakeAsr("asr", true), new FakeTranslator("tr", ItemsLimits));
        var state = await Run(job);
        Assert.Equal((VideoJobPhase.Failed, VideoErrors.NoSpeech), (state.Phase, state.ErrorCode));
    }

    [Fact]
    public async Task Results_stay_in_the_process_after_cancel_and_after_done_and_are_bounded()
    {
        var rig = NewRig();
        var entered = new TaskCompletionSource();
        var asr = new FakeAsr("asr", true, async (n, _, ct) => { if (n == 0) return Two(n); entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Two(n); });
        var jobs = new VideoJobs(rig.Tokens, rig.Decoder, files, () => asr, () => new FakeTranslator("tr", ItemsLimits)) { Confirmation = new Confirm() };
        var job = jobs.Start(rig.Token, "en", "zh", slicing: new MediaSliceOptions(TimeSpan.FromSeconds(10))).Job!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        jobs.CancelActive(); // the window closes
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var kept = jobs.Results.Get(job.Id)!;
        Assert.Equal(VideoJobPhase.Cancelled, kept.State.Phase);
        Assert.Equal(2, kept.Cues.Count);
        Assert.Equal("movie.mp4", kept.DisplayName);
        Assert.Contains(jobs.Results.List(), r => r.JobId == job.Id);
        Assert.True(jobs.Results.Remove(job.Id));
        Assert.Null(jobs.Results.Get(job.Id));

        var store = new VideoResultStore();
        var ids = new List<string>();
        for (int i = 0; i < VideoResultStore.Capacity + 3; i++)
        {
            var j = new VideoJob($"j{i}", rig.Token, new VideoJobOptions("en", "zh"), rig.Slicer, rig.Tokens, files, () => new Confirm(), new FakeAsr("a", false), new FakeTranslator("t", ItemsLimits));
            await Run(j); // fails at once (no timecodes), so it is finished
            store.Put(j); ids.Add(j.Id);
        }
        Assert.Equal(VideoResultStore.Capacity, store.List().Count);
        Assert.Null(store.Get("j0")); Assert.NotNull(store.Get(ids[^1]));
    }
}
