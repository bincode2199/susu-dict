using System.Diagnostics;
using System.Text.Json;
using Susu.Contracts;
using Susu.Ui;
using Susu.Windows.Shell;

namespace Susu.Host;

/// <summary>
/// <c>susu --smoke report.json [--cycles N]</c>: a scripted, input-free run of the real shell for F03 evidence.
/// Each cycle opens every window kind through the coordinator, waits for the page's Ready + snapshot, then closes
/// it. Afterwards it waits for the keep-warm release (shortened to 2 s in this mode), for BrowserProcessExited,
/// reopens Settings once from cold, and releases again. Timings are open → UiReady in milliseconds.
/// </summary>
internal sealed class Smoke(string reportPath, int cycles, ShellCoordinator coordinator, WindowPlatform platform, UiDispatcher dispatcher, TrayIcon tray, bool development)
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, List<double>> samples = [];
    private readonly List<string> errors = [];
    private readonly List<string> diagnostics = [];
    private TaskCompletionSource<WindowKind>? ready;
    private TaskCompletionSource? released, exited;
    private double releaseMs = -1, exitMs = -1, secondExitMs = -1;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly List<(string Point, long PrivateBytes, long ManagedBytes, int Handles, int Threads)> process = [];

    private void Sample(string point)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var self = Process.GetCurrentProcess();
        process.Add((point, self.PrivateMemorySize64, GC.GetTotalMemory(forceFullCollection: true), self.HandleCount, self.Threads.Count));
    }

    public int ExitCode { get; private set; } = 1;

    public void Start()
    {
        coordinator.PageReady += kind => ready?.TrySetResult(kind);
        coordinator.Diagnostic += message =>
        {
            if (message == "webview.released") released?.TrySetResult();
            else if (diagnostics.Count < 50) diagnostics.Add(message);
        };
        platform.Diagnostic += message => { if (diagnostics.Count < 50) diagnostics.Add(message); };
        platform.BrowserExited += () => exited?.TrySetResult();
        dispatcher.Post(async () =>
        {
            try { await RunAsync(); }
            catch (Exception e) { errors.Add($"{e.GetType().Name}: {e.Message}"); }
            platform.Exit();
        });
    }

    private async Task RunAsync()
    {
        var kinds = development ? new[] { WindowKind.Settings, WindowKind.Main, WindowKind.Tray } : [WindowKind.Settings, WindowKind.Tray];
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            foreach (var kind in kinds)
                await OpenAndCloseAsync(kind, cycle == 0 ? "cold" : "warm");
            if (cycle == 0) Sample("after-first-cycle");
            else if ((cycle + 1) % 50 == 0 && cycle + 1 < cycles) Sample($"after-cycle-{cycle + 1}");
        }
        Sample("after-last-cycle");

        var watch = Stopwatch.StartNew();
        released = new();
        exited = new();
        if (!await Within(released.Task)) { errors.Add("keep-warm release did not happen"); return; }
        releaseMs = watch.Elapsed.TotalMilliseconds;
        if (!await Within(exited.Task)) { errors.Add("BrowserProcessExited not observed after release"); return; }
        exitMs = watch.Elapsed.TotalMilliseconds;
        Sample("after-release");

        // Reopen after a full release: the environment and controller are rebuilt and the page gets a fresh session.
        await OpenAndCloseAsync(WindowKind.Settings, "after-release");
        watch.Restart();
        released = new();
        exited = new();
        if (!await Within(released.Task) || !await Within(exited.Task)) { errors.Add("second release/exit not observed"); return; }
        secondExitMs = watch.Elapsed.TotalMilliseconds;
        Sample("after-second-release");
        ExitCode = errors.Count == 0 ? 0 : 1;
    }

    private async Task OpenAndCloseAsync(WindowKind kind, string phase)
    {
        ready = new();
        var watch = Stopwatch.StartNew();
        coordinator.Open(kind);
        if (!await Within(ready.Task)) { errors.Add($"{kind} {phase}: page did not become ready"); return; }
        Add($"{kind}.{phase}", watch.Elapsed.TotalMilliseconds);
        await Task.Delay(150);
        coordinator.OnWindowRequest(kind, kind == WindowKind.Tray ? Domain.WindowRequest.Escape : Domain.WindowRequest.Close);
        await Task.Delay(100);
    }

    private static async Task<bool> Within(Task task) => await Task.WhenAny(task, Task.Delay(StepTimeout)) == task;

    private void Add(string key, double ms)
    {
        if (!samples.TryGetValue(key, out var list)) samples[key] = list = [];
        list.Add(ms);
    }

    public void Finish()
    {
        using var stream = File.Create(reportPath);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("startedAt", started);
        json.WriteString("scope", "F03 shell lifecycle smoke: real susu.exe, WebView2, coordinator-driven (no synthetic input); open→UiReady timings in ms");
        json.WriteString("webViewRuntime", WebViewRuntime.InstalledVersion());
        json.WriteBoolean("developmentBuild", development);
        json.WriteBoolean("trayIconAdded", tray.Added);
        json.WriteNumber("cycles", cycles);
        json.WriteStartObject("openToReadyMs");
        foreach (var (key, list) in samples.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var sorted = list.Order().ToArray();
            json.WriteStartObject(key);
            json.WriteNumber("samples", sorted.Length);
            json.WriteNumber("p50", Math.Round(Percentile(sorted, 0.50), 1));
            json.WriteNumber("p95", Math.Round(Percentile(sorted, 0.95), 1));
            json.WriteNumber("max", Math.Round(sorted[^1], 1));
            json.WriteEndObject();
        }
        json.WriteEndObject();
        json.WriteNumber("releaseAfterLastHideMs", Math.Round(releaseMs, 1));
        json.WriteNumber("browserExitedAfterLastHideMs", Math.Round(exitMs, 1));
        json.WriteNumber("secondCycleBrowserExitedMs", Math.Round(secondExitMs, 1));
        json.WriteStartArray("hostProcess");
        foreach (var (point, bytes, managed, handles, threads) in process)
        {
            json.WriteStartObject();
            json.WriteString("point", point);
            json.WriteNumber("privateMiB", Math.Round(bytes / 1048576.0, 2));
            json.WriteNumber("managedHeapMiB", Math.Round(managed / 1048576.0, 2));
            json.WriteNumber("handles", handles);
            json.WriteNumber("threads", threads);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("errors");
        foreach (var e in errors) json.WriteStringValue(e);
        json.WriteEndArray();
        json.WriteStartArray("diagnostics");
        foreach (var d in diagnostics) json.WriteStringValue(d);
        json.WriteEndArray();
        json.WriteBoolean("passed", ExitCode == 0);
        json.WriteEndObject();
    }

    public static void WriteFailure(string reportPath, string reason)
        => File.WriteAllText(reportPath, $"{{\"passed\":false,\"errors\":[\"{reason}\"]}}");

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return -1;
        double rank = p * (sorted.Length - 1);
        int low = (int)Math.Floor(rank), high = (int)Math.Ceiling(rank);
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }
}

/// <summary>
/// <c>susu --measure events.jsonl</c> (PER02 with production windows): opens every window this build offers,
/// waits for each page to be ready, hides them all and records the hide tick (GetTickCount64 clock), then
/// keeps running under the real 10-minute keep-warm rule and exits 905 s later. An external sampler
/// (tools/measure-windows.ps1 -ArgumentList ...) measures the whole process tree meanwhile.
/// </summary>
internal sealed class Measure(string eventsPath, ShellCoordinator coordinator, WindowPlatform platform, UiDispatcher dispatcher, bool development)
{
    public int ExitCode { get; private set; } = 1;

    private void Write(string name) => File.AppendAllText(eventsPath, $"{{\"event\":\"{name}\",\"tick\":{Environment.TickCount64}}}\n");

    public void Start()
    {
        TaskCompletionSource<WindowKind>? ready = null;
        coordinator.PageReady += kind => ready?.TrySetResult(kind);
        platform.BrowserExited += () => Write("browser-exited");
        coordinator.Diagnostic += message => { if (message == "webview.released") Write("released"); };
        dispatcher.Post(async () =>
        {
            var kinds = development ? new[] { WindowKind.Settings, WindowKind.Main, WindowKind.Tray } : [WindowKind.Settings, WindowKind.Tray];
            foreach (var kind in kinds)
            {
                ready = new();
                coordinator.Open(kind);
                if (await Task.WhenAny(ready.Task, Task.Delay(30_000)) != ready.Task) { Write($"not-ready-{kind}"); platform.Exit(); return; }
                Write($"ready-{kind}");
            }
            await Task.Delay(1000);
            foreach (var kind in kinds) coordinator.OnWindowRequest(kind, kind == WindowKind.Tray ? Domain.WindowRequest.Escape : Domain.WindowRequest.Close);
            Write("all-hidden");
            await Task.Delay(TimeSpan.FromSeconds(905));
            ExitCode = 0;
            platform.Exit();
        });
    }
}
