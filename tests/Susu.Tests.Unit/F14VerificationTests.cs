using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Storage;
using Susu.Testing;
using Susu.Windows.Media;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// Independent F14 verification (testing agent). Gaps the coding agents' tests leave: media -> slices -> ASR -> translation -> cues ->
/// SRT/VTT/TXT exported and parsed back by a parser written here (times within 1 ms, count, order, escaping, hours above 99); real Media
/// Foundation decode of WAV and AAC through the whole job; the real OpenAI package in the sandbox against a loopback vendor with hostile
/// text; A03 at every layer; upload "no"/cancel; cancel at every stage; quota on ASR then translation with provider call counts; two-slice
/// back-pressure; disk full on decode and export; closed-window export; the page never receiving a path or token; busy; the 42-minute
/// time axis. In the non-parallel media collection so it cannot disturb the process-wide memory and handle measurements.
/// Real vendors, real players, a real desktop and dialogs: not executed.
/// </summary>
[Collection(MediaDecodeCollection.Name)]
public sealed class F14VerificationTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IDisposable> cleanup = [];
    public void Dispose() { foreach (var d in cleanup) d.Dispose(); }

    // ================= kit =================

    private sealed class Kit : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly string Cache;
        public readonly FileLeases Leases;
        public readonly LeasedFiles Files;
        public readonly MediaTokens Tokens = new();
        public Kit() { Cache = Path.Combine(Root.Root, "cache"); Directory.CreateDirectory(Cache); Leases = new FileLeases(Cache); Files = new LeasedFiles(Leases); }
        public string Media(string name, string content = "x") { string p = Path.Combine(Root.Root, name); File.WriteAllText(p, content); return p; }
        public int FilesLeft => Directory.EnumerateFiles(Cache, "*", SearchOption.AllDirectories).Count();
        public void AssertClean() { Assert.Equal(0, Leases.ActiveCount); Assert.Equal(0, FilesLeft); }
        public void Dispose() { Leases.Dispose(); Root.Dispose(); }
    }

    private sealed class CountingDecoder(Func<IMediaAudioReader> open) : IMediaDecoder
    {
        public int Probes, Opens;
        public Task<MediaProbe> ProbeAsync(string path, CancellationToken ct) { Interlocked.Increment(ref Probes); return Task.FromResult(open().Probe); }
        public Task<IMediaAudioReader> OpenAsync(string path, CancellationToken ct) { Interlocked.Increment(ref Opens); return Task.FromResult(open()); }
    }

    private static MediaProbe Probe(int seconds) => new(TimeSpan.FromSeconds(seconds), true, [new MediaAudioStream(1, "aac", 44100, 2, true)]);

    private static CountingDecoder FakeMedia(int seconds) => new(() => new VideoJobTests.FakeReader(Probe(seconds), VideoJobTests.Blocks(seconds, _ => true).GetEnumerator()));

    /// <summary>Blocks forever (until cancelled) after <paramref name="before"/> blocks.</summary>
    private sealed class BlockedReader(MediaProbe probe, int before) : IMediaAudioReader
    {
        private int i;
        public MediaProbe Probe { get; } = probe;
        public async Task<MediaPcmBlock?> ReadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (i >= before) { await Task.Delay(Timeout.Infinite, ct); }
            var pcm = new byte[32000];
            for (int s = 0; s < 16000; s++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(s * 2), 1000);
            return new MediaPcmBlock(pcm, TimeSpan.FromSeconds(i++));
        }
        public void Dispose() { }
    }

    private sealed class BlockTranslator(Func<int, bool> block) : ISubtitleTranslationProvider
    {
        public int Calls;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ServiceId => "blk";
        public string DisplayName => "Blocking";
        public TranslationLimits Limits => VideoJobTests.ItemsLimits;
        public string? QuotaNoteKey => null;
        public async Task<SubtitleBatchOutcome> TranslateBatchAsync(SubtitleBatchCall call, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref Calls) - 1;
            if (block(n)) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return VideoJobTests.Echo(call);
        }
    }

    private static TranscribeWindowTests.FakeAsr Asr(Func<int, CancellationToken, Task<AsrOutcome>> f, bool timecodes = true) => new(f, timecodes);

    private static VideoJobs NewJobs(Kit kit, IMediaDecoder decoder, IAsrProvider? asr, ISubtitleTranslationProvider? tr, IVideoUploadConfirmation? confirm = null) =>
        new(kit.Tokens, decoder, kit.Files, () => asr, () => tr) { Confirmation = confirm ?? new VideoJobTests.Confirm() };

    private static readonly MediaSliceOptions Ten = new(TimeSpan.FromSeconds(10));

    private static VideoJob StartJob(VideoJobs jobs, Kit kit, string name = "clip.mp4", MediaSliceOptions? slicing = null)
    {
        var start = jobs.Start(kit.Tokens.Issue(kit.Media(name))!, "en", "zh", slicing: slicing ?? Ten);
        Assert.Null(start.Refusal);
        return start.Job!;
    }

    private static async Task<VideoJobState> WaitState(VideoJob job, Func<VideoJobState, bool> predicate, int seconds = 30)
    {
        var tcs = new TaskCompletionSource<VideoJobState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(VideoJobState s) { if (predicate(s)) tcs.TrySetResult(s); }
        job.StateChanged += On;
        try { On(job.State); return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(seconds), Ct); }
        finally { job.StateChanged -= On; }
    }

    private static async Task<VideoJobState> Finish(VideoJob job)
    {
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        return job.State;
    }

    // ================= subtitle parsers (written here, independent of the formatter) =================

    private sealed record Parsed(long Start, long End, string[] Lines);
    private sealed record Exp(double Start, double End, string Original, string? Translation);

    private static readonly Regex Stamp = new(@"^(\d{2,}):(\d{2}):(\d{2})([.,])(\d{3})$", RegexOptions.CultureInvariant);

    private static long ParseStamp(string s, char sep)
    {
        var m = Stamp.Match(s);
        Assert.True(m.Success, $"bad time stamp '{s}'");
        Assert.Equal(sep.ToString(), m.Groups[4].Value);
        return ((long.Parse(m.Groups[1].Value) * 60 + long.Parse(m.Groups[2].Value)) * 60 + long.Parse(m.Groups[3].Value)) * 1000 + long.Parse(m.Groups[5].Value);
    }

    private static (long, long) ParseTiming(string line, char sep)
    {
        var parts = line.Split(" --> ");
        Assert.Equal(2, parts.Length);
        return (ParseStamp(parts[0], sep), ParseStamp(parts[1], sep));
    }

    private static string Decode(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM");
        string text = new UTF8Encoding(false, true).GetString(bytes); // strict: throws on invalid UTF-8
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text);
        return text;
    }

    private static List<Parsed> ParseSrt(string text)
    {
        var list = new List<Parsed>();
        foreach (var block in text.TrimEnd('\n').Split("\n\n"))
        {
            var lines = block.Split('\n');
            Assert.Equal((list.Count + 1).ToString(), lines[0]); // contiguous numbering
            var (s, e) = ParseTiming(lines[1], ',');
            Assert.True(lines.Length >= 3, "cue without text");
            list.Add(new Parsed(s, e, [.. lines.Skip(2).Select(l => l.Replace("\u200B", ""))]));
        }
        return list;
    }

    private static List<Parsed> ParseVtt(string text)
    {
        Assert.StartsWith("WEBVTT\n\n", text);
        var list = new List<Parsed>();
        foreach (var block in text["WEBVTT\n\n".Length..].TrimEnd('\n').Split("\n\n"))
        {
            var lines = block.Split('\n');
            var (s, e) = ParseTiming(lines[0], '.');
            Assert.True(lines.Length >= 2, "cue without text");
            Assert.DoesNotContain(lines.Skip(1), l => l.Contains("-->") || l.Contains('<') || l.Contains('>'));
            list.Add(new Parsed(s, e, [.. lines.Skip(1).Select(l => l.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&"))]));
        }
        return list;
    }

    private static List<Parsed> ParseTxt(string text)
    {
        var list = new List<Parsed>();
        foreach (var block in text.TrimEnd('\n').Split("\n\n"))
        {
            var lines = block.Split('\n');
            var m = Regex.Match(lines[0], @"^\[([^\]]+)\] (.*)$");
            Assert.True(m.Success, lines[0]);
            long s = ParseStamp(m.Groups[1].Value, '.');
            var rest = new List<string> { m.Groups[2].Value };
            foreach (var l in lines.Skip(1)) { Assert.StartsWith("    ", l); rest.Add(l[4..]); }
            list.Add(new Parsed(s, s, [.. rest]));
        }
        return list;
    }

    private static string[] Lines(string? t) => t is null ? [] :
        [.. t.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u2028', '\n').Replace('\t', ' ').Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];

    private static string[] ExpectedLines(Exp e, SubtitleMode mode) => mode switch
    {
        SubtitleMode.Original => Lines(e.Original),
        SubtitleMode.Translation => Lines(e.Translation),
        SubtitleMode.BilingualOriginalFirst => [.. Lines(e.Original), .. Lines(e.Translation)],
        _ => [.. Lines(e.Translation), .. Lines(e.Original)],
    };

    private static void AssertExport(string path, SubtitleFormat format, SubtitleMode mode, IReadOnlyList<Exp> cues, double toleranceMs)
    {
        string text = Decode(path);
        var parsed = format switch { SubtitleFormat.Srt => ParseSrt(text), SubtitleFormat.Vtt => ParseVtt(text), _ => ParseTxt(text) };
        var want = cues.Where(c => mode != SubtitleMode.Translation || c.Translation is not null).ToList();
        Assert.Equal(want.Count, parsed.Count);
        for (int i = 0; i < want.Count; i++)
        {
            Assert.InRange(Math.Abs(parsed[i].Start - want[i].Start * 1000), 0, toleranceMs);
            if (format != SubtitleFormat.Txt) Assert.InRange(Math.Abs(parsed[i].End - want[i].End * 1000), 0, toleranceMs);
            Assert.Equal(ExpectedLines(want[i], mode), parsed[i].Lines);
            if (i > 0) Assert.True(parsed[i].Start >= parsed[i - 1].Start, "order");
        }
    }

    private static readonly (SubtitleFormat F, string Ext)[] Formats = [(SubtitleFormat.Srt, "srt"), (SubtitleFormat.Vtt, "vtt"), (SubtitleFormat.Txt, "txt")];
    private static readonly SubtitleMode[] Modes = [SubtitleMode.Original, SubtitleMode.Translation, SubtitleMode.BilingualOriginalFirst, SubtitleMode.BilingualTranslationFirst];

    private static void ExportAllAndCompare(SubtitleExporter exporter, string jobId, string dir, IReadOnlyList<Exp> cues, double toleranceMs)
    {
        foreach (var (f, ext) in Formats)
            foreach (var mode in Modes)
            {
                string path = Path.Combine(dir, $"out-{mode}.{ext}");
                var r = exporter.ExportTo(jobId, mode, f, path);
                Assert.True(r.Ok, $"{f} {mode}: {r.Error}");
                AssertExport(path, f, mode, cues, toleranceMs);
            }
    }

    // ================= hostile script: what the "vendor" says, per 10 s slice =================

    private static readonly (double S, double E, string T)[][] Script =
    [
        [(0.5, 2.0, "He said --> go <b>now</b> & left"), (3.0, 5.0, "line1\r\nline2\r\n\r\nline4")],
        [(0.0, 1.5, "WEBVTT"), (2.25, 4.0, "日本語 émoji 😀 \"quote\" 'x'"), (2.5, 3.5, "overlap & <v B>speaker</v>")],
        [(1.0, 2.0, "[00:00:01.000] fake stamp"), (3.0, 4.0, "42")],
    ];

    private static AsrOutcome Say(int slice) => new AsrOutcome.Transcribed("segments", "x", [.. Script[slice % 3].Select(s => new AsrSegment(s.S, s.E, s.T))]);

    private static List<Exp> ExpectedFromScript(int slices, double sliceSeconds = 10) =>
        [.. Enumerable.Range(0, slices).SelectMany(k => Script[k % 3].Select(s => new Exp(k * sliceSeconds + s.S, k * sliceSeconds + s.E, s.T, "T:" + s.T)))];

    private static void WriteWav(string path, int seconds, int rate = 44100, int channels = 2)
    {
        int frames = rate * seconds;
        using var f = File.Create(path);
        var h = new byte[44];
        "RIFF"u8.CopyTo(h); BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4), 36 + frames * channels * 2);
        "WAVEfmt "u8.CopyTo(h.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), rate); BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), rate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), (short)(channels * 2)); BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(40), frames * channels * 2);
        f.Write(h);
        var buf = new byte[rate * channels * 2];
        for (int s = 0; s < seconds; s++)
        {
            for (int i = 0; i < rate; i++)
                for (int c = 0; c < channels; c++)
                    BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan((i * channels + c) * 2), (short)(Math.Sin(2 * Math.PI * 440 * (s * rate + i) / rate) * 12000));
            f.Write(buf);
        }
    }

    // ================= 1. export round trip =================

    [Fact] // VID04/A06: 12 exports of a seeded, hostile, 100-hour cue set parse back: count, order, times within 1 ms, text, numbering
    public void Fuzzed_cues_round_trip_through_all_twelve_exports_including_hours_above_99()
    {
        string[] pool = ["He said --> go <b>now</b> & left", "line1\r\nline2\r\n\r\nline4", "WEBVTT", "日本語 émoji 😀 \"q\" 'x'", "[00:00:01.000] fake stamp", "42",
            "NOTE not a comment", "a\u2028b", "-->", "&amp; already", "<v Speaker>hi</v>", "tab\there", "  padded  ", "1\n2\n3"];
        var rng = new Random(20261001);
        var cues = new List<VideoCue>();
        var exp = new List<Exp>();
        double t = 0;
        void Add(double start, double dur)
        {
            string o = pool[rng.Next(pool.Length)];
            string? tr = rng.Next(5) == 0 ? null : "译:" + pool[rng.Next(pool.Length)];
            string id = "c" + (cues.Count + 1);
            cues.Add(new VideoCue(id, start, start + dur, o, tr));
            exp.Add(new Exp(start, start + dur, o, tr));
        }
        for (int i = 0; i < 150; i++) { t += rng.NextDouble() * 2.5; Add(t, 0.2 + rng.NextDouble() * 4); }
        foreach (double h in new[] { 99.0, 99.9999, 100.0, 100.5, 101.25 }) { t = Math.Max(t, h * 3600 + rng.NextDouble()); Add(t, 1.5); }
        using var root = new TempRoot();
        var result = new VideoJobResult("fz", "Fuzz.mp4", new VideoJobState("fz", VideoJobPhase.Done), cues, null);
        var exporter = new SubtitleExporter(id => id == "fz" ? result : null);
        ExportAllAndCompare(exporter, "fz", root.Root, exp, toleranceMs: 1);
        string srt = File.ReadAllText(Path.Combine(root.Root, "out-Original.srt"));
        Assert.Contains("100:", srt);
        Assert.Matches(@"(?m)^99:\d\d:\d\d,\d{3} --> ", srt);
    }

    // ================= 2. end to end through the job =================

    private async Task<(VideoJob Job, SubtitleExporter Exporter, string Dir)> RunWholeJobAsync(Kit kit, IMediaDecoder decoder, string file, TranscribeWindowTests.FakeAsr asr)
    {
        var tr = new VideoJobTests.FakeTranslator("tr", VideoJobTests.ItemsLimits);
        var jobs = NewJobs(kit, decoder, asr, tr);
        var start = jobs.Start(kit.Tokens.Issue(file)!, "en", "zh", slicing: Ten);
        Assert.Null(start.Refusal);
        var job = start.Job!;
        Assert.Equal(VideoJobPhase.Done, (await Finish(job)).Phase);
        var result = jobs.Results.Get(job.Id)!;
        Assert.Equal(Enumerable.Range(1, result.Cues.Count).Select(i => "c" + i), result.Cues.Select(c => c.Id));
        string dir = Path.Combine(kit.Root.Root, "out" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return (job, new SubtitleExporter(jobs.Results), dir);
    }


    [Theory] // real Media Foundation (WAV, AAC in MP4 and M4A) -> real slicer -> job -> translation -> export, parsed back
    [InlineData("clip.wav")]
    [InlineData("clip.mp4")]
    [InlineData("clip.m4a")]
    public async Task Real_media_foundation_decode_through_the_whole_job_to_parsed_exports(string name)
    {
        using var kit = new Kit();
        string file = Path.Combine(kit.Root.Root, name);
        if (name.EndsWith(".wav")) WriteWav(file, 25);
        else Assert.SkipWhen(MediaFixtureWriter.Write(file, 25) is { } why, $"this machine cannot write {name}");
        var asr = Asr((n, ct) => Task.FromResult(Say(n)));
        var (job, exporter, outDir) = await RunWholeJobAsync(kit, new MediaFoundationDecoder(), file, asr);
        Assert.Equal(3, asr.Calls); // 25 s in 10 s slices
        var cues = job.Cues();
        Assert.Equal(7, cues.Count);
        bool exact = name.EndsWith(".wav");
        double tol = exact ? 1 : 150; // AAC: container start and encoder delay
        var want = ExpectedFromScript(3);
        for (int i = 0; i < want.Count; i++)
        {
            Assert.InRange(Math.Abs(cues[i].Start - want[i].Start) * 1000, 0, tol);
            Assert.InRange(Math.Abs(cues[i].End - want[i].End) * 1000, 0, tol);
        }
        ExportAllAndCompare(exporter, job.Id, outDir,
            [.. cues.Select(c => new Exp(c.Start, c.End, c.Original, c.Translation))], 1);
        Assert.All(cues, c => Assert.Equal("T:" + c.Original, c.Translation));
        kit.AssertClean();
    }

    [Fact] // the 42-minute time axis: nine 5-minute slices, every cue exactly on slice offset + segment time, export parses back at 40:01.250
    public async Task Forty_two_minute_timeline_offsets_are_exact_in_the_export()
    {
        using var kit = new Kit();
        var asr = Asr((n, ct) => Task.FromResult<AsrOutcome>(new AsrOutcome.Transcribed("segments", "x", [new AsrSegment(1.25, 2.5, $"slice {n}"), new AsrSegment(100.125, 101, $"late {n}")])));
        var tr = new VideoJobTests.FakeTranslator("tr", VideoJobTests.ItemsLimits);
        var jobs = NewJobs(kit, FakeMedia(42 * 60), asr, tr);
        var job = StartJob(jobs, kit, slicing: new MediaSliceOptions(TimeSpan.FromMinutes(5)));
        Assert.Equal(VideoJobPhase.Done, (await Finish(job)).Phase);
        Assert.Equal(9, asr.Calls);
        var cues = job.Cues();
        Assert.Equal(18, cues.Count);
        for (int k = 0; k < 9; k++)
        {
            Assert.Equal(300.0 * k + 1.25, cues[2 * k].Start);
            Assert.Equal(300.0 * k + 100.125, cues[2 * k + 1].Start);
        }
        var exp = cues.Select(c => new Exp(c.Start, c.End, c.Original, c.Translation)).ToList();
        string dir = Path.Combine(kit.Root.Root, "o"); Directory.CreateDirectory(dir);
        var ex = new SubtitleExporter(jobs.Results);
        ExportAllAndCompare(ex, job.Id, dir, exp, 1);
        Assert.Contains("00:40:01,250 --> 00:40:02,500", File.ReadAllText(Path.Combine(dir, "out-Original.srt")));
        kit.AssertClean();
    }

    [Fact] // the shipped OpenAI package in the real sandbox, real MF WAV, loopback vendor with hostile segment text -> exports parse back
    public async Task Sandboxed_real_package_with_real_wav_and_hostile_vendor_text_exports_and_parses()
    {
        int hits = 0; var sizes = new List<int>();
        using var server = new LoopbackHttpServer(req =>
        {
            int n = Interlocked.Increment(ref hits) - 1;
            lock (sizes) sizes.Add(req.Body.Length);
            return LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(new { segments = Script[n % 3].Select(s => new { start = s.S, end = s.E, text = s.T }) }));
        });
        using var rig = F12VerificationTests.Build(SpeechCatalog.OpenAiAsr, server);
        Assert.SkipWhen(rig is null, "susu.exe is not published");
        using var kit = new Kit();
        string file = Path.Combine(kit.Root.Root, "clip.wav"); WriteWav(file, 25);
        var tr = new VideoJobTests.FakeTranslator("tr", VideoJobTests.ItemsLimits);
        var jobs = new VideoJobs(kit.Tokens, new MediaFoundationDecoder(), rig!.Files, () => rig.Provider("whisper-1"), () => tr) { Confirmation = new VideoJobTests.Confirm() };
        var job = jobs.Start(kit.Tokens.Issue(file)!, "en", "zh", slicing: Ten).Job!;
        Assert.Equal(VideoJobPhase.Done, (await Finish(job)).Phase);
        Assert.Equal(3, hits);
        Assert.All(sizes.Take(2), s => Assert.InRange(s, 320_000, 340_000)); // 10 s of 16 kHz mono 16-bit plus multipart framing
        string dir = Path.Combine(kit.Root.Root, "o"); Directory.CreateDirectory(dir);
        ExportAllAndCompare(new SubtitleExporter(jobs.Results), job.Id, dir, ExpectedFromScript(3), 1);
        await rig.AssertNoFilesLeftAsync();
        Assert.Equal(0, rig.Leases.ActiveCount);
    }

    // ================= 3. A03 at every layer =================

    [Fact] // A03: a timecode-less ASR (Gemini) never yields a video job, a cue or a time, at VideoJobs, VideoJob, ChangeTranslator and SelectSpeech
    public async Task A03_text_only_asr_is_refused_at_every_layer_with_no_cue_and_no_time()
    {
        using var kit = new Kit();
        var asr = Asr((n, ct) => Task.FromResult(Say(n)), timecodes: false);
        var tr = new VideoJobTests.FakeTranslator("tr", VideoJobTests.ItemsLimits);
        var decoder = FakeMedia(30);
        var confirm = new VideoJobTests.Confirm();
        // VideoJobs
        var jobs = NewJobs(kit, decoder, asr, tr, confirm);
        var refused = jobs.Start(kit.Tokens.Issue(kit.Media("a.mp4"))!, "en", "zh");
        Assert.Equal((null, VideoErrors.NoTimecodes), (refused.Job, refused.Refusal));
        Assert.Empty(jobs.Results.List());
        // VideoJob built directly (bypassing VideoJobs): fails before probing, asking or sending
        string token = kit.Tokens.Issue(kit.Media("b.mp4"))!;
        using var direct = new VideoJob("direct", token, new VideoJobOptions("en", "zh"), new MediaSlicer(kit.Tokens, decoder, kit.Files, Ten), kit.Tokens, kit.Files, () => confirm, asr, tr);
        _ = direct.Start();
        Assert.Equal((VideoJobPhase.Failed, VideoErrors.NoTimecodes), ((await Finish(direct)).Phase, direct.State.ErrorCode));
        Assert.Empty(direct.Cues());
        Assert.Equal((0, 0, 0, 0, 0), (decoder.Opens, asr.Calls, tr.Calls.Count, confirm.Notices.Count, kit.Leases.ActiveCount));
        // SwitchAsr to a text-only model is refused while the job is parked on quota
        var quota = Asr((n, ct) => TranscribeWindowTests.AsrQuota(n, ct));
        var parked = StartJob(NewJobs(kit, FakeMedia(20), quota, tr), kit);
        await WaitState(parked, s => s.Phase == VideoJobPhase.QuotaExhausted);
        Assert.False(parked.SwitchAsr(asr));
        Assert.Equal(VideoJobPhase.QuotaExhausted, parked.State.Phase);
        parked.Cancel(); await Finish(parked);
        Assert.Empty(parked.Cues());
        kit.AssertClean();

        // Settings and the Transcribe window (shell)
        using var rig = new TranscribeWindowTests.Rig();
        rig.Shell.Open(WindowKind.Settings); rig.Ready(WindowKind.Settings);
        var req = new SpeechSelectRequest(rig.Config.State.Revision, rig.Config.State.FileHash, "videoAsr", "gemini-asr", "gemini-2.5-flash");
        Assert.Equal("needs-timecodes", rig.Run(WindowKind.Settings, UiCommands.SelectSpeech, req).Error);
        Assert.Equal("openai-asr", rig.Config.State.Effective.Speech.VideoAsr.Instance); // the selection was not saved
        Assert.Equal(SpeechCatalog.NeedsTimecodes, SpeechCatalog.Check(SpeechSlot.VideoAsr, new SpeechSelection(SpeechCatalog.GeminiAsr, "gemini-2.5-flash")));
        rig.Asr = new TranscribeWindowTests.FakeAsr(TranscribeWindowTests.Segments, timecodes: false);
        rig.Open();
        Assert.True((await rig.Shell.OnMediaDroppedAsync(rig.MediaFile)).Ok);
        Assert.True(rig.T(UiCommands.StartTranscription).Ok);
        Assert.Equal(("picked", VideoErrors.NoTimecodes), (rig.Phase, rig.Shell.TranscribeWindowView!.ErrorCode));
        Assert.Empty(rig.CueEvents());
        Assert.Equal((0, 0, 0), (rig.Asr.Calls, rig.Decoder.Opens, rig.Jobs.Results.List().Count));
    }


    // ================= 4. upload confirmation no / cancel =================

    [Theory] // T06: no answer, cancel while the question is open, cancel of a never-answered job: no ASR, no translation, no lease, no file
    [InlineData("no")]
    [InlineData("cancel")]
    [InlineData("throws")]
    public async Task Upload_confirmation_no_or_cancel_leaves_no_request_lease_or_file(string how)
    {
        using var kit = new Kit();
        var asr = Asr((n, ct) => Task.FromResult(Say(n)));
        var tr = new VideoJobTests.FakeTranslator("tr", VideoJobTests.ItemsLimits);
        var asked = new TaskCompletionSource();
        var confirm = new VideoJobTests.Confirm(async (notice, ct) =>
        {
            asked.TrySetResult();
            if (how == "no") return false;
            if (how == "throws") throw new InvalidOperationException("dialog crashed");
            await Task.Delay(Timeout.Infinite, ct); return true;
        });
        var jobs = NewJobs(kit, FakeMedia(30), asr, tr, confirm);
        var job = StartJob(jobs, kit);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        if (how == "cancel") { Assert.Equal(VideoJobPhase.AwaitingConfirm, (await WaitState(job, s => s.Phase == VideoJobPhase.AwaitingConfirm)).Phase); job.Cancel(); }
        var end = await Finish(job);
        Assert.Contains(end.Phase, new[] { VideoJobPhase.Cancelled, VideoJobPhase.Failed });
        Assert.NotEqual(VideoJobPhase.Done, end.Phase);
        if (how == "no") Assert.Equal(VideoErrors.NotConfirmed, end.ErrorCode);
        Assert.Equal((0, 0), (asr.Calls, tr.Calls.Count));
        Assert.Empty(job.Cues());
        kit.AssertClean();
        Assert.Null(jobs.Active);
        // the file is still pickable and a new job may start after the refusal
        Assert.Null(jobs.Start(kit.Tokens.Issue(kit.Media("again.mp4"))!, "en", "zh", slicing: Ten).Refusal);
        jobs.CancelActive();
    }

    [Fact] // shell: declining and closing with the question open both end with no request and no lease
    public async Task Shell_confirmation_no_and_close_leave_nothing()
    {
        using var rig = new TranscribeWindowTests.Rig();
        await rig.StartToConfirmAsync();
        Assert.True(rig.T(UiCommands.ConfirmTranscription, new TranscribeConfirmRequest(false)).Ok);
        Assert.True(await rig.WaitPhase("picked"));
        Assert.Equal((0, 0), (rig.Asr.Calls, rig.Translators.Sum(t => t.Inner.Calls.Count)));
        Assert.True(await Eventually.WaitAsync(() => rig.Leases.ActiveCount == 0));
        Assert.True(rig.T(UiCommands.StartTranscription).Ok);
        Assert.True(await rig.WaitPhase("confirm"));
        Assert.True(rig.T(UiCommands.Close).Ok);
        Assert.True(await Eventually.WaitAsync(() => rig.Jobs.Active is null));
        Assert.True(await Eventually.WaitAsync(() => rig.Leases.ActiveCount == 0));
        Assert.Equal((0, 0), (rig.Asr.Calls, rig.Translators.Sum(t => t.Inner.Calls.Count)));
    }

    [Fact] // A03 in the quota dialog: a text-only ASR (Gemini) is refused as a replacement; the job stays parked and nothing is saved
    public async Task A03_quota_switch_to_text_only_asr_is_refused_and_keeps_the_job_parked()
    {
        using var rig = new TranscribeWindowTests.Rig();
        rig.Asr = new TranscribeWindowTests.FakeAsr((n, ct) => TranscribeWindowTests.AsrQuota(n, ct));
        await rig.StartToConfirmAsync();
        rig.T(UiCommands.ConfirmTranscription, new TranscribeConfirmRequest(true));
        Assert.True(await rig.WaitPhase("quota"));
        var before = rig.Config.State.Effective.Speech.VideoAsr;
        Assert.Equal("needs-timecodes", rig.T(UiCommands.ChangeTranslator, new TranscribeSwitchRequest("asr", "gemini-asr", "gemini-2.5-flash")).Error);
        Assert.Equal(before, rig.Config.State.Effective.Speech.VideoAsr);
        Assert.Equal("quota", rig.Phase);
        Assert.Empty(rig.CueEvents());
        Assert.Equal(1, rig.Asr.Calls);
    }

    // ================= 5. cancel at every stage =================

    [Theory] // VID03: cancel during decode, upload, translate and quota: terminal Cancelled, cues so far readable, 0 leases, 0 files
    [InlineData("decode")]
    [InlineData("upload")]
    [InlineData("translate")]
    public async Task Cancel_at_each_stage_leaves_no_lease_file_or_pending_call(string stage)
    {
        using var kit = new Kit();
        var started = new TaskCompletionSource();
        TranscribeWindowTests.FakeAsr asr = Asr(async (n, ct) =>
        {
            if (stage == "upload" && n == 1) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return Say(n);
        });
        var tr = new BlockTranslator(n => stage == "translate" && n == 1);
        IMediaDecoder decoder = stage == "decode"
            ? new CountingDecoder(() => new BlockedReader(Probe(60), before: 15)) // slice 0 done, slice 1 never completes
            : FakeMedia(60);
        var jobs = NewJobs(kit, decoder, asr, tr);
        var job = StartJob(jobs, kit);
        switch (stage)
        {
            case "decode": Assert.True(await Eventually.WaitAsync(() => asr.Calls == 1 && job.Cues().Count == 2)); await Task.Delay(100, Ct); break;
            case "upload": await started.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct); break;
            default: await tr.Started.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct); break;
        }
        job.Cancel();
        var end = await Finish(job);
        Assert.Equal(VideoJobPhase.Cancelled, end.Phase);
        kit.AssertClean();
        Assert.True(job.Cues().Count >= 2); // slice 0's cues stay readable
        Assert.Equal(job.Cues().Count, jobs.Results.Get(job.Id)!.Cues.Count);
        int calls = asr.Calls;
        await Task.Delay(150, Ct);
        Assert.Equal(calls, asr.Calls); // nothing new after the cancel
        Assert.Null(jobs.Active);
    }

    [Fact] // export cancel: the save dialog still open when the caller cancels writes nothing; a refused dialog writes nothing
    public async Task Cancel_during_export_dialog_writes_no_file_or_temp()
    {
        using var kit = new Kit();
        var tr = new VideoJobTests.FakeTranslator("tr", VideoJobTests.ItemsLimits);
        var jobs = NewJobs(kit, FakeMedia(10), Asr((n, ct) => Task.FromResult(Say(n))), tr);
        var job = StartJob(jobs, kit);
        await Finish(job);
        string dir = Path.Combine(kit.Root.Root, "save"); Directory.CreateDirectory(dir);
        var picker = new PendingPicker();
        var exporter = new SubtitleExporter(jobs.Results, picker);
        using var cts = new CancellationTokenSource();
        var export = exporter.ExportAsync(job.Id, SubtitleMode.Original, SubtitleFormat.Srt, cts.Token);
        await picker.Asked.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        cts.Cancel();
        try { var r = await export; Assert.False(r.Ok); } catch (OperationCanceledException) { }
        Assert.Empty(Directory.GetFileSystemEntries(dir));
        Assert.False(File.Exists(Path.Combine(dir, "never.srt")));
    }

    private sealed class PendingPicker : ISubtitleSavePicker
    {
        public readonly TaskCompletionSource Asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string?> PickAsync(string name, SubtitleFormat format, CancellationToken ct) { Asked.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null; }
    }

    [Fact] // VID03: close (CancelActive) mid-run, reopen: the partial list is exportable with no further ASR, translation or upload; gone after Remove
    public async Task Partial_result_after_close_exports_without_new_work()
    {
        using var kit = new Kit();
        var asr = Asr(async (n, ct) => { if (n == 2) await Task.Delay(Timeout.Infinite, ct); return Say(n); });
        var tr = new VideoJobTests.FakeTranslator("tr", VideoJobTests.ItemsLimits);
        var jobs = NewJobs(kit, FakeMedia(30), asr, tr);
        var job = StartJob(jobs, kit);
        Assert.True(await Eventually.WaitAsync(() => asr.Calls == 3 && job.State.Translated == 5));
        jobs.CancelActive();
        Assert.Equal(VideoJobPhase.Cancelled, (await Finish(job)).Phase);
        (int a, int t) = (asr.Calls, tr.Calls.Count);
        string dir = Path.Combine(kit.Root.Root, "o"); Directory.CreateDirectory(dir);
        var ex = new SubtitleExporter(jobs.Results);
        ExportAllAndCompare(ex, job.Id, dir, ExpectedFromScript(2), 1);
        Assert.Equal((a, t), (asr.Calls, tr.Calls.Count));
        Assert.True(jobs.Results.Remove(job.Id));
        Assert.Equal(SubtitleExportErrors.UnknownJob, ex.ExportTo(job.Id, SubtitleMode.Original, SubtitleFormat.Srt, Path.Combine(dir, "x.srt")).Error);
        kit.AssertClean();
    }

    // ================= 6. quota: ASR then translator, switch, resume only the missing items =================

    [Fact] // T07: ASR quota -> switch ASR -> translator quota -> switch translator; counts prove only the missing work is redone
    public async Task Quota_on_asr_then_on_translator_redoes_only_the_missing_items()
    {
        using var kit = new Kit();
        var a = Asr((n, ct) => n == 1 ? TranscribeWindowTests.AsrQuota(n, ct) : Task.FromResult(Say(n)));
        int bSlice = 0;
        var b = Asr((n, ct) => Task.FromResult(Say(1 + bSlice++)));
        var t1 = new VideoJobTests.FakeTranslator("t1", VideoJobTests.ItemsLimits, (call, n) => n == 1 ? new SubtitleBatchOutcome.Failure(new ProviderError(ErrorKind.Quota)) : VideoJobTests.Echo(call));
        var t2 = new VideoJobTests.FakeTranslator("t2", VideoJobTests.ItemsLimits);
        var jobs = NewJobs(kit, FakeMedia(30), a, t1);
        var job = StartJob(jobs, kit);

        var s1 = await WaitState(job, s => s.Phase == VideoJobPhase.QuotaExhausted);
        Assert.Equal(VideoQuotaSide.Asr, s1.Quota);
        Assert.Equal((2, 2), (a.Calls, job.Cues().Count)); // slice 0 done, slice 1 refused
        Assert.Single(t1.Calls);
        Assert.True(job.SwitchAsr(b)); Assert.True(job.Resume());

        var s2 = await WaitState(job, s => s.Phase == VideoJobPhase.QuotaExhausted && s.Quota == VideoQuotaSide.Translation);
        Assert.Equal(1, b.Calls); // slice 1 once on the new engine; no slice 2 yet
        Assert.Equal(2, t1.Calls.Count);
        Assert.Equal(2, s2.Translated); // slice 0's cues only
        Assert.True(job.SwitchTranslator(t2)); Assert.True(job.Resume());
        Assert.Equal(VideoJobPhase.Done, (await Finish(job)).Phase);

        Assert.Equal((2, 2, 7), (a.Calls, b.Calls, job.Cues().Count));
        Assert.Equal(7, job.Cues().Count(c => c.Translation is not null));
        var t1Ok = t1.Calls.Where((_, i) => i != 1).SelectMany(c => c.Parts.Select(p => p.Id)).ToList();
        var t2Ids = t2.Calls.SelectMany(c => c.Parts.Select(p => p.Id)).ToList();
        Assert.Empty(t1Ok.Intersect(t2Ids)); // nothing already translated is sent again
        Assert.Equal(t1.Calls[1].Parts.Select(p => p.Id), t2.Calls[0].Parts.Select(p => p.Id)); // exactly the refused batch first
        Assert.Equal(7, t1Ok.Count + t2Ids.Count);
        Assert.Equal(ExpectedFromScript(3).Select(e => e.Start), job.Cues().Select(c => c.Start));
        kit.AssertClean();
    }

    // ================= 7. back-pressure =================

    [Fact] // VID02: a consumer that holds slices never lets decode run more than two slices ahead (reader reads, files, leases)
    public async Task Slow_consumer_keeps_decode_two_slices_ahead_at_most()
    {
        using var kit = new Kit();
        int reads = 0;
        var inner = new VideoJobTests.FakeReader(Probe(120), VideoJobTests.Blocks(120, _ => true).GetEnumerator());
        var reader = new CountingReader(inner, () => Interlocked.Increment(ref reads));
        var slicer = new MediaSlicer(kit.Tokens, new CountingDecoder(() => reader), kit.Files, Ten);
        using var session = await slicer.OpenAsync(kit.Tokens.Issue(kit.Media("m.mp4"))!, Ct);
        var held = new List<MediaSlice>();
        var s0 = Assert.IsType<MediaStep.Slice>(await session.NextAsync(Ct)).Value;
        held.Add(s0);
        await Task.Delay(400, Ct);
        Assert.InRange(reads, 20, 31); // two slices of 10 blocks, never a third
        Assert.InRange(kit.Leases.ActiveCount, 1, 2);
        Assert.True(kit.FilesLeft <= 2);
        var s1 = Assert.IsType<MediaStep.Slice>(await session.NextAsync(Ct)).Value;
        held.Add(s1);
        await Task.Delay(400, Ct);
        Assert.InRange(reads, 20, 31);
        Assert.Equal(2, kit.Leases.ActiveCount);
        // release one at a time, slowly; the window never exceeds two
        int count = 2; double maxAlive = 2;
        s0.Dispose(); held.Remove(s0);
        while (true)
        {
            var step = await session.NextAsync(Ct);
            if (step is MediaStep.End end) { Assert.Equal(12, end.Slices); break; }
            var slice = Assert.IsType<MediaStep.Slice>(step).Value;
            Assert.Equal(TimeSpan.FromSeconds(10 * count), slice.Start);
            held.Add(slice); count++;
            maxAlive = Math.Max(maxAlive, kit.Leases.ActiveCount);
            await Task.Delay(30, Ct);
            held[0].Dispose(); held.RemoveAt(0);
        }
        foreach (var s in held) s.Dispose();
        Assert.Equal(12, count);
        Assert.True(maxAlive <= 2, $"alive {maxAlive}");
        Assert.Equal(121, reads); // 120 blocks plus the read that returns end of stream
    }

    private sealed class CountingReader(IMediaAudioReader inner, Action onRead) : IMediaAudioReader
    {
        public MediaProbe Probe => inner.Probe;
        public Task<MediaPcmBlock?> ReadAsync(CancellationToken ct) { onRead(); return inner.ReadAsync(ct); }
        public void Dispose() => inner.Dispose();
    }

    [Fact] // the same through the job: a slow ASR (the consumer) sees at most two slice leases and two slice files at every upload
    public async Task Slow_asr_in_the_job_sees_at_most_two_slice_files_at_every_upload()
    {
        using var kit = new Kit();
        int max = 0;
        var asr = Asr(async (n, ct) =>
        {
            await Task.Delay(120, ct);
            max = Math.Max(max, Math.Max(kit.Leases.ActiveCount, kit.FilesLeft));
            return Say(n);
        });
        var jobs = NewJobs(kit, FakeMedia(100), asr, new VideoJobTests.FakeTranslator("tr", VideoJobTests.ItemsLimits));
        var job = StartJob(jobs, kit);
        Assert.Equal(VideoJobPhase.Done, (await Finish(job)).Phase);
        Assert.Equal(10, asr.Calls);
        Assert.InRange(max, 1, 3); // two slices plus the chunk copy the ASR pipeline makes of the slice being uploaded
        kit.AssertClean();
    }

    // ================= 8. disk full =================

    private sealed class FullStream(FileStream inner) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, Math.Min(count, 10)); inner.Flush(); throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)); }
        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);
        public override void Flush() => inner.Flush();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact] // VID02: disk full while the job decodes: parks as Paused (media.diskFull), no torn file; resume finishes with every slice once
    public async Task Disk_full_during_decode_pauses_the_job_and_resume_completes_without_torn_files()
    {
        using var kit = new Kit();
        int opens = 0;
        var slicing = new MediaSliceOptions(TimeSpan.FromSeconds(10), OpenWrite: p =>
        {
            var fs = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return Interlocked.Increment(ref opens) <= 1 ? new FullStream(fs) : fs;
        });
        var asr = Asr((n, ct) => Task.FromResult(Say(n)));
        var jobs = NewJobs(kit, FakeMedia(30), asr, new VideoJobTests.FakeTranslator("tr", VideoJobTests.ItemsLimits));
        var job = StartJob(jobs, kit, slicing: slicing);
        var paused = await WaitState(job, s => s.Phase == VideoJobPhase.Paused);
        Assert.Equal(MediaErrors.DiskFull, paused.ErrorCode);
        Assert.Equal(0, asr.Calls);
        Assert.True(kit.FilesLeft == 0, "torn file left");
        Assert.True(job.Resume());
        Assert.Equal(VideoJobPhase.Done, (await Finish(job)).Phase);
        Assert.Equal(3, asr.Calls);
        Assert.Equal(7, job.Cues().Count);
        Assert.Equal(ExpectedFromScript(3).Select(e => e.Start), job.Cues().Select(c => c.Start));
        kit.AssertClean();
    }

    private sealed class TornTemp(string path) : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    {
        public override void Write(ReadOnlySpan<byte> buffer) { base.Write(buffer[..(buffer.Length / 2)]); Flush(); throw new IOException("disk", unchecked((int)0x80070070)); }
    }

    [Fact] // VID04: disk full during export of a real job result: Retryable, old target untouched, no temp; retry writes the identical full file
    public async Task Disk_full_during_export_recovers_with_no_partial_file()
    {
        using var kit = new Kit();
        var jobs = NewJobs(kit, FakeMedia(30), Asr((n, ct) => Task.FromResult(Say(n))), new VideoJobTests.FakeTranslator("tr", VideoJobTests.ItemsLimits));
        var job = StartJob(jobs, kit);
        await Finish(job);
        string dir = Path.Combine(kit.Root.Root, "o"); Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "x.srt");
        File.WriteAllText(target, "previous");
        var r = new SubtitleExporter(jobs.Results.Get, null, p => new TornTemp(p)).ExportTo(job.Id, SubtitleMode.BilingualOriginalFirst, SubtitleFormat.Srt, target);
        Assert.Equal((false, SubtitleExportErrors.DiskFull, true), (r.Ok, r.Error, r.Retryable));
        Assert.Equal("previous", File.ReadAllText(target));
        Assert.Equal([target], Directory.GetFileSystemEntries(dir));
        Assert.True(new SubtitleExporter(jobs.Results).ExportTo(job.Id, SubtitleMode.BilingualOriginalFirst, SubtitleFormat.Srt, target).Ok);
        AssertExport(target, SubtitleFormat.Srt, SubtitleMode.BilingualOriginalFirst, ExpectedFromScript(3), 1);
        Assert.Equal([target], Directory.GetFileSystemEntries(dir));
    }

    // ================= 9. busy =================

    [Fact] // one job at a time: a second start is video.busy and disturbs nothing; after the first ends a new job runs with its own ids
    public async Task Busy_refuses_a_second_job_and_a_new_job_starts_after_the_first_ends()
    {
        using var kit = new Kit();
        var gate = new TaskCompletionSource();
        var asr = Asr(async (n, ct) => { await gate.Task.WaitAsync(ct); return Say(0); });
        var jobs = NewJobs(kit, FakeMedia(10), asr, new VideoJobTests.FakeTranslator("tr", VideoJobTests.ItemsLimits));
        var first = StartJob(jobs, kit);
        Assert.True(await Eventually.WaitAsync(() => asr.Calls == 1));
        var second = jobs.Start(kit.Tokens.Issue(kit.Media("two.mp4"))!, "en", "zh", slicing: Ten);
        Assert.Equal((null, VideoErrors.Busy), (second.Job, second.Refusal));
        Assert.Single(jobs.Results.List());
        Assert.Equal(1, asr.Calls);
        gate.SetResult();
        Assert.Equal(VideoJobPhase.Done, (await Finish(first)).Phase);
        var third = StartJob(jobs, kit, "three.mp4");
        Assert.NotEqual(first.Id, third.Id);
        Assert.Equal(VideoJobPhase.Done, (await Finish(third)).Phase);
        Assert.Equal(2, jobs.Results.List().Count);
        Assert.Equal(2, jobs.Results.Get(first.Id)!.Cues.Count);
        kit.AssertClean();
    }

    // ================= 10. the page never sees a path or a token =================

    [Fact] // every message posted to every window through pick, drop, confirm, quota, switch, resume, failure, export: no path, folder, lease name or token
    public async Task Page_never_receives_a_path_or_token_in_any_view_or_event()
    {
        using var rig = new TranscribeWindowTests.Rig();
        var tokens = new List<string>();
        rig.Shell.MediaPicker = new RecordingPicker(rig.Picker, tokens);
        using var saveRoot = new TempRoot();
        rig.Translate = (n, call) => n == 0 ? new SubtitleBatchOutcome.Failure(new ProviderError(ErrorKind.Quota)) : VideoJobTests.Echo(call);
        await rig.StartToConfirmAsync();
        rig.T(UiCommands.ConfirmTranscription, new TranscribeConfirmRequest(true));
        Assert.True(await rig.WaitPhase("quota"));
        var other = rig.Shell.TranscribeWindowView!.Choices.First(c => !c.Current);
        rig.T(UiCommands.ChangeTranslator, new TranscribeSwitchRequest("translation", other.Id));
        rig.T(UiCommands.ResumeTranscription);
        Assert.True(await rig.WaitPhase("done"));
        rig.Save.Path = Path.Combine(saveRoot.Root, "out.srt");
        Assert.True(rig.T(UiCommands.Export, new TranscribeExportRequest("srt", "bilingual")).Ok);
        rig.T(UiCommands.Close);
        rig.Shell.OnHotkey("videoTranscribe"); rig.Ready(WindowKind.Transcribe);
        Assert.True((await rig.Shell.OnMediaDroppedAsync(rig.MediaFile)).Ok); // drop path
        rig.Asr = new TranscribeWindowTests.FakeAsr((n, ct) => Task.FromResult<AsrOutcome>(new AsrOutcome.Failure(new ProviderError(ErrorKind.Auth, "nope"))));
        rig.Ready(WindowKind.Transcribe);

        Assert.NotEmpty(tokens);
        var secrets = new List<string> { rig.Root.Root, rig.MediaFile, "susu-transcribe-leases" };
        secrets.AddRange(tokens);
        var field = typeof(MediaTokens).GetField("paths", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field?.GetValue(rig.Tokens) is System.Collections.IDictionary dict) foreach (var k in dict.Keys) secrets.Add((string)k);
        int scanned = 0;
        foreach (var p in rig.Platform.Posted)
        {
            string wire = p.Envelope.Payload?.GetRawText() ?? "";
            scanned++;
            foreach (string secret in secrets.Where(x => x.Length > 3))
            {
                string label = secret.Length > 12 ? secret[..12] + "..." : secret;
                Assert.False(wire.Contains(secret, StringComparison.OrdinalIgnoreCase), $"{label} leaked in {p.Kind}/{p.Envelope.Name}");
                Assert.False(wire.Contains(secret.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase), $"escaped {label} leaked in {p.Kind}/{p.Envelope.Name}");
            }
        }
        Assert.True(scanned > 20);
        // the export result tells the user-chosen path (documented) but nothing about the media or cache location
        Assert.Contains(rig.Platform.Posted, p => p.Envelope.Payload?.GetRawText().Contains("out.srt") == true);
    }

    private sealed class RecordingPicker(IMediaPicker inner, List<string> seen) : IMediaPicker
    {
        public async Task<string?> PickAsync(CancellationToken ct) { var t = await inner.PickAsync(ct); if (t is not null) seen.Add(t); return t; }
    }

    // ================= 11. stale video translator =================

    [Fact] // a saved video translator that is no longer offered: the page shows the stale id, but switching to it or re-saving it is refused
    public void Stale_video_translator_is_shown_but_cannot_be_chosen_again()
    {
        using var rig = new TranscribeWindowTests.Rig();
        var s = rig.Config.State;
        var stale = s.Effective with { Speech = s.Effective.Speech with { VideoTranslator = "gone-service" } };
        Assert.Equal(SaveStatus.Saved, rig.Config.Save(stale, s.Revision, s.FileHash).Status);
        rig.Shell.Open(WindowKind.Settings); rig.Ready(WindowKind.Settings);
        var speech = rig.Snapshot(WindowKind.Settings).Settings!.Speech!;
        Assert.Equal("gone-service", speech.VideoTranslator);
        Assert.DoesNotContain("gone-service", speech.VideoTranslatorChoices!);
        Assert.Equal("range", rig.Run(WindowKind.Settings, UiCommands.SelectSpeech, new SpeechSelectRequest(rig.Config.State.Revision, rig.Config.State.FileHash, "videoTranslator", "gone-service", "")).Error);
        // clearing it (empty = first enabled) is accepted
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SelectSpeech, new SpeechSelectRequest(rig.Config.State.Revision, rig.Config.State.FileHash, "videoTranslator", "", "")).Ok);
        Assert.Equal("", rig.Config.State.Effective.Speech.VideoTranslator);
    }
}

