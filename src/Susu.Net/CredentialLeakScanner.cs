using System.Text;

namespace Susu.Net;

/// <summary>
/// PLAN 4.5.4.5 defense-in-depth (TEST-PLAN S10): before a response reaches the plugin, intercept the
/// *known* literal forms of a credential value the host itself just injected - raw, URL-encoded,
/// standard Base64 - if they reappear anywhere in the response body or the (already header-allow-listed)
/// response headers. This is not a security boundary: a server that transforms the value in any other
/// way (re-encodes, truncates, hashes, wraps in its own JSON) is not caught, and PLAN says so
/// explicitly - the credential's receiving party is inside the trust boundary the user already accepted
/// by configuring the service. This only catches the sloppy/accidental case (an API that echoes back
/// the Authorization header it received, or embeds the raw key in a debug field).
/// </summary>
public static class CredentialLeakScanner
{
    /// <summary>Minimum value length considered before scanning - short values (a few characters) would
    /// produce false positives against ordinary response content and are not useful to flag.</summary>
    private const int MinLength = 6;

    public static bool ContainsKnownForm(BrokerHttpResponse response, IReadOnlyCollection<string> secretValues)
    {
        var forms = BuildForms(secretValues);
        if (forms.Count == 0) return false;
        if (response.TextBody is { } text && ContainsAny(text, forms)) return true;
        if (response.JsonBody is not null && ContainsAny(response.JsonBody.ToJsonString(), forms)) return true;
        foreach (var value in response.Headers.Values) if (ContainsAny(value, forms)) return true;
        return false;
    }

    /// <summary>Same check for a value a caller is about to write to a log line (S10 "日志无认证...").</summary>
    public static bool ContainsKnownForm(string text, IReadOnlyCollection<string> secretValues)
        => ContainsAny(text, BuildForms(secretValues));

    private static List<string> BuildForms(IReadOnlyCollection<string> secretValues)
    {
        var forms = new List<string>();
        foreach (string value in secretValues)
        {
            if (value.Length < MinLength) continue;
            forms.Add(value);
            string urlEncoded = Uri.EscapeDataString(value);
            if (urlEncoded != value) forms.Add(urlEncoded);
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            forms.Add(base64);
        }
        return forms;
    }

    private static bool ContainsAny(string haystack, List<string> forms)
    {
        foreach (var form in forms) if (haystack.Contains(form, StringComparison.Ordinal)) return true;
        return false;
    }
}
