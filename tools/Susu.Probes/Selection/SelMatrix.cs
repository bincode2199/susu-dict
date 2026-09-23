using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Susu.Probes.PluginHost;

namespace Susu.Probes.Selection;

/// <summary>
/// SEL01 ten-program matrix: each program shows synthetic marker text, the driver selects it with
/// real keyboard input, then the bounded helper (UIA → IA2) reads it. Cold first query is measured
/// with a 5 s diagnostic deadline; warm queries use the product's 500 ms helper deadline.
/// </summary>
internal static partial class SelMatrix
{
    internal sealed record Sample(double HelperMs, string Reason, string Source, bool Matched, double UiaMs, double Ia2Ms, string? Error, string? Excerpt = null);
    internal sealed record AppResult(
        string Id, string Program, string Version, string Framework, string Mode, bool WindowFound, bool Activated,
        Sample? Cold, Sample[] Warm, Sample? Ia2Only, Sample? Password, Sample? Empty, double[]? Rect, string? Note,
        string? SelectMethod = null, int Providers = 0, int PasswordElements = 0);
    internal sealed record Report(DateTimeOffset Timestamp, string OS, string Scope, AppResult[] Apps, string[] Notes);

    private sealed record App(string Id, string Program, string Framework, Func<string> Version, Func<Process?> Launch, Func<string, bool> Title,
        Action<nint> Select, Action<nint>? FocusPassword = null, Action<nint>? CollapseSelection = null, Func<nint, bool>? Extra = null, string? Note = null, Action<nint>? Prepare = null);

    private const string Marker = "Su-Su selection marker";

    public static int Run(string evidencePath, int warm, string? only = null)
    {
        string root = Path.GetFullPath(".");
        string fixtures = Path.Combine(AppContext.BaseDirectory, "selection");
        string page = new Uri(Path.Combine(fixtures, "page.html")).AbsoluteUri;
        string apps = Path.Combine(root, ".tools", "test-apps");
        string work = Path.Combine(root, "artifacts", "probe-data", "sel-matrix", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string textFile = Path.Combine(work, "Su-Su-SEL-fixture.txt");
        File.WriteAllText(textFile, "Su-Su selection marker alpha beta 中文 gamma (Notepad)");
        string firefoxProfile = Path.Combine(work, "firefox-profile");
        Directory.CreateDirectory(firefoxProfile);
        File.WriteAllText(Path.Combine(firefoxProfile, "user.js"), string.Join('\n',
            "user_pref(\"browser.shell.checkDefaultBrowser\", false);",
            "user_pref(\"browser.aboutwelcome.enabled\", false);",
            "user_pref(\"startup.homepage_welcome_url\", \"\");",
            "user_pref(\"browser.startup.homepage_override.mstone\", \"ignore\");",
            "user_pref(\"datareporting.policy.dataSubmissionEnabled\", false);",
            "user_pref(\"toolkit.telemetry.reportingpolicy.firstRun\", false);",
            "user_pref(\"browser.tabs.warnOnClose\", false);"));
        string fixturesExe = Path.Combine(root, "artifacts", "sel-fixtures", "Susu.SelectionFixtures.exe");
        string edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        string firefox = Path.Combine(apps, "firefox", "firefox.exe");
        string electronApp = Path.Combine(fixtures, "electron-app");

        Process? Start(string file, params string[] args)
        {
            var info = new ProcessStartInfo(file) { UseShellExecute = false };
            info.Environment["DOTNET_ROOT"] = Path.Combine(root, ".tools", "dotnet"); // framework-dependent WPF/WinForms fixture
            foreach (string a in args) info.ArgumentList.Add(a);
            return Process.Start(info);
        }
        string FileVersion(string path) => File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "?" : "missing";
        void ClickPage(nint hwnd, int dy = 180)
        {
            Desktop.GetWindowRect(hwnd, out var r);
            Desktop.Click(r.Left + 60, r.Top + dy);
            Thread.Sleep(200);
        }
        void SelectAll(nint _) => Desktop.Chord(Desktop.VkControl, Desktop.VkA);

        var list = new List<App>
        {
            new("notepad", "Notepad (Windows 11)", "WinUI/RichEdit", () => "Microsoft.WindowsNotepad (Store)",
                () => Start("notepad.exe", textFile), t => t.Contains("Su-Su-SEL-fixture", StringComparison.Ordinal),
                SelectAll, CollapseSelection: _ => Desktop.Chord(Desktop.VkEnd)),
            new("charmap", "Character Map", "Win32 Edit/RichEdit", () => FileVersion(Path.Combine(Environment.SystemDirectory, "charmap.exe")),
                () => Start("charmap.exe"), t => t == "Character Map",
                SelectAll, CollapseSelection: _ => Desktop.Chord(Desktop.VkEnd),
                Prepare: hwnd => { if (!Desktop.FillAndFocus(hwnd, c => c.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase) || c == "Edit", "Su-Su selection marker alpha beta (charmap)")) throw new InvalidOperationException("charmap edit not found"); Thread.Sleep(200); }),
            new("wpf", "WPF TextBox fixture (.NET 10)", "WPF", () => FileVersion(fixturesExe),
                () => Start(fixturesExe, "wpf", "text"), t => t == "Su-Su SEL fixture WPF text", SelectAll,
                FocusPassword: _ => Desktop.Chord(Desktop.VkTab), CollapseSelection: _ => Desktop.Chord(Desktop.VkEnd)),
            new("winforms", "WinForms RichTextBox fixture (.NET 10)", "WinForms/RichEdit", () => FileVersion(fixturesExe),
                () => Start(fixturesExe, "winforms", "text"), t => t == "Su-Su SEL fixture WinForms text", SelectAll,
                FocusPassword: _ => Desktop.Chord(Desktop.VkTab), CollapseSelection: _ => Desktop.Chord(Desktop.VkEnd)),
            new("edge", "Microsoft Edge", "Chromium", () => FileVersion(edge),
                () => Start(edge, "--new-window", "--no-first-run", "--no-default-browser-check", $"--user-data-dir={Path.Combine(work, "edge-profile")}", page),
                t => t.StartsWith("Su-Su SEL fixture page", StringComparison.Ordinal) && t.Contains("Edge", StringComparison.Ordinal),
                hwnd => { ClickPage(hwnd, 260); SelectAll(hwnd); }, FocusPassword: hwnd => { ClickPage(hwnd, 260); Desktop.Chord(Desktop.VkTab); }),
            new("firefox", "Mozilla Firefox", "Gecko", () => FileVersion(firefox),
                () => Start(firefox, "-no-remote", "-profile", firefoxProfile, "-new-window", page),
                t => t.StartsWith("Su-Su SEL fixture page", StringComparison.Ordinal) && t.Contains("Firefox", StringComparison.Ordinal),
                hwnd => { ClickPage(hwnd, 260); SelectAll(hwnd); }, FocusPassword: hwnd => { ClickPage(hwnd, 260); Desktop.Chord(Desktop.VkTab); }),
            new("electron-22", "Electron 22 (old Chromium 108)", "Electron/Chromium", () => FileVersion(Path.Combine(apps, "electron-22.3.27", "electron.exe")),
                () => Start(Path.Combine(apps, "electron-22.3.27", "electron.exe"), electronApp), t => t.StartsWith("Su-Su SEL fixture Electron 22", StringComparison.Ordinal),
                hwnd => { ClickPage(hwnd, 200); SelectAll(hwnd); }, FocusPassword: hwnd => { ClickPage(hwnd, 200); Desktop.Chord(Desktop.VkTab); }),
            new("electron-44", "Electron 44 (current)", "Electron/Chromium", () => FileVersion(Path.Combine(apps, "electron-44.4.5", "electron.exe")),
                () => Start(Path.Combine(apps, "electron-44.4.5", "electron.exe"), electronApp), t => t.StartsWith("Su-Su SEL fixture Electron 44", StringComparison.Ordinal),
                hwnd => { ClickPage(hwnd, 200); SelectAll(hwnd); }, FocusPassword: hwnd => { ClickPage(hwnd, 200); Desktop.Chord(Desktop.VkTab); }),
            new("terminal", "Windows Terminal", "WinUI/DirectX terminal", () => "Microsoft.WindowsTerminal (Store)",
                () => Start("wt.exe", "-w", "new", "--title", "Su-Su SEL terminal", "--suppressApplicationTitle", "cmd.exe", "/k", "echo Su-Su selection marker alpha beta (terminal)"),
                t => t.Contains("Su-Su SEL terminal", StringComparison.Ordinal),
                _ => { Thread.Sleep(500); Desktop.Chord(Desktop.VkControl, Desktop.VkShift, Desktop.VkA); }),
            new("conhost", "Console Host (conhost)", "Win32 console", () => FileVersion(Path.Combine(Environment.SystemDirectory, "conhost.exe")),
                () => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "conhost.exe"), "cmd.exe /k \"title Su-Su SEL console& echo Su-Su selection marker alpha beta (console)\"") { UseShellExecute = true }),
                t => t.Contains("Su-Su SEL console", StringComparison.Ordinal),
                _ => { Thread.Sleep(500); SelectAll(0); }),
        };

        bool interactive = Desktop.InputAvailable();
        Console.Error.WriteLine($"interactive desktop: {interactive}");
        var results = new List<AppResult>();
        foreach (var app in list.Where(a => only is null || only.Split(',').Contains(a.Id)))
        {
            Console.Error.WriteLine($"== {app.Id}");
            results.Add(interactive ? RunApp(app, warm) : RunProvider(app, warm));
        }
        var notes = new List<string>
        {
            "Marker text is synthetic; programs were launched by the driver with isolated profiles/files and closed afterwards.",
            "Selection is made with real SendInput (Ctrl+A, Ctrl+Shift+A for Windows Terminal); browser pages are first clicked to focus the document.",
            "Cold = first helper query after the program started (accessibility tree may be built lazily), 5 s diagnostic deadline; the text is then selected again and warm = product 500 ms helper deadline including helper process start.",
            "IA2-only row forces the second level on the same selection to show whether IA2 would work where UIA already does.",
            "Password row: Tab moves focus to the fixture's password field; expected reason 'password' with no text.",
        };
        var report = new Report(DateTimeOffset.UtcNow, Environment.OSVersion.VersionString, "F00 SEL01 prototype matrix (not F08 acceptance)", [.. results], [.. notes]);
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(report, SelJson.Default.Report));
        foreach (var r in results)
            Console.Error.WriteLine($"{r.Id,-12} window={r.WindowFound} cold={r.Cold?.Reason}/{r.Cold?.Source}/{r.Cold?.HelperMs:F0}ms match={r.Cold?.Matched} warmOk={r.Warm.Count(w => w.Matched)}/{r.Warm.Length} p95={(r.Warm.Length > 0 ? EngineBench.Summarize([.. r.Warm.Select(w => w.HelperMs)]).P95 : 0)} ia2={r.Ia2Only?.Reason} pwd={r.Password?.Reason} empty={r.Empty?.Reason}");
        return 0;
    }

    private static Sample Query(nint hwnd, bool ia2Only, int deadlineMs)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var helper = SelectionTests.Start(ia2Only ? "--ia2-child" : "--selection-child", hwnd.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string output = SelectionTests.ReadBounded(helper, deadlineMs);
            double ms = timer.Elapsed.TotalMilliseconds;
            var result = JsonSerializer.Deserialize(output, ProbeJson.Default.SelectionResult)!;
            return new Sample(Math.Round(ms, 1), result.Reason, result.Source, result.Text.Contains(Marker, StringComparison.Ordinal), Math.Round(result.UiaMs, 1), Math.Round(result.Ia2Ms, 1), null, result.Text.Length > 90 ? result.Text[..90] : result.Text);
        }
        catch (Exception error) { return new Sample(Math.Round(timer.Elapsed.TotalMilliseconds, 1), "error", "none", false, 0, 0, $"{error.GetType().Name}: {error.Message}"); }
    }

    private static double[]? LastRect(nint hwnd)
    {
        try
        {
            using var helper = SelectionTests.Start("--selection-child", hwnd.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var result = JsonSerializer.Deserialize(SelectionTests.ReadBounded(helper, 2000), ProbeJson.Default.SelectionResult)!;
            return result.Rect;
        }
        catch { return null; }
    }

    private static AppResult RunApp(App app, int warmCount)
    {
        string version = app.Version();
        Process? launched = null;
        nint hwnd = 0;
        try
        {
            launched = app.Launch();
            hwnd = Desktop.WaitForWindow(app.Title, 30000, app.Extra);
            if (hwnd == 0) return new AppResult(app.Id, app.Program, version, app.Framework, "input", false, false, null, [], null, null, null, null, "window not found within 30 s");
            Thread.Sleep(2500); // let the page/document finish loading
            bool active = Desktop.Activate(hwnd);
            if (!active) return new AppResult(app.Id, app.Program, version, app.Framework, "input", true, false, null, [], null, null, null, null, "could not activate window (foreground lock)");
            app.Prepare?.Invoke(hwnd);
            app.Select(hwnd);
            Thread.Sleep(400);
            if (Desktop.GetForegroundWindow() != hwnd) Desktop.Activate(hwnd);
            var cold = Query(hwnd, false, 5000);
            // Providers that build accessibility state lazily (Chromium, Firefox cache domains) only see
            // selections made after the first query: select again before the warm samples.
            if (Desktop.GetForegroundWindow() != hwnd) Desktop.Activate(hwnd);
            app.Select(hwnd);
            Thread.Sleep(400);
            var warm = new List<Sample>();
            for (int i = 0; i < warmCount; i++)
            {
                if (Desktop.GetForegroundWindow() != hwnd) Desktop.Activate(hwnd);
                warm.Add(Query(hwnd, false, 500));
            }
            var rect = LastRect(hwnd);
            var ia2 = Query(hwnd, true, 2000);
            Sample? empty = null, password = null;
            if (app.CollapseSelection is not null)
            {
                Desktop.Activate(hwnd);
                app.CollapseSelection(hwnd);
                Thread.Sleep(300);
                empty = Query(hwnd, false, 2000);
            }
            if (app.FocusPassword is not null)
            {
                Desktop.Activate(hwnd);
                app.FocusPassword(hwnd);
                Thread.Sleep(400);
                password = Query(hwnd, false, 2000);
            }
            return new AppResult(app.Id, app.Program, version, app.Framework, "input", true, true, cold, [.. warm], ia2, password, empty, rect, app.Note);
        }
        catch (Exception error)
        {
            return new AppResult(app.Id, app.Program, version, app.Framework, "input", hwnd != 0, false, null, [], null, null, null, null, $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            if (hwnd != 0 && Desktop.IsWindow(hwnd))
            {
                Desktop.GetWindowThreadProcessId(hwnd, out uint pid);
                Desktop.Close(hwnd);
                var deadline = Environment.TickCount64 + 5000;
                while (Desktop.IsWindow(hwnd) && Environment.TickCount64 < deadline) Thread.Sleep(100);
                if (Desktop.IsWindow(hwnd)) { try { Process.GetProcessById((int)pid).Kill(true); } catch (ArgumentException) { } }
            }
            try { if (launched is { HasExited: false }) launched.Kill(true); } catch (InvalidOperationException) { }
            launched?.Dispose();
            Thread.Sleep(500);
        }
    }

    /// <summary>
    /// Non-interactive session: selection created through UIA Select()/IA2 setSelection and read by
    /// searching the window's providers (harness-only path). Answers provider capability (UIA vs IA2),
    /// cold tree construction and query latency; hotkey-time focus behaviour is not exercised.
    /// </summary>
    private static AppResult RunProvider(App app, int warmCount)
    {
        string version = app.Version();
        Process? launched = null;
        nint hwnd = 0;
        static Sample ToSample((ProviderProbe.Output? Result, double TotalMs) r, string expectKind)
        {
            var o = r.Result;
            string reason = o?.Error is not null ? "error" : o?.Text.Length > 0 ? "selected" : "empty";
            string source = o?.Method switch { 1 => "uia", 2 => "ia2", _ => "none" };
            return new Sample(Math.Round(r.TotalMs, 1), reason, source, o?.Text.Contains(Marker, StringComparison.Ordinal) == true, Math.Round(o?.Ms ?? 0, 1), 0, o?.Error);
        }
        try
        {
            launched = app.Launch();
            hwnd = Desktop.WaitForWindow(app.Title, 30000, app.Extra);
            if (hwnd == 0) return new AppResult(app.Id, app.Program, version, app.Framework, "provider", false, false, null, [], null, null, null, null, "window not found within 30 s");
            Thread.Sleep(3000);
            app.Prepare?.Invoke(hwnd);
            var select = ProviderProbe.Invoke("select", hwnd, Marker, 15000);
            var selectSample = new Sample(Math.Round(select.TotalMs, 1), select.Result?.Error is not null ? "error" : select.Result?.Method > 0 ? "selected" : "not-found",
                select.Result?.Method switch { 1 => "uia", 2 => "ia2", _ => "none" }, select.Result?.Method > 0, Math.Round(select.Result?.Ms ?? 0, 1), 0, select.Result?.Error);
            Thread.Sleep(300);
            var warm = new List<Sample>();
            int passwords = 0;
            for (int i = 0; i < warmCount; i++)
            {
                var read = ProviderProbe.Invoke("read", hwnd, Marker, 3000);
                passwords = Math.Max(passwords, read.Result?.PasswordElements ?? 0);
                warm.Add(ToSample(read, "read"));
            }
            var ia2 = ToSample(ProviderProbe.Invoke("read-ia2", hwnd, Marker, 5000), "read-ia2");
            return new AppResult(app.Id, app.Program, version, app.Framework, "provider", true, false, selectSample, [.. warm], ia2, null, null, null,
                "Non-interactive session: selection made via provider API; cold = first provider search + select (includes lazy tree build).",
                selectSample.Source, select.Result?.Providers ?? 0, passwords);
        }
        catch (Exception error)
        {
            return new AppResult(app.Id, app.Program, version, app.Framework, "provider", hwnd != 0, false, null, [], null, null, null, null, $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            if (hwnd != 0 && Desktop.IsWindow(hwnd))
            {
                Desktop.GetWindowThreadProcessId(hwnd, out uint pid);
                Desktop.Close(hwnd);
                var deadline = Environment.TickCount64 + 5000;
                while (Desktop.IsWindow(hwnd) && Environment.TickCount64 < deadline) Thread.Sleep(100);
                if (Desktop.IsWindow(hwnd)) { try { Process.GetProcessById((int)pid).Kill(true); } catch (ArgumentException) { } }
            }
            try { if (launched is { HasExited: false }) launched.Kill(true); } catch (InvalidOperationException) { }
            launched?.Dispose();
            Thread.Sleep(500);
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Report))]
    internal partial class SelJson : JsonSerializerContext;
}
