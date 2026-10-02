using System.Diagnostics;
using System.Runtime.InteropServices;
using Susu.Abstractions;
using Susu.Windows.Selection;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F08.1 helper against real windows: a Win32 EDIT created by the test (UIA level, password, empty selection), the
/// shipped susu_selection.dll IA2 vtable fixture, real child processes for the deadline kill, and the structural
/// "no settings/secrets/network" rule. Needs artifacts/windows-native (tools/build-native.ps1) and a desktop session.
/// </summary>
public class SelectionHostTests
{
    private static bool NativeAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, "susu_selection.dll"));
    /// <summary>The per-read UIA budget: generous, because the edit window answers from its own thread and a full parallel run can starve it for more than the 300 ms the product allows.</summary>
    private const int UiaBudgetMs = 5000;

    /// <summary>A top-level window with an EDIT child on its own pumping thread; the text "prefix selected text suffix".</summary>
    private sealed class EditWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private volatile bool _stop;
        private nint _parent;
        public nint Edit;

        public EditWindow(bool password, int start, int end)
        {
            _thread = new Thread(() =>
            {
                _parent = CreateWindowEx(0x08000000 /*WS_EX_NOACTIVATE*/, "STATIC", "Su-Su F08.1 selection test", 0x00CF0000, 60, 60, 520, 200, 0, 0, 0, 0);
                Edit = CreateWindowEx(0, "EDIT", "prefix selected text suffix", 0x50000000 | (password ? 0x0020u | 0x0080u : 0x0004u), 10, 10, 460, 100, _parent, 0, 0, 0);
                SendMessage(Edit, 0x00B1 /*EM_SETSEL*/, start, end);
                ShowWindow(_parent, 4 /*SW_SHOWNOACTIVATE*/);
                _ready.Set();
                while (!_stop)
                {
                    while (PeekMessage(out var msg, 0, 0, 0, 1)) { TranslateMessage(in msg); DispatchMessage(in msg); }
                    Thread.Sleep(5);
                }
                DestroyWindow(_parent);
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(5000);
            if (Edit == 0) throw new InvalidOperationException("EDIT window was not created.");
        }

        public void Dispose() { _stop = true; _thread.Join(5000); }
    }

    [Fact]
    public void Uia_reads_the_selected_text_with_rect_and_dpi()
    {
        if (!NativeAvailable) Assert.Skip("susu_selection.dll not built (tools/build-native.ps1)");
        using var window = new EditWindow(password: false, 7, 20);
        var reply = SelectionHost.Read(window.Edit, UiaBudgetMs);
        Assert.Equal("selected", reply.Reason);
        Assert.Equal("uia", reply.Source);
        Assert.Equal("selected text", reply.Text);
        Assert.Equal(1, reply.Ranges);
        Assert.NotNull(reply.Rect);
        Assert.True(reply.Rect![2] > reply.Rect[0] && reply.Rect[3] > reply.Rect[1]);
        Assert.InRange(reply.Dpi, 96, 480);
    }

    [Fact]
    public void Password_field_is_never_read()
    {
        if (!NativeAvailable) Assert.Skip("susu_selection.dll not built (tools/build-native.ps1)");
        using var window = new EditWindow(password: true, 0, 27);
        var reply = SelectionHost.Read(window.Edit, UiaBudgetMs);
        Assert.Equal("password", reply.Reason);
        Assert.Equal("", reply.Text);
        Assert.Null(reply.Rect);
    }

    [Fact]
    public void Empty_selection_is_an_explicit_failure_not_the_whole_text()
    {
        if (!NativeAvailable) Assert.Skip("susu_selection.dll not built (tools/build-native.ps1)");
        using var window = new EditWindow(password: false, 7, 7);
        var reply = SelectionHost.Read(window.Edit, UiaBudgetMs);
        Assert.Equal("empty", reply.Reason);
        Assert.Equal("", reply.Text);
    }

    [Fact]
    public void Host_mode_writes_one_reply_line()
    {
        if (!NativeAvailable) Assert.Skip("susu_selection.dll not built (tools/build-native.ps1)");
        using var window = new EditWindow(password: false, 7, 20);
        var output = new StringWriter();
        Assert.Equal(0, SelectionHost.Run(SelectionHost.Arguments(window.Edit, UiaBudgetMs), output, new StringWriter(), processWatchdog: false));
        Assert.Equal("selected text", SelectionHost.Parse(output.ToString().Trim()).Text);
    }

    [Fact]
    public void Ia2_level_rejects_invalid_ranges_and_merges_multiple_selections()
    {
        if (!NativeAvailable) Assert.Skip("susu_selection.dll not built (tools/build-native.ps1)");
        SelectionHost.SelfTest(); // synthetic IA2 vtable: bounds, 33 ranges, negative offsets, embedded NUL, "first\nnext"
    }

    [Fact]
    public void Snapshot_is_cheap_and_reports_the_foreground_process()
    {
        var platform = Win32SelectionPlatform.ForCurrentProcess();
        var timer = Stopwatch.StartNew();
        var snapshot = platform.Snapshot();
        Assert.True(timer.Elapsed < SelectionDeadlines.Snapshot, $"snapshot took {timer.ElapsedMilliseconds} ms");
        if (snapshot.Window != 0) Assert.NotEqual(0, snapshot.ProcessId);
    }

    [Fact]
    public async Task Real_hung_helper_process_is_terminated_at_the_deadline()
    {
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        // The helper arguments are appended after "#", so the stand-in helper just sleeps.
        var platform = new Win32SelectionPlatform(powershell, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 20 #"]);
        var reader = new SelectionReader(platform);
        var started = DateTime.Now;
        var timer = Stopwatch.StartNew();
        var result = await reader.ReadAsync(new ForegroundSnapshot(1, 1, 1, 1, "x", false, 0), CancellationToken.None);
        Assert.Equal(SelectionStatus.Timeout, result.Status);
        Assert.InRange(timer.ElapsedMilliseconds, 400, 1500);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Process.GetProcessesByName("powershell"), p => { try { return !p.HasExited && p.StartTime >= started.AddMilliseconds(-50) && p.StartTime < started.AddSeconds(2); } catch (Exception) { return false; } });
    }

    [Fact]
    public async Task Real_helper_process_reply_is_read_and_validated()
    {
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        string line = SelectionHost.Serialize(new HelperReply("selected", "hello", "uia", [1, 2, 30, 40], 96, 1)).Replace("'", "''");
        var platform = new Win32SelectionPlatform(powershell, ["-NoProfile", "-NonInteractive", "-Command", $"[Console]::Out.Write('{line}') #"]);
        using var helper = platform.Start(1, 300);
        var reply = await helper.Completion.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal("hello", reply.Text);
        Assert.Equal([1d, 2, 30, 40], reply.Rect!);
    }

    [Fact]
    public async Task Published_host_selection_mode_reads_a_real_window_within_the_deadline()
    {
        string? exe = FindPublishedHost();
        if (exe is null || !NativeAvailable) Assert.Skip("publish susu.exe (dev.ps1 publish) to run the end-to-end helper test");
        using var window = new EditWindow(password: false, 7, 20);
        // The EDIT is not foreground (the reader would report FocusChanged), so drive the real helper process directly.
        using var helper = new Win32SelectionPlatform(exe!, [SelectionHost.Mode]).Start(window.Edit, UiaBudgetMs);
        var reply = await helper.Completion.WaitAsync(SelectionDeadlines.Acquire + TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal("selected text", reply.Text);
        Assert.Equal("uia", reply.Source);
    }

    [Fact]
    public void Helper_assembly_references_no_settings_secrets_plugin_or_network_code()
    {
        // Structural SEL rule (ARCHITECTURE 4.1): the helper assembly and everything it can reach reference no
        // storage/secrets, plugin, UI or network assembly.
        var seen = new HashSet<string>();
        var queue = new Queue<System.Reflection.Assembly>([typeof(SelectionHost).Assembly]);
        while (queue.Count > 0)
        {
            var assembly = queue.Dequeue();
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                string name = reference.Name!;
                if (!seen.Add(name)) continue;
                Assert.False(name.StartsWith("System.Net", StringComparison.Ordinal) || name.StartsWith("Microsoft.Data", StringComparison.Ordinal), $"{assembly.GetName().Name} references {name}");
                if (name.StartsWith("Susu.", StringComparison.Ordinal))
                {
                    Assert.Contains(name, new[] { "Susu.Abstractions", "Susu.Domain", "Susu.Contracts" });
                    queue.Enqueue(System.Reflection.Assembly.Load(reference));
                }
            }
        }
        Assert.Contains("Susu.Abstractions", seen);
    }

    private static string? FindPublishedHost()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish", "susu.exe");
            if (File.Exists(candidate) && File.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, "susu_selection.dll"))) return candidate;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Msg { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out Msg message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(in Msg message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(in Msg message);
}
