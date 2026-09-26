using System.Globalization;
using System.Text;

namespace Susu.Domain;

/// <summary>What kind of text a source is, for the dictionary-card decision (PLAN 6.1, TEST-PLAN DICT01).</summary>
public enum TextForm
{
    /// <summary>Nothing but whitespace.</summary>
    Empty,
    /// <summary>PLAN 6.1 Latin word form: only letters, hyphens and apostrophes, no whitespace, at most 32 characters.</summary>
    LatinWord,
    /// <summary>PLAN 6.1 Chinese word form: only Han characters, at most 4.</summary>
    ChineseWord,
    /// <summary>Letters of one script with no punctuation or digits that is not a word form: several Latin words
    /// (at most <see cref="TextForms.MaxPhraseWords"/>) or a longer Han run (at most <see cref="TextForms.MaxPhraseHan"/>).</summary>
    Phrase,
    /// <summary>Several words ending in sentence punctuation, or letters-only text longer than a phrase.</summary>
    Sentence,
    /// <summary>Contains punctuation or symbols (other than a word form's hyphens/apostrophes) but is not a sentence, e.g. "hello," or "好！".</summary>
    Punctuated,
    /// <summary>Latin letters and Han characters together.</summary>
    Mixed,
    /// <summary>Digits, other scripts (kana, Cyrillic, ...), or a Latin run too long to be a word.</summary>
    Other,
}

/// <summary>
/// PLAN 6.1 "词典形态触发": after trimming, only a Latin word form (letters, hyphen, apostrophe; no whitespace;
/// ≤ 32 characters) or a Chinese word form (Han only, ≤ 4 characters) may use the dictionary card. Digits,
/// punctuation, whitespace or mixed scripts never trigger it, and nothing triggers without a configured
/// dictionary service. The finer non-word forms (phrase/sentence/punctuated) are labels only; they never
/// change the decision.
/// </summary>
public static class TextForms
{
    public const int MaxLatinWord = 32, MaxChineseWord = 4, MaxPhraseWords = 6, MaxPhraseHan = 8;

    public static TextForm Classify(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return TextForm.Empty;

        int latin = 0, han = 0, joiners = 0, spaces = 0, punctuation = 0, digits = 0, other = 0, scalars = 0;
        foreach (Rune rune in t.EnumerateRunes())
        {
            scalars++;
            if (IsLatinLetter(rune)) latin++;
            else if (IsHan(rune)) han++;
            else if (IsWordJoiner(rune)) joiners++;
            else if (Rune.IsWhiteSpace(rune)) spaces++;
            else if (Rune.IsDigit(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber) digits++;
            else if (Rune.IsPunctuation(rune) || Rune.IsSymbol(rune)) punctuation++;
            else other++;
        }

        if (latin > 0 && han > 0) return TextForm.Mixed;
        if (digits > 0 || other > 0) return TextForm.Other;
        if (latin == 0 && han == 0) return TextForm.Punctuated; // punctuation/symbols/joiners (and spaces) only

        if (latin > 0 && spaces == 0 && punctuation == 0)
            return scalars <= MaxLatinWord ? TextForm.LatinWord : TextForm.Other;
        if (han > 0 && spaces == 0 && punctuation == 0 && joiners == 0)
            return han <= MaxChineseWord ? TextForm.ChineseWord : han <= MaxPhraseHan ? TextForm.Phrase : TextForm.Sentence;

        int units = latin > 0 ? t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length : han;
        if (punctuation > 0 || (han > 0 && joiners > 0))
            return units >= 2 && EndsWithSentencePunctuation(t) ? TextForm.Sentence : TextForm.Punctuated;
        // Letters (plus word joiners) separated by whitespace only.
        return latin > 0
            ? (units <= MaxPhraseWords ? TextForm.Phrase : TextForm.Sentence)
            : (han <= MaxPhraseHan ? TextForm.Phrase : TextForm.Sentence);
    }

    public static bool IsWordForm(TextForm form) => form is TextForm.LatinWord or TextForm.ChineseWord;

    /// <summary>Whether an expanded card for <paramref name="text"/> may query the dictionary (PLAN 6.1): a word form
    /// and at least one configured dictionary service. Only Youdao is a dictionary source; there is no fallback.</summary>
    public static bool UsesDictionary(string? text, bool dictionaryConfigured) => dictionaryConfigured && IsWordForm(Classify(text));

    /// <summary>The trimmed word to look up, or null when <paramref name="text"/> is not a word form.</summary>
    public static string? DictionaryWord(string? text) => IsWordForm(Classify(text)) ? text!.Trim() : null;

    // Latin-script letters: ASCII, Latin-1 Supplement letters (not × ÷), Latin Extended-A/B, IPA-free
    // Extended Additional (Vietnamese etc.).
    private static bool IsLatinLetter(Rune r)
    {
        int v = r.Value;
        return v is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            || (v is >= 0xC0 and <= 0x24F && v != 0xD7 && v != 0xF7)
            || v is >= 0x1E00 and <= 0x1EFF;
    }

    // Same ranges as Languages.Guess (CJK Unified Ideographs, Ext A, Ext B-F, Compatibility Ideographs).
    private static bool IsHan(Rune r)
        => r.Value is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2EBEF or >= 0xF900 and <= 0xFAFF;

    // PLAN 6.1 allows hyphens and apostrophes inside a Latin word form ("well-known", "don't", "rock’n’roll").
    private static bool IsWordJoiner(Rune r) => r.Value is '-' or '\'' or 0x2010 or 0x2011 or 0x2019;

    private static bool EndsWithSentencePunctuation(string t)
    {
        string end = t.TrimEnd('"', '\'', ')', '”', '’', '」', '』', '）');
        return end.Length > 0 && end[^1] is '.' or '!' or '?' or '。' or '！' or '？' or '…';
    }
}
