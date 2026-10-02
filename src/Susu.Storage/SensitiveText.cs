using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace Susu.Storage;

/// <summary>
/// Pattern scrubbing for free text that may reach a log or a diagnostics file (F17.2, PLAN 4.5.4 item 5, TEST-PLAN S10). It removes credentials
/// by shape (Authorization and cookie headers, bearer/basic tokens, key=value secrets, JWTs, common key prefixes, URL user-info and query strings),
/// then identity values (e-mail addresses, IP addresses, MAC addresses). <see cref="Strong"/> additionally removes file paths, media file names, GUIDs
/// (machine ids) and long opaque blobs; it is for text that is free-form by nature (plugin messages, error text). Patterns are defense in depth:
/// the diagnostics export does not rely on them, it writes only allow-listed fields (<see cref="DiagnosticsExporter"/>). Arbitrary transformations
/// of a secret (a server that echoes it re-encoded in an unknown way) are outside what any pattern can promise (TEST-PLAN S10).
/// </summary>
public static partial class SensitiveText
{
    public const string Mask = "[redacted]";

    /// <summary>Credentials, URL query strings, e-mail and IP addresses. Leaves ids (GUID request ids, hashes) alone so log lines stay correlatable.</summary>
    public static string Standard(string text)
    {
        if (text.Length == 0) return text;
        text = UrlUserInfo().Replace(text, "$1" + Mask + "@");
        text = UrlQuery().Replace(text, m => m.Groups[1].Value);
        text = HeaderLine().Replace(text, "$1: " + Mask);
        text = BearerToken().Replace(text, "$1 " + Mask);
        text = KeyValue().Replace(text, "$1$2" + Mask);
        text = Jwt().Replace(text, Mask);
        text = KnownPrefix().Replace(text, Mask);
        text = Email().Replace(text, "[email]");
        text = Ipv4().Replace(text, "[ip]");
        text = Ipv6Full().Replace(text, "[ip]");
        text = Ipv6Short().Replace(text, "[ip]");
        text = Mac().Replace(text, "[mac]");
        return text;
    }

    /// <summary><see cref="Standard"/> plus paths, media and document file names, GUIDs and long opaque tokens.</summary>
    public static string Strong(string text)
    {
        text = Standard(text);
        text = WindowsPath().Replace(text, "[path]");
        text = UncPath().Replace(text, "[path]");
        text = FileUri().Replace(text, "[path]");
        text = FileName().Replace(text, "[file]");
        text = Guid().Replace(text, "[id]");
        text = Blob().Replace(text, "[blob]");
        return text;
    }

    [GeneratedRegex(@"\b((?:https?|wss?|ftp)://)[^/\s:@""']+(?::[^/\s@""']*)?@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlUserInfo();

    [GeneratedRegex(@"\b((?:https?|wss?)://[^\s?#""']+)[?#][^\s""']*", RegexOptions.IgnoreCase)]
    private static partial Regex UrlQuery();

    [GeneratedRegex(@"\b(authorization|proxy-authorization|cookie|set-cookie|x-api-key|api-key|x-auth-token|x-access-token|x-amz-security-token|x-goog-api-key)\s*[:=]\s*[^\r\n""]*", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderLine();

    [GeneratedRegex(@"\b(bearer|basic|digest|negotiate|ntlm)\s+[A-Za-z0-9._~+/=\-]{6,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"([\w.\-]*(?:api[_\-]?key|apikey|secret|token|passw(?:or)?d|pwd|passphrase|credential|signature|access[_\-]?key|private[_\-]?key|session(?:id)?|sid|auth|cookie|bearer)[\w.\-]*)([""']?\s*[:=]\s*[""']?)[^\s""'&,;}\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]*")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(?:sk|pk|rk)-[A-Za-z0-9_\-]{12,}|\bxox[abprs]-[A-Za-z0-9\-]{10,}|\bgh[pousr]_[A-Za-z0-9]{20,}|\bgithub_pat_[A-Za-z0-9_]{20,}|\bAKIA[0-9A-Z]{12,}|\bAIza[0-9A-Za-z_\-]{20,}")]
    private static partial Regex KnownPrefix();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![\w.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\w.]*\w)")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"(?<![\w:])(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}(?![\w:])")]
    private static partial Regex Ipv6Full();

    [GeneratedRegex(@"(?<![\w:])(?:(?:[0-9A-Fa-f]{1,4}:){1,7}:(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,6})?|::[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,6})(?![\w:])")]
    private static partial Regex Ipv6Short();

    [GeneratedRegex(@"(?<![\w:\-])(?:[0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}(?![\w:\-])")]
    private static partial Regex Mac();

    [GeneratedRegex(@"[A-Za-z]:(?:\\{1,2}|/)[^\s""'<>|*?]*")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"(?:\\\\|//)[A-Za-z0-9_.$\-]+(?:\\{1,2}|/)[^\s""'<>|*?]*")]
    private static partial Regex UncPath();

    [GeneratedRegex(@"file:/{2,3}[^\s""'<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex FileUri();

    [GeneratedRegex(@"[^\s""'\\/:*?<>|]+\.(?:mp3|mp4|m4a|m4v|wav|flac|ogg|opus|webm|mkv|avi|mov|aac|wma|png|jpe?g|bmp|gif|webp|tiff?|susubak|txt|docx?|pdf|srt|vtt|csv|apkg|xlsx?|zip)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileName();

    [GeneratedRegex(@"\{?\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b\}?")]
    private static partial Regex Guid();

    [GeneratedRegex(@"[A-Za-z0-9+/_\-]{32,}={0,2}")]
    private static partial Regex Blob();
}

/// <summary>
/// Literal values that must never appear in a log or a diagnostics file: saved keys and the proxy password, plus identities (user and machine names,
/// the profile folder, account labels, file names). Every value is matched in its raw form and in the forms an encoder could have produced from it:
/// URL percent encoding (also doubled and with <c>+</c> for spaces), JSON string escapes, Base64 at all three byte alignments (standard and URL-safe, so
/// a value inside a longer Base64 run is found), hex, and ignoring case. Values shorter than 4 characters are not registered (they would mask ordinary text).
/// </summary>
public sealed class SensitiveLiterals
{
    public const int MinLength = 4;
    private readonly object gate = new();
    private readonly HashSet<string> values = new(StringComparer.Ordinal);
    private List<string> forms = [];

    public int Count { get { lock (gate) return values.Count; } }

    public void Add(string? value)
    {
        if (value is null || value.Trim().Length < MinLength) return;
        lock (gate)
        {
            if (!values.Add(value)) return;
            var set = new HashSet<string>(forms, StringComparer.Ordinal);
            foreach (var form in FormsOf(value)) set.Add(form);
            forms = [.. set.OrderByDescending(f => f.Length).ThenBy(f => f, StringComparer.Ordinal)];
        }
    }

    public void AddRange(IEnumerable<string?> items) { foreach (var item in items) Add(item); }

    /// <summary>Replaces every known form with <see cref="SensitiveText.Mask"/>.</summary>
    public string Mask(string text)
    {
        string[] snapshot;
        lock (gate) snapshot = [.. forms];
        foreach (var form in snapshot) text = text.Replace(form, SensitiveText.Mask, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>True when any registered value occurs in <paramref name="text"/> in any known form.</summary>
    public bool Contains(string text)
    {
        string[] snapshot;
        lock (gate) snapshot = [.. forms];
        foreach (var form in snapshot) if (text.Contains(form, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static IEnumerable<string> FormsOf(string value)
    {
        yield return value;
        string escaped = Uri.EscapeDataString(value);
        yield return escaped;
        yield return Uri.EscapeDataString(escaped);
        if (value.Contains(' ')) yield return escaped.Replace("%20", "+", StringComparison.Ordinal);
        yield return JavaScriptEncoder.Default.Encode(value);
        yield return JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(value);
        yield return value.Replace("\\", "\\\\", StringComparison.Ordinal);
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        yield return Convert.ToHexStringLower(utf8);
        yield return Convert.ToHexString(utf8);
        yield return Convert.ToHexStringLower(Encoding.Unicode.GetBytes(value));
        foreach (var piece in Base64Pieces(utf8))
        {
            yield return piece;
            yield return piece.Replace('+', '-').Replace('/', '_');
            yield return Uri.EscapeDataString(piece); // Base64 inside a query string
        }
        foreach (var piece in Base64Pieces(Encoding.Unicode.GetBytes(value))) yield return piece;
    }

    /// <summary>The Base64 characters that do not depend on neighbouring bytes, for the value at each of the three alignments.</summary>
    private static IEnumerable<string> Base64Pieces(byte[] bytes)
    {
        for (int pad = 0; pad < 3; pad++)
        {
            var buffer = new byte[pad + bytes.Length];
            bytes.CopyTo(buffer, pad);
            string b64 = Convert.ToBase64String(buffer).TrimEnd('=');
            int skip = pad == 0 ? 0 : (pad * 8 + 5) / 6; // chars that include bytes in front of the value
            int stable = buffer.Length * 8 / 6;          // chars fully determined by the bytes up to the value's end
            int take = Math.Min(stable, b64.Length) - skip;
            if (take >= 6) yield return b64.Substring(skip, take);
        }
    }
}

/// <summary>
/// Keeps the log's masking set current (F17.2): every saved key and the proxy password (now and whenever a value is stored), and the identities the
/// machine and the settings carry (user name, machine name, profile folder, account labels, proxy host and user name, host-supplied ids such as the machine GUID).
/// </summary>
public static class SensitiveRegistry
{
    public static void Attach(SensitiveLiterals literals, SecretStore secrets, Abstractions.ISettingsStore settings, IEnumerable<string?> hostIdentities)
    {
        literals.AddRange(hostIdentities);
        literals.Add(Environment.UserName);
        literals.Add(Environment.MachineName);
        literals.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        foreach (var (account, name) in secrets.Entries())
            if (secrets.TryRead(account, name, out var value)) literals.Add(value);
        secrets.ValueStored += literals.Add;
        FromSettings(literals, settings.State.Effective);
        settings.Changed += state => FromSettings(literals, state.Effective);
    }

    public static void FromSettings(SensitiveLiterals literals, Domain.AppSettings s)
    {
        // A label that is just the service id (the default) is not personal; a name the user typed is.
        var serviceIds = s.Instances.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var account in s.Accounts) if (!serviceIds.Contains(account.Label) && account.Label != account.Id) literals.Add(account.Label);
        literals.Add(s.Network.ProxyHost);
        literals.Add(s.Network.ProxyUsername);
    }
}
