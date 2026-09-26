using Susu.Contracts;

namespace Susu.Domain;

/// <summary>
/// Dictionary result rules (PLAN 4.4 <c>DictionaryResult</c>, 6.1). A dictionary call ends in exactly one of:
/// an error (the plugin threw a <c>PluginError</c>, surfaced as a failed call), a legal empty entry (a successful
/// result with nothing to show - the vendor has no entry, e.g. Youdao errorCode 120), or an entry. Only a legal
/// empty entry falls back to the plain translation card; an error never does (DICT01).
/// </summary>
public static class DictionaryEntries
{
    /// <summary>A successful result with no phonetics, parts of speech, word forms or examples.</summary>
    public static bool IsEmpty(DictionaryResult result)
        => (result.Phonetics?.Length ?? 0) == 0 && (result.Parts?.Length ?? 0) == 0 && (result.Forms?.Length ?? 0) == 0 && (result.Examples?.Length ?? 0) == 0;

    /// <summary>
    /// The UI card body for a plugin entry (F09.2): the same structured fields as plain strings, with items that
    /// lack their text dropped, and each audio link replaced by the opaque id <paramref name="registerAudio"/> returns
    /// for it. Only absolute https links are offered for registration; anything else is dropped, so a raw URL never
    /// reaches the page.
    /// </summary>
    public static DictionaryEntryView ToView(DictionaryResult result, Func<string, string> registerAudio)
    {
        static bool Has(string? s) => !string.IsNullOrWhiteSpace(s);
        string? Audio(string? url)
            => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? registerAudio(uri.AbsoluteUri) : null;
        return new DictionaryEntryView(
            result.Word ?? "",
            [.. (result.Phonetics ?? []).Where(p => p is not null && Has(p.Ipa)).Select(p => new DictionaryPhoneticView(p.Accent ?? "", p.Ipa, Audio(p.AudioUrl)))],
            [.. (result.Parts ?? []).Where(p => p is not null).Select(p => new DictionaryPartView(p.Pos ?? "", [.. (p.Means ?? []).Where(Has)])).Where(p => p.Means.Length > 0)],
            [.. (result.Forms ?? []).Where(f => f is not null && Has(f.Value)).Select(f => new DictionaryFormView(f.Name ?? "", f.Value))],
            [.. (result.Examples ?? []).Where(e => e is not null && Has(e.Src)).Select(e => new DictionaryExampleView(e.Src, e.Dst ?? ""))]);
    }

    /// <summary>True when the view has nothing to show (every item was dropped); treated like a legal empty entry.</summary>
    public static bool IsEmpty(DictionaryEntryView view) => view.Phonetics.Length == 0 && view.Parts.Length == 0 && view.Forms.Length == 0 && view.Examples.Length == 0;
}
