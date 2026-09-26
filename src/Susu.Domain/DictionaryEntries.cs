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
}
