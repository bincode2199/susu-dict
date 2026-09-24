using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Susu.Probes.PluginHost;

/// <summary>
/// PER03 "selection done → first request dispatched" on the local-detection path: Unicode script
/// fast path (PLAN 3.2 step 1), ELS for mixed text (step 2), then the Invoke frame is written to the
/// sandboxed plugin host. Remote detection RTT is out of scope (reported separately by design).
/// </summary>
internal static partial class DetectDispatch
{
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_els_detect", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int Els(string text, char* output, uint capacity);

    internal sealed record Row(string Case, string Path, EngineBench.Stat DetectMs, EngineBench.Stat DispatchMs, EngineBench.Stat TotalMs, string Detected);
    internal sealed record Report(DateTimeOffset Timestamp, double ElsFirstCallMs, Row[] Rows, string[] Notes);

    /// <summary>Script fast path: only Han → zh-Hans, only Latin letters → en, otherwise null (ask ELS).</summary>
    public static string? FastPath(string text)
    {
        bool han = false, latin = false;
        foreach (char c in text)
        {
            if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿') han = true;
            else if (char.IsAsciiLetter(c) || c is >= 'À' and <= 'ɏ') latin = true;
        }
        return han && !latin ? "zh-Hans" : latin && !han ? "en" : null;
    }

    public static unsafe string DetectEls(string text)
    {
        char* buffer = stackalloc char[256];
        Marshal.ThrowExceptionForHR(Els(text, buffer, 256));
        return new string(buffer); // first (most relevant) candidate
    }

    public static int Run(string evidencePath, int samples)
    {
        var timer = Stopwatch.StartNew();
        DetectEls("warm-up sentence for the language detection service");
        double first = timer.Elapsed.TotalMilliseconds;
        string staged = XMatrix.Stage("detect-dispatch");
        string exe = Path.Combine(staged, Path.GetFileName(Environment.ProcessPath!));
        using var session = HostSession.Start(new HostSession.Options(exe, staged, "quickjs"));
        if (!session.Load("bench", "plugins/bench").Ok) throw new InvalidOperationException("bench load failed");
        var cases = new (string Name, string Text)[]
        {
            ("english", "The quick brown fox jumps over the lazy dog."),
            ("chinese", "今天的天气很好，我们去公园散步吧。"),
            ("mixed", "请把这个 API response 翻译成中文 please"),
        };
        var rows = new List<Row>();
        foreach (var (name, text) in cases)
        {
            List<double> detect = [], dispatch = [], total = [];
            string detected = "", path = "";
            for (int i = 0; i < samples + 3; i++)
            {
                var t = Stopwatch.StartNew();
                string? fast = FastPath(text);
                detected = fast ?? DetectEls(text);
                path = fast is null ? "els" : "unicode-fast-path";
                double d = t.Elapsed.TotalMilliseconds;
                string request = JsonSerializer.Serialize(new DispatchRequest(text, detected), DispatchJson.Default.DispatchRequest);
                var call = session.Invoke("bench", "translate", request, ["https://api.bench.example"]); // returns after the Invoke frame is written
                double all = t.Elapsed.TotalMilliseconds;
                if (!call.Result.Wait(5000)) throw new TimeoutException("call did not finish");
                if (i < 3) continue;
                detect.Add(d); dispatch.Add(all - d); total.Add(all);
            }
            rows.Add(new Row(name, path, EngineBench.Summarize(detect), EngineBench.Summarize(dispatch), EngineBench.Summarize(total), detected));
        }
        session.Shutdown();
        var report = new Report(DateTimeOffset.UtcNow, Math.Round(first, 3), [.. rows],
            ["Total = detection + grant issue + Invoke frame serialization and pipe write to the sandboxed plugin host (first request dispatched to the provider).",
             "ElsFirstCallMs is the one-time service resolution + first recognition in a process; the product should resolve ELS at startup or on first idle, not on the hotkey path."]);
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(report, DispatchJson.Default.Report));
        Console.WriteLine(JsonSerializer.Serialize(report, DispatchJson.Default.Report));
        return 0;
    }

    internal sealed record DispatchRequest(string Text, string From);

    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Report))]
    [JsonSerializable(typeof(DispatchRequest))]
    internal partial class DispatchJson : JsonSerializerContext;
}
