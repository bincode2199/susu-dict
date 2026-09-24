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

[TsExport("plugin")] public sealed record TranslateRequest(string Text);
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

[TsExport("plugin")] public sealed record OcrBlock(string Text, double[]? Box = null);
[TsExport("plugin")] public sealed record OcrResult(OcrBlock[] Blocks);

[TsExport("plugin")] public sealed record AsrSegment(double Start, double End, string Text);
/// <summary><c>kind</c> is "text" (Text set) or "segments" (Segments set); must match the requested output.</summary>
[TsExport("plugin")] public sealed record AsrResult(string Kind, string? Text = null, AsrSegment[]? Segments = null);

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
}

/// <summary>Marks a contract type for TypeScript generation (tools/Susu.ContractsGen). Group selects the output file.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Enum | AttributeTargets.Struct, Inherited = false)]
public sealed class TsExportAttribute(string group) : Attribute
{
    public string Group { get; } = group;
}
