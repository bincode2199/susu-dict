using System.Buffers.Binary;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Jobs;

/// <summary>The PCM layout of a WAV file: where the samples are and how they are coded.</summary>
public sealed record WavInfo(int SampleRate, int Channels, int BitsPerSample, long DataOffset, long DataBytes)
{
    public long Samples => DataBytes / Math.Max(1, Channels * BitsPerSample / 8);
}

/// <summary>Minimal RIFF/WAVE reader and writer for the 16-bit PCM audio the recorder and the ASR chunks use (F12.2).</summary>
public static class Wav16
{
    public const int HeaderBytes = 44;

    /// <summary>Reads the header of a PCM WAV (chunks before "data" are skipped); null when it is not a well-formed PCM WAV.</summary>
    public static WavInfo? ReadInfo(Stream stream)
    {
        Span<byte> riff = stackalloc byte[12];
        stream.Position = 0;
        if (stream.Read(riff) != 12 || !riff[..4].SequenceEqual("RIFF"u8) || !riff[8..12].SequenceEqual("WAVE"u8)) return null;
        int rate = 0, channels = 0, bits = 0;
        bool haveFormat = false;
        Span<byte> head = stackalloc byte[8];
        while (stream.Read(head) == 8)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            if (head[..4].SequenceEqual("fmt "u8))
            {
                if (size < 16 || size > 64) return null;
                var fmt = new byte[16];
                if (stream.Read(fmt, 0, 16) != 16) return null;
                if (BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan()) != 1) return null; // PCM only
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                haveFormat = true;
                stream.Position += size - 16 + (size & 1);
            }
            else if (head[..4].SequenceEqual("data"u8))
            {
                if (!haveFormat) return null;
                long offset = stream.Position;
                long bytes = Math.Min(size, stream.Length - offset); // a file cut short keeps what is there
                return new WavInfo(rate, channels, bits, offset, Math.Max(0, bytes));
            }
            else stream.Position += size + (size & 1);
        }
        return null;
    }

    public static byte[] Header(long dataBytes, int sampleRate)
    {
        var h = new byte[HeaderBytes];
        "RIFF"u8.CopyTo(h); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)(36 + dataBytes));
        "WAVEfmt "u8.CopyTo(h.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)sampleRate); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), (uint)(sampleRate * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)dataBytes);
        return h;
    }

    /// <summary>RMS of every <paramref name="frameSamples"/> of 16-bit mono samples (the last frame may be shorter).</summary>
    public static double[] FrameRms(Stream stream, WavInfo info, int frameSamples)
    {
        var result = new List<double>((int)(info.Samples / frameSamples) + 1);
        stream.Position = info.DataOffset;
        var buffer = new byte[frameSamples * 2 * 64];
        long left = info.Samples * 2;
        double sum = 0; int inFrame = 0;
        while (left > 0)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read <= 0) break;
            left -= read;
            for (int i = 0; i + 1 < read; i += 2)
            {
                double v = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i));
                sum += v * v;
                if (++inFrame == frameSamples) { result.Add(Math.Sqrt(sum / inFrame)); sum = 0; inFrame = 0; }
            }
        }
        if (inFrame > 0) result.Add(Math.Sqrt(sum / inFrame));
        return [.. result];
    }
}

/// <summary>One chunk to send: a sample range of the source (silence trimmed off both ends, with a short pad kept around speech).</summary>
public sealed record AsrChunkPlan(long StartSample, long EndSample)
{
    public long Samples => EndSample - StartSample;
}

/// <summary>
/// Silence detection and chunk planning (F12.2, A04/A05). Frames of 20 ms are silent below an adaptive RMS threshold. A chunk
/// never exceeds <c>maxChunkSamples</c>: it ends at the latest silence of at least <see cref="MinSilenceFrames"/> in the second
/// half of the window, and with no such silence it is cut hard at the limit (audio with no pause is never held back or
/// extended). Silence at a chunk's edges is trimmed (a window of silence is not sent at all) but chunk positions stay
/// absolute, so the timeline keeps every pause and later text never moves earlier.
/// </summary>
public static class AsrChunkPlanner
{
    public const int FrameMs = 20, MinSilenceFrames = 20 /* 400 ms */, PadFrames = 10 /* 200 ms */;
    public const double MinThreshold = 150, MaxThreshold = 500;

    public static int FrameSamples(int sampleRate) => sampleRate * FrameMs / 1000;

    /// <summary>Silent below this RMS: three times the 10th-percentile frame (the noise floor), clamped to 150..500 of 32768.</summary>
    public static double Threshold(IReadOnlyList<double> rms)
    {
        if (rms.Count == 0) return MinThreshold;
        var sorted = rms.OrderBy(v => v).ToArray();
        double floor = sorted[sorted.Length / 10];
        return Math.Clamp(floor * 3, MinThreshold, MaxThreshold);
    }

    public static List<AsrChunkPlan> Plan(IReadOnlyList<double> rms, long totalSamples, int sampleRate, long maxChunkSamples)
    {
        int frame = FrameSamples(sampleRate);
        int max = (int)Math.Max(1, maxChunkSamples / frame);
        double threshold = Threshold(rms);
        bool Silent(int i) => rms[i] < threshold;
        var plans = new List<AsrChunkPlan>();
        int count = rms.Count;
        for (int pos = 0; pos < count;)
        {
            int hard = Math.Min(count, pos + max), end = hard;
            if (hard < count)
            {
                // The latest silent run of at least MinSilenceFrames inside the second half of the window.
                int from = pos + max / 2;
                for (int i = hard - 1; i >= from; i--)
                {
                    if (!Silent(i)) continue;
                    int runEnd = i + 1, runStart = i;
                    while (runStart > from && Silent(runStart - 1)) runStart--;
                    if (runEnd - runStart >= MinSilenceFrames) { end = runEnd >= hard ? hard : (runStart + runEnd) / 2; break; }
                    i = runStart;
                }
            }
            int first = -1, last = -1;
            for (int i = pos; i < end; i++) if (!Silent(i)) { if (first < 0) first = i; last = i; }
            if (first >= 0)
            {
                int start = Math.Max(pos, first - PadFrames), stop = Math.Min(end, last + 1 + PadFrames);
                plans.Add(new AsrChunkPlan((long)start * frame, Math.Min(totalSamples, (long)stop * frame)));
            }
            pos = end;
        }
        return plans;
    }
}

/// <summary>What one run of the pipeline produced. Text is in chunk order; Segments only when timecodes were requested.</summary>
public sealed record AsrTranscript(string Text, IReadOnlyList<AsrSegment>? Segments, int Chunks, TimeSpan Audio, TimeSpan Sent);

public abstract record AsrRunOutcome
{
    public sealed record Done(AsrTranscript Transcript) : AsrRunOutcome;
    /// <summary>No chunk had speech (all silent), or the service heard none: never reported as an empty success.</summary>
    public sealed record NoSpeech : AsrRunOutcome;
    public sealed record Failure(ProviderError Error) : AsrRunOutcome;
}

/// <summary>Progress of a run: chunk <paramref name="Index"/> of <paramref name="Total"/> is being transcribed.</summary>
public readonly record struct AsrProgress(int Index, int Total);

/// <summary>
/// The shared ASR pipeline (F12.2, PLAN 4.7.2): negotiate the encoding with the model, plan chunks with the silence detector
/// (the model's file, whole-request and duration limits all hold for each chunk), write each chunk as a WAV lease, send it to
/// the provider by handle, merge text and time offsets in order. Chunks go one at a time, so a cancel leaves at most the call
/// in flight, which is cancelled; every chunk lease is released as soon as its call ends, however it ends.
/// </summary>
public sealed class AsrTranscriber(ILeasedFileFactory files, TimeSpan timeout, Func<string>? idFactory = null)
{
    private readonly Func<string> ids = idFactory ?? (() => $"asr-{Guid.NewGuid():N}");

    public async Task<AsrRunOutcome> RunAsync(IAsrProvider provider, ILeasedFile wav, string output, string? lang, CancellationToken cancellationToken, Action<AsrProgress>? progress = null)
    {
        if (output == "segments" && !provider.Timecodes) return Fail("asr.noTimecodes");
        var limits = provider.Limits;
        if (AsrEncodings.Negotiate(limits) is not { } format) return Fail("asr.format");
        string path = wav.FilePath;
        WavInfo? info; double[] rms;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            info = Wav16.ReadInfo(stream);
            if (info is not { Channels: 1, BitsPerSample: 16 } || info.SampleRate != format.SampleRate) return Fail("asr.inputFormat");
            rms = Wav16.FrameRms(stream, info, AsrChunkPlanner.FrameSamples(info.SampleRate));
        }
        catch (IOException) { return Fail("asr.readFailed"); }
        var plan = AsrChunkPlanner.Plan(rms, info.Samples, info.SampleRate, limits.MaxChunkSamples(format.SampleRate));
        if (plan.Count == 0) return new AsrRunOutcome.NoSpeech();

        var texts = new List<string>();
        var merged = new List<AsrSegment>();
        TimeSpan sent = TimeSpan.Zero;
        for (int i = 0; i < plan.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(new AsrProgress(i, plan.Count));
            var chunk = plan[i];
            double offset = (double)chunk.StartSample / info.SampleRate, seconds = (double)chunk.Samples / info.SampleRate;
            ILeasedFile? file;
            try { file = Write(path, info, chunk, limits, format); }
            catch (IOException error) { return Fail(error.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) ? "asr.diskFull" : "asr.writeFailed"); }
            if (file is null) return Fail("asr.limits"); // the written chunk did not fit the model's limits: nothing is sent
            try
            {
                var call = new AsrCall(file.LeaseId, file.Mime, file.Bytes, seconds, provider.Model, output, lang, ids(), timeout, limits.EffectiveRequestCap);
                AsrOutcome outcome;
                try { outcome = await provider.TranscribeAsync(call, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { outcome = new AsrOutcome.Failure(new ProviderError(ErrorKind.Unavailable, error.GetType().Name)); }
                cancellationToken.ThrowIfCancellationRequested();
                switch (outcome)
                {
                    case AsrOutcome.Failure f: return new AsrRunOutcome.Failure(f.Error with { Detail = $"chunk {i + 1}/{plan.Count}: {f.Error.Detail}" });
                    case AsrOutcome.Transcribed t when t.Kind == "segments" && t.Segments is not null:
                        if (AsrTimeline.Append(merged, t.Segments, offset) is { } bad) return new AsrRunOutcome.Failure(bad with { Detail = $"chunk {i + 1}/{plan.Count}: {bad.Detail}" });
                        texts.Add(t.Text);
                        break;
                    case AsrOutcome.Transcribed t when t.Kind == "text" && output == "text":
                        texts.Add(t.Text);
                        break;
                    default: return new AsrRunOutcome.Failure(new ProviderError(ErrorKind.BadResponse, "result does not match the requested output"));
                }
                sent += TimeSpan.FromSeconds(seconds);
            }
            finally { file.Dispose(); }
        }
        string text = AsrText.Join(texts);
        if (text.Length == 0 && merged.Count == 0) return new AsrRunOutcome.NoSpeech();
        return new AsrRunOutcome.Done(new AsrTranscript(text, output == "segments" ? merged : null, plan.Count, TimeSpan.FromSeconds((double)info.Samples / info.SampleRate), sent));
    }

    /// <summary>
    /// Writes one chunk as a WAV lease and measures it (the actual file and the estimated whole request, PLAN 4.7.2). Null when
    /// the measured chunk exceeds the limits - nothing is sent (a safety net: the plan already sizes chunks by the same maths).
    /// </summary>
    private ILeasedFile? Write(string sourcePath, WavInfo info, AsrChunkPlan chunk, AsrLimits limits, AsrFormat format)
    {
        var file = files.Create("asr", format.Mime, "wav");
        try
        {
            long dataBytes = chunk.Samples * 2;
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var target = new FileStream(file.FilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                target.Write(Wav16.Header(dataBytes, info.SampleRate));
                source.Position = info.DataOffset + chunk.StartSample * 2;
                var buffer = new byte[64 * 1024];
                long left = dataBytes;
                while (left > 0)
                {
                    int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (read <= 0) throw new IOException("source shorter than its header says");
                    target.Write(buffer, 0, read);
                    left -= read;
                }
            }
            long actual = new FileInfo(file.FilePath).Length;
            if (actual > limits.EffectiveFileCap || limits.EstimateRequestBytes(actual) > limits.EffectiveRequestCap || chunk.Samples > (long)limits.MaxSeconds * info.SampleRate)
            { file.Dispose(); return null; }
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private static AsrRunOutcome Fail(string code) => new AsrRunOutcome.Failure(new ProviderError(ErrorKind.Unavailable, code));
}
