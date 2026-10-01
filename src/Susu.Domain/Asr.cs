using System.Text;
using Susu.Contracts;

namespace Susu.Domain;

/// <summary>How a model takes the audio: a multipart file part, or Base64 inside a JSON body (PLAN 4.7.2).</summary>
public enum AsrUpload { Multipart, Base64Json }

/// <summary>One complete audio encoding a model declares (container, codec, MIME, sample rate, channels). AAC only counts when a model declares it in full.</summary>
public sealed record AsrFormat(string Container, string Codec, string Mime, int SampleRate, int Channels)
{
    public static readonly AsrFormat Wav16kMono = new("wav", "pcm_s16le", "audio/wav", 16000, 1);
}

/// <summary>
/// A model's declared media limits (PLAN 4.7.2 <c>asr.models</c>): <see cref="MaxBytes"/> is the encoded file,
/// <see cref="MaxRequestBytes"/> the whole upload body (multipart or Base64/JSON overhead included, <see cref="RequestOverheadBytes"/>
/// is the conservative fixed part), <see cref="MaxSeconds"/> the length of one chunk. The vendor's limits and the host's
/// (32 MiB binary, 48 MiB Base64 JSON; PLAN 4.5.1) apply, the lower winning.
/// </summary>
public sealed record AsrLimits(IReadOnlyList<AsrFormat> Formats, long MaxBytes, long MaxRequestBytes, int MaxSeconds, AsrUpload Upload, long RequestOverheadBytes)
{
    public const int WavHeaderBytes = 44;
    private const long HostBinaryCap = 32L * 1024 * 1024, HostBase64JsonCap = 48L * 1024 * 1024;

    /// <summary>The estimated size of the whole upload body for an encoded file of <paramref name="fileBytes"/>.</summary>
    public long EstimateRequestBytes(long fileBytes)
        => (Upload == AsrUpload.Base64Json ? (fileBytes + 2) / 3 * 4 : fileBytes) + RequestOverheadBytes;

    /// <summary>The cap on the final upload body: the model's, but never above what the host transports.</summary>
    public long EffectiveRequestCap => Math.Min(MaxRequestBytes, Upload == AsrUpload.Base64Json ? HostBase64JsonCap : HostBinaryCap + RequestOverheadBytes);

    /// <summary>The cap on the encoded file.</summary>
    public long EffectiveFileCap => Math.Min(MaxBytes, HostBinaryCap);

    /// <summary>
    /// The most 16-bit mono samples one WAV chunk may carry so the file, the estimated whole request and the duration all fit
    /// (the largest n with 44 + 2n ≤ file cap, estimate ≤ request cap, n ≤ maxSeconds × rate). 0 when not even that fits.
    /// </summary>
    public long MaxChunkSamples(int sampleRate)
    {
        long lo = 0, hi = (long)MaxSeconds * sampleRate;
        while (lo < hi)
        {
            long mid = lo + (hi - lo + 1) / 2;
            long file = WavHeaderBytes + 2 * mid;
            if (file <= EffectiveFileCap && EstimateRequestBytes(file) <= EffectiveRequestCap) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}

/// <summary>
/// The local encoders (F12.2): only 16-bit PCM WAV is produced. The host takes the intersection of a model's formats and the
/// local ones; empty means the model cannot be chosen (PLAN 4.7.2) - it never silently switches to AAC or any other codec.
/// </summary>
public static class AsrEncodings
{
    public static readonly IReadOnlyList<AsrFormat> LocalFormats = [AsrFormat.Wav16kMono];

    /// <summary>The format to encode with, or null: no declared format is locally encodable, or not even one second fits the limits.</summary>
    public static AsrFormat? Negotiate(AsrLimits? limits, IReadOnlyList<AsrFormat>? local = null)
    {
        if (limits is null) return null;
        var supported = local ?? LocalFormats;
        var common = limits.Formats.Where(f => supported.Any(l => l == f)).ToList();
        // Preference: WAV, 16 kHz, mono, 16-bit PCM first (the only thing produced today).
        var chosen = common.FirstOrDefault(f => f == AsrFormat.Wav16kMono) ?? common.FirstOrDefault();
        return chosen is null || limits.MaxChunkSamples(chosen.SampleRate) < chosen.SampleRate ? null : chosen;
    }
}

/// <summary>How the text of consecutive chunks becomes one text (PLAN 6.2: in chunk order, nothing reordered or dropped).</summary>
public static class AsrText
{
    public static string Join(IEnumerable<string?> parts)
    {
        var text = new StringBuilder();
        foreach (var raw in parts)
        {
            string part = (raw ?? "").Trim();
            if (part.Length == 0) continue;
            if (text.Length > 0 && !(IsCjk(text[^1]) || IsCjk(part[0]))) text.Append(' ');
            text.Append(part);
        }
        return text.ToString();
    }

    private static bool IsCjk(char c) => c is (>= '぀' and <= 'ヿ') or (>= '㐀' and <= '鿿') or (>= '豈' and <= '﫿') or (>= '＀' and <= '￯') or '。' or '，';
}

/// <summary>Merging timed segments of consecutive chunks onto the original timeline (PLAN 4.7.2, A05/A08).</summary>
public static class AsrTimeline
{
    /// <summary>
    /// Moves one chunk's segments by the chunk's absolute start. Silence stays on the timeline: the offset is the chunk's real
    /// position in the audio, so a pause (a skipped or trimmed silent stretch) pushes later subtitles later, never earlier.
    /// Returns the error when the merged list is not non-decreasing by start (a bad result never reaches an SRT).
    /// </summary>
    public static ProviderError? Append(List<AsrSegment> merged, IReadOnlyList<AsrSegment> chunkSegments, double chunkStartSeconds)
    {
        if (!double.IsFinite(chunkStartSeconds) || chunkStartSeconds < 0) return new ProviderError(ErrorKind.BadResponse, "invalid chunk offset");
        double last = merged.Count == 0 ? 0 : merged.Max(s => s.Start);
        foreach (var s in chunkSegments)
        {
            var moved = s with { Start = s.Start + chunkStartSeconds, End = s.End + chunkStartSeconds };
            if (moved.Start < last) return new ProviderError(ErrorKind.BadResponse, "segments would move earlier on the timeline");
            last = moved.Start;
            merged.Add(moved);
        }
        return null;
    }
}
