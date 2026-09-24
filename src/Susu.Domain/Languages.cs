namespace Susu.Domain;

/// <summary>Canonical BCP-47 language entry (PLAN 4.10): the host only speaks canonical codes.</summary>
public sealed record Language(string Code, string ChineseName, string EnglishName, string NativeName);

/// <summary>
/// The host language table. First release contains zh-Hans and en; the structure is extensible and
/// plugins map canonical codes to their own through their manifest.
/// </summary>
public static class Languages
{
    public static readonly Language ChineseSimplified = new("zh-Hans", "简体中文", "Chinese (Simplified)", "简体中文");
    public static readonly Language English = new("en", "英语", "English", "English");

    private static readonly Dictionary<string, Language> table = new(StringComparer.Ordinal)
    {
        [ChineseSimplified.Code] = ChineseSimplified,
        [English.Code] = English,
    };

    public static IReadOnlyCollection<Language> All => table.Values;

    public static bool IsCanonical(string? code) => code is not null && table.ContainsKey(code);

    public static Language Get(string code) => table.TryGetValue(code, out var language) ? language : throw new ArgumentException($"'{code}' is not a canonical language code.", nameof(code));

    /// <summary>
    /// Maps a detector result (ELS names like "zh-Hans", "en", "en-US") to a canonical code, or null.
    /// Region subtags of a supported language collapse to it; Traditional Chinese is not zh-Hans.
    /// </summary>
    public static string? FromDetector(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (table.ContainsKey(name)) return name;
        if (name.StartsWith("en-", StringComparison.OrdinalIgnoreCase) || name.Equals("en", StringComparison.OrdinalIgnoreCase)) return English.Code;
        if (name.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) || name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) || name.Equals("zh-SG", StringComparison.OrdinalIgnoreCase)) return ChineseSimplified.Code;
        return null;
    }

    /// <summary>Default target for a detected source in the zh↔en first release.</summary>
    public static string OppositeOf(string source) => source == English.Code ? ChineseSimplified.Code : English.Code;
}

/// <summary>Unicode script fast path (PLAN 3.2 step 1).</summary>
public static class ScriptDetector
{
    /// <summary>Only Han → zh-Hans; only Latin letters → en; mixed or neither → null (use the detection service).</summary>
    public static string? Detect(string text)
    {
        bool han = false, latin = false;
        foreach (var rune in text.EnumerateRunes())
        {
            int v = rune.Value;
            if (v is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2EBEF or >= 0xF900 and <= 0xFAFF) han = true;
            else if (v is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 0xC0 and <= 0x24F) latin = true;
        }
        return han && !latin ? Languages.ChineseSimplified.Code : latin && !han ? Languages.English.Code : null;
    }
}
