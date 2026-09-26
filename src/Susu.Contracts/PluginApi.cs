using System.Text.Json;
using System.Text.Json.Serialization;

namespace Susu.Contracts;

/// <summary>Plugin capabilities (PLAN 4.7). Wire names are lowercase.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Capability>))]
public enum Capability
{
    [JsonStringEnumMemberName("translate")] Translate,
    [JsonStringEnumMemberName("dictionary")] Dictionary,
    [JsonStringEnumMemberName("detect")] Detect,
    [JsonStringEnumMemberName("ocr")] Ocr,
    [JsonStringEnumMemberName("tts")] Tts,
    [JsonStringEnumMemberName("asr")] Asr,
    [JsonStringEnumMemberName("vocab")] Vocab,
}

// ---- Plugin API v1 candidate (PLAN 4.4, ARCHITECTURE 3.1 / 8.3). Frozen only at G1. ----

/// <summary><c>From</c>/<c>To</c> are canonical BCP-47 codes (Susu.Domain.Languages) the host has already
/// resolved (detected or user-selected); a plugin that ignores them falls back to its own default pair.
/// <c>Prompt</c> (F07.3, AI services only): the complete instruction the host rendered from SetPrompt, with the
/// text already inserted once. A plugin sends it as is and never templates it again; absent means the plugin's
/// own default instruction applies.</summary>
[TsExport("plugin")] public sealed record TranslateRequest(string Text, string? From = null, string? To = null, string? Prompt = null);
[TsExport("plugin")] public sealed record TranslateChunk(string Text, bool? Done = null);
[TsExport("plugin")] public sealed record TranslateResult(string Text, string? DetectedFrom = null, JsonElement? Raw = null);
[TsExport("plugin")] public sealed record BatchItem(string Id, string Text);
[TsExport("plugin")] public sealed record TranslateBatchRequest(BatchItem[] Items);
[TsExport("plugin")] public sealed record TranslateBatchResult(BatchItem[] Items);

[TsExport("plugin")] public sealed record Phonetic(string Accent, string Ipa, string? AudioUrl = null);
[TsExport("plugin")] public sealed record PartOfSpeech(string Pos, string[] Means);
[TsExport("plugin")] public sealed record WordForm(string Name, string Value);
[TsExport("plugin")] public sealed record Example(string Src, string Dst);
[TsExport("plugin")] public sealed record DictionaryResult(string Word, Phonetic[] Phonetics, PartOfSpeech[] Parts, WordForm[]? Forms = null, Example[]? Examples = null);

[TsExport("plugin")] public sealed record DetectCandidate(string Lang, double Confidence);

/// <summary>One recognized block (PLAN 4.4 <c>OcrResult</c>). <c>Box</c> is <c>[x, y, w, h]</c> normalized to [0, 1] of the
/// image. F11.2 adds two optional fields: <c>Kind</c> "text" (default) or "formula" (then <c>Text</c> is LaTeX source, kept
/// as is and copyable), and <c>Confidence</c> in [0, 1].</summary>
[TsExport("plugin")] public sealed record OcrBlock(string Text, double[]? Box = null, string? Kind = null, double? Confidence = null);
[TsExport("plugin")] public sealed record OcrResult(OcrBlock[] Blocks);

/// <summary>The request of the <c>ocr</c> capability (PLAN 4.4 <c>ocr(req: { image: FileHandle; lang?: string })</c>).</summary>
[TsExport("plugin")] public sealed record OcrRequest(FileHandleInfo Image, string? Lang = null);

[TsExport("plugin")] public sealed record AsrSegment(double Start, double End, string Text);
/// <summary><c>kind</c> is "text" (Text set) or "segments" (Segments set); must match the requested output.</summary>
[TsExport("plugin")] public sealed record AsrResult(string Kind, string? Text = null, AsrSegment[]? Segments = null);

/// <summary>The plugin-visible shape of a $file reference (PLAN 4.5/4.5.1): a host-issued opaque id
/// plus read-only metadata, never a path or the raw bytes. <c>Width</c>/<c>Height</c> (F11.2): pixel size of an image
/// input, so a plugin can normalize vendor pixel boxes. The metadata is informational: the broker always sends the leased
/// file's real bytes, whatever a plugin claims about them (B06).</summary>
[TsExport("plugin")] public sealed record FileHandleInfo(string Id, string Mime, long Bytes, double? DurationMs = null, int? Width = null, int? Height = null);
[TsExport("plugin")] public sealed record TtsResult(FileHandleInfo Audio);

[TsExport("plugin")] public sealed record OptionsRequest(string Field, long DependsOnRevision, string? Cursor = null);
[TsExport("plugin")] public sealed record OptionItem(string Value, string Label);
[TsExport("plugin")] public sealed record OptionsResult(OptionItem[] Items, string? NextCursor = null);

[TsExport("plugin")] public sealed record VocabRequest(string OperationId, string Action, long EntryRevision, string Word, string Lang, JsonElement? Content = null);
/// <summary><c>status</c>: applied | found | absent | unknown.</summary>
[TsExport("plugin")] public sealed record VocabResult(string Status, string? RemoteId = null);

[TsExport("plugin")] public sealed record Voice(string Id, string Name, string Lang);

/// <summary>Validation of plugin results the host must enforce before use (PLAN 4.7.2, 4.7.1).</summary>
public static class PluginResultValidation
{
    /// <summary>ASR segments: finite, 0 ≤ start &lt; end ≤ duration, non-decreasing start, non-empty text. Overlap allowed.</summary>
    public static ProviderError? ValidateSegments(AsrResult result, string requestedOutput, double durationSeconds)
    {
        if (result.Kind != requestedOutput) return new ProviderError(ErrorKind.BadResponse, $"asr kind '{result.Kind}' does not match requested '{requestedOutput}'");
        if (result.Kind == "text") return string.IsNullOrWhiteSpace(result.Text) ? new ProviderError(ErrorKind.BadResponse, "empty text") : null;
        if (result.Kind != "segments" || result.Segments is null) return new ProviderError(ErrorKind.BadResponse, "segments missing");
        double previousStart = 0;
        foreach (var s in result.Segments)
        {
            if (!double.IsFinite(s.Start) || !double.IsFinite(s.End)) return new ProviderError(ErrorKind.BadResponse, "non-finite time");
            if (s.Start < 0 || s.End <= s.Start || s.End > durationSeconds) return new ProviderError(ErrorKind.BadResponse, "time out of range");
            if (s.Start < previousStart) return new ProviderError(ErrorKind.BadResponse, "segments not ordered by start");
            if (string.IsNullOrWhiteSpace(s.Text)) return new ProviderError(ErrorKind.BadResponse, "empty segment text");
            previousStart = s.Start;
        }
        return null;
    }

    /// <summary>OCR result limits (F11.2): at most this many blocks, and this many characters of text in total (the same
    /// cap as a submitted source text, so the result can always enter the translation pipeline).</summary>
    public const int MaxOcrBlocks = 5000, MaxOcrChars = 100_000;

    /// <summary>
    /// OCR blocks (F11.2): a text per block, <c>kind</c> absent/"text"/"formula", <c>confidence</c> finite in [0, 1], and a box
    /// of four finite numbers inside the unit square. A result with no non-blank text is valid here; the caller reports it as
    /// "no text recognized", never as an empty success.
    /// </summary>
    public static ProviderError? ValidateOcr(OcrResult? result)
    {
        if (result?.Blocks is null) return new ProviderError(ErrorKind.BadResponse, "blocks missing");
        if (result.Blocks.Length > MaxOcrBlocks) return new ProviderError(ErrorKind.BadResponse, $"too many blocks ({result.Blocks.Length})");
        long chars = 0;
        const double Slack = 1e-6;
        foreach (var block in result.Blocks)
        {
            if (block?.Text is null) return new ProviderError(ErrorKind.BadResponse, "block text missing");
            if (block.Kind is not (null or "text" or "formula")) return new ProviderError(ErrorKind.BadResponse, $"unknown block kind '{block.Kind}'");
            if (block.Confidence is { } c && (!double.IsFinite(c) || c < 0 || c > 1)) return new ProviderError(ErrorKind.BadResponse, "confidence out of range");
            if (block.Box is { } box)
            {
                if (box.Length != 4 || box.Any(v => !double.IsFinite(v) || v < 0)) return new ProviderError(ErrorKind.BadResponse, "box is not [x, y, w, h] in [0, 1]");
                if (box[0] + box[2] > 1 + Slack || box[1] + box[3] > 1 + Slack) return new ProviderError(ErrorKind.BadResponse, "box outside the image");
            }
            chars += block.Text.Length;
        }
        return chars > MaxOcrChars ? new ProviderError(ErrorKind.BadResponse, $"recognized text too long ({chars} characters)") : null;
    }
}

/// <summary>Marks a contract type for TypeScript generation (tools/Susu.ContractsGen). Group selects the output file.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Enum | AttributeTargets.Struct, Inherited = false)]
public sealed class TsExportAttribute(string group) : Attribute
{
    public string Group { get; } = group;
}
