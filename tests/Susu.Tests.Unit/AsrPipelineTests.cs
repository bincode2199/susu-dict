using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Plugins;
using Susu.Storage;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F12.2 shared ASR pipeline with a fake provider (no sandbox): encoding negotiation (A04), silence detection and hard cut (A05),
/// WAV chunking against file, whole-request and duration limits (A04), time offsets and text merge (A05, A08), result
/// validation (A06), the job (cancel/cleanup, no request for silence, B07). Vendors: none. Real-vendor runs (A01) are not executed.
/// </summary>
public class AsrPipelineTests
{
    private const int Rate = 16000;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>16 kHz mono WAV from parts: <c>Tone(s)</c> = loud 440 Hz, <c>Quiet(s)</c> = digital silence.</summary>
    internal static byte[] Wav(params (bool Loud, double Seconds)[] parts)
    {
        var data = new List<byte>();
        long n = 0;
        foreach (var (loud, seconds) in parts)
            for (long i = 0, count = (long)(seconds * Rate); i < count; i++, n++)
            {
                short s = loud ? (short)(8000 * Math.Sin(2 * Math.PI * 440 * n / Rate)) : (short)0;
                data.Add((byte)(s & 0xFF)); data.Add((byte)(s >> 8));
            }
        return [.. Wav16.Header(data.Count, Rate), .. data];
    }

    internal static (bool, double) Tone(double s) => (true, s);
    internal static (bool, double) Quiet(double s) => (false, s);

    private sealed class Rig : IDisposable
    {
        public FileLeases Leases { get; } = new(TestTemp.NewDir("susu-asr-leases"));
        public LeasedFiles Files => new(Leases);

        public ILeasedFile Audio(byte[] wav)
        {
            var file = Files.Create("asr-input", "audio/wav", "wav");
            File.WriteAllBytes(file.FilePath, wav);
            return file;
        }

        public RecordedAudio Recorded(byte[] wav, bool silent = false) => new(Audio(wav), TimeSpan.FromSeconds((wav.Length - 44) / 2.0 / Rate), Rate, silent);
        public void Dispose() => Leases.Dispose();
    }

    private sealed record Seen(string Lease, string Mime, long Bytes, double Seconds, string Output, string? Lang, long Cap, byte[] Wav, long Offset);

    /// <summary>A provider that reads each chunk file at call time (so a chunk's real bytes are checked) and answers through <paramref name="answer"/>.</summary>
    private sealed class FakeAsr(AsrLimits limits, bool timecodes, Func<AsrCall, int, byte[], CancellationToken, Task<AsrOutcome>>? answer = null) : IAsrProvider
    {
        public List<Seen> Calls { get; } = [];
        public Rig? Owner;
        public string InstanceId => "fake-asr";
        public string Model => "fake";
        public bool Timecodes => timecodes;
        public AsrLimits Limits => limits;

        public async Task<AsrOutcome> TranscribeAsync(AsrCall call, CancellationToken cancellationToken)
        {
            byte[] bytes;
            lock (Calls)
            {
                // The leased chunk is the only place its bytes live; read it through the lease store exactly as the broker would.
                var lease = Owner!.Leases.AddReference(call.Audio) ?? throw new InvalidOperationException("unknown lease");
                try { bytes = File.ReadAllBytes(Owner.Leases.PathOf(lease)); } finally { Owner.Leases.Release(lease); }
                Calls.Add(new Seen(call.Audio, call.Mime, call.Bytes, call.DurationSeconds, call.Output, call.Lang, call.MaxRequestBytes, bytes, 0));
            }
            int index = Calls.Count - 1;
            if (answer is not null) return await answer(call, index, bytes, cancellationToken);
            return call.Output == "segments"
                ? new AsrOutcome.Transcribed("segments", $"part {index}", [new AsrSegment(0.1, call.DurationSeconds - 0.1, $"part {index}")])
                : new AsrOutcome.Transcribed("text", $"part {index}", null);
        }
    }

    private static AsrLimits Wav(long maxBytes, long maxRequestBytes, int maxSeconds, AsrUpload upload = AsrUpload.Multipart, long overhead = 2048)
        => new([AsrFormat.Wav16kMono], maxBytes, maxRequestBytes, maxSeconds, upload, overhead);

    private static async Task<AsrRunOutcome> RunAsync(Rig rig, FakeAsr provider, byte[] wav, string output = "text", string? lang = null, CancellationToken? token = null)
    {
        provider.Owner = rig;
        using var input = rig.Audio(wav);
        return await new AsrTranscriber(rig.Files, TimeSpan.FromSeconds(30)).RunAsync(provider, input, output, lang, token ?? Ct);
    }

    private static AsrTranscript Done(AsrRunOutcome outcome) => Assert.IsType<AsrRunOutcome.Done>(outcome).Transcript;
    private static ProviderError Failed(AsrRunOutcome outcome) => Assert.IsType<AsrRunOutcome.Failure>(outcome).Error;

    // ---------------- A04: encoding negotiation and size limits ----------------

    [Fact]
    public void The_shipped_models_negotiate_wav_16k_mono_and_hold_300_seconds_per_chunk()
    {
        foreach (var package in SpeechCatalog.All.Where(p => p.Capability == Capability.Asr))
            foreach (var model in package.Models)
            {
                Assert.Equal(AsrFormat.Wav16kMono, AsrEncodings.Negotiate(model.Limits));
                Assert.Null(SpeechCatalog.Check(SpeechSlot.Asr, new SpeechSelection(package.InstanceId, model.Id)));
                // 300 s of 16 kHz 16-bit mono = 9.6 MB: inside every declared file and request limit.
                Assert.Equal(300L * Rate, model.Limits!.MaxChunkSamples(Rate));
            }
    }

    [Fact] // A04: no local format in common => the model cannot be chosen; nothing silently becomes AAC
    public void A_model_without_a_local_format_is_not_selectable_and_never_encodes_aac()
    {
        var aac = new AsrFormat("m4a", "aac", "audio/mp4", 16000, 1);
        var limits = new AsrLimits([aac], 24_000_000, 25_000_000, 300, AsrUpload.Multipart, 2048);
        Assert.Null(AsrEncodings.Negotiate(limits));
        Assert.False(SpeechCatalog.Encodable(new SpeechModel("aac-only", false, limits)));
        // A model that also declares WAV keeps WAV; AAC is only an intersection member when the host could encode it (it cannot).
        Assert.Equal(AsrFormat.Wav16kMono, AsrEncodings.Negotiate(new AsrLimits([aac, AsrFormat.Wav16kMono], 24_000_000, 25_000_000, 300, AsrUpload.Multipart, 2048)));
        // WAV 44.1 kHz stereo is not a local format either.
        Assert.Null(AsrEncodings.Negotiate(new AsrLimits([new AsrFormat("wav", "pcm_s16le", "audio/wav", 44100, 2)], 24_000_000, 25_000_000, 300, AsrUpload.Multipart, 2048)));
        // Limits so small that not even a second fits make the model unusable as well.
        Assert.Null(AsrEncodings.Negotiate(Wav(maxBytes: 30_000, 25_000_000, 300)));
    }

    [Fact]
    public async Task A_model_with_no_local_format_sends_nothing()
    {
        using var rig = new Rig();
        var aac = new AsrLimits([new AsrFormat("m4a", "aac", "audio/mp4", 16000, 1)], 24_000_000, 25_000_000, 300, AsrUpload.Multipart, 2048);
        var provider = new FakeAsr(aac, false);
        Assert.Equal("asr.format", Failed(await RunAsync(rig, provider, Wav(Tone(3)))).Detail);
        Assert.Empty(provider.Calls);
    }

    [Fact] // A04: a smaller maxBytes cuts shorter WAV chunks; every real file fits and the format stays WAV
    public async Task A_shrunk_file_limit_cuts_shorter_wav_chunks()
    {
        using var rig = new Rig();
        var limits = Wav(maxBytes: 200_000, maxRequestBytes: 25_000_000, 300); // (200000 - 44) / 2 samples = 6.25 s
        Assert.Equal(99_978, limits.MaxChunkSamples(Rate));
        var provider = new FakeAsr(limits, false);
        var transcript = Done(await RunAsync(rig, provider, Wav(Tone(20))));
        Assert.Equal(4, transcript.Chunks);
        Assert.Equal(4, provider.Calls.Count);
        Assert.All(provider.Calls, c =>
        {
            Assert.Equal("audio/wav", c.Mime);
            Assert.True(c.Bytes <= 200_000, $"{c.Bytes}");
            Assert.Equal(c.Bytes, c.Wav.Length);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(c.Wav, 0, 4));
        });
        Assert.Equal(20.0, provider.Calls.Sum(c => c.Seconds), 2); // not a sample is dropped from continuous speech
    }

    [Fact] // A04: the whole request (Base64 expansion + JSON wrapper) is what must fit maxRequestBytes, not the file
    public async Task The_request_limit_counts_base64_and_overhead()
    {
        using var rig = new Rig();
        var limits = Wav(maxBytes: 24_000_000, maxRequestBytes: 100_000, 300, AsrUpload.Base64Json, overhead: 4096);
        long samples = limits.MaxChunkSamples(Rate);
        long file = 44 + 2 * samples;
        Assert.True(limits.EstimateRequestBytes(file) <= 100_000);
        Assert.True(limits.EstimateRequestBytes(file + 2) > 100_000, "the chunk is as long as the request limit allows");
        var provider = new FakeAsr(limits, false);
        Done(await RunAsync(rig, provider, Wav(Tone(8))));
        Assert.True(provider.Calls.Count >= 2);
        Assert.All(provider.Calls, c =>
        {
            Assert.True(limits.EstimateRequestBytes(c.Bytes) <= 100_000, $"estimated {limits.EstimateRequestBytes(c.Bytes)}");
            Assert.Equal(100_000, c.Cap); // the broker is told the same cap and enforces it on the real body
        });
    }

    [Fact] // PLAN 4.5.1: the vendor limit and the host limit apply, the lower wins
    public void The_host_transport_caps_win_over_a_larger_declared_limit()
    {
        var huge = Wav(maxBytes: 500_000_000, maxRequestBytes: 900_000_000, 36_000);
        Assert.Equal(32L * 1024 * 1024, huge.EffectiveFileCap);
        long file = 44 + 2 * huge.MaxChunkSamples(Rate);
        Assert.True(file <= 32L * 1024 * 1024);
        var b64 = Wav(maxBytes: 500_000_000, maxRequestBytes: 900_000_000, 36_000, AsrUpload.Base64Json, 4096);
        Assert.True(b64.EstimateRequestBytes(44 + 2 * b64.MaxChunkSamples(Rate)) <= 48L * 1024 * 1024);
    }

    // ---------------- A05: silence, hard cut, timeline ----------------

    [Fact] // A05: more than 5 minutes with no pause is cut hard at the limit, not held back or merged
    public async Task Continuous_sound_over_five_minutes_is_cut_hard_at_the_limit()
    {
        using var rig = new Rig();
        var provider = new FakeAsr(SpeechCatalog.OpenAiLimits, true);
        var transcript = Done(await RunAsync(rig, provider, Wav(Tone(330)), "segments"));
        Assert.Equal(2, provider.Calls.Count);
        Assert.Equal([300.0, 30.0], provider.Calls.Select(c => c.Seconds).ToArray());
        // Absolute times: the second chunk starts exactly at 300 s, so its 0.1 s segment start lands at 300.1 s.
        Assert.Equal([0.1, 300.1], transcript.Segments!.Select(s => Math.Round(s.Start, 3)).ToArray());
        Assert.All(provider.Calls, c => Assert.True(c.Bytes <= 24_000_000));
    }

    [Fact] // A05: the cut falls in a pause when there is one near the limit
    public async Task A_pause_near_the_limit_is_where_the_audio_is_cut()
    {
        using var rig = new Rig();
        var provider = new FakeAsr(Wav(24_000_000, 25_000_000, 10), true);
        // 6 s speech, 1 s pause, 6 s speech: the 10 s window holds a pause in its second half.
        var transcript = Done(await RunAsync(rig, provider, Wav(Tone(8.5), Quiet(1), Tone(5)), "segments"));
        Assert.Equal(2, provider.Calls.Count);
        Assert.True(provider.Calls[0].Seconds is > 8.5 and < 10, $"{provider.Calls[0].Seconds}");
        // Cut inside the pause: neither chunk loses speech.
        Assert.InRange(provider.Calls.Sum(c => c.Seconds), 13.5, 14.6); // all 13.5 s of speech plus the pads, the pause mostly gone
        Assert.True(transcript.Segments![1].Start is >= 9.39 and <= 9.6, $"{transcript.Segments[1].Start}"); // speech resumes at 9.5 s, minus the 0.2 s pad, plus the 0.1 s in-chunk start
    }

    [Fact] // A05: a long silence keeps its place on the timeline; silent windows are not sent; later text never moves earlier
    public async Task Silence_keeps_its_place_and_speech_after_it_lands_at_its_real_time()
    {
        using var rig = new Rig();
        var provider = new FakeAsr(Wav(24_000_000, 25_000_000, 20), true);
        var transcript = Done(await RunAsync(rig, provider, Wav(Tone(5), Quiet(40), Tone(5)), "segments"));
        Assert.Equal(2, provider.Calls.Count); // the all-silent window [20 s, 40 s) was never uploaded
        Assert.True(provider.Calls.Sum(c => c.Seconds) < 12, $"{provider.Calls.Sum(c => c.Seconds)} s of 50 s were sent");
        var starts = transcript.Segments!.Select(s => s.Start).ToArray();
        Assert.True(starts[0] < 1);
        Assert.True(starts[1] is >= 44.5 and <= 45.5, $"second subtitle at {starts[1]} s, speech resumes at 45 s");
        Assert.Equal(starts.Order().ToArray(), starts);
    }

    [Fact] // A05: leading silence is trimmed but counted: a recording that starts with 7 s of quiet puts the text at 7 s
    public async Task Leading_silence_is_trimmed_from_the_upload_but_not_from_the_timeline()
    {
        using var rig = new Rig();
        var provider = new FakeAsr(SpeechCatalog.OpenAiLimits, true);
        var transcript = Done(await RunAsync(rig, provider, Wav(Quiet(7), Tone(3), Quiet(4)), "segments"));
        Assert.Single(provider.Calls);
        Assert.True(provider.Calls[0].Seconds < 4); // 3 s of sound plus the pads
        Assert.True(transcript.Segments![0].Start is >= 6.8 and <= 7.4, $"{transcript.Segments[0].Start}");
    }

    [Fact] // A05: nothing audible => no request at all, reported as no speech
    public async Task Silent_audio_makes_no_request()
    {
        using var rig = new Rig();
        var provider = new FakeAsr(SpeechCatalog.OpenAiLimits, true);
        Assert.IsType<AsrRunOutcome.NoSpeech>(await RunAsync(rig, provider, Wav(Quiet(12))));
        Assert.Empty(provider.Calls);
    }

    [Fact] // A08 (recording part): a 10-minute recording with pauses, sized by the real OpenAI limits, merged in order on one timeline
    public async Task A_ten_minute_recording_is_chunked_offset_and_merged_completely()
    {
        using var rig = new Rig();
        var parts = new List<(bool, double)>();
        for (int i = 0; i < 120; i++) { parts.Add(Tone(4)); parts.Add(Quiet(1)); } // 600 s: 4 s of speech, 1 s pause
        byte[] wav = Wav([.. parts]);
        var provider = new FakeAsr(SpeechCatalog.OpenAiLimits, true, (call, index, _, _) => Task.FromResult<AsrOutcome>(new AsrOutcome.Transcribed("segments", $"a{index} b{index}",
            [new AsrSegment(0.2, 1.0, $"a{index}"), new AsrSegment(0.9, 3.0, $"b{index}")]))); // legitimate speaker overlap inside a chunk
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var transcript = Done(await RunAsync(rig, provider, wav, "segments"));
        TestContext.Current.TestOutputHelper?.WriteLine($"A08 recording: 600 s, {provider.Calls.Count} requests, {clock.ElapsedMilliseconds} ms of pipeline time, {transcript.Sent.TotalSeconds:F1} s sent");
        Assert.InRange(provider.Calls.Count, 2, 3);
        Assert.All(provider.Calls, c => Assert.True(c.Seconds <= 300 && c.Bytes <= 24_000_000));
        var segments = transcript.Segments!;
        Assert.Equal(provider.Calls.Count * 2, segments.Count);
        Assert.Equal(segments.Select(s => s.Start).Order().ToArray(), segments.Select(s => s.Start).ToArray());
        Assert.Contains(segments, s => s.Start > 300); // the later chunk carries its absolute offset
        Assert.Equal(600, transcript.Audio.TotalSeconds, 0);
        Assert.Equal(string.Join(' ', Enumerable.Range(0, provider.Calls.Count).SelectMany(i => new[] { $"a{i}", $"b{i}" })), transcript.Text);
        Assert.True(transcript.Sent.TotalSeconds >= 480, "the speech (480 s) is all sent");
    }

    [Fact]
    public void Offsets_never_move_a_later_subtitle_earlier()
    {
        var merged = new List<AsrSegment> { new(10, 12, "a") };
        Assert.Equal(ErrorKind.BadResponse, AsrTimeline.Append(merged, [new AsrSegment(0.5, 1, "b")], 5)!.Kind); // 5.5 s after a 10 s start
        Assert.Equal("a", Assert.Single(merged).Text);
        Assert.Null(AsrTimeline.Append(merged, [new AsrSegment(1, 2, "c"), new AsrSegment(1.5, 3, "d")], 12)); // overlapping speakers kept
        Assert.Equal([10, 13, 13.5], merged.Select(s => s.Start).ToArray());
        Assert.Equal(ErrorKind.BadResponse, AsrTimeline.Append([], [], -1)!.Kind);
        Assert.Equal(ErrorKind.BadResponse, AsrTimeline.Append([], [], double.NaN)!.Kind);
    }

    [Fact]
    public void Text_merges_in_chunk_order_without_inventing_breaks()
    {
        Assert.Equal("hello world again", AsrText.Join(["hello ", null, " world", "", "again"]));
        Assert.Equal("你好世界 and more", AsrText.Join(["你好", "世界 and", "more"]));
        Assert.Equal("", AsrText.Join([" ", null]));
    }

    // ---------------- A06: result validation ----------------

    private static AsrOutcome Parse(string resultJson, double duration = 10, string output = "segments")
        => PluginAsrProvider.Parse(JsonDocument.Parse(resultJson).RootElement, output, duration);

    private static string Segs(string items) => $$"""{"kind":"segments","segments":[{{items}}]}""";

    [Theory] // A06: every bad shape is bad_response, nothing is repaired
    [InlineData("""{"start":null,"end":2,"text":"nan"}""")] // JSON has no NaN; a plugin's NaN arrives as null
    [InlineData("""{"start":-1,"end":2,"text":"negative"}""")]
    [InlineData("""{"start":3,"end":3,"text":"zero length"}""")]
    [InlineData("""{"start":4,"end":2,"text":"end before start"}""")]
    [InlineData("""{"start":1,"end":11,"text":"past the chunk"}""")]
    [InlineData("""{"start":"1","end":2,"text":"string time"}""")]
    [InlineData("""{"end":2,"text":"no start"}""")]
    [InlineData("""{"start":1,"text":"no end"}""")]
    [InlineData("""{"start":1,"end":2}""")]
    [InlineData("""{"start":1,"end":2,"text":"  "}""")]
    [InlineData("""{"start":1,"end":2,"text":null}""")]
    [InlineData("""{"start":3,"end":4,"text":"ok"},{"start":1,"end":2,"text":"unordered"}""")]
    [InlineData("""7""")]
    public void Invalid_segments_are_bad_response(string items)
    {
        var error = Assert.IsType<AsrOutcome.Failure>(Parse(Segs(items))).Error;
        Assert.Equal(ErrorKind.BadResponse, error.Kind);
    }

    [Fact]
    public void Invalid_results_overall_are_bad_response()
    {
        Assert.Equal(ErrorKind.BadResponse, Assert.IsType<AsrOutcome.Failure>(Parse("""{"kind":"text","text":"x"}""", output: "segments")).Error.Kind); // wrong kind
        Assert.Equal(ErrorKind.BadResponse, Assert.IsType<AsrOutcome.Failure>(Parse("""{"kind":"segments"}""")).Error.Kind);
        Assert.Equal(ErrorKind.BadResponse, Assert.IsType<AsrOutcome.Failure>(Parse("""{"kind":"segments","segments":{"a":1}}""")).Error.Kind);
        Assert.Equal(ErrorKind.BadResponse, Assert.IsType<AsrOutcome.Failure>(Parse("""{"kind":"text"}""", output: "text")).Error.Kind);
        Assert.Equal(ErrorKind.BadResponse, Assert.IsType<AsrOutcome.Failure>(Parse("""{"text":"x"}""", output: "text")).Error.Kind);
        Assert.Equal(ErrorKind.BadResponse, Assert.IsType<AsrOutcome.Failure>(Parse("[1]")).Error.Kind);
        // The in-process contract check also refuses a real NaN and infinity.
        Assert.Equal(ErrorKind.BadResponse, PluginResultValidation.ValidateSegments(new AsrResult("segments", null, [new AsrSegment(double.NaN, 2, "x")]), "segments", 10)!.Kind);
        Assert.Equal(ErrorKind.BadResponse, PluginResultValidation.ValidateSegments(new AsrResult("segments", null, [new AsrSegment(0, double.PositiveInfinity, "x")]), "segments", 10)!.Kind);
    }

    [Fact] // A06: legitimate speaker overlap is kept as given; times are never evened out
    public void Legitimate_overlap_and_exact_times_are_kept()
    {
        var result = Assert.IsType<AsrOutcome.Transcribed>(Parse(Segs("""{"start":0.5,"end":4.25,"text":"hello"},{"start":2,"end":6.125,"text":"over"},{"start":2,"end":3,"text":"same start"}""")));
        Assert.Equal([(0.5, 4.25), (2.0, 6.125), (2.0, 3.0)], result.Segments!.Select(s => (s.Start, s.End)).ToArray());
        Assert.Equal("hello over same start", result.Text);
        // The empty array is valid (a chunk with no speech); an end that rounds the chunk length up by under 10 ms is not out of range.
        Assert.Empty(Assert.IsType<AsrOutcome.Transcribed>(Parse(Segs(""))).Segments!);
        Assert.IsType<AsrOutcome.Transcribed>(Parse(Segs("""{"start":0,"end":10.01,"text":"x"}"""), 10.003));
        Assert.IsType<AsrOutcome.Failure>(Parse(Segs("""{"start":0,"end":10.02,"text":"x"}"""), 10.003));
    }

    [Fact] // A06: a bad result from the service ends the job as Failed and nothing of it reaches the text, segments or translation
    public async Task A_bad_result_never_reaches_the_transcript_or_translation()
    {
        using var rig = new Rig();
        var provider = new FakeAsr(SpeechCatalog.OpenAiLimits, true, (call, _, _, _) => Task.FromResult(
            PluginAsrProvider.Parse(JsonDocument.Parse(Segs("""{"start":1,"end":2,"text":"fine"},{"start":5,"end":4,"text":"bad"}""")).RootElement, "segments", call.DurationSeconds)));
        provider.Owner = rig;
        string? translated = null;
        var job = new AsrJob(() => provider, rig.Files, t => { translated = t; return Task.CompletedTask; }, () => true);
        var state = await job.TranscribeAsync(rig.Recorded(Wav(Tone(5))), output: "segments");
        Assert.Equal(AsrPhase.Failed, state.Phase);
        Assert.Equal(ErrorKind.BadResponse, state.Error!.Kind);
        Assert.Null(state.Text);
        Assert.Null(state.Segments);
        Assert.Null(translated);
        Assert.Equal(0, rig.Leases.ActiveCount);
    }

    [Fact] // A03 (provider side): a text-only model is never asked for segments and never gets invented times
    public async Task A_text_only_model_cannot_serve_segments()
    {
        using var rig = new Rig();
        var provider = new FakeAsr(SpeechCatalog.GeminiLimits, timecodes: false);
        Assert.Equal("asr.noTimecodes", Failed(await RunAsync(rig, provider, Wav(Tone(3)), "segments")).Detail);
        Assert.Empty(provider.Calls);
        var text = Done(await RunAsync(rig, provider, Wav(Tone(3))));
        Assert.Null(text.Segments);
        Assert.Equal("part 0", text.Text);
        Assert.Equal(SpeechCatalog.NeedsTimecodes, SpeechCatalog.Check(SpeechSlot.VideoAsr, new SpeechSelection("gemini-asr", "gemini-2.5-flash")));
        Assert.Null(SpeechCatalog.Check(SpeechSlot.Asr, new SpeechSelection("gemini-asr", "gemini-2.5-flash")));
    }

    [Fact]
    public async Task A_failing_chunk_fails_the_run_without_partial_text()
    {
        using var rig = new Rig();
        var provider = new FakeAsr(Wav(24_000_000, 25_000_000, 6), false, (_, index, _, _) => Task.FromResult<AsrOutcome>(index == 1
            ? new AsrOutcome.Failure(new ProviderError(ErrorKind.Quota, "out of credit")) : new AsrOutcome.Transcribed("text", "one", null)));
        var error = Failed(await RunAsync(rig, provider, Wav(Tone(14))));
        Assert.Equal(ErrorKind.Quota, error.Kind);
        Assert.Contains("chunk 2/", error.Detail);
        Assert.Equal(0, rig.Leases.ActiveCount); // every chunk lease and the input were released
    }

    // ---------------- the job: B07 cancel, ownership, translation ----------------

    [Fact] // A05/B07: cancel stops the running call, releases every chunk and the recording, publishes Cancelled, translates nothing
    public async Task Cancel_leaves_no_active_call_and_no_files()
    {
        using var rig = new Rig();
        var started = new TaskCompletionSource();
        var provider = new FakeAsr(Wav(24_000_000, 25_000_000, 6), false, async (_, _, _, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null!; });
        provider.Owner = rig;
        int translated = 0;
        var job = new AsrJob(() => provider, rig.Files, _ => { translated++; return Task.CompletedTask; }, () => true);
        var running = job.TranscribeAsync(rig.Recorded(Wav(Tone(20))));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(2, rig.Leases.ActiveCount); // the recording and the chunk in flight
        job.Cancel();
        var final = await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(AsrPhase.Cancelled, final.Phase);
        Assert.Single(provider.Calls); // later chunks were never started
        Assert.Equal(0, rig.Leases.ActiveCount);
        Assert.Equal(0, translated);
    }

    [Fact] // a new job supersedes the running one, whose result is never published or translated
    public async Task A_new_job_supersedes_the_running_one()
    {
        using var rig = new Rig();
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var provider = new FakeAsr(SpeechCatalog.OpenAiLimits, false, async (call, index, _, ct) =>
        {
            if (index == 0) { started.TrySetResult(); await release.Task.WaitAsync(ct); }
            return new AsrOutcome.Transcribed("text", $"text {index}", null);
        });
        provider.Owner = rig;
        var translated = new List<string>();
        var job = new AsrJob(() => provider, rig.Files, t => { translated.Add(t); return Task.CompletedTask; }, () => true);
        var first = job.TranscribeAsync(rig.Recorded(Wav(Tone(3))));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = await job.TranscribeAsync(rig.Recorded(Wav(Tone(3))));
        Assert.Equal(AsrPhase.Transcribed, second.Phase);
        Assert.Equal(AsrPhase.Cancelled, (await first.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Phase);
        Assert.Equal(["text 1"], translated);
        Assert.Equal(0, rig.Leases.ActiveCount);
    }

    [Fact]
    public async Task The_job_transcribes_translates_and_releases_the_recording()
    {
        using var rig = new Rig();
        var provider = new FakeAsr(SpeechCatalog.OpenAiLimits, true);
        provider.Owner = rig;
        string? translated = null;
        var phases = new List<AsrPhase>();
        var job = new AsrJob(() => provider, rig.Files, t => { translated = t; return Task.CompletedTask; }, () => true);
        job.StateChanged += s => phases.Add(s.Phase);
        var state = await job.TranscribeAsync(rig.Recorded(Wav(Tone(3))), "en");
        Assert.Equal((AsrPhase.Transcribed, "part 0", true), (state.Phase, state.Text, state.Translated));
        Assert.Equal("part 0", translated);
        Assert.Equal("en", provider.Calls.Single().Lang);
        Assert.Equal("text", provider.Calls.Single().Output); // ordinary voice never asks for timecodes
        Assert.Equal(AsrPhase.Transcribing, phases[0]);
        Assert.Equal(0, rig.Leases.ActiveCount);
        // Auto-translate off: the text waits for the user.
        translated = null;
        var manual = new AsrJob(() => provider, rig.Files, t => { translated = t; return Task.CompletedTask; }, () => false);
        Assert.False((await manual.TranscribeAsync(rig.Recorded(Wav(Tone(3))))).Translated);
        Assert.Null(translated);
    }

    [Fact] // the recorder's Silent flag means no request; no service means nothing is called
    public async Task Silent_recordings_and_missing_services_call_nothing()
    {
        using var rig = new Rig();
        var provider = new FakeAsr(SpeechCatalog.OpenAiLimits, true);
        provider.Owner = rig;
        var job = new AsrJob(() => provider, rig.Files, _ => Task.CompletedTask, () => true);
        Assert.Equal(AsrPhase.NoSpeech, (await job.TranscribeAsync(rig.Recorded(Wav(Tone(3)), silent: true))).Phase);
        Assert.Empty(provider.Calls);
        var none = new AsrJob(() => null, rig.Files, _ => Task.CompletedTask, () => true);
        Assert.Equal(AsrPhase.Unavailable, (await none.TranscribeAsync(rig.Recorded(Wav(Tone(3))))).Phase);
        Assert.Equal(0, rig.Leases.ActiveCount);
    }

    [Fact] // a recording that is not 16 kHz mono 16-bit PCM is refused locally; nothing is sent
    public async Task An_unsupported_input_wav_is_refused_before_sending()
    {
        using var rig = new Rig();
        var provider = new FakeAsr(SpeechCatalog.OpenAiLimits, true);
        Assert.Equal("asr.inputFormat", Failed(await RunAsync(rig, provider, [.. "not a wav file at all"u8.ToArray(), .. new byte[100]])).Detail);
        var stereo = Wav(Tone(1));
        stereo[22] = 2; // channels
        Assert.Equal("asr.inputFormat", Failed(await RunAsync(rig, provider, stereo)).Detail);
        Assert.Empty(provider.Calls);
    }
}
