using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Windows.Clipboard;
using Susu.Windows.Selection;
using Xunit;

namespace Susu.Tests.Unit;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class F08RealProcessCollection { public const string Name = "F08RealProcess"; }

/// <summary>
/// F08 independent verification, C05 against the real clipboard: a second real process (Windows PowerShell with
/// WinForms, STA) writes the clipboard while the borrow waits for the target's copy. Its update must never become the
/// candidate, and the old snapshot must never be restored over it.
/// </summary>
[Collection(RealClipboardCollection.Name)]
public class F08ClipboardRaceVerificationTests
{
    private const uint CF_UNICODETEXT = 13;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>A separate process that sets the clipboard text when it reads a line on stdin, then stays alive until stdin closes.</summary>
    private sealed class Intruder : IDisposable
    {
        private readonly Process _process;
        public int ProcessId => _process.Id;

        public Intruder(string text)
        {
            var info = new ProcessStartInfo(PowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-Sta", "-NoProfile", "-NonInteractive", "-Command",
                $"Add-Type -AssemblyName System.Windows.Forms; [Console]::Out.WriteLine('ready'); [Console]::Out.Flush(); [void][Console]::In.ReadLine(); [System.Windows.Forms.Clipboard]::SetText('{text}'); [Console]::Out.WriteLine('done'); [Console]::Out.Flush(); [void][Console]::In.ReadLine()" })
                info.ArgumentList.Add(a);
            _process = Process.Start(info)!;
            var ready = _process.StandardOutput.ReadLineAsync(Ct).AsTask();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "intruder did not start");
            Assert.Equal("ready", ready.Result);
        }

        public void Signal() { _process.StandardInput.WriteLine("go"); _process.StandardInput.Flush(); }

        public void WaitWritten()
        {
            var done = _process.StandardOutput.ReadLineAsync(Ct).AsTask();
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "intruder did not write");
            Assert.Equal("done", done.Result);
        }

        public void Dispose()
        {
            try { _process.StandardInput.Close(); if (!_process.WaitForExit(3000)) _process.Kill(); } catch (Exception) { }
            _process.Dispose();
        }
    }

    private sealed class Platform(nint foreground, Action copy) : IClipboardPlatform
    {
        private readonly Win32ClipboardPlatform _win32 = new("unused");
        public readonly List<(uint Sequence, int Owner)> Restores = [];
        public readonly List<ClipboardUpdate> Updates = [];
        public int Copies;
        public nint ForegroundWindow() => foreground;
        public uint SequenceNumber() => _win32.SequenceNumber();
        public bool ModifiersDown() => false;
        public bool ProcessRunning(int processId, long startTime) => _win32.ProcessRunning(processId, startTime);
        public void SendCopy() { Copies++; copy(); }
        public async Task<IClipboardHelper> StartHelperAsync(CancellationToken cancellationToken) => new Recording(await StartInProcessAsync(cancellationToken), this);
    }

    private sealed class Recording(IClipboardHelper inner, Platform owner) : IClipboardHelper
    {
        public int UpdateCount => inner.UpdateCount;
        public IReadOnlyList<ClipboardUpdate> UpdatesSince(int index) { var u = inner.UpdatesSince(index); lock (owner.Updates) { owner.Updates.Clear(); owner.Updates.AddRange(inner.UpdatesSince(0)); } return u; }
        public Task WaitForUpdateAsync(int count, CancellationToken cancellationToken) => inner.WaitForUpdateAsync(count, cancellationToken);
        public Task<ClipboardSnapshotInfo> SnapshotAsync(CancellationToken cancellationToken) => inner.SnapshotAsync(cancellationToken);
        public Task<ClipboardText> ReadTextAsync(CancellationToken cancellationToken) => inner.ReadTextAsync(cancellationToken);
        public Task<ClipboardRestoreResult> RestoreAsync(uint expectedSequence, int expectedOwnerPid, TimeSpan openWait, CancellationToken cancellationToken)
        { lock (owner.Restores) owner.Restores.Add((expectedSequence, expectedOwnerPid)); return inner.RestoreAsync(expectedSequence, expectedOwnerPid, openWait, cancellationToken); }
        public void Dispose() => inner.Dispose();
    }

    private static async Task<ClipboardHelperConnection> StartInProcessAsync(CancellationToken cancel)
    {
        var toHost = new AnonymousPipeServerStream(PipeDirection.Out);
        var hostIn = new AnonymousPipeClientStream(PipeDirection.In, toHost.ClientSafePipeHandle);
        var fromHost = new AnonymousPipeServerStream(PipeDirection.In);
        var hostOut = new AnonymousPipeClientStream(PipeDirection.Out, fromHost.ClientSafePipeHandle);
        var thread = new Thread(() =>
        {
            try { ClipboardHost.Run(hostIn, hostOut, processWatchdog: false); }
            catch (IOException) { }
            finally { hostOut.Dispose(); hostIn.Dispose(); }
        }) { IsBackground = true, Name = "f08-verify-clipboard-host" };
        thread.Start();
        var connection = new ClipboardHelperConnection(new StreamWriter(toHost, new UTF8Encoding(false)) { NewLine = "\n" }, new StreamReader(fromHost, Encoding.UTF8), () => { });
        await connection.Ready.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        return connection;
    }

    /// <summary>The target is this test process (a message-only owner window stands in for its copy).</summary>
    private static ForegroundSnapshot Target => new(4321, 4321, Environment.ProcessId, 0, "Static", false, 0);

    [Fact] // C05: only an unrelated process updates the clipboard during the copy wait
    public async Task C05_update_from_a_real_second_process_is_never_the_candidate_and_is_not_restored_over()
    {
        PutText("original");
        using var intruder = new Intruder("intruder text");
        var platform = new Platform(4321, intruder.Signal);
        var result = await new ClipboardBorrower(platform).BorrowAsync(Target, Ct);
        intruder.WaitWritten();
        TestContext.Current.TestOutputHelper?.WriteLine($"{result.Status} {result.Reason} restore={result.Restore} updates=[{string.Join(", ", platform.Updates.Select(u => $"{u.Sequence}/{u.OwnerProcessId}"))}] intruder={intruder.ProcessId} self={Environment.ProcessId}");
        Assert.Equal(1, platform.Copies);
        Assert.NotEqual(BorrowStatus.Borrowed, result.Status);
        Assert.Equal("", result.Text);
        Assert.Empty(platform.Restores);            // the old snapshot is never written over the other process's content
        Assert.DoesNotContain(platform.Updates, u => u.OwnerProcessId == Environment.ProcessId);
        await Task.Delay(300, Ct);
        Assert.Equal("intruder text", ReadText());
    }

    [Fact] // C05: the target copies and another process writes right after; the other process's content always survives
    public async Task C05_target_copy_raced_by_a_real_second_process_keeps_the_newest_content()
    {
        PutText("original");
        using var intruder = new Intruder("intruder wins");
        var platform = new Platform(4321, () => { PutText("selected by target"); intruder.Signal(); });
        var result = await new ClipboardBorrower(platform).BorrowAsync(Target, Ct);
        intruder.WaitWritten();
        TestContext.Current.TestOutputHelper?.WriteLine($"{result.Status} {result.Reason} restore={result.Restore} restores={platform.Restores.Count}");
        Assert.NotEqual("intruder wins", result.Text); // never translates the other process's text
        await Task.Delay(300, Ct);
        Assert.Equal("intruder wins", ReadText());     // and never overwrites it with the old snapshot
    }

    private sealed class NoReader : ISelectionReader
    {
        public int Calls;
        public ForegroundSnapshot Snapshot() { Calls++; return Target; }
        public Task<SelectionResult> ReadAsync(ForegroundSnapshot snapshot, CancellationToken cancellationToken) { Calls++; return Task.FromResult(SelectionResult.Failure(SelectionStatus.Unsupported)); }
        public void Prime(nint window) { }
    }

    [Fact] // DEV-PLAN F08 demo: the clipboard command reads the existing clipboard directly (real clipboard, real helper code), no selection, no Ctrl+C, nothing changed
    public async Task Clipboard_command_reads_the_real_existing_clipboard_without_copying()
    {
        PutText("already on the clipboard");
        uint before = GetClipboardSequenceNumber();
        var platform = new Platform(4321, () => throw new InvalidOperationException("must not copy"));
        var reader = new NoReader();
        var capture = new CaptureCoordinator(reader, new ClipboardBorrower(platform), () => true);
        var outcome = await capture.CaptureAsync(CaptureTrigger.Clipboard, Ct);
        Assert.Equal((CaptureStatus.Text, "already on the clipboard", "clipboard"), (outcome.Status, outcome.Text, outcome.Source));
        Assert.Equal((0, 0), (platform.Copies, reader.Calls));
        Assert.Empty(platform.Restores);
        Assert.Equal(before, GetClipboardSequenceNumber());
        Assert.Equal("already on the clipboard", ReadText());
    }

    // ---------- clipboard fixtures ----------

    private static void PutText(string text)
    {
        byte[] data = Encoding.Unicode.GetBytes(text + "\0");
        nint window = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, -3, 0, 0, 0);
        try
        {
            Assert.True(OpenWithRetry(window, 2000), "clipboard busy");
            try
            {
                EmptyClipboard();
                nint memory = GlobalAlloc(2, (nuint)data.Length);
                Marshal.Copy(data, 0, GlobalLock(memory), data.Length);
                GlobalUnlock(memory);
                Assert.NotEqual(0, SetClipboardData(CF_UNICODETEXT, memory));
            }
            finally { CloseClipboard(); }
        }
        finally { DestroyWindow(window); }
    }

    private static string ReadText()
    {
        Assert.True(OpenWithRetry(0, 3000), "clipboard busy");
        try
        {
            nint data = GetClipboardData(CF_UNICODETEXT);
            if (data == 0) return "";
            int size = (int)GlobalSize(data);
            var bytes = new byte[size];
            Marshal.Copy(GlobalLock(data), bytes, 0, size);
            GlobalUnlock(data);
            return Encoding.Unicode.GetString(bytes).Split('\0')[0];
        }
        finally { CloseClipboard(); }
    }

    private static bool OpenWithRetry(nint owner, int ms)
    {
        var timer = Stopwatch.StartNew();
        do { if (OpenClipboard(owner)) return true; Thread.Sleep(5); } while (timer.ElapsedMilliseconds < ms);
        return false;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
}

/// <summary>
/// F08 independent verification, SEL02 with real helper processes (Windows PowerShell stand-ins launched through the
/// production <see cref="Win32SelectionPlatform"/> and <see cref="ProcessSelectionHelper"/>): repeated hotkeys, slow
/// start, ready-then-hang, failing helpers, the restart breaker, and that no helper process outlives its read.
/// Not parallel with other tests: SelectionHostTests counts live powershell.exe processes by start time.
/// </summary>
[Collection(F08RealProcessCollection.Name)]
public class F08SelectionHelperVerificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    private static readonly FieldInfo ProcessField = typeof(ProcessSelectionHelper).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Delegates to the real platform and keeps every launched process so the test can prove it was reaped.</summary>
    private sealed class Tracking(ISelectionPlatform inner) : ISelectionPlatform
    {
        public readonly List<Process> Launched = [];
        public long NowMilliseconds => inner.NowMilliseconds;
        public ForegroundSnapshot Snapshot() => inner.Snapshot();
        public nint ForegroundWindow() => inner.ForegroundWindow();
        public string WindowClass(nint window) => "Edit";
        public ISelectionHelper Start(nint window, int uiaBudgetMs)
        {
            var helper = inner.Start(window, uiaBudgetMs);
            lock (Launched) Launched.Add((Process)ProcessField.GetValue(helper)!);
            return helper;
        }
        public int Starts { get { lock (Launched) return Launched.Count; } }
    }

    private static Tracking Stand(string script) => new(new Win32SelectionPlatform(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", script + " #"]));
    private static ForegroundSnapshot Snap(int pid, string cls = "Edit") => new(1000 + pid, 1000 + pid, pid, 42, cls, false, 0);

    private static async Task AssertAllReapedAsync(Tracking platform)
    {
        var deadline = Stopwatch.StartNew();
        List<Process> alive;
        do
        {
            lock (platform.Launched) alive = platform.Launched.Where(p => { try { return !p.HasExited; } catch (InvalidOperationException) { return false; } }).ToList();
            if (alive.Count == 0) return;
            await Task.Delay(50, Ct);
        } while (deadline.ElapsedMilliseconds < 2000);
        foreach (var p in alive) try { p.Kill(); } catch (Exception) { }
        Assert.Fail($"{alive.Count} helper process(es) outlived their read");
    }

    [Fact] // SEL02: three hotkeys in a row against a hung provider: older reads end as Cancelled at once, the last hits the 500 ms deadline, every helper is killed
    public async Task Repeated_hotkeys_supersede_real_helpers_and_none_survive()
    {
        var platform = Stand("Start-Sleep -Seconds 30");
        var reader = new SelectionReader(platform);
        var timer = Stopwatch.StartNew();
        var first = reader.ReadAsync(Snap(1), Ct);
        long syncMs = timer.ElapsedMilliseconds; // the caller (UI thread) gets its task back without waiting for the process
        await Task.Delay(150, Ct);
        var second = reader.ReadAsync(Snap(2), Ct);
        await Task.Delay(150, Ct);
        var thirdStart = Stopwatch.StartNew();
        var third = reader.ReadAsync(Snap(3), Ct);
        var r1 = await first;
        var r2 = await second;
        var r3 = await third;
        TestContext.Current.TestOutputHelper?.WriteLine($"sync {syncMs} ms; r1 {r1.Status} {r1.ElapsedMs} ms; r2 {r2.Status} {r2.ElapsedMs} ms; r3 {r3.Status} {r3.ElapsedMs} ms; starts {platform.Starts}");
        Assert.True(syncMs < 100, $"ReadAsync blocked the caller for {syncMs} ms");
        Assert.Equal(SelectionStatus.Cancelled, r1.Status);
        Assert.Equal(SelectionStatus.Cancelled, r2.Status);
        Assert.True(r1.ElapsedMs < 450 && r2.ElapsedMs < 450, "superseded reads must end when the newer hotkey arrives");
        Assert.Equal(SelectionStatus.Timeout, r3.Status);
        Assert.InRange(thirdStart.ElapsedMilliseconds, 400, 1500);
        await AssertAllReapedAsync(platform);
    }

    [Fact] // SEL02: a helper whose runtime starts slower than the 500 ms acquire deadline: Timeout, not cached per target, but counted by the breaker (no unbounded restarts)
    public async Task Slow_starting_real_helper_times_out_is_not_cached_and_trips_the_breaker_after_three()
    {
        var platform = Stand("Start-Sleep -Milliseconds 2500; [Console]::Error.WriteLine('selection-host: ready'); Start-Sleep -Seconds 30");
        var reader = new SelectionReader(platform);
        for (int i = 1; i <= 3; i++)
        {
            var r = await reader.ReadAsync(Snap(7), Ct); // same target each time
            Assert.Equal(SelectionStatus.Timeout, r.Status);
            Assert.NotEqual("cache", r.Source);
            Assert.Equal(i, platform.Starts);          // not cached: a slow start says nothing about the target
        }
        var blocked = await reader.ReadAsync(Snap(8), Ct);
        Assert.Equal(SelectionStatus.HelperUnavailable, blocked.Status);
        Assert.Equal(3, platform.Starts);              // cool-down: no fourth process
        await AssertAllReapedAsync(platform);
    }

    [Fact] // SEL02: a helper that started (ready marker) and then hangs in the provider: Timeout is cached for that target, the next hotkey launches nothing
    public async Task Ready_then_hung_real_helper_is_cached_for_the_target()
    {
        var platform = Stand("[Console]::Error.WriteLine('selection-host: ready'); [Console]::Error.Flush(); Start-Sleep -Seconds 30");
        // A 3 s acquire leaves room for PowerShell's own start-up so the helper is ready before the deadline.
        var reader = new SelectionReader(platform, SelectionReaderOptions.Default with { Acquire = TimeSpan.FromSeconds(3) });
        var first = await reader.ReadAsync(Snap(9), Ct);
        Assert.Equal(SelectionStatus.Timeout, first.Status);
        var again = await reader.ReadAsync(Snap(9), Ct);
        Assert.Equal((SelectionStatus.Timeout, "cache"), (again.Status, again.Source));
        Assert.Equal(1, platform.Starts);
        var other = await reader.ReadAsync(Snap(9, "OtherClass"), Ct); // the key includes the window class
        Assert.Equal(2, platform.Starts);
        Assert.Equal(SelectionStatus.Timeout, other.Status);
        await AssertAllReapedAsync(platform);
    }

    [Fact] // SEL02/SEL03: a helper that exits with an error is HelperFailed (explicit), cached per target, and three in a row stop further launches
    public async Task Exiting_real_helper_fails_explicitly_and_the_breaker_bounds_restarts()
    {
        var platform = new Tracking(new Win32SelectionPlatform(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "exit", "3"]));
        var reader = new SelectionReader(platform, SelectionReaderOptions.Default with { Acquire = TimeSpan.FromSeconds(5) });
        for (int pid = 1; pid <= 3; pid++)
        {
            var r = await reader.ReadAsync(Snap(20 + pid), Ct);
            Assert.Equal(SelectionStatus.HelperFailed, r.Status);
            Assert.Equal("", r.Text);
        }
        Assert.Equal(SelectionStatus.HelperFailed, (await reader.ReadAsync(Snap(21), Ct)).Status); // cached, no launch
        Assert.Equal(SelectionStatus.HelperUnavailable, (await reader.ReadAsync(Snap(30), Ct)).Status);
        Assert.Equal(3, platform.Starts);
        await AssertAllReapedAsync(platform);
    }

    [Fact] // SEL02: a reply that arrives after the deadline is dropped even though the process wrote a valid "selected" line
    public async Task Late_valid_reply_from_a_real_helper_is_dropped()
    {
        string line = SelectionHost.Serialize(new HelperReply("selected", "late text", "uia", [1, 2, 30, 40], 96, 1)).Replace("'", "''");
        var platform = Stand($"[Console]::Error.WriteLine('selection-host: ready'); Start-Sleep -Milliseconds 1500; [Console]::Out.Write('{line}')");
        var reader = new SelectionReader(platform, SelectionReaderOptions.Default with { Acquire = TimeSpan.FromMilliseconds(900) });
        var r = await reader.ReadAsync(Snap(40), Ct);
        Assert.Equal(SelectionStatus.Timeout, r.Status);
        Assert.Equal("", r.Text);
        await AssertAllReapedAsync(platform);
    }
}

/// <summary>F08 independent verification, UI01 placement invariants over grids of positions and monitor layouts.</summary>
public class F08PlacementVerificationTests
{
    // Primary 100 %, a 150 % monitor at negative X, a 200 % monitor on the right, a small 125 % monitor above (negative Y),
    // and a work area smaller than the float itself.
    private static readonly MonitorInfo Primary = new("m1", new PixelRect(0, 0, 1920, 1040), 96);
    private static readonly MonitorInfo[] Layout =
    [
        Primary,
        new("m2", new PixelRect(-2880, 0, 2880, 1560), 144),
        new("m3", new PixelRect(1920, 0, 3840, 2080), 192),
        new("m4", new PixelRect(200, -768, 1024, 768), 120),
        new("m5", new PixelRect(5760, 0, 300, 200), 96),
    ];

    private static bool Inside(PixelRect r, MonitorInfo m) => r.X >= m.WorkArea.X && r.Y >= m.WorkArea.Y && r.Right <= m.WorkArea.Right && r.Bottom <= m.WorkArea.Bottom && r.Width > 0 && r.Height > 0;

    [Theory] // UI01: whatever the remembered position (valid, half off-screen, unplugged), the float is wholly on one work area
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Float_is_always_wholly_reachable(int cursorMonitor)
    {
        var spec = WindowSpec.For(WindowKind.Selection);
        var cursor = Layout[cursorMonitor];
        for (int x = -4000; x <= 7000; x += 97)
            for (int y = -1200; y <= 2600; y += 89)
            {
                var rect = PlacementPolicy.Compute(spec, Layout, cursor, (x, y));
                Assert.True(Layout.Any(m => Inside(rect, m)), $"remembered ({x},{y}) → {rect} is not wholly inside one work area");
                bool titleOnSomeMonitor = Layout.Any(m => m.WorkArea.Contains(x + Dip.ToPixels(spec.WidthDip, m.Dpi) / 2, y + Dip.ToPixels(WindowSpec.TitleBarDip, m.Dpi) / 2));
                if (!titleOnSomeMonitor)
                {
                    // invalid position → centered on the cursor's monitor
                    Assert.True(Inside(rect, cursor), $"invalid ({x},{y}) should center on {cursor.Hint}: {rect}");
                    Assert.InRange(rect.X - cursor.WorkArea.X - (cursor.WorkArea.Right - rect.Right), -1, 1);
                }
            }
        var none = PlacementPolicy.Compute(spec, Layout, cursor, null);
        Assert.True(Inside(none, cursor));
    }

    [Fact] // UI01: auto height never leaves the work area, has a floor, and scrolls (caps) above work height − 32 DIP
    public void Fit_height_bounds_hold_for_any_content_height_and_position()
    {
        var spec = WindowSpec.For(WindowKind.Selection);
        foreach (var m in Layout)
            foreach (int content in new[] { int.MinValue, -10, 0, 1, 120, 420, 900, 5000, int.MaxValue })
                foreach (var (dx, dy) in new[] { (0, 0), (-5000, -5000), (m.WorkArea.Width - 1, m.WorkArea.Height - 1), (99999, 99999) })
                {
                    var current = new PixelRect(m.WorkArea.X + dx, m.WorkArea.Y + dy, Dip.ToPixels(380, m.Dpi), Dip.ToPixels(420, m.Dpi));
                    var rect = PlacementPolicy.FitHeight(spec, current, m, content);
                    Assert.True(Inside(rect, m), $"{m.Hint} content {content} at +({dx},{dy}) → {rect}");
                    int cap = Math.Min(Dip.ToPixels(Math.Max(1, Dip.ToDip(m.WorkArea.Height, m.Dpi) - WindowSpec.FloatMarginDip), m.Dpi), m.WorkArea.Height);
                    Assert.True(rect.Height <= cap, $"{m.Hint} content {content}: height {rect.Height} > cap {cap}");
                }
    }

    [Fact] // UI01 / DESIGN 9: the failure bar is wholly on one work area for any pointer, and never covers the pointer when it fits
    public void Failure_bar_is_always_reachable_and_clear_of_the_pointer()
    {
        var spec = WindowSpec.For(WindowKind.Error);
        for (int x = -4000; x <= 7000; x += 61)
            for (int y = -1200; y <= 2600; y += 53)
            {
                var rect = PlacementPolicy.NearPointer(spec, Layout, Primary, (x, y));
                Assert.True(Layout.Any(m => Inside(rect, m)), $"pointer ({x},{y}) → {rect}");
                var home = Layout.FirstOrDefault(m => m.WorkArea.Contains(x, y));
                if (home is not null && home.WorkArea.Height >= 3 * rect.Height)
                    Assert.False(rect.Contains(x, y), $"bar {rect} covers the pointer ({x},{y}) on {home.Hint}");
            }
    }
}

/// <summary>F08 independent verification, J01 with the borrow in flight: a newer hotkey during level 3.</summary>
public class F08CaptureSupersedeVerificationTests
{
    private sealed class UnsupportedReader : ISelectionReader
    {
        public ForegroundSnapshot Snapshot() => FakeClipboardPlatform.Target();
        public Task<SelectionResult> ReadAsync(ForegroundSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(SelectionResult.Failure(SelectionStatus.Unsupported));
        public void Prime(nint window) { }
    }

    private static ClipboardBorrowOptions Fast => new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150));

    [Fact] // J01 + C04: the first capture has already sent Ctrl+C and is reading; a newer hotkey supersedes it. The first still restores the
           // clipboard, is never submitted, and the newer capture borrows and is the only one submitted. After Ctrl+C the old read is
           // bounded by its own read budget (ClipboardLimits.ReadText), not by the supersede, so the newer capture waits at most that long.
           // (A read that never returns is the hung-target case: abandoned without restore, see ClipboardBorrowTests.)
    public async Task Newer_hotkey_during_the_borrow_supersedes_it_and_the_old_borrow_still_restores()
    {
        var ct = TestContext.Current.CancellationToken;
        int helpers = 0;
        var platform = new FakeClipboardPlatform();
        platform.NextHelper = () => ++helpers == 1
            ? new FakeClipboardHelper { Text = async _ => { await Task.Delay(150, CancellationToken.None); return new ClipboardText(ClipboardTextStatus.Ok, "first"); } }
            : new FakeClipboardHelper
            {
                Snapshot = _ => Task.FromResult(new ClipboardSnapshotInfo(ClipboardSnapshotStatus.Ok, platform.Sequence, 1, 1, null)),
                Text = _ => Task.FromResult(new ClipboardText(ClipboardTextStatus.Ok, "second")),
            };
        var capture = new CaptureCoordinator(new UnsupportedReader(), new ClipboardBorrower(platform, Fast), () => true);
        var submitted = new List<string>();
        Task Submit(string text) { lock (submitted) submitted.Add(text); return Task.CompletedTask; }

        var first = capture.TranslateAsync(CaptureTrigger.Selection, Submit, ct);
        await Eventually.WaitAsync(() => platform.Copies != 0);
        Assert.Equal(1, platform.Copies);
        var second = capture.TranslateAsync(CaptureTrigger.Selection, Submit, ct);
        var supersededAt = Stopwatch.StartNew();
        var old = await first.WaitAsync(TimeSpan.FromSeconds(30), ct);
        var current = await second.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.True(supersededAt.ElapsedMilliseconds < 1500, $"newer capture took {supersededAt.ElapsedMilliseconds} ms");

        Assert.Equal((CaptureStatus.Superseded, ""), (old.Status, old.Text));
        Assert.Equal((CaptureStatus.Text, "second", "borrow"), (current.Status, current.Text, current.Source));
        Assert.Equal(["second"], submitted);
        Assert.Single(platform.Helpers[0].RestoreCalls); // the superseded borrow still put the user's clipboard back
        Assert.Equal(2, platform.Copies);
        Assert.True(capture.IsCurrent(current.Generation));
        Assert.False(capture.IsCurrent(old.Generation));
    }
}
