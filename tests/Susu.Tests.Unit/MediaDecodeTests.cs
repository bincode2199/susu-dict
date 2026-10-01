using System.Buffers.Binary;
using Susu.Abstractions;
using Susu.Jobs;
using Susu.Storage;
using Susu.Windows.Media;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F14.1 media decode (TEST-PLAN VID01, VID02, PER05 media part; ARCHITECTURE 7 IMediaDecoder, 8 temp budget). The slicer, token
/// table and back-pressure run against a fake reader (exact and fast); the Media Foundation tests use real files: WAV written by
/// the test, AAC/MP4, AAC/M4A and MP3 written by the machine's own MF encoders (reported as skipped when it has none).
/// Serialized (own collection): its buffers and threads would disturb the process-wide memory/handle measurements of the recording tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MediaDecodeCollection { public const string Name = "MediaDecode"; }

[Collection(MediaDecodeCollection.Name)]
public class MediaDecodeTests : IDisposable
{
    private const int Rate = 16000;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempRoot root = new();
    private readonly FileLeases leases;
    private readonly LeasedFiles files;

    public MediaDecodeTests() { leases = new FileLeases(root.Paths.Cache); files = new LeasedFiles(leases); }

    public void Dispose() { leases.Dispose(); root.Dispose(); }

    private int SliceFiles => Directory.Exists(root.Paths.Cache) ? Directory.EnumerateFiles(root.Paths.Cache, "*", SearchOption.AllDirectories).Count() : 0;

    // ---- fakes ----

    private sealed class FakeReader(MediaProbe probe, IEnumerator<MediaPcmBlock> blocks, Exception? failAtEnd = null) : IMediaAudioReader
    {
        public int Reads, Disposals;
        public MediaProbe Probe { get; } = probe;
        public Task<MediaPcmBlock?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (blocks.MoveNext()) { Interlocked.Increment(ref Reads); return Task.FromResult<MediaPcmBlock?>(blocks.Current); }
            if (failAtEnd is not null) throw failAtEnd;
            return Task.FromResult<MediaPcmBlock?>(null);
        }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    private sealed class FakeDecoder(Func<string, IMediaAudioReader> open) : IMediaDecoder
    {
        public Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken) => Task.FromResult(open(path).Probe);
        public Task<IMediaAudioReader> OpenAsync(string path, CancellationToken cancellationToken) => Task.FromResult(open(path));
    }

    private static MediaProbe Probe(double seconds) => new(TimeSpan.FromSeconds(seconds), true, [new MediaAudioStream(1, "aac", 44100, 2, true)]);

    /// <summary>Blocks of a constant sample value, each stamped at its own start (plus an optional skew per block).</summary>
    private static IEnumerable<MediaPcmBlock> Blocks(int seconds, double blockSeconds = 1, Func<int, double>? skew = null)
    {
        int samples = (int)(Rate * blockSeconds);
        for (int i = 0; i < seconds / blockSeconds; i++)
        {
            var pcm = new byte[samples * 2];
            for (int s = 0; s < samples; s++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(s * 2), 1000);
            yield return new MediaPcmBlock(pcm, TimeSpan.FromSeconds(i * blockSeconds + (skew?.Invoke(i) ?? 0)));
        }
    }

    private (MediaSlicer Slicer, FakeReader Reader, string Token) Fake(int seconds, MediaSliceOptions options, Exception? failAtEnd = null, IEnumerable<MediaPcmBlock>? blocks = null)
    {
        var reader = new FakeReader(Probe(seconds), (blocks ?? Blocks(seconds)).GetEnumerator(), failAtEnd);
        var tokens = new MediaTokens();
        string file = Path.Combine(root.Root, "in.mp4"); File.WriteAllText(file, "x");
        string token = tokens.Issue(file)!;
        return (new MediaSlicer(tokens, new FakeDecoder(_ => reader), files, options), reader, token);
    }

    private static MediaSliceOptions Opt(int sliceSeconds, Func<string, Stream>? write = null) => new(TimeSpan.FromSeconds(sliceSeconds), 2, write);

    private static async Task<List<MediaSlice>> Drain(MediaSession session)
    {
        var all = new List<MediaSlice>();
        while (true)
        {
            var step = await session.NextAsync(Ct);
            if (step is MediaStep.Slice s) all.Add(s.Value); else return all;
        }
    }

    // ---- tokens ----

    [Fact]
    public void Token_is_opaque_resolves_only_on_host_and_can_be_revoked()
    {
        var tokens = new MediaTokens();
        string file = Path.Combine(root.Root, "My Holiday.mp4"); File.WriteAllText(file, "x");
        string token = tokens.Issue(file)!;
        Assert.DoesNotContain("Holiday", token); Assert.DoesNotContain(root.Root, token);
        Assert.Equal("My Holiday.mp4", tokens.DisplayName(token)); // the name, never the directory
        Assert.True(tokens.TryResolve(token, out string path)); Assert.Equal(Path.GetFullPath(file), path);
        Assert.False(tokens.TryResolve("media-nope", out _));
        Assert.Null(tokens.Issue(Path.Combine(root.Root, "missing.mp4")));
        Assert.Null(tokens.Issue(root.Root)); // a directory is not a file
        Assert.NotEqual(token, tokens.Issue(file)); // every pick is a new token
        tokens.Revoke(token);
        Assert.False(tokens.TryResolve(token, out _));
    }

    [Fact]
    public async Task Unknown_token_has_its_own_error()
    {
        var slicer = new MediaSlicer(new MediaTokens(), new FakeDecoder(_ => throw new InvalidOperationException()), files);
        var e = await Assert.ThrowsAsync<MediaDecodeException>(() => slicer.OpenAsync("media-forged", Ct));
        Assert.Equal(MediaErrors.UnknownToken, e.Code);
    }

    // ---- slicing, time axis ----

    [Fact]
    public async Task Slices_are_16k_mono_wav_with_correct_start_and_length()
    {
        var (slicer, reader, token) = Fake(25, Opt(10));
        using var session = await slicer.OpenAsync(token, Ct);
        var slices = new List<MediaSlice>();
        while (true)
        {
            var step = await session.NextAsync(Ct);
            if (step is MediaStep.Slice s) { slices.Add(s.Value); s.Value.Dispose(); } // releasing lets the next one start
            else { Assert.Equal(new MediaStep.End(3, TimeSpan.FromSeconds(25)), step); break; }
        }
        Assert.Equal([0.0, 10.0, 20.0], slices.Select(s => s.Start.TotalSeconds));
        Assert.Equal([10.0, 10.0, 5.0], slices.Select(s => s.Duration.TotalSeconds));
        Assert.Equal(1, reader.Disposals); // the file is released when decoding ended
        Assert.Equal(0, SliceFiles);
    }

    [Fact]
    public async Task Slice_file_is_a_valid_wav_with_the_decoded_samples()
    {
        var (slicer, _, token) = Fake(4, Opt(3));
        using var session = await slicer.OpenAsync(token, Ct);
        var first = ((MediaStep.Slice)await session.NextAsync(Ct)).Value;
        using var stream = File.OpenRead(first.Wav.FilePath);
        var info = Wav16.ReadInfo(stream)!;
        Assert.Equal(new WavInfo(Rate, 1, 16, 44, 3 * Rate * 2), info);
        Assert.Equal("audio/wav", first.Wav.Mime);
        stream.Position = info.DataOffset + 100;
        var two = new byte[2]; stream.ReadExactly(two);
        Assert.Equal(1000, BinaryPrimitives.ReadInt16LittleEndian(two));
    }

    [Fact]
    public async Task A_gap_in_container_timestamps_is_filled_with_silence_so_the_time_axis_holds()
    {
        // The first block starts at 0.5 s; block 2 is stamped 2 s after the samples decoded so far: times must stay on the container's axis.
        var blocks = Blocks(4, 1, i => i < 2 ? 0.5 : 2.5);
        var (slicer, _, token) = Fake(4, Opt(100), blocks: blocks);
        using var session = await slicer.OpenAsync(token, Ct);
        var slice = ((MediaStep.Slice)await session.NextAsync(Ct)).Value;
        Assert.Equal(6.5, slice.Duration.TotalSeconds); // 0.5 lead + 2 audio + 2 gap + 2 audio
        using var stream = File.OpenRead(slice.Wav.FilePath);
        var info = Wav16.ReadInfo(stream)!;
        var data = new byte[info.DataBytes]; stream.Position = info.DataOffset; stream.ReadExactly(data);
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(100))); // leading silence
        Assert.Equal(1000, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan((int)(0.5 * Rate * 2) + 100)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan((int)(3.5 * Rate * 2)))); // the filled gap
        Assert.Equal(1000, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan((int)(4.6 * Rate * 2)))); // audio after it, at its stamped time
    }

    [Fact]
    public async Task Small_timestamp_jitter_is_not_turned_into_silence()
    {
        var (slicer, _, token) = Fake(4, Opt(100), blocks: Blocks(4, 1, i => 0.05 * i));
        using var session = await slicer.OpenAsync(token, Ct);
        var slice = ((MediaStep.Slice)await session.NextAsync(Ct)).Value;
        Assert.Equal(4.0, slice.Duration.TotalSeconds);
    }

    // ---- exits ----

    [Fact]
    public async Task A_file_without_decodable_audio_ends_with_noAudio()
    {
        var (slicer, _, token) = Fake(0, Opt(10));
        using var session = await slicer.OpenAsync(token, Ct);
        var step = Assert.IsType<MediaStep.Failure>(await session.NextAsync(Ct));
        Assert.Equal(MediaErrors.NoAudio, step.Code);
        Assert.False(step.Retryable);
        Assert.Equal(step, await session.NextAsync(Ct)); // terminal
    }

    [Fact]
    public async Task Open_errors_of_the_decoder_pass_through_with_their_code()
    {
        foreach (string code in new[] { MediaErrors.UnsupportedEncoding, MediaErrors.NoAudio, MediaErrors.Corrupt, MediaErrors.NotFound })
        {
            var tokens = new MediaTokens();
            string file = Path.Combine(root.Root, "in.bin"); File.WriteAllText(file, "x");
            var slicer = new MediaSlicer(tokens, new FakeDecoder(_ => throw new MediaDecodeException(code, "x")), files);
            var e = await Assert.ThrowsAsync<MediaDecodeException>(() => slicer.OpenAsync(tokens.Issue(file)!, Ct));
            Assert.Equal(code, e.Code);
        }
    }

    [Fact]
    public async Task Decode_failure_midway_keeps_earlier_slices_and_reports_corrupt()
    {
        var (slicer, reader, token) = Fake(25, Opt(10), failAtEnd: new MediaDecodeException(MediaErrors.Corrupt, "bad frame"));
        using var session = await slicer.OpenAsync(token, Ct);
        var a = ((MediaStep.Slice)await session.NextAsync(Ct)).Value;
        var b = ((MediaStep.Slice)await session.NextAsync(Ct)).Value;
        a.Dispose(); // upload of the first is done, so decoding goes on into the damaged part (back-pressure)
        var step = Assert.IsType<MediaStep.Failure>(await session.NextAsync(Ct));
        Assert.Equal(MediaErrors.Corrupt, step.Code);
        Assert.True(File.Exists(b.Wav.FilePath)); // a finished slice is still usable after the failure
        Assert.Equal(1, reader.Disposals);
        b.Dispose();
        Assert.Equal(0, SliceFiles);
    }

    // ---- back-pressure ----

    [Fact]
    public async Task Decode_never_runs_more_than_two_slices_ahead_and_resumes_when_one_is_released()
    {
        var (slicer, reader, token) = Fake(100, Opt(10));
        using var session = await slicer.OpenAsync(token, Ct);
        await WaitUntil(() => leases.ActiveCount == 2);
        await Task.Delay(300, Ct); // time for a runaway decoder to show itself
        Assert.Equal(2, leases.ActiveCount);
        Assert.Equal(2, SliceFiles);
        Assert.Equal(20, reader.Reads); // exactly two 10-second slices of 1-second blocks, nothing more

        var first = ((MediaStep.Slice)await session.NextAsync(Ct)).Value; // taking a slice is not releasing it
        await Task.Delay(200, Ct);
        Assert.Equal(20, reader.Reads);
        first.Dispose(); // upload done: one more slice may be decoded
        await WaitUntil(() => reader.Reads == 30);
        await Task.Delay(200, Ct);
        Assert.Equal(30, reader.Reads);
        Assert.Equal(2, leases.ActiveCount);
    }

    [Fact]
    public async Task A_whole_long_file_passes_with_only_two_slices_alive_at_any_time()
    {
        var (slicer, _, token) = Fake(600, Opt(30)); // 20 slices
        using var session = await slicer.OpenAsync(token, Ct);
        int max = 0, count = 0;
        while (await session.NextAsync(Ct) is MediaStep.Slice s)
        {
            max = Math.Max(max, leases.ActiveCount);
            count++;
            s.Value.Dispose();
        }
        Assert.Equal(20, count);
        Assert.True(max <= 2, $"alive {max}");
        Assert.Equal(0, SliceFiles);
    }

    // ---- cancel ----

    [Fact]
    public async Task Cancel_stops_decode_releases_the_file_and_leaves_no_temp_files()
    {
        var (slicer, reader, token) = Fake(100, Opt(10));
        var session = await slicer.OpenAsync(token, Ct);
        await WaitUntil(() => leases.ActiveCount == 2);
        var taken = ((MediaStep.Slice)await session.NextAsync(Ct)).Value; // one handed out (in "upload"), one prefetched
        session.Dispose();
        int reads = reader.Reads;
        Assert.Equal(1, reader.Disposals);
        Assert.Equal(0, leases.ActiveCount);
        Assert.Equal(0, SliceFiles); // handed-out slice included
        Assert.True(taken.Wav.Released);
        await Task.Delay(200, Ct);
        Assert.Equal(reads, reader.Reads);
    }

    [Fact]
    public async Task Cancelling_the_token_cleans_up_the_same_way()
    {
        var (slicer, reader, token) = Fake(100, Opt(10));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var session = await slicer.OpenAsync(token, cts.Token);
        await WaitUntil(() => leases.ActiveCount == 2);
        await cts.CancelAsync();
        await WaitUntil(() => leases.ActiveCount == 0);
        Assert.Equal(0, SliceFiles);
        Assert.Equal(1, reader.Disposals);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_safe_after_a_finished_decode()
    {
        var (slicer, _, token) = Fake(5, Opt(10));
        var session = await slicer.OpenAsync(token, Ct);
        var slice = ((MediaStep.Slice)await session.NextAsync(Ct)).Value;
        Assert.IsType<MediaStep.End>(await session.NextAsync(Ct));
        session.Dispose(); session.Dispose(); slice.Dispose();
        Assert.Equal(0, SliceFiles);
    }

    // ---- disk full ----

    private sealed class FlakyDisk(int failures, int hresult = unchecked((int)0x80070070))
    {
        public int Remaining = failures;
        public Stream Open(string path)
        {
            var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            if (Interlocked.Decrement(ref Remaining) < 0) return file;
            return new FullStream(file, hresult);
        }
    }

    /// <summary>Accepts the first bytes (a torn file) then reports the disk full, like a real ERROR_DISK_FULL mid-write.</summary>
    private sealed class FullStream(FileStream inner, int hresult) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, Math.Min(count, 10));
            inner.Flush();
            throw new IOException("There is not enough space on the disk.", hresult);
        }
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

    [Fact]
    public async Task Disk_full_is_recoverable_the_torn_file_is_deleted_and_the_retry_has_the_same_audio_without_decoding_again()
    {
        var disk = new FlakyDisk(failures: 2);
        var (slicer, reader, token) = Fake(25, Opt(10, disk.Open));
        using var session = await slicer.OpenAsync(token, Ct);

        var failure = Assert.IsType<MediaStep.Failure>(await session.NextAsync(Ct));
        Assert.Equal(MediaErrors.DiskFull, failure.Code);
        Assert.True(failure.Retryable);
        Assert.Equal(0, SliceFiles); // the half-written file is gone, nothing is corrupt
        Assert.Equal(0, leases.ActiveCount);
        int readsAtFailure = reader.Reads;

        session.Retry(); // still full: fails again, same slice
        failure = Assert.IsType<MediaStep.Failure>(await session.NextAsync(Ct));
        Assert.Equal(MediaErrors.DiskFull, failure.Code);
        Assert.Equal(0, SliceFiles);

        session.Retry(); // space freed
        var slice = ((MediaStep.Slice)await session.NextAsync(Ct)).Value;
        Assert.Equal(0, slice.Index); Assert.Equal(10.0, slice.Duration.TotalSeconds);
        Assert.True(reader.Reads >= readsAtFailure);
        using (var stream = File.OpenRead(slice.Wav.FilePath))
        {
            var info = Wav16.ReadInfo(stream)!;
            Assert.Equal(10 * Rate * 2, info.DataBytes);
            Assert.Equal(info.DataOffset + info.DataBytes, stream.Length);
        }
        slice.Dispose();
        var rest = await Drain(session); // the remaining slices follow normally, in order, with the time axis intact
        Assert.Equal([10.0, 20.0], rest.Select(s => s.Start.TotalSeconds));
        Assert.Equal(25, reader.Reads); // every block was decoded exactly once, retries included
    }

    [Fact]
    public async Task Disk_full_is_recognized_by_either_win32_code_and_other_write_errors_end_the_session()
    {
        var (slicer, _, token) = Fake(5, Opt(10, new FlakyDisk(1, unchecked((int)0x80070027)).Open)); // ERROR_HANDLE_DISK_FULL
        using (var session = await slicer.OpenAsync(token, Ct))
            Assert.Equal(MediaErrors.DiskFull, Assert.IsType<MediaStep.Failure>(await session.NextAsync(Ct)).Code);

        var (slicer2, _, token2) = Fake(5, Opt(10, new FlakyDisk(1, unchecked((int)0x80070005)).Open));
        using var session2 = await slicer2.OpenAsync(token2, Ct);
        var step = Assert.IsType<MediaStep.Failure>(await session2.NextAsync(Ct));
        Assert.Equal(MediaErrors.WriteFailed, step.Code);
        Assert.False(step.Retryable);
        Assert.Equal(0, SliceFiles);
    }

    [Fact]
    public async Task Cancel_while_waiting_for_space_cleans_up()
    {
        var (slicer, reader, token) = Fake(25, Opt(10, new FlakyDisk(100).Open));
        var session = await slicer.OpenAsync(token, Ct);
        Assert.Equal(MediaErrors.DiskFull, Assert.IsType<MediaStep.Failure>(await session.NextAsync(Ct)).Code);
        session.Dispose();
        Assert.Equal(0, SliceFiles);
        Assert.Equal(1, reader.Disposals);
    }

    // ---- Media Foundation (real) ----

    private string NewMediaPath(string name) => Path.Combine(root.Root, name);

    private static void WriteSineWav(string path, int seconds, int rate, int channels, double hz = 440)
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
                    BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan((i * channels + c) * 2), (short)(Math.Sin(2 * Math.PI * hz * (s * rate + i) / rate) * 12000));
            f.Write(buf);
        }
    }

    private static async Task<(long Samples, double Rms, double Hz)> DecodeAll(MediaFoundationDecoder decoder, string path)
    {
        using var reader = await decoder.OpenAsync(path, Ct);
        long samples = 0; double sum = 0; var all = new List<short>();
        while (await reader.ReadAsync(Ct) is { } block)
        {
            Assert.Equal(0, block.Pcm.Length % 2);
            for (int i = 0; i < block.Pcm.Length; i += 2) { short v = BinaryPrimitives.ReadInt16LittleEndian(block.Pcm.AsSpan(i)); sum += (double)v * v; all.Add(v); }
            samples += block.Pcm.Length / 2;
        }
        int crossings = 0;
        for (int i = 1; i < all.Count; i++) if (all[i - 1] < 0 && all[i] >= 0) crossings++;
        return (samples, Math.Sqrt(sum / Math.Max(1, samples)), crossings / ((double)all.Count / Rate));
    }

    [Fact]
    public async Task Real_mf_probes_and_decodes_wav_of_other_rates_and_channel_counts_to_16k_mono()
    {
        var decoder = new MediaFoundationDecoder();
        foreach (var (rate, channels) in new[] { (44100, 2), (48000, 1), (8000, 1), (22050, 2) })
        {
            string path = NewMediaPath($"t{rate}-{channels}.wav");
            WriteSineWav(path, 3, rate, channels);
            var probe = await decoder.ProbeAsync(path, Ct);
            Assert.InRange(probe.Duration.TotalSeconds, 2.9, 3.1);
            Assert.False(probe.HasVideo);
            var stream = Assert.Single(probe.AudioStreams);
            Assert.Equal((rate, channels, "pcm", true), (stream.SampleRate, stream.Channels, stream.Codec, stream.Decodable));

            var (samples, rms, hz) = await DecodeAll(decoder, path);
            Assert.InRange(samples, 3 * Rate - 800, 3 * Rate + 800); // length within 50 ms
            Assert.InRange(rms, 6000, 10000); // 12000 amplitude sine = 8485 rms; not silence, not clipped
            Assert.InRange(hz, 430, 450); // pitch survived conversion
        }
    }

    [Fact]
    public async Task Real_mf_through_the_slicer_gives_ordered_slices_with_the_whole_audio()
    {
        string path = NewMediaPath("slices.wav"); WriteSineWav(path, 7, 44100, 2);
        var tokens = new MediaTokens();
        var slicer = new MediaSlicer(tokens, new MediaFoundationDecoder(), files, Opt(3));
        using var session = await slicer.OpenAsync(tokens.Issue(path)!, Ct);
        Assert.InRange(session.Probe.Duration.TotalSeconds, 6.9, 7.1);
        var starts = new List<double>(); double total = 0;
        while (await session.NextAsync(Ct) is MediaStep.Slice s)
        {
            starts.Add(s.Value.Start.TotalSeconds); total += s.Value.Duration.TotalSeconds;
            using (var stream = File.OpenRead(s.Value.Wav.FilePath)) Assert.Equal(new WavInfo(Rate, 1, 16, 44, (long)Math.Round(s.Value.Duration.TotalSeconds * Rate) * 2), Wav16.ReadInfo(stream));
            s.Value.Dispose();
        }
        Assert.Equal([0.0, 3.0, 6.0], starts);
        Assert.InRange(total, 6.9, 7.1);
        Assert.Equal(0, SliceFiles);
    }

    [Theory]
    [InlineData("aac.mp4")]
    [InlineData("aac.m4a")]
    [InlineData("tone.mp3")]
    public async Task Real_mf_decodes_compressed_audio_written_by_the_machines_own_encoder(string name)
    {
        string path = NewMediaPath(name);
        string? why = MediaFixtureWriter.Write(path, 4);
        Assert.SkipWhen(why is not null, $"this machine cannot write {name}: {why}");
        var decoder = new MediaFoundationDecoder();
        var probe = await decoder.ProbeAsync(path, Ct);
        Assert.InRange(probe.Duration.TotalSeconds, 3.8, 4.4);
        var stream = Assert.Single(probe.AudioStreams);
        Assert.True(stream.Decodable);
        Assert.Equal(name.EndsWith("mp3") ? "mp3" : "aac", stream.Codec);
        var (samples, rms, hz) = await DecodeAll(decoder, path);
        Assert.InRange(samples, 4 * Rate - 3200, 4 * Rate + 4800); // encoder delay/padding allowed
        Assert.InRange(rms, 5000, 11000);
        Assert.InRange(hz, 425, 455);
    }

    [Fact]
    public async Task Real_mf_exits_missing_garbage_and_truncated_files_with_a_code_not_a_crash()
    {
        var decoder = new MediaFoundationDecoder();
        var missing = await Assert.ThrowsAsync<MediaDecodeException>(() => decoder.OpenAsync(NewMediaPath("nope.mp4"), Ct));
        Assert.Equal(MediaErrors.NotFound, missing.Code);

        string garbage = NewMediaPath("garbage.mp4");
        File.WriteAllBytes(garbage, Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 % 251)).ToArray());
        var bad = await Assert.ThrowsAnyAsync<MediaDecodeException>(() => decoder.OpenAsync(garbage, Ct));
        Assert.Contains(bad.Code, new[] { MediaErrors.UnsupportedEncoding, MediaErrors.Corrupt });

        string empty = NewMediaPath("empty.mkv"); File.WriteAllBytes(empty, []);
        var e2 = await Assert.ThrowsAnyAsync<MediaDecodeException>(() => decoder.OpenAsync(empty, Ct));
        Assert.Contains(e2.Code, new[] { MediaErrors.UnsupportedEncoding, MediaErrors.Corrupt });

        // a WAV cut in the middle decodes what is there (the header length is larger than the file), or exits cleanly as corrupt
        string whole = NewMediaPath("whole.wav"); WriteSineWav(whole, 3, 16000, 1);
        string cut = NewMediaPath("cut.wav"); File.WriteAllBytes(cut, File.ReadAllBytes(whole)[..(44 + 16000 * 2 * 2)]);
        try
        {
            var (samples, _, _) = await DecodeAll(decoder, cut);
            Assert.InRange(samples, 1, 3 * Rate);
        }
        catch (MediaDecodeException e) { Assert.Equal(MediaErrors.Corrupt, e.Code); }
    }

    [Fact]
    public async Task Real_mf_cancel_during_read_throws_cancelled_and_releases_the_file()
    {
        string path = NewMediaPath("cancel.wav"); WriteSineWav(path, 5, 44100, 2);
        var decoder = new MediaFoundationDecoder();
        using (var reader = await decoder.OpenAsync(path, Ct))
        {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cts.Token));
        }
        File.Delete(path); // would throw if the reader still held the file
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Real_mf_session_cancel_leaves_no_temp_files_and_frees_the_source()
    {
        string path = NewMediaPath("long.wav"); WriteSineWav(path, 20, 16000, 1);
        var tokens = new MediaTokens();
        var slicer = new MediaSlicer(tokens, new MediaFoundationDecoder(), files, Opt(2));
        var session = await slicer.OpenAsync(tokens.Issue(path)!, Ct);
        var taken = ((MediaStep.Slice)await session.NextAsync(Ct)).Value;
        await WaitUntil(() => leases.ActiveCount == 2);
        session.Dispose();
        Assert.Equal(0, SliceFiles);
        Assert.True(taken.Wav.Released);
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Real_mf_unknown_audio_format_is_probed_without_the_decodable_flag_and_exits_with_a_code_on_open()
    {
        // WAV whose format tag is one nobody decodes (0x7777): the container is fine, the codec is not
        string path = NewMediaPath("weird.wav"); WriteSineWav(path, 1, 16000, 1);
        var bytes = File.ReadAllBytes(path); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 0x7777); File.WriteAllBytes(path, bytes);
        var decoder = new MediaFoundationDecoder();
        try
        {
            var probe = await decoder.ProbeAsync(path, Ct);
            Assert.Null(probe.Selected);
            var e = await Assert.ThrowsAsync<MediaDecodeException>(() => decoder.OpenAsync(path, Ct));
            Assert.Contains(e.Code, new[] { MediaErrors.UnsupportedEncoding, MediaErrors.NoAudio });
        }
        catch (MediaDecodeException e) { Assert.Contains(e.Code, new[] { MediaErrors.UnsupportedEncoding, MediaErrors.Corrupt }); } // MF refused at open
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++) await Task.Delay(10, Ct);
        Assert.True(condition(), "condition not reached");
    }
}
