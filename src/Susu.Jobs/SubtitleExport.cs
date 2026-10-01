using System.Globalization;
using System.Text;
using Susu.Abstractions;

namespace Susu.Jobs;

/// <summary>A cue that was left out of an export, and why. <see cref="Code"/> is one of the <see cref="SubtitleIssues"/> constants.</summary>
public sealed record SubtitleIssue(string CueId, string Code);

public static class SubtitleIssues
{
    /// <summary>NaN, infinity, or a negative start.</summary>
    public const string BadTime = "time.invalid";
    /// <summary>End not after start (also after rounding to milliseconds).</summary>
    public const string BadRange = "time.range";
    /// <summary>Beyond the media duration (plus the tolerance) or absurdly large.</summary>
    public const string OutOfRange = "time.outOfRange";
    /// <summary>Starts before an earlier accepted cue. Nothing is re-sorted: the cue is reported and left out.</summary>
    public const string OutOfOrder = "time.outOfOrder";
    public const string DuplicateId = "cue.duplicateId";
    public const string EmptyText = "cue.emptyText";
    /// <summary>A translation-only export has nothing to show for this cue (never translated or failed); original text is not substituted.</summary>
    public const string MissingTranslation = "translation.missing";
}

/// <summary>
/// Result of building subtitle text. <see cref="Text"/> is null when no cue could be exported. <see cref="Overlaps"/> counts accepted cues
/// whose start is before the previous accepted cue's end: legitimate speaker overlap, kept as is.
/// </summary>
public sealed record SubtitleBuild(string? Text, IReadOnlyList<SubtitleIssue> Issues, int Exported, int Overlaps);

/// <summary>
/// Pure subtitle validation and formatting (F14.3, TEST-PLAN A06 and VID03). Rules: a cue with a bad time is reported and excluded, never
/// repaired; times are never made up, shifted or split evenly; overlapping cues are kept; the file is UTF-8 without BOM with LF line ends
/// (the spec names no BOM; players that need one are not a requirement).
/// </summary>
public static class SubtitleFormatter
{
    /// <summary>ASR and decode may differ from the container duration by a little; more than this past the end is out of range.</summary>
    public static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(1);
    /// <summary>Without a known duration, anything past this is treated as garbage (about 277 hours).</summary>
    public const double MaxSeconds = 1_000_000;

    public static SubtitleBuild Build(IReadOnlyList<VideoCue> cues, TimeSpan? mediaDuration, SubtitleMode mode, SubtitleFormat format)
    {
        var issues = new List<SubtitleIssue>();
        var timed = new List<(VideoCue Cue, long Start, long End)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long lastStart = long.MinValue;
        double limit = mediaDuration is { } d ? Math.Min(MaxSeconds, d.TotalSeconds + DurationTolerance.TotalSeconds) : MaxSeconds;
        foreach (var cue in cues)
        {
            string? bad = null;
            if (!double.IsFinite(cue.Start) || !double.IsFinite(cue.End) || cue.Start < 0 || cue.End < 0) bad = SubtitleIssues.BadTime;
            else if (cue.End <= cue.Start) bad = SubtitleIssues.BadRange;
            else if (cue.End > limit) bad = SubtitleIssues.OutOfRange;
            long start = 0, end = 0;
            if (bad is null)
            {
                start = Millis(cue.Start); end = Millis(cue.End);
                if (end <= start) bad = SubtitleIssues.BadRange;
                else if (start < lastStart) bad = SubtitleIssues.OutOfOrder;
                else if (!seen.Add(cue.Id)) bad = SubtitleIssues.DuplicateId;
            }
            if (bad is not null) { issues.Add(new SubtitleIssue(cue.Id, bad)); continue; }
            lastStart = start;
            timed.Add((cue, start, end));
        }

        var sb = new StringBuilder();
        if (format == SubtitleFormat.Vtt) sb.Append("WEBVTT\n\n");
        int exported = 0, overlaps = 0;
        long previousEnd = long.MinValue;
        foreach (var (cue, start, end) in timed)
        {
            string? original = Clean(cue.Original), translation = Clean(cue.Translation);
            var lines = new List<string>();
            switch (mode)
            {
                case SubtitleMode.Original: if (original is not null) lines.Add(original); break;
                case SubtitleMode.Translation: if (translation is not null) lines.Add(translation); break;
                case SubtitleMode.BilingualOriginalFirst: AddIfAny(lines, original); AddIfAny(lines, translation); break;
                default: AddIfAny(lines, translation); AddIfAny(lines, original); break;
            }
            if (lines.Count == 0)
            {
                issues.Add(new SubtitleIssue(cue.Id, mode == SubtitleMode.Translation && original is not null ? SubtitleIssues.MissingTranslation : SubtitleIssues.EmptyText));
                continue;
            }
            exported++;
            if (exported > 1 && start < previousEnd) overlaps++;
            previousEnd = Math.Max(previousEnd == long.MinValue ? end : previousEnd, end);
            string body = string.Join("\n", lines.Select(l => Escape(l, format)));
            switch (format)
            {
                case SubtitleFormat.Srt:
                    sb.Append(exported.ToString(CultureInfo.InvariantCulture)).Append('\n').Append(Stamp(start, ',')).Append(" --> ").Append(Stamp(end, ','))
                        .Append('\n').Append(body).Append("\n\n");
                    break;
                case SubtitleFormat.Vtt:
                    sb.Append(Stamp(start, '.')).Append(" --> ").Append(Stamp(end, '.')).Append('\n').Append(body).Append("\n\n");
                    break;
                default:
                    sb.Append('[').Append(Stamp(start, '.')).Append("] ").Append(body.Replace("\n", "\n    ")).Append("\n\n");
                    break;
            }
        }
        return new SubtitleBuild(exported == 0 ? null : sb.ToString(), issues, exported, overlaps);
    }

    private static void AddIfAny(List<string> lines, string? text) { if (text is not null) lines.Add(text); }

    private static long Millis(double seconds) => (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);

    /// <summary>hh:mm:ss,mmm (SRT) or hh:mm:ss.mmm (VTT, TXT). Hours may exceed two digits; they are never wrapped.</summary>
    public static string Stamp(long millis, char separator)
    {
        long h = millis / 3_600_000, m = millis / 60_000 % 60, s = millis / 1000 % 60, ms = millis % 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00}{separator}{ms:000}");
    }

    /// <summary>
    /// Normalizes text for a cue: any line ending becomes LF, control characters and BOMs go, a lone surrogate becomes U+FFFD, every line is
    /// trimmed, and blank lines are dropped (a blank line would end the cue in SRT and VTT). Returns null when nothing is left.
    /// </summary>
    internal static string? Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append(c).Append(text[++i]); continue; }
            if (char.IsSurrogate(c)) { sb.Append((char)0xFFFD); continue; }
            if (c == '\r') { sb.Append('\n'); if (i + 1 < text.Length && text[i + 1] == '\n') i++; continue; }
            if (c is '\n' or (char)0x2028 or (char)0x2029 or (char)0x85 or'\v' or '\f') { sb.Append('\n'); continue; }
            if (c == '\t') { sb.Append(' '); continue; }
            if (char.IsControl(c) || c == (char)0xFEFF) continue;
            sb.Append(c);
        }
        var lines = sb.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string Escape(string line, SubtitleFormat format) => format switch
    {
        // WebVTT: & and < start markup or entities; > is escaped too so "-->" can never appear inside a cue.
        SubtitleFormat.Vtt => line.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"),
        // SRT has no escaping; "-->" inside text confuses some parsers, so an invisible zero-width space breaks the arrow.
        SubtitleFormat.Srt => line.Replace("-->", "--" + (char)0x200B + ">"),
        _ => line,
    };
}

/// <summary>Builds a safe default file name for the save dialog.</summary>
public static class SubtitleFileName
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Make(string? displayName, SubtitleMode mode, SubtitleFormat format)
    {
        // Own parsing: Path.GetFileName treats "a:" as a drive and would drop the start of a name such as "a:b.mp4".
        string name = displayName?.Replace('/', '\\').Split('\\').Last() ?? "";
        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in name) sb.Append(char.IsControl(c) || Array.IndexOf(invalid, c) >= 0 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : c);
        string stem = sb.ToString();
        if (stem.Length > 80) { stem = stem[..80]; if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1]; }
        stem = stem.Trim().TrimEnd('.', ' ');
        if (stem.Length == 0) stem = "subtitles";
        // "CON.txt" is also reserved: check the part before the first dot.
        if (Reserved.Contains(stem.Split('.')[0].TrimEnd(' '))) stem = "_" + stem;
        string suffix = mode switch
        {
            SubtitleMode.Original => "original",
            SubtitleMode.Translation => "translation",
            SubtitleMode.BilingualOriginalFirst => "bilingual",
            _ => "bilingual-translation-first",
        };
        return $"{stem}.{suffix}.{format.ToString().ToLowerInvariant()}";
    }
}

/// <summary>Outcome of an export. <see cref="Path"/> is set only when a file was written.</summary>
public sealed record SubtitleExportResult(bool Ok, string? Error, bool Retryable, string? Path, int Exported, int Overlaps, IReadOnlyList<SubtitleIssue> Issues)
{
    public bool Cancelled => !Ok && Error == SubtitleExportErrors.Cancelled;
}

public static class SubtitleExportErrors
{
    public const string Cancelled = "export.cancelled";
    public const string UnknownJob = "export.unknownJob";
    public const string NoPicker = "export.noPicker";
    public const string NothingToExport = "export.nothingToExport";
    public const string PathInvalid = "export.pathInvalid";
    public const string DiskFull = "export.diskFull";
    public const string WriteFailed = "export.writeFailed";
}

/// <summary>
/// Exports a result held in the <see cref="VideoResultStore"/> as SRT, VTT or TXT (F14.3). The store survives closing the transcribe
/// window (not a restart), so reopen and export need no new work. The file is written next to its target as a flushed temp file and then
/// moved over the target: a disk-full or any other failure leaves no partial file and an existing target untouched.
/// </summary>
public sealed class SubtitleExporter
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Func<string, VideoJobResult?> lookup;
    private readonly ISubtitleSavePicker? picker;
    private readonly Func<string, Stream>? createTemp;

    /// <param name="createTemp">Test seam: opens the temp file stream (disk-full injection). Production passes none.</param>
    public SubtitleExporter(Func<string, VideoJobResult?> lookup, ISubtitleSavePicker? picker = null, Func<string, Stream>? createTemp = null)
    { this.lookup = lookup; this.picker = picker; this.createTemp = createTemp; }

    public SubtitleExporter(VideoResultStore store, ISubtitleSavePicker? picker = null, Func<string, Stream>? createTemp = null) : this(store.Get, picker, createTemp) { }

    /// <summary>Asks the user where to save, then exports. A cancelled dialog is <see cref="SubtitleExportResult.Cancelled"/> and writes nothing.</summary>
    public async Task<SubtitleExportResult> ExportAsync(string jobId, SubtitleMode mode, SubtitleFormat format, CancellationToken cancellationToken)
    {
        var job = lookup(jobId);
        if (job is null) return Fail(SubtitleExportErrors.UnknownJob);
        if (picker is null) return Fail(SubtitleExportErrors.NoPicker);
        string? path = await picker.PickAsync(SubtitleFileName.Make(job.DisplayName, mode, format), format, cancellationToken);
        return path is null ? Fail(SubtitleExportErrors.Cancelled) : ExportTo(jobId, mode, format, path);
    }

    public SubtitleExportResult ExportTo(string jobId, SubtitleMode mode, SubtitleFormat format, string path)
    {
        var job = lookup(jobId);
        if (job is null) return Fail(SubtitleExportErrors.UnknownJob);
        var build = SubtitleFormatter.Build(job.Cues, job.MediaDuration, mode, format);
        if (build.Text is null) return new SubtitleExportResult(false, SubtitleExportErrors.NothingToExport, false, null, 0, 0, build.Issues);

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return Fail(SubtitleExportErrors.PathInvalid, build); }
        string? dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || Directory.Exists(full)) return Fail(SubtitleExportErrors.PathInvalid, build);

        string temp = $"{full}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = createTemp?.Invoke(temp) ?? new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(Utf8.GetBytes(build.Text));
                stream.Flush();
                if (stream is FileStream fs) fs.Flush(flushToDisk: true);
            }
            File.Move(temp, full, overwrite: true);
            return new SubtitleExportResult(true, null, false, full, build.Exported, build.Overlaps, build.Issues);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            bool full1 = e is IOException io && (io.HResult & 0xFFFF) is 0x27 or 0x70;
            return new SubtitleExportResult(false, full1 ? SubtitleExportErrors.DiskFull : SubtitleExportErrors.WriteFailed, full1, null, build.Exported, build.Overlaps, build.Issues);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static SubtitleExportResult Fail(string code, SubtitleBuild? build = null) =>
        new(false, code, false, null, 0, 0, build?.Issues ?? []);
}
