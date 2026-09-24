using System.Text;

namespace Susu.Domain;

public enum InputUnit { Utf8Bytes, UnicodeScalars }
public enum BatchMode { Single, Items }

/// <summary>Per service/model translation limits (PLAN 4.7.1). Missing limits block activation.</summary>
public sealed record TranslationLimits(InputUnit Unit, int MaxInput, BatchMode Mode, int MaxItems, int MaxTotalInput)
{
    /// <summary>Returns an error message when the declaration is unusable, otherwise null.</summary>
    public string? Validate()
    {
        if (MaxInput <= 0) return "maxInput must be positive";
        if (MaxItems <= 0 || MaxTotalInput <= 0) return "batch limits must be positive";
        if (Mode == BatchMode.Single && MaxItems != 1) return "single mode requires maxItems 1";
        if (MaxTotalInput < MaxInput && Mode == BatchMode.Single) return "maxTotalInput below maxInput";
        return null;
    }

    public int Measure(ReadOnlySpan<char> text) => Unit == InputUnit.Utf8Bytes ? Encoding.UTF8.GetByteCount(text) : CountScalars(text);

    private static int CountScalars(ReadOnlySpan<char> text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++) { count++; if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++; }
        return count;
    }
}

/// <summary>A piece of the original text; <see cref="Start"/>/<see cref="Length"/> index the original UTF-16 string.</summary>
public sealed record TextChunk(int Index, int Start, int Length, string Text, bool HardSplit);

/// <summary>
/// Splits text for a service limit (PLAN 4.7.1): paragraph, then sentence, then whitespace boundaries;
/// otherwise a hard cut on a Unicode scalar boundary. Chunks concatenate back to the exact original.
/// </summary>
public static class TextChunker
{
    public static IReadOnlyList<TextChunk> Split(string text, TranslationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chunks = new List<TextChunk>();
        int position = 0;
        while (position < text.Length)
        {
            var rest = text.AsSpan(position);
            if (limits.Measure(rest) <= limits.MaxInput)
            {
                chunks.Add(new TextChunk(chunks.Count, position, rest.Length, rest.ToString(), false));
                break;
            }
            int fit = LargestFittingPrefix(rest, limits);
            if (fit == 0) throw new InvalidOperationException("A single Unicode scalar exceeds the service limit.");
            int cut = PreferredBreak(rest[..fit]);
            bool hard = cut == 0;
            if (hard) cut = fit;
            chunks.Add(new TextChunk(chunks.Count, position, cut, rest[..cut].ToString(), hard));
            position += cut;
        }
        return chunks;
    }

    /// <summary>Largest prefix (in UTF-16 units, ending on a scalar boundary) whose measure fits.</summary>
    private static int LargestFittingPrefix(ReadOnlySpan<char> text, TranslationLimits limits)
    {
        int used = 0, end = 0;
        while (end < text.Length)
        {
            int width = char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
            int cost = limits.Unit == InputUnit.UnicodeScalars ? 1 : Encoding.UTF8.GetByteCount(text.Slice(end, width));
            if (used + cost > limits.MaxInput) break;
            used += cost;
            end += width;
        }
        return end;
    }

    /// <summary>End index (exclusive) of the best boundary inside window; 0 when none.</summary>
    private static int PreferredBreak(ReadOnlySpan<char> window)
    {
        int paragraph = window.LastIndexOf('\n');
        if (paragraph > 0) return paragraph + 1;
        for (int i = window.Length - 1; i > 0; i--)
        {
            char c = window[i - 1];
            if (c is '。' or '！' or '？' or '；' or '…') return i;
            if (c is '.' or '!' or '?' or ';' && char.IsWhiteSpace(window[i])) return i + 1;
        }
        for (int i = window.Length - 1; i > 0; i--)
            if (char.IsWhiteSpace(window[i - 1]) && !char.IsHighSurrogate(window[i - 1])) return i;
        return 0;
    }
}

/// <summary>A subtitle cue with its host-assigned stable id.</summary>
public sealed record SubtitleSegment(string SegmentId, double Start, double End, string Text);

/// <summary>What a plugin sees: a unique id per (segment, part).</summary>
public sealed record SubtitlePart(string Id, string SegmentId, int PartIndex, string Text);

public enum BatchProblem { None, Missing, Duplicate, Extra, InvalidText }

/// <summary>
/// Subtitle id mapping (PLAN 4.7.1): long cues are split into parts; results are matched only by id;
/// a malformed batch yields no result and is degraded once to single-item calls.
/// </summary>
public static class SubtitleMapper
{
    public static IReadOnlyList<SubtitlePart> ToParts(IEnumerable<SubtitleSegment> segments, TranslationLimits limits)
    {
        var parts = new List<SubtitlePart>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            if (!seen.Add(segment.SegmentId)) throw new ArgumentException($"duplicate segment id {segment.SegmentId}");
            var chunks = TextChunker.Split(segment.Text, limits);
            foreach (var chunk in chunks) parts.Add(new SubtitlePart($"{segment.SegmentId}#{chunk.Index}", segment.SegmentId, chunk.Index, chunk.Text));
        }
        return parts;
    }

    /// <summary>Checks a batch response against the request ids. Order may differ; nothing else may.</summary>
    public static BatchProblem Validate(IReadOnlyList<SubtitlePart> requested, IReadOnlyList<(string? Id, string? Text)> returned)
    {
        var expected = requested.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var got = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, text) in returned)
        {
            if (id is null || !expected.Contains(id)) return BatchProblem.Extra;
            if (!got.Add(id)) return BatchProblem.Duplicate;
            if (text is null) return BatchProblem.InvalidText;
        }
        return got.Count == expected.Count ? BatchProblem.None : BatchProblem.Missing;
    }

    /// <summary>Joins translated parts back per segment in part order; timing comes from the original cue.</summary>
    public static IReadOnlyList<SubtitleSegment> Merge(IReadOnlyList<SubtitleSegment> original, IReadOnlyList<SubtitlePart> parts, IReadOnlyDictionary<string, string> translatedById)
    {
        var result = new List<SubtitleSegment>(original.Count);
        foreach (var segment in original)
        {
            var own = parts.Where(p => p.SegmentId == segment.SegmentId).OrderBy(p => p.PartIndex).ToArray();
            if (own.Any(p => !translatedById.ContainsKey(p.Id))) continue; // incomplete: left for retry/partial export
            result.Add(segment with { Text = string.Concat(own.Select(p => translatedById[p.Id])) });
        }
        return result;
    }

    /// <summary>Scheduling soft cap for video batches (PLAN 4.7.1): ≤20 items and ≈1500 scalars, never above the service limits.</summary>
    public static IReadOnlyList<IReadOnlyList<SubtitlePart>> Batches(IReadOnlyList<SubtitlePart> parts, TranslationLimits limits, int softItems = 20, int softScalars = 1500)
    {
        var batches = new List<IReadOnlyList<SubtitlePart>>();
        int maxItems = limits.Mode == BatchMode.Single ? 1 : Math.Min(limits.MaxItems, softItems);
        var current = new List<SubtitlePart>();
        int total = 0, scalars = 0;
        foreach (var part in parts)
        {
            int size = limits.Measure(part.Text), partScalars = part.Text.EnumerateRunes().Count();
            if (current.Count > 0 && (current.Count >= maxItems || total + size > limits.MaxTotalInput || scalars + partScalars > softScalars))
            {
                batches.Add(current); current = []; total = 0; scalars = 0;
            }
            current.Add(part); total += size; scalars += partScalars;
        }
        if (current.Count > 0) batches.Add(current);
        return batches;
    }
}
