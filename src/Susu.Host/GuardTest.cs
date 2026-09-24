#if DEV_PREVIEW
using System.Text.Json;
using Susu.Contracts;
using Susu.Windows.Shell;

namespace Susu.Host;

/// <summary>
/// <c>susu --guard-test &lt;fixture folder&gt; &lt;report.json&gt;</c> (development builds only, S08): loads a hostile page without
/// the production CSP into the real WebView host and records what the page could do and what the native guards
/// blocked. The page must not reach any other origin, open windows, get permissions, download, or navigate away.
/// </summary>
internal sealed class GuardTest(string reportPath, WindowPlatform platform, UiDispatcher dispatcher)
{
    private readonly Dictionary<string, string> results = new(StringComparer.Ordinal);
    private readonly List<string> blocked = [];
    private int loads;
    public int ExitCode { get; private set; } = 1;

    public void Start()
    {
        platform.PageMessage += (_, json) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("name", out var name) && doc.RootElement.TryGetProperty("outcome", out var outcome))
                    results[name.GetString() ?? "?"] = outcome.GetString() ?? "";
            }
            catch (JsonException) { }
        };
        platform.Diagnostic += message => { if (message.StartsWith("webview.blocked", StringComparison.Ordinal)) blocked.Add(message["webview.blocked ".Length..]); };
        platform.Timing += (_, phase, _) => { if (phase == "PageLoaded") loads++; };
        dispatcher.Post(async () =>
        {
            long started = DateTime.UtcNow.Ticks;
            platform.Show(WindowKind.Main, activate: false);
            await Task.Delay(TimeSpan.FromSeconds(9));
            Write(started);
            platform.Exit();
        });
    }

    private void Write(long startedTicks)
    {
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        bool downloaded = Directory.Exists(downloads) && Directory.GetFiles(downloads, "x*.txt").Any(f => File.GetCreationTimeUtc(f).Ticks >= startedTicks);
        string Get(string key) => results.TryGetValue(key, out var v) ? v : "(no report)";
        bool NotOk(string key) => !Get(key).StartsWith("status 200", StringComparison.Ordinal) && Get(key) != "(no report)";
        var checks = new (string Name, bool Passed, string Observed)[]
        {
            ("fetch-external refused", NotOk("fetch-external"), Get("fetch-external")),
            ("fetch-file refused", NotOk("fetch-file"), Get("fetch-file")),
            ("fetch-loopback refused", NotOk("fetch-loopback"), Get("fetch-loopback")),
            ("external image blocked", Get("image-external") == "blocked", Get("image-external")),
            ("external iframe not loaded", !Get("iframe-external").Contains("example.com", StringComparison.Ordinal), Get("iframe-external")),
            ("window.open refused", Get("window-open") == "null", Get("window-open")),
            ("geolocation denied", Get("geolocation").StartsWith("denied", StringComparison.Ordinal), Get("geolocation")),
            ("download cancelled (no file in Downloads)", !downloaded, downloaded ? "file created" : "no file"),
            ("page stayed on its origin after navigation attempts", Get("still-here") == "same page" && loads == 1, $"{Get("still-here")}; page loads={loads}"),
            ("blocked requests reported for example.com", blocked.Any(b => b.Contains("example.com", StringComparison.Ordinal)), string.Join(", ", blocked.Distinct())),
        };
        ExitCode = checks.All(c => c.Passed) ? 0 : 1;
        using var stream = File.Create(reportPath);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("scope", "S08 native guard test: hostile page without CSP in the real WebView2 host (susu.exe, development build)");
        json.WriteString("webViewRuntime", WebViewRuntime.InstalledVersion());
        json.WriteStartArray("checks");
        foreach (var (name, passed, observed) in checks)
        {
            json.WriteStartObject();
            json.WriteString("check", name);
            json.WriteBoolean("passed", passed);
            json.WriteString("observed", observed);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartObject("informational");
        json.WriteString("websocket-external", Get("websocket-external") + " (the native layer does not filter WebSockets; production relies on the CSP connect-src 'none')");
        json.WriteString("host-objects", Get("host-objects"));
        json.WriteEndObject();
        json.WriteBoolean("passed", ExitCode == 0);
        json.WriteEndObject();
    }
}
#endif
