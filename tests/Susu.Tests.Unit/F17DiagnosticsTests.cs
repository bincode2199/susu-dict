using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Susu.Abstractions;
using Susu.Domain;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F17.2 sensitive-information filtering (TEST-PLAN S10, S07 logs; DATA08 for the clean entries). A log and a state are seeded with every kind of secret and
/// sensitive value, the diagnostics file is exported, and the finished zip is scanned for each seeded value in its raw, URL-encoded, Base64, hex and JSON-escaped
/// forms, inside each entry after decompression and inside any nested zip or gzip. What no pattern can promise (a server that echoes a value re-encoded in an
/// arbitrary way) is documented by a test and is not claimed.
/// </summary>
public sealed class F17DiagnosticsTests : IDisposable
{
    private readonly TempRoot root = new();
    private readonly ManualClock clock = new();

    public void Dispose() => root.Dispose();

    // ---- seeded values: every kind the export must never carry ----
    private const string ApiKey = "sk-live-9fA2kQ7zXb41LmN8pRt0VwYc";
    private const string Token = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const string Secret = "s3cr3t-Value/with+odd=chars&more";
    private const string ProxyPassword = "pr0xy-P@ssw0rd!";
    private const string Cookie = "sessionid=abcdef1234567890; csrftoken=zzzzyyyyxxxx";
    private const string QuerySecret = "qs-9d8c7b6a5f4e3d2c1b0a";
    private const string AccountName = "Alice Personal DeepL";
    private const string UserName = "alice.example";
    private const string MachineName = "ALICE-DESKTOP-77";
    private const string MachineGuid = "5f0c9a3e-1b2d-4c6f-8a7e-0d9b3c2a1f44";
    private const string ProxyHost = "corp-proxy.alice-intranet.test";
    private const string ProxyUser = "alice-proxy-login";
    private const string Ipv4 = "203.0.113.57";
    private const string Ipv6 = "2001:db8:85a3::8a2e:370:7334";
    private const string Email = "alice.liu@example.org";
    private const string InputText = "我银行卡尾号 4417 的余额是多少 Please translate my private diary entry";
    private const string OutputText = "Your balance is whatever the secret translation said";
    private const string OcrText = "OCR captured: invoice #88231 for Alice";
    private const string AudioPath = @"C:\Users\alice.example\Music\private voice memo.wav";
    private const string MediaName = "family-holiday-2026.mp4";
    private const string FileNameOnly = "tax-return-final.pdf";

    private static readonly string[] Everything =
    [
        ApiKey, Token, Secret, ProxyPassword, Cookie, "abcdef1234567890", QuerySecret, AccountName, UserName, MachineName, MachineGuid, ProxyHost, ProxyUser,
        Ipv4, Ipv6, Email, InputText, OutputText, OcrText, AudioPath, MediaName, FileNameOnly, "private diary", "voice memo", "balance is whatever", "invoice #88231",
    ];

    private SensitiveLiterals Registry()
    {
        var literals = new SensitiveLiterals();
        literals.AddRange([ApiKey, Token, Secret, ProxyPassword, QuerySecret, AccountName, UserName, MachineName, MachineGuid, ProxyHost, ProxyUser]);
        return literals;
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private string[] HostileLines() =>
    [
        // free text in plugin messages
        J(("event", "plugin.log"), ("service", "deepl"), ("level", "info"), ("message", $"translating '{InputText}' with key {ApiKey} => {OutputText}")),
        J(("event", "plugin.log"), ("service", "deepl"), ("message", $"ocr: {OcrText}; file {AudioPath}; {MediaName}; {FileNameOnly}; machine {MachineName} user {UserName} id {MachineGuid}")),
        // secret in every encoding inside a message
        J(("event", "plugin.log"), ("message", $"{B64(ApiKey)} {Uri.EscapeDataString(Secret)} {Convert.ToHexString(Encoding.UTF8.GetBytes(Token))} {B64("user:" + ProxyPassword)}")),
        // urls: query secrets, user-info, IPs, private hosts, paths with tokens
        J(("event", "net.call"), ("url", $"https://api.example.com/v1/translate?key={QuerySecret}&q={Uri.EscapeDataString(InputText)}"), ("status", 200), ("durationMs", 83)),
        J(("event", "net.call"), ("url", $"https://alice:{ProxyPassword}@api.example.com/v1/x"), ("status", 401)),
        J(("event", "net.call"), ("url", $"http://{Ipv4}:8080/x"), ("status", 200)),
        J(("event", "net.call"), ("url", $"http://[{Ipv6}]/x"), ("status", 200)),
        J(("event", "net.call"), ("url", "https://alice-desktop-77.local/x"), ("status", 200)),
        J(("event", "net.call"), ("url", $"https://{ProxyHost}/x"), ("status", 200)),
        J(("event", "net.call"), ("url", $"https://api.telegram.test/bot{Token}/getMe"), ("status", 200)),
        // fields that are not on the allow-list, in many shapes
        $"{{\"t\":\"2026-10-02T01:02:03.0000000Z\",\"event\":\"x.y\",\"authorization\":\"Bearer {ApiKey}\",\"cookie\":\"{Cookie}\",\"body\":\"{InputText}\",\"text\":\"{OutputText}\",\"path\":\"{AudioPath.Replace("\\", "\\\\")}\",\"nested\":{{\"apiKey\":\"{ApiKey}\",\"deep\":[\"{Secret}\"]}},\"email\":\"{Email}\",\"ip\":\"{Ipv4}\",\"account\":\"{AccountName}\"}}",
        // a secret as the event name, as an identifier, as a code in other encodings, as an unknown key
        J(("event", ApiKey), ("service", ApiKey), ("code", B64(ApiKey).TrimEnd('=')), ("window", Uri.EscapeDataString(Secret)), ("phase", Token)),
        $"{{\"t\":\"2026-10-02T01:02:03.0000000Z\",\"event\":\"x\",\"{ApiKey}\":1,\"requestId\":\"{ApiKey}\",\"jobId\":\"{Email}\"}}",
        // a nested compressed blob carried as a string field
        J(("event", "x"), ("message", GzipBase64(ApiKey + InputText)), ("code", GzipBase64(Secret))),
        // not JSON, truncated JSON, a JSON array, an oversized line
        $"plain text line with {ApiKey} and {Email} and {AudioPath}",
        $"{{\"t\":\"2026-10-02T01:02:03.0000000Z\",\"event\":\"cut\",\"message\":\"{InputText}",
        $"[\"{ApiKey}\"]",
        J(("event", "big"), ("message", new string('x', DiagnosticsExporter.MaxLineChars + 10))),
        // an honest line the export should keep
        J(("event", "translate.done"), ("requestId", "0f3c9a1e2b4d4c5e8f60718293a4b5c6"), ("service", "deepl"), ("capability", "translate"), ("status", 200), ("durationMs", 142), ("bytes", 1203), ("count", 2)),
    ];

    private static string GzipBase64(string text)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(Encoding.UTF8.GetBytes(text));
        return Convert.ToBase64String(output.ToArray());
    }

    private static string J(params (string Key, object Value)[] fields)
    {
        var map = new Dictionary<string, object> { ["t"] = "2026-10-02T01:02:03.0000000Z" };
        foreach (var (k, v) in fields) map[k] = v;
        return JsonSerializer.Serialize(map);
    }

    private DiagnosticsExporter Exporter(SensitiveLiterals? literals = null, DiagnosticsInfo? info = null)
        => new(root.Paths, clock, literals ?? Registry(), () => info ?? new DiagnosticsInfo("1.2.3.0", "1.2.3", "Microsoft Windows 10.0.26200", ".NET 10.0.0", "zh-Hans", "system", 1, 2, 24, 5, 3, 4096));

    private string Export(DiagnosticsExporter exporter, out DiagnosticsOutcome outcome)
    {
        string path = Path.Combine(root.Root, "out", $"diag-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        outcome = exporter.Export(path);
        return path;
    }

    private void SeedLogs(IEnumerable<string> lines, string name = "susu-20261002.jsonl")
        => File.WriteAllLines(Path.Combine(root.Paths.Logs, name), lines, new UTF8Encoding(false));

    // ---- scanning ----

    /// <summary>Every form in which <paramref name="needle"/> could hide: raw, URL, doubled URL, JSON, hex, Base64 (all alignments), UTF-16.</summary>
    private static IEnumerable<string> Forms(string needle) => SensitiveLiterals.FormsOf(needle);

    /// <summary>Returns a description of the first needle found in the bytes or in anything decodable from them, or null.</summary>
    private static string? Find(byte[] data, IEnumerable<string> needles, int depth = 0)
    {
        var list = needles.ToArray();
        string asText = Encoding.UTF8.GetString(data);
        string latin = Encoding.Latin1.GetString(data);
        foreach (var needle in list)
        {
            if (asText.Contains(needle, StringComparison.OrdinalIgnoreCase)) return $"text:{needle[..Math.Min(12, needle.Length)]}";
            foreach (var form in Forms(needle))
            {
                if (form.Length < 4) continue;
                if (asText.Contains(form, StringComparison.Ordinal)) return $"text:{needle[..Math.Min(12, needle.Length)]}";
                if (latin.Contains(form, StringComparison.Ordinal)) return $"raw:{needle[..Math.Min(12, needle.Length)]}";
                if (Contains(data, Encoding.Unicode.GetBytes(form))) return $"utf16:{needle[..Math.Min(12, needle.Length)]}";
            }
        }
        if (depth >= 3) return null;
        // decoded views: URL-decoded text, every Base64-looking run, every gzip/zip magic
        string decoded = SafeUnescape(asText);
        if (decoded != asText && Find(Encoding.UTF8.GetBytes(decoded), list, depth + 1) is { } viaUrl) return "url>" + viaUrl;
        foreach (Match m in Regex.Matches(asText, @"[A-Za-z0-9+/_\-]{16,}={0,2}"))
        {
            if (TryBase64(m.Value) is { } bytes && Find(bytes, list, depth + 1) is { } viaB64) return "b64>" + viaB64;
        }
        if (data.Length > 4 && data[0] == 0x1f && data[1] == 0x8b) { try { using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress); using var ms = new MemoryStream(); gz.CopyTo(ms); if (Find(ms.ToArray(), list, depth + 1) is { } viaGz) return "gz>" + viaGz; } catch (InvalidDataException) { } }
        if (data.Length > 4 && data[0] == 'P' && data[1] == 'K')
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(data));
                foreach (var entry in zip.Entries)
                {
                    using var s = entry.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
                    if (Encoding.UTF8.GetBytes(entry.FullName) is var nameBytes && Find(nameBytes, list, depth + 1) is { } viaName) return "zipname>" + viaName;
                    if (Find(ms.ToArray(), list, depth + 1) is { } viaZip) return $"zip:{entry.FullName}>{viaZip}";
                }
            }
            catch (InvalidDataException) { }
        }
        return null;
    }

    private static bool Contains(byte[] hay, byte[] needle) => hay.AsSpan().IndexOf(needle) >= 0;
    private static string SafeUnescape(string s) { try { return Uri.UnescapeDataString(s.Replace("+", "%20")); } catch (UriFormatException) { return s; } }
    private static byte[]? TryBase64(string run)
    {
        string s = run.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(s); } catch (FormatException) { return null; }
    }

    /// <summary>The scan of a finished export: the raw zip bytes, then each entry (name and content) after decompression.</summary>
    private static string? ScanZip(string path, IEnumerable<string> needles)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (Find(bytes, needles) is { } raw) return "zipbytes:" + raw;
        using var zip = new ZipArchive(new MemoryStream(bytes));
        foreach (var entry in zip.Entries)
        {
            using var s = entry.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
            if (Find(Encoding.UTF8.GetBytes(entry.FullName), needles) is { } name) return $"name:{name}";
            if (Find(ms.ToArray(), needles) is { } hit) return $"{entry.FullName}:{hit}";
        }
        return null;
    }

    private static string ReadEntry(string zipPath, string name)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ---- the scanner itself must be able to see things (otherwise the adversarial tests prove nothing) ----

    [Fact]
    public void Scanner_finds_each_encoding_of_a_value_including_nested_zip_and_gzip()
    {
        foreach (var needle in new[] { ApiKey, Secret, AccountName, "private diary" })
        {
            string[] carriers =
            [
                needle, Uri.EscapeDataString(needle), B64(needle), B64("xx" + needle), B64("x" + needle + "yy"), Convert.ToHexString(Encoding.UTF8.GetBytes(needle)), JsonSerializer.Serialize(needle),
                B64(Uri.EscapeDataString(needle)),
            ];
            foreach (var carrier in carriers)
                Assert.NotNull(Find(Encoding.UTF8.GetBytes($"{{\"a\":\"{carrier.Trim('"')}\"}}"), [needle]));
            using var gz = new MemoryStream();
            using (var g = new GZipStream(gz, CompressionLevel.Optimal, leaveOpen: true)) g.Write(Encoding.UTF8.GetBytes("prefix " + needle));
            Assert.NotNull(Find(gz.ToArray(), [needle]));
            Assert.NotNull(Find(Encoding.UTF8.GetBytes(B64(Convert.ToBase64String(gz.ToArray()))), [needle]));
        }
        Assert.Null(Find(Encoding.UTF8.GetBytes("nothing to see here, status 200, 83 ms"), Everything));
    }

    // ---- the export ----

    [Fact]
    public void Export_of_a_hostile_log_holds_none_of_the_seeded_values_in_any_form()
    {
        SeedLogs(HostileLines());
        var exporter = Exporter();
        string path = Export(exporter, out var outcome);
        Assert.True(outcome.Ok, outcome.Error);
        Assert.Null(ScanZip(path, Everything));
        // the honest line survives, in full, and the host of a public service address stays
        string logs = ReadEntry(path, "logs/susu-20261002.jsonl");
        Assert.Contains("\"event\":\"translate.done\"", logs);
        Assert.Contains("\"durationMs\":142", logs);
        Assert.Contains("\"requestId\":\"0f3c9a1e2b4d4c5e8f60718293a4b5c6\"", logs);
        Assert.Contains("\"url\":\"https://api.example.com\"", logs);
        Assert.True(outcome.DroppedLines >= 5, $"dropped {outcome.DroppedLines}");
        Assert.True(outcome.LogLines >= 10);
    }

    [Fact]
    public void Every_line_is_a_json_object_of_allow_listed_fields_only()
    {
        SeedLogs(HostileLines());
        string path = Export(Exporter(), out _);
        var allowed = new HashSet<string>(RedactingLog.AllowedFields) { "t", "event", "messageChars" };
        foreach (var line in ReadEntry(path, "logs/susu-20261002.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                Assert.Contains(p.Name, allowed);
                Assert.NotEqual(JsonValueKind.Object, p.Value.ValueKind);
                Assert.NotEqual(JsonValueKind.Array, p.Value.ValueKind);
            }
        }
    }

    [Fact]
    public void Plugin_messages_become_their_length_and_urls_become_scheme_and_host_or_a_placeholder()
    {
        SeedLogs(
        [
            J(("event", "plugin.log"), ("service", "deepl"), ("message", "hello world")),
            J(("event", "n"), ("url", "https://api.example.com:8443/a/b?c=d#e")),
            J(("event", "n"), ("url", "http://10.1.2.3/")),
            J(("event", "n"), ("url", "http://localhost:5000/")),
            J(("event", "n"), ("url", "ftp://files.example.com/")),
            J(("event", "n"), ("url", "not a url")),
            J(("event", "n"), ("url", "https://printer.lan/x")),
        ]);
        var lines = ReadEntry(Export(Exporter(), out _), "logs/susu-20261002.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("\"messageChars\":11", lines[0]);
        Assert.DoesNotContain("hello", lines[0]);
        Assert.Contains("\"url\":\"https://api.example.com\"", lines[1]);
        Assert.Contains("\"url\":\"[host]\"", lines[2]);
        Assert.Contains("\"url\":\"[host]\"", lines[3]);
        Assert.Contains("\"url\":\"[url]\"", lines[4]);
        Assert.Contains("\"url\":\"[url]\"", lines[5]);
        Assert.Contains("\"url\":\"[host]\"", lines[6]);
    }

    [Fact]
    public void Credential_shaped_values_are_dropped_even_when_nothing_was_registered()
    {
        // The registry is empty: only the shape rules and the allow-list protect the file (a key the app was never told about).
        SeedLogs(
        [
            J(("event", "a"), ("service", ApiKey)),
            J(("event", "b"), ("code", Token)),
            J(("event", "c"), ("phase", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhbGljZSJ9.c2lnbmF0dXJl")),
            J(("event", "d"), ("service", "AKIAABCDEFGHIJKLMNOP")),
            J(("event", "e"), ("requestId", "alice.liu@example.org")),
        ]);
        string path = Export(Exporter(new SensitiveLiterals()), out var outcome);
        Assert.True(outcome.Ok);
        Assert.Null(ScanZip(path, [ApiKey, Token, "eyJzdWIiOiJhbGljZSJ9", "AKIAABCDEFGHIJKLMNOP", Email]));
    }

    [Fact]
    public void Diagnostics_json_holds_only_numbers_and_shape_checked_strings()
    {
        var hostile = new DiagnosticsInfo(ApiKey, $"1.0+{MachineName}", $"Windows {UserName} {Ipv4}", $"{AudioPath}", "zh-Hans; DROP", ProxyHost, 1, 2, 3, 4, 5, 6);
        var hostileExporter = Exporter(info: hostile);
        string path = Export(hostileExporter, out var outcome);
        Assert.True(outcome.Ok, outcome.Error + " in " + hostileExporter.LastLeakEntry);
        using var doc = JsonDocument.Parse(ReadEntry(path, "diagnostics.json"));
        var root = doc.RootElement;
        Assert.Equal("unknown", root.GetProperty("appVersion").GetString());
        Assert.Equal("unknown", root.GetProperty("build").GetString());
        Assert.Equal("unknown", root.GetProperty("os").GetString());
        Assert.Equal("unknown", root.GetProperty("runtime").GetString());
        Assert.Equal("other", root.GetProperty("uiLanguage").GetString());
        Assert.Equal("other", root.GetProperty("proxyMode").GetString());
        Assert.Null(ScanZip(path, Everything));
        var good = ReadEntry(Export(Exporter(), out _), "diagnostics.json");
        Assert.Contains("\"appVersion\": \"1.2.3.0\"", good);
        Assert.Contains("\"uiLanguage\": \"zh-Hans\"", good);
        Assert.Contains("\"savedKeys\": 2", good);
    }

    [Fact]
    public void A_registered_value_found_in_a_finished_entry_stops_the_export_and_writes_nothing()
    {
        var literals = Registry();
        literals.Add("Su-Su diagnostics"); // the README's own title: stands in for a value that slipped through to an entry
        string path = Path.Combine(root.Root, "blocked.zip");
        var outcome = Exporter(literals).Export(path);
        Assert.False(outcome.Ok);
        Assert.Equal("leak-detected", outcome.Error);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(root.Root, "blocked*"));
    }

    [Fact]
    public void A_destination_that_cannot_be_written_reports_write_failed_and_leaves_no_temp_file()
    {
        string folder = Path.Combine(root.Root, "missing-folder");
        var outcome = Exporter().Export(Path.Combine(folder, "x.zip"));
        Assert.False(outcome.Ok);
        Assert.Equal("write-failed", outcome.Error);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void An_existing_file_is_replaced_whole_and_logs_beyond_the_size_budget_are_trimmed_to_the_newest()
    {
        var many = Enumerable.Range(0, 60_000).Select(i => J(("event", "bulk.line"), ("count", i), ("service", "deepl"), ("code", "ok"), ("phase", "p"), ("bytes", i * 7)));
        SeedLogs(many);
        string path = Path.Combine(root.Root, "replace.zip");
        File.WriteAllText(path, "old");
        var outcome = Exporter().Export(path);
        Assert.True(outcome.Ok);
        string logs = ReadEntry(path, "logs/susu-20261002.jsonl");
        Assert.True(Encoding.UTF8.GetByteCount(logs) <= DiagnosticsExporter.MaxLogBytes);
        Assert.Contains("\"count\":59999", logs);
        Assert.DoesNotContain("\"count\":0,", logs);
        Assert.Equal(outcome.Bytes, new FileInfo(path).Length);
    }

    [Fact]
    public void Only_log_files_are_read_and_old_and_new_days_are_kept_in_order()
    {
        File.WriteAllText(Path.Combine(root.Paths.Logs, "notes.txt"), ApiKey);
        File.WriteAllText(Path.Combine(root.Paths.Logs, "susu-20261001.jsonl.bak"), ApiKey);
        File.WriteAllText(Path.Combine(root.Paths.Logs, $"{ApiKey}.jsonl"), ApiKey);
        SeedLogs([J(("event", "day.one"))], "susu-20261001.jsonl");
        SeedLogs([J(("event", "day.two"))], "susu-20261002.jsonl");
        string path = Export(Exporter(), out var outcome);
        Assert.Equal(2, outcome.LogFiles);
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal(["README.txt", "diagnostics.json", "logs/susu-20261001.jsonl", "logs/susu-20261002.jsonl"], zip.Entries.Select(e => e.FullName).ToArray());
        Assert.Null(ScanZip(path, [ApiKey]));
    }

    // ---- the real logger end to end ----

    [Fact]
    public void Logs_written_by_the_real_logger_never_carry_the_seeded_values_and_export_clean()
    {
        using var log = new RedactingLog(root.Paths.Logs, clock);
        log.Literals.AddRange([ApiKey, Token, Secret, ProxyPassword, QuerySecret, AccountName, UserName, MachineName, MachineGuid, ProxyHost, ProxyUser]);
        // fields the log allows, filled with hostile values
        log.Event("net.call", ("url", $"https://api.example.com/v1?key={QuerySecret}&token={Token}"), ("status", 200), ("service", "deepl"), ("code", $"echo:{B64(ApiKey)}"),
            ("message", $"Authorization: Bearer {ApiKey}"), ("phase", Uri.EscapeDataString(Secret)));
        log.Event("net.call", ("message", $"Cookie: {Cookie}"), ("code", $"{Email} {Ipv4} [{Ipv6}]"));
        log.Plugin("deepl", "info", $"input {InputText} output {OutputText} file {AudioPath} {MediaName} user {UserName} machine {MachineName} {MachineGuid}");
        log.Plugin("deepl", "warn", $"{ProxyUser}:{ProxyPassword}@{ProxyHost} account {AccountName} {B64(ProxyPassword)} {FileNameOnly}");
        string onDisk = File.ReadAllText(log.CurrentFile);
        var sensitive = new[] { ApiKey, Token, Secret, ProxyPassword, QuerySecret, AccountName, UserName, MachineName, MachineGuid, ProxyHost, ProxyUser, Email, Ipv4, Ipv6, "abcdef1234567890", AudioPath, MediaName, FileNameOnly };
        Assert.Null(Find(Encoding.UTF8.GetBytes(onDisk), sensitive));
        // and the export, with the same registry
        string path = Export(new DiagnosticsExporter(root.Paths, clock, log.Literals, () => new DiagnosticsInfo("1.0.0.0", "1.0.0", "Windows", ".NET", "en", "system", 0, 0, 0, 0, 0, 0)), out var outcome);
        Assert.True(outcome.Ok);
        Assert.Null(ScanZip(path, sensitive.Concat([InputText, OutputText, "private voice memo"])));
    }

    [Fact]
    public void S10_known_forms_of_a_secret_echoed_by_a_server_are_masked_in_the_log()
    {
        using var log = new RedactingLog(root.Paths.Logs, clock);
        log.RegisterSecret(ApiKey);
        string basic = B64($"alice:{ApiKey}");
        string[] echoes =
        [
            ApiKey, Uri.EscapeDataString(ApiKey), B64(ApiKey), B64(ApiKey).TrimEnd('='), Uri.EscapeDataString(B64(ApiKey)), Convert.ToHexString(Encoding.UTF8.GetBytes(ApiKey)),
            $"Authorization: Basic {basic}", $"Authorization: Bearer {ApiKey}", $"{{\"apiKey\":\"{ApiKey}\"}}", $"x-api-key={ApiKey}", $"https://h.example/p?api_key={ApiKey}", "prefix" + B64("x" + ApiKey) + "suffix",
            B64("xx" + ApiKey),
        ];
        foreach (var echo in echoes) log.Event("server.echo", ("message", echo), ("code", echo));
        string onDisk = File.ReadAllText(log.CurrentFile);
        Assert.Null(Find(Encoding.UTF8.GetBytes(onDisk), [ApiKey]));
        Assert.Contains("[redacted]", onDisk);
    }

    [Fact]
    public void S10_arbitrary_transformations_of_a_secret_are_outside_the_guarantee()
    {
        // TEST-PLAN S10: a server that echoes the value re-encoded in a way nobody anticipated cannot be caught by masking. The test records the limit instead of hiding it.
        using var log = new RedactingLog(root.Paths.Logs, clock);
        log.RegisterSecret(ApiKey);
        string rot13 = new([.. ApiKey.Select(c => char.IsAsciiLetterLower(c) ? (char)('a' + (c - 'a' + 13) % 26) : char.IsAsciiLetterUpper(c) ? (char)('A' + (c - 'A' + 13) % 26) : c)]);
        string reversed = new([.. ApiKey.Reverse()]);
        log.Event("server.echo", ("message", $"{rot13} {reversed}"));
        string onDisk = File.ReadAllText(log.CurrentFile);
        Assert.Contains(rot13, onDisk);
        Assert.Contains(reversed, onDisk);
        // the diagnostics export still drops the free-text field wholesale, so the file does not carry them either
        string path = Export(new DiagnosticsExporter(root.Paths, clock, log.Literals, () => new DiagnosticsInfo("1.0.0.0", "1.0.0", "Windows", ".NET", "en", "system", 0, 0, 0, 0, 0, 0)), out _);
        Assert.Null(ScanZip(path, [rot13, reversed, ApiKey]));
    }

    [Fact]
    public void The_log_masks_a_key_written_after_attach_and_keys_already_saved_and_the_machine_identity()
    {
        var protector = new XorProtector();
        using var store = new SettingsStore(root.Paths, clock);
        var secrets = new SecretStore(root.Paths.Secrets, protector);
        secrets.Write("acct", "apiKey", "already-saved-key-111");
        using var log = new RedactingLog(root.Paths.Logs, clock);
        SensitiveRegistry.Attach(log.Literals, secrets, store, [MachineGuid]);
        secrets.Write("acct", "secondKey", "written-later-key-222");
        var state = store.State;
        store.Save(state.Effective with { Accounts = [new AccountSettings("a1", AccountName, ["apiKey"], [])], Network = state.Effective.Network with { ProxyHost = ProxyHost, ProxyUsername = ProxyUser } }, state.Revision, state.FileHash);
        log.Event("e", ("message", $"already-saved-key-111 written-later-key-222 {MachineGuid} {Environment.UserName} {Environment.MachineName} {AccountName} {ProxyHost} {ProxyUser}"));
        string onDisk = File.ReadAllText(log.CurrentFile);
        foreach (var value in new[] { "already-saved-key-111", "written-later-key-222", MachineGuid, AccountName, ProxyHost, ProxyUser })
            Assert.DoesNotContain(value, onDisk, StringComparison.OrdinalIgnoreCase);
        if (Environment.UserName.Length >= SensitiveLiterals.MinLength) Assert.DoesNotContain(Environment.UserName, onDisk, StringComparison.OrdinalIgnoreCase);
        // a default label (the service id) is not personal and stays readable
        var s2 = store.State;
        store.Save(s2.Effective with { Accounts = [new AccountSettings("a2", "deepl", ["apiKey"], [])] }, s2.Revision, s2.FileHash);
        log.Event("e2", ("service", "deepl"));
        Assert.Contains("\"service\":\"deepl\"", File.ReadAllText(log.CurrentFile));
    }

    // ---- the pattern scrubber ----

    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi-123456", "abc.def.ghi-123456")]
    [InlineData("proxy-authorization=Basic dXNlcjpwYXNz", "dXNlcjpwYXNz")]
    [InlineData("Cookie: a=1; b=2; sid=XYZ", "sid=XYZ")]
    [InlineData("set-cookie: token=abcd1234; Path=/", "abcd1234")]
    [InlineData("{\"apiKey\":\"hunter2hunter2\"}", "hunter2hunter2")]
    [InlineData("password=correcthorse&x=1", "correcthorse")]
    [InlineData("client_secret: 'tops3cretvalue'", "tops3cretvalue")]
    [InlineData("https://user:pw123456@host.example/p", "pw123456")]
    [InlineData("https://h.example/p?access_token=zzzzzzzz1234#frag", "zzzzzzzz1234")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.abcdefghijk", "eyJzdWIiOiIxMjM0In0")]
    [InlineData("key sk-proj-ABCDEFGHIJKLMNOP1234 end", "sk-proj-ABCDEFGHIJKLMNOP1234")]
    [InlineData("AIzaSyA1234567890abcdefghijklmnop", "AIzaSyA1234567890abcdefghijklmnop")]
    [InlineData("mail me at jane.doe+x@mail.example.com now", "jane.doe+x@mail.example.com")]
    [InlineData("from 198.51.100.23 to 10.0.0.5", "198.51.100.23")]
    [InlineData("peer fe80::1ff:fe23:4567:890a seen", "fe80::1ff:fe23:4567:890a")]
    [InlineData("peer 2001:db8:0:0:0:0:2:1 seen", "2001:db8:0:0:0:0:2:1")]
    [InlineData("mac 00:1A:2B:3C:4D:5E", "00:1A:2B:3C:4D:5E")]
    public void Standard_scrub_removes_credential_and_identity_shapes(string input, string secret)
    {
        string output = SensitiveText.Standard(input);
        Assert.DoesNotContain(secret, output);
    }

    [Theory]
    [InlineData(@"opened C:\Users\alice\Documents\notes.txt ok", @"alice")]
    [InlineData(@"opened C:\\Users\\alice\\Music\\my song.mp3", "Music")]
    [InlineData(@"share \\fileserver\home\alice\x.docx", "fileserver")]
    [InlineData("file:///C:/Users/alice/a.png", "alice")]
    [InlineData("saved holiday-photo.png and report-q3.pdf", "holiday-photo")]
    [InlineData("machine id 5f0c9a3e-1b2d-4c6f-8a7e-0d9b3c2a1f44 here", "5f0c9a3e")]
    [InlineData("blob aGVsbG8gd29ybGQgdGhpcyBpcyBhIGxvbmcgb3BhcXVlIHRva2Vu end", "aGVsbG8")]
    public void Strong_scrub_also_removes_paths_file_names_guids_and_blobs(string input, string leaked)
    {
        Assert.DoesNotContain(leaked, SensitiveText.Strong(input));
    }

    [Fact]
    public void Standard_scrub_keeps_ordinary_text_ids_versions_and_times_readable()
    {
        const string text = "translate.done requestId=0f3c9a1e2b4d4c5e8f60718293a4b5c6 status 200 in 83 ms at 12:30:45 version 10.0.26200.1 service app.susu.deepl";
        Assert.Equal(text, SensitiveText.Standard(text));
    }

    [Fact]
    public void Literal_registry_ignores_short_values_and_matches_every_form_case_insensitively()
    {
        var r = new SensitiveLiterals();
        r.Add("abc"); r.Add("   "); r.Add(null);
        Assert.Equal(0, r.Count);
        Assert.Equal("keep abc", r.Mask("keep abc"));
        r.Add("Ünï cödé Key+1");
        foreach (var form in SensitiveLiterals.FormsOf("Ünï cödé Key+1")) Assert.True(r.Contains($"zz {form} zz"), form);
        Assert.True(r.Contains("UNI".Length > 0 ? "xx ünï cödé key+1 xx" : ""));
        Assert.DoesNotContain("Key%2B1", r.Mask("a b " + Uri.EscapeDataString("Ünï cödé Key+1")));
    }

    // ---- private folders (F17.1 open item 3) ----

    [Fact]
    public void The_import_stage_and_the_config_journal_are_restricted_to_the_current_user_and_system()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var rig = new BackupRig();
        rig.Seed();
        string file = Path.Combine(rig.Root.Root, "ok.susubak");
        Assert.True(rig.Service.Export(file, new BackupExportOptions(false, null)).Ok);
        rig.Service.Preview(file, null);
        AssertRestricted(rig.Paths.Imports);
        AssertRestricted(Path.Combine(rig.Paths.Imports, "staged"), root: false);
        Assert.True(File.Exists(Path.Combine(rig.Paths.Imports, "staged", "secrets.dat")));
        // a switch at start writes the restore point and the journal; both are restricted too
        Assert.True(rig.Service.Apply(rig.Service.Preview(file, null).Token));
        rig.Restart();
        AssertRestricted(Path.Combine(rig.Paths.Imports, "restore-point"), root: false);
        Assert.True(Directory.Exists(rig.Paths.Transactions));
        AssertRestricted(rig.Paths.Transactions);
    }

    [Fact]
    public void Restricting_a_folder_removes_inheritance_and_other_accounts_and_is_idempotent()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = Path.Combine(root.Root, "private");
        Directory.CreateDirectory(dir);
        Assert.True(PrivateFolder.Describe(dir).Inherits); // a plain folder inherits the profile's rules
        PrivateFolder.Ensure(dir);
        PrivateFolder.Ensure(dir);
        AssertRestricted(dir);
        File.WriteAllText(Path.Combine(dir, "child.txt"), "x"); // files created inside inherit the restricted rules
        var child = new FileInfo(Path.Combine(dir, "child.txt")).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference.Value).Distinct().ToArray();
        Assert.Equal(PrivateFolder.Describe(dir).Sids.Order().ToArray(), child.Order().ToArray());
    }

    private static void AssertRestricted(string dir, bool root = true)
    {
        var (inherits, sids) = PrivateFolder.Describe(dir);
        if (root) Assert.False(inherits, $"{dir} still inherits"); // a child of a restricted folder inherits the restricted rules, which is the point
        string me = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        Assert.Contains(me, sids);
        Assert.Contains("S-1-5-18", sids); // SYSTEM
        Assert.True(sids.Count == 2, string.Join(",", sids));       // no Users, Everyone, Administrators or anyone else
    }
}


