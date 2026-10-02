using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Susu.Abstractions;

namespace Susu.Storage;

/// <summary>
/// Structured JSONL log with redaction (PLAN 4.5.4 item 5, ARCHITECTURE 8.4). Only whitelisted fields are
/// written: request id, status, duration, size, error code and similar. URLs lose their query, known secret
/// values (and their URL/Base64 forms) are masked, plugin messages are capped at 4 KiB and 20 per second per
/// plugin with a dropped counter. Files rotate daily, are kept 7 days and at most 10 MiB in total.
/// </summary>
public sealed partial class RedactingLog : IDisposable
{
    public const int PluginMessageBytes = 4096, PluginPerSecond = 20;
    public static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "requestId", "jobId", "attempt", "service", "capability", "window", "status", "durationMs", "bytes", "errorKind", "code", "count", "phase", "url", "level", "message", "dropped",
    };

    private readonly string directory;
    private readonly IClock clock;
    private readonly long maxTotalBytes;
    private readonly object gate = new();
    private readonly SensitiveLiterals literals = new();
    private readonly Dictionary<string, (long Second, int Count, long Dropped)> pluginRate = new(StringComparer.Ordinal);
    private static readonly JsonWriterOptions writerOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public RedactingLog(string directory, IClock clock, long maxTotalBytes = 10 << 20)
    {
        this.directory = directory;
        this.clock = clock;
        this.maxTotalBytes = maxTotalBytes;
        Directory.CreateDirectory(directory);
    }

    public long DroppedFields { get; private set; }

    /// <summary>The registry of values that must never be written; the diagnostics export checks its output against the same set.</summary>
    public SensitiveLiterals Literals => literals;

    /// <summary>Registers a secret value (or an identity such as the user name) so it and its URL, JSON, Base64 and hex forms never reach the log.</summary>
    public void RegisterSecret(string value) => literals.Add(value);

    /// <summary>Masks registered values in every known form, then credentials, e-mail and IP addresses by pattern (<see cref="SensitiveText.Standard"/>).</summary>
    public string Redact(string text) => SensitiveText.Standard(literals.Mask(text));

    public void Event(string name, params (string Key, object? Value)[] fields)
    {
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, writerOptions))
        {
            json.WriteStartObject();
            json.WriteString("t", clock.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("event", Redact(name));
            foreach (var (key, value) in fields)
            {
                if (!AllowedFields.Contains(key)) { DroppedFields++; continue; }
                switch (value)
                {
                    case null: json.WriteNull(key); break;
                    case bool b: json.WriteBoolean(key, b); break;
                    case int or long or double or float: json.WriteNumber(key, Convert.ToDouble(value, CultureInfo.InvariantCulture)); break;
                    default: json.WriteString(key, Redact(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")); break;
                }
            }
            json.WriteEndObject();
        }
        Append(buffer.ToArray());
    }

    /// <summary>Plugin <c>$log</c>: returns false when the message was dropped by the rate limit.</summary>
    public bool Plugin(string installationId, string level, string message)
    {
        message = SensitiveText.Strong(message);
        long second = clock.NowMilliseconds / 1000;
        long droppedBefore;
        lock (gate)
        {
            var (s, count, dropped) = pluginRate.TryGetValue(installationId, out var r) ? r : (second, 0, 0L);
            if (s != second) { droppedBefore = dropped; (s, count, dropped) = (second, 0, 0); }
            else droppedBefore = 0;
            if (count >= PluginPerSecond) { pluginRate[installationId] = (s, count, dropped + 1); return false; }
            pluginRate[installationId] = (s, count + 1, dropped);
        }
        if (Encoding.UTF8.GetByteCount(message) > PluginMessageBytes)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            int cut = PluginMessageBytes;
            while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
            message = Encoding.UTF8.GetString(bytes, 0, cut) + "…";
        }
        if (droppedBefore > 0) Event("plugin.log.dropped", ("service", installationId), ("dropped", droppedBefore));
        Event("plugin.log", ("service", installationId), ("level", level is "debug" or "info" or "warn" or "error" ? level : "info"), ("message", message));
        return true;
    }

    public string CurrentFile => Path.Combine(directory, $"susu-{clock.UtcNow:yyyyMMdd}.jsonl");

    private void Append(byte[] line)
    {
        lock (gate)
        {
            using (var stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                stream.Write(line);
                stream.WriteByte((byte)'\n');
            }
            Enforce();
        }
    }

    /// <summary>Deletes logs older than 7 days, then oldest files until the total fits (the current file is truncated last).</summary>
    public void Enforce()
    {
        lock (gate)
        {
            var files = new DirectoryInfo(directory).GetFiles("susu-*.jsonl").OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            string cutoff = $"susu-{clock.UtcNow.AddDays(-7):yyyyMMdd}.jsonl";
            foreach (var old in files.Where(f => string.CompareOrdinal(f.Name, cutoff) < 0).ToList()) { old.Delete(); files.Remove(old); }
            long total = files.Sum(f => f.Length);
            while (total > maxTotalBytes && files.Count > 1) { total -= files[0].Length; files[0].Delete(); files.RemoveAt(0); }
            if (total > maxTotalBytes && files.Count == 1)
            {
                // Single oversized day: keep the newest half.
                var only = files[0];
                byte[] data = File.ReadAllBytes(only.FullName);
                int start = Array.IndexOf(data, (byte)'\n', data.Length / 2) + 1;
                File.WriteAllBytes(only.FullName, data[start..]);
            }
        }
    }

    [GeneratedRegex(@"\b((?:https?|wss?)://[^\s?#""']+)[?#][^\s""']*", RegexOptions.IgnoreCase)]
    private static partial Regex UrlQuery();

    public void Dispose() { }
}
