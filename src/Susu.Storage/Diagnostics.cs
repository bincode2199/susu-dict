using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Susu.Abstractions;

namespace Susu.Storage;

/// <summary>Typed facts for <c>diagnostics.json</c>. Only these numbers and shape-checked strings are written; no setting text, label, address or path.</summary>
public sealed record DiagnosticsInfo(string AppVersion, string Build, string Os, string Runtime, string UiLanguage, string ProxyMode,
    int Accounts, int SavedKeys, int Instances, int EnabledServices, int Favorites, long DatabaseBytes);

/// <summary>
/// The diagnostics file (F17.2, TEST-PLAN S10, ARCHITECTURE 8.4): a zip of <c>diagnostics.json</c>, a README and the rotated logs. It is built by allow-listing,
/// not by cleaning: a log line is parsed and only whitelisted fields are copied, each checked for the shape of an identifier, a number or a bare host; free
/// text (plugin messages, errors), URL paths and queries, unknown fields and anything that does not parse as one JSON object are dropped, and the drop is
/// counted. Then a fail-closed check: if any registered secret or identity (in the forms <see cref="SensitiveLiterals"/> knows) or any credential-shaped
/// text (<see cref="SensitiveText.Standard"/>) survives in a line, the line is dropped; if one is found in a finished entry, nothing is written.
/// </summary>
public sealed class DiagnosticsExporter(AppPaths paths, IClock clock, SensitiveLiterals literals, Func<DiagnosticsInfo> info)
{
    public const long MaxLogBytes = 4 << 20;
    public const int MaxLineChars = 16 << 10;

    private static readonly HashSet<string> NumberKeys = new(StringComparer.Ordinal) { "attempt", "status", "durationMs", "bytes", "count", "dropped" };
    private static readonly HashSet<string> IdentifierKeys = new(StringComparer.Ordinal)
        { "requestId", "jobId", "service", "capability", "window", "status", "errorKind", "code", "phase", "level", "attempt" };

    private static readonly Regex Identifier = new(@"^[A-Za-z0-9][A-Za-z0-9._:/\-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EventName = new(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Hex = new(@"^[0-9A-Fa-f\-]{32,36}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PlainText = new(@"^[0-9A-Za-z .()+_\-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Host = new(@"^[A-Za-z0-9]([A-Za-z0-9\-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9\-]*[A-Za-z0-9])?)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] PrivateSuffixes = [".local", ".lan", ".internal", ".home", ".corp", ".intranet", ".localdomain", ".home.arpa"];
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default };

    public DiagnosticsOutcome Export(string path)
    {
        try
        {
            var (logs, files, kept, dropped) = CollectLogs();
            byte[] zip = BuildZip(logs, files, kept, dropped);
            string temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temp, zip);
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return new DiagnosticsOutcome(true, null, files, kept, dropped, zip.Length);
        }
        catch (DiagnosticsLeakException) { return new DiagnosticsOutcome(false, "leak-detected", 0, 0, 0, 0); }
        catch (IOException e) when ((e.HResult & 0xFFFF) is 39 or 112) { return new DiagnosticsOutcome(false, "disk-full", 0, 0, 0, 0); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return new DiagnosticsOutcome(false, "write-failed", 0, 0, 0, 0); }
    }

    private (List<(string Name, string Text)> Logs, int Files, int Kept, int Dropped) CollectLogs()
    {
        var result = new List<(string, string)>();
        int files = 0, kept = 0, dropped = 0;
        if (!Directory.Exists(paths.Logs)) return (result, 0, 0, 0);
        long budget = MaxLogBytes;
        foreach (var file in new DirectoryInfo(paths.Logs).GetFiles("susu-*.jsonl").OrderByDescending(f => f.Name, StringComparer.Ordinal))
        {
            if (budget <= 0) break;
            string[] lines;
            try { lines = File.ReadAllLines(file.FullName, Encoding.UTF8); }
            catch (IOException) { continue; }
            var accepted = new List<string>();
            for (int i = lines.Length - 1; i >= 0; i--) // newest lines first so the budget keeps the end of the day
            {
                if (lines[i].Length == 0) continue;
                var line = FilterLine(lines[i]);
                if (line is null) { dropped++; continue; }
                if (budget - line.Length - 1 < 0) { budget = 0; break; }
                budget -= line.Length + 1;
                accepted.Add(line);
            }
            if (accepted.Count == 0) continue;
            accepted.Reverse();
            kept += accepted.Count;
            files++;
            result.Add((file.Name, string.Join('\n', accepted) + "\n"));
        }
        result.Reverse();
        return (result, files, kept, dropped);
    }

    /// <summary>Returns the sanitized line, or null when it cannot be made safe. Public for tests.</summary>
    public string? FilterLine(string line)
    {
        if (line.Length > MaxLineChars) return null;
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, WriterOptions))
            {
                json.WriteStartObject();
                if (!doc.RootElement.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)) return null;
                json.WriteString("t", when.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                string name = doc.RootElement.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.String ? ev.GetString() ?? "" : "";
                json.WriteString("event", EventName.IsMatch(name) ? name : "[event]");
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    string key = property.Name;
                    if (key is "t" or "event") continue;
                    if (key == "message")
                    {
                        if (property.Value.ValueKind == JsonValueKind.String) json.WriteNumber("messageChars", property.Value.GetString()!.Length); // the text can carry user content
                        continue;
                    }
                    if (key == "url")
                    {
                        json.WriteString("url", property.Value.ValueKind == JsonValueKind.String ? SafeOrigin(property.Value.GetString()!) : "[url]");
                        continue;
                    }
                    if (!RedactingLog.AllowedFields.Contains(key)) continue;
                    switch (property.Value.ValueKind)
                    {
                        case JsonValueKind.Number when NumberKeys.Contains(key) && property.Value.TryGetInt64(out long n): json.WriteNumber(key, n); break;
                        case JsonValueKind.True or JsonValueKind.False when key != "requestId": json.WriteBoolean(key, property.Value.GetBoolean()); break;
                        case JsonValueKind.String when IdentifierKeys.Contains(key): json.WriteString(key, SafeIdentifier(property.Value.GetString()!)); break;
                        default: break; // objects, arrays, null and strings in other fields are not copied
                    }
                }
                json.WriteEndObject();
            }
            string text = Encoding.UTF8.GetString(buffer.ToArray());
            if (literals.Contains(text) || SensitiveText.Standard(text) != text) return null;
            return text;
        }
        catch (JsonException) { return null; }
    }

    private string SafeIdentifier(string value)
    {
        if (!Identifier.IsMatch(value)) return "[x]";
        if (literals.Contains(value) || SensitiveText.Standard(value) != value) return "[x]";
        bool opaque = value.Length >= 24 && !Hex.IsMatch(value) && value.Any(char.IsAsciiDigit) && value.Any(char.IsAsciiLetter) && !value.Contains('.') && !value.Contains('/');
        return opaque ? "[x]" : value;
    }

    /// <summary>Scheme and host only, and only for a public-looking DNS name: no user-info, port path or query, no IP address, no one-label or private-suffix host.</summary>
    private string SafeOrigin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ws" or "wss")) return "[url]";
        string host = uri.IdnHost.ToLowerInvariant();
        if (!Host.IsMatch(host) || System.Net.IPAddress.TryParse(host, out _) || host.All(c => char.IsAsciiDigit(c) || c == '.')) return "[host]";
        if (PrivateSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)) || literals.Contains(host)) return "[host]";
        return $"{uri.Scheme}://{host}";
    }

    private byte[] BuildZip(List<(string Name, string Text)> logs, int files, int kept, int dropped)
    {
        var entries = new List<(string Name, string Text)> { ("README.txt", Readme()), ("diagnostics.json", InfoJson(files, kept, dropped)) };
        foreach (var (name, text) in logs) entries.Add(($"logs/{name}", text));
        foreach (var (_, text) in entries)
            if (literals.Contains(text)) throw new DiagnosticsLeakException();
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(clock.UtcNow.Year, clock.UtcNow.Month, clock.UtcNow.Day, 0, 0, 0, TimeSpan.Zero);
                using var writer = entry.Open();
                writer.Write(new UTF8Encoding(false).GetBytes(text));
            }
        }
        return stream.ToArray();
    }

    private static string Readme() => string.Join("\r\n",
        "Su-Su diagnostics",
        "",
        "Made by the About page. It holds version and platform facts (diagnostics.json) and the recent logs in a reduced form (logs/).",
        "Each log line keeps its time, event name, status codes, durations, sizes, counts and short identifiers, and the host name of a service address.",
        "It never holds keys, tokens, request or response text, translation or recognition text, file names or paths, account names, machine or user names, or addresses.",
        "Log messages from plugins are replaced by their length. Review the files before you share them; nothing is sent anywhere by the app.",
        "");

    private string InfoJson(int files, int kept, int dropped)
    {
        var d = info();
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = WriterOptions.Encoder }))
        {
            json.WriteStartObject();
            json.WriteString("generated", clock.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("appVersion", Version(d.AppVersion));
            json.WriteString("build", Version(d.Build));
            json.WriteString("os", Plain(d.Os));
            json.WriteString("runtime", Plain(d.Runtime));
            json.WriteString("uiLanguage", d.UiLanguage is "zh-Hans" or "en" ? d.UiLanguage : "other");
            json.WriteString("proxyMode", d.ProxyMode is "system" or "none" or "http" or "socks5" ? d.ProxyMode : "other");
            json.WriteNumber("accounts", d.Accounts);
            json.WriteNumber("savedKeys", d.SavedKeys);
            json.WriteNumber("instances", d.Instances);
            json.WriteNumber("enabledServices", d.EnabledServices);
            json.WriteNumber("favorites", d.Favorites);
            json.WriteNumber("databaseBytes", d.DatabaseBytes);
            json.WriteNumber("logFiles", files);
            json.WriteNumber("logLines", kept);
            json.WriteNumber("logLinesDropped", dropped);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static readonly Regex VersionShape = new(@"^[0-9]+(\.[0-9]+){1,3}([\-+][0-9A-Za-z.\-]{1,40})?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static string Version(string value) => VersionShape.IsMatch(value) ? value : "unknown";

    private string Plain(string value) => PlainText.IsMatch(value) && !literals.Contains(value) && SensitiveText.Standard(value) == value ? value : "unknown";
}

/// <summary>A registered secret or identity was found in a finished diagnostics entry; nothing was written.</summary>
public sealed class DiagnosticsLeakException : Exception;

/// <summary>The About page's service: facts, the log folder and the diagnostics export (F17.2).</summary>
public sealed class AboutService(AboutInfo info, AppPaths paths, DiagnosticsExporter exporter, Func<string, bool> openFolder) : IAboutService
{
    public AboutInfo Info() => info with { LogLocation = DisplayPath(paths.Logs) };

    public LogUsage Logs()
    {
        try
        {
            if (!Directory.Exists(paths.Logs)) return new LogUsage(0, 0);
            var files = new DirectoryInfo(paths.Logs).GetFiles("susu-*.jsonl");
            return new LogUsage(files.Length, files.Sum(f => f.Length));
        }
        catch (IOException) { return new LogUsage(0, 0); }
    }

    public bool OpenLogFolder()
    {
        try { Directory.CreateDirectory(paths.Logs); return openFolder(paths.Logs); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return false; }
    }

    public DiagnosticsOutcome ExportDiagnostics(string path) => exporter.Export(path);

    /// <summary>Environment-variable form so the page never shows the user name; folders outside the profile show a generic name.</summary>
    public static string DisplayPath(string path)
    {
        string full = Path.GetFullPath(path);
        string local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (full.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "%LOCALAPPDATA%" + full[local.Length..];
        return "<data folder>" + Path.DirectorySeparatorChar + "logs";
    }
}
