using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Susu.Abstractions;
using Susu.Jobs;
using Susu.Windows.Clipboard;
using Xunit;

namespace Susu.Tests.Unit;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealClipboardCollection { public const string Name = "RealClipboard"; }

/// <summary>
/// F08.2 against the real Windows clipboard (C02–C04, C06 and an end-to-end borrow from a real EDIT window). The
/// clipboard is global, so this class never runs in parallel with anything. The helper runs in-process on its own
/// thread over pipes (same protocol and code as <c>susu.exe --clipboard-host</c>); "terminating" it closes its pipe.
/// Needs an interactive desktop session.
/// </summary>
[Collection(RealClipboardCollection.Name)]
public class ClipboardIntegrationTests
{
    private const uint CF_UNICODETEXT = 13, CF_DIB = 8, CF_HDROP = 15;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- in-process helper and platform ----------

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
        }) { IsBackground = true, Name = "test-clipboard-host" };
        thread.Start();
        var connection = new ClipboardHelperConnection(new StreamWriter(toHost, new UTF8Encoding(false)) { NewLine = "\n" }, new StreamReader(fromHost, Encoding.UTF8), () => { });
        await connection.Ready.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        return connection;
    }

    /// <summary>Real Win32 queries and SendInput; in-process helpers, optionally wrapped by <see cref="Wrap"/>.</summary>
    private sealed class InProcessPlatform : IClipboardPlatform
    {
        private readonly Win32ClipboardPlatform _win32 = new("unused");
        public Func<IClipboardHelper, IClipboardHelper> Wrap = h => h;
        public int Copies;
        /// <summary>Fixed foreground for tests that stop before Ctrl+C (they must not depend on who has the real foreground).</summary>
        public nint? Foreground;
        public nint ForegroundWindow() => Foreground ?? _win32.ForegroundWindow();
        public uint SequenceNumber() => _win32.SequenceNumber();
        public bool ModifiersDown() => _win32.ModifiersDown();
        public bool ProcessRunning(int processId, long startTime) => _win32.ProcessRunning(processId, startTime);
        /// <summary>Replaces SendInput (e.g. WM_COPY straight to the EDIT when the session has no foreground to inject into).</summary>
        public Action? Copy;
        public void SendCopy() { Copies++; if (Copy is { } copy) copy(); else _win32.SendCopy(); }
        public async Task<IClipboardHelper> StartHelperAsync(CancellationToken cancellationToken) => Wrap(await StartInProcessAsync(cancellationToken));
    }

    /// <summary>Delegating helper with hooks around snapshot and restore (to inject a user's copy or a clipboard holder at an exact step).</summary>
    private sealed class HookedHelper(IClipboardHelper inner) : IClipboardHelper
    {
        public Action AfterSnapshot = () => { };
        public Action BeforeRestore = () => { };
        public int UpdateCount => inner.UpdateCount;
        public IReadOnlyList<ClipboardUpdate> UpdatesSince(int index) => inner.UpdatesSince(index);
        public Task WaitForUpdateAsync(int count, CancellationToken cancellationToken) => inner.WaitForUpdateAsync(count, cancellationToken);
        public async Task<ClipboardSnapshotInfo> SnapshotAsync(CancellationToken cancellationToken) { var info = await inner.SnapshotAsync(cancellationToken); AfterSnapshot(); return info; }
        public Task<ClipboardText> ReadTextAsync(CancellationToken cancellationToken) => inner.ReadTextAsync(cancellationToken);
        public Task<ClipboardRestoreResult> RestoreAsync(uint expectedSequence, int expectedOwnerPid, TimeSpan openWait, CancellationToken cancellationToken)
        { BeforeRestore(); return inner.RestoreAsync(expectedSequence, expectedOwnerPid, openWait, cancellationToken); }
        public void Dispose() => inner.Dispose();
    }

    // ---------- clipboard fixtures ----------

    private static uint Format(string name) => RegisterClipboardFormat(name);

    private static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text + "\0");

    /// <summary>Replaces the clipboard with these formats, owned by a throw-away message-only window.</summary>
    private static void Put(params (uint Format, byte[] Data)[] items)
    {
        nint window = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, -3 /*HWND_MESSAGE*/, 0, 0, 0);
        try
        {
            Assert.True(OpenWithRetry(window, 2000), "clipboard busy");
            try
            {
                EmptyClipboard();
                foreach (var (format, data) in items)
                {
                    nint memory = GlobalAlloc(2, (nuint)Math.Max(data.Length, 1));
                    Marshal.Copy(data, 0, GlobalLock(memory), data.Length);
                    GlobalUnlock(memory);
                    Assert.NotEqual(0, SetClipboardData(format, memory));
                }
            }
            finally { CloseClipboard(); }
        }
        finally { DestroyWindow(window); }
    }

    private static byte[]? Read(uint format)
    {
        Assert.True(OpenWithRetry(0, 3000), "clipboard busy");
        try
        {
            nint data = GetClipboardData(format);
            if (data == 0) return null;
            int size = (int)GlobalSize(data);
            var bytes = new byte[size];
            Marshal.Copy(GlobalLock(data), bytes, 0, size);
            GlobalUnlock(data);
            return bytes;
        }
        finally { CloseClipboard(); }
    }

    private static string ReadText() => Read(CF_UNICODETEXT) is { } bytes ? Encoding.Unicode.GetString(bytes).Split('\0')[0] : "";

    private static bool OpenWithRetry(nint owner, int ms)
    {
        var timer = Stopwatch.StartNew();
        do { if (OpenClipboard(owner)) return true; Thread.Sleep(5); } while (timer.ElapsedMilliseconds < ms);
        return false;
    }

    /// <summary>Opens the clipboard on another thread and holds it (another application blocking restore, C06).</summary>
    private static Thread Hold(int milliseconds, ManualResetEventSlim held)
    {
        var thread = new Thread(() =>
        {
            if (!OpenWithRetry(0, 2000)) return;
            held.Set();
            Thread.Sleep(milliseconds);
            CloseClipboard();
        }) { IsBackground = true };
        thread.Start();
        return thread;
    }

    private static (uint, byte[])[] Dib()
    {
        var header = new byte[40 + 16];
        BitConverter.GetBytes(40).CopyTo(header, 0);      // biSize
        BitConverter.GetBytes(2).CopyTo(header, 4);       // width
        BitConverter.GetBytes(2).CopyTo(header, 8);       // height
        BitConverter.GetBytes((short)1).CopyTo(header, 12);
        BitConverter.GetBytes((short)32).CopyTo(header, 14);
        BitConverter.GetBytes(16).CopyTo(header, 20);     // biSizeImage
        for (int i = 40; i < header.Length; i++) header[i] = (byte)(i * 7);
        return [(CF_DIB, header)];
    }

    private static (uint, byte[])[] FileList()
    {
        var paths = Encoding.Unicode.GetBytes("C:\\Su-Su\\missing\\a.txt\0C:\\Su-Su\\missing\\b.txt\0\0");
        var drop = new byte[20 + paths.Length];
        BitConverter.GetBytes(20).CopyTo(drop, 0); // pFiles
        BitConverter.GetBytes(1).CopyTo(drop, 16); // fWide
        paths.CopyTo(drop, 20);
        return [(CF_HDROP, drop), (Format("Preferred DropEffect"), BitConverter.GetBytes(1))];
    }

    // ---------- C02 ----------

    [Fact]
    public async Task C02_whitelisted_formats_are_restored_byte_identical()
    {
        var cases = new Dictionary<string, (uint Format, byte[] Data)[]>
        {
            ["text"] = [(CF_UNICODETEXT, Utf16("Su-Su original 原文"))],
            ["html+text"] = [(Format("HTML Format"), Encoding.UTF8.GetBytes("Version:0.9\r\nStartHTML:00000000\r\n<b>Su-Su</b>\0")), (CF_UNICODETEXT, Utf16("Su-Su"))],
            ["rtf+text"] = [(Format("Rich Text Format"), Encoding.ASCII.GetBytes("{\\rtf1\\ansi Su-Su {\\b bold}}\0")), (CF_UNICODETEXT, Utf16("Su-Su bold"))],
            ["dib"] = Dib(),
            ["hdrop"] = FileList(),
        };
        foreach (var (name, items) in cases)
        {
            Put(items);
            using var helper = await StartInProcessAsync(Ct);
            var info = await helper.SnapshotAsync(Ct);
            Assert.True(info.Status == ClipboardSnapshotStatus.Ok, $"{name}: {info.Status} {info.OffendingFormat}");
            Put((CF_UNICODETEXT, Utf16("candidate copy")));
            var restore = await helper.RestoreAsync(GetClipboardSequenceNumber(), 0, TimeSpan.FromMilliseconds(250), Ct);
            Assert.Equal(ClipboardRestoreStatus.Restored, restore.Status);
            foreach (var (format, data) in items)
            {
                var back = Read(format);
                Assert.True(back is not null && back.AsSpan(0, data.Length).SequenceEqual(data), $"{name}: format {format} not restored byte-identical");
            }
        }
    }

    // ---------- C03 ----------

    [Fact]
    public async Task C03_private_virtual_and_oversized_clipboards_are_refused_and_left_intact()
    {
        uint privateFormat = Format("Su-Su F08 private test format");
        var cases = new (string Name, (uint, byte[])[] Items, ClipboardSnapshotStatus Expected, uint Check)[]
        {
            ("private", [(CF_UNICODETEXT, Utf16("x")), (privateFormat, [1, 2, 3])], ClipboardSnapshotStatus.PrivateFormat, privateFormat),
            ("virtual", [(Format("FileGroupDescriptorW"), new byte[600])], ClipboardSnapshotStatus.VirtualFiles, Format("FileGroupDescriptorW")),
            ("large", [(CF_UNICODETEXT, new byte[(16 << 20) + 1024])], ClipboardSnapshotStatus.TooLarge, CF_UNICODETEXT),
        };
        foreach (var (name, items, expected, check) in cases)
        {
            Put(items);
            using var helper = await StartInProcessAsync(Ct);
            var info = await helper.SnapshotAsync(Ct);
            Assert.True(info.Status == expected, $"{name}: {info.Status}");
            Assert.NotNull(Read(check)); // the original data is still there for the original application
        }
    }

    [Fact]
    public async Task C03_slow_delayed_rendering_refuses_before_copy_without_blocking_the_caller()
    {
        using var owner = new DelayedOwner(renderMs: 600, "Su-Su delayed original");
        var platform = new InProcessPlatform { Foreground = 1234 };
        var target = new ForegroundSnapshot(1234, 0, 4242, 0, "", false, 0);
        var borrower = new ClipboardBorrower(platform);
        var timer = Stopwatch.StartNew();
        var pending = borrower.BorrowAsync(target, Ct);
        long returnedAfter = timer.ElapsedMilliseconds;
        var result = await pending;
        Assert.True(returnedAfter < 100, $"caller blocked {returnedAfter} ms");
        Assert.Equal((BorrowStatus.Refused, BorrowReason.SlowRender), (result.Status, result.Reason));
        Assert.InRange(result.SnapshotMs, 150, 500);
        Assert.Equal(0, platform.Copies);
        Assert.Equal("Su-Su delayed original", ReadText()); // the owner still renders it for a paste
    }

    // ---------- C04 ----------

    [Fact]
    public async Task C04_user_copy_during_the_snapshot_cancels_and_is_kept()
    {
        Put((CF_UNICODETEXT, Utf16("before")));
        var platform = new InProcessPlatform { Foreground = 1234, Wrap = h => new HookedHelper(h) { AfterSnapshot = () => Put((CF_UNICODETEXT, Utf16("user copy"))) } };
        var target = new ForegroundSnapshot(1234, 0, 4242, 0, "", false, 0);
        var result = await new ClipboardBorrower(platform).BorrowAsync(target, Ct);
        Assert.Equal((BorrowStatus.Cancelled, BorrowReason.ClipboardChanged), (result.Status, result.Reason));
        Assert.Equal(0, platform.Copies);
        Assert.Equal("user copy", ReadText());
    }

    // ---------- C06 ----------

    [Fact]
    public async Task C06_restore_blocked_by_another_holder_is_bounded()
    {
        Put((CF_UNICODETEXT, Utf16("original")));
        using var helper = await StartInProcessAsync(Ct);
        Assert.Equal(ClipboardSnapshotStatus.Ok, (await helper.SnapshotAsync(Ct)).Status);
        Put((CF_UNICODETEXT, Utf16("candidate")));
        uint sequence = GetClipboardSequenceNumber();
        using var held = new ManualResetEventSlim();
        var holder = Hold(900, held);
        Assert.True(held.Wait(2000, Ct));
        var timer = Stopwatch.StartNew();
        var restore = await helper.RestoreAsync(sequence, 0, TimeSpan.FromMilliseconds(250), Ct);
        Assert.Equal(ClipboardRestoreStatus.Busy, restore.Status);
        Assert.InRange(timer.ElapsedMilliseconds, 200, 700);
        holder.Join();
        Assert.Equal("candidate", ReadText()); // current content kept
    }

    // ---------- the real helper process ----------

    [Fact]
    public async Task Published_helper_process_snapshots_reads_and_restores()
    {
        string? exe = FindPublishedHost();
        if (exe is null) Assert.Skip("publish susu.exe (src/Susu.Host, Release, win-x64) to run the helper-process test");
        Put((CF_UNICODETEXT, Utf16("process original")));
        var timer = Stopwatch.StartNew();
        using var start = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        start.CancelAfter(TimeSpan.FromSeconds(5));
        using var helper = await new Win32ClipboardPlatform(exe!).StartHelperAsync(start.Token);
        long startMs = timer.ElapsedMilliseconds;
        Assert.Equal(ClipboardSnapshotStatus.Ok, (await helper.SnapshotAsync(Ct)).Status);
        Put((CF_UNICODETEXT, Utf16("process candidate")));
        await helper.WaitForUpdateAsync(0, Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct); // WM_CLIPBOARDUPDATE reached the parent
        Assert.Equal("process candidate", (await helper.ReadTextAsync(Ct)).Text);
        var restore = await helper.RestoreAsync(GetClipboardSequenceNumber(), 0, TimeSpan.FromMilliseconds(250), Ct);
        Assert.Equal(ClipboardRestoreStatus.Restored, restore.Status);
        Assert.Equal("process original", ReadText());
        TestContext.Current.TestOutputHelper?.WriteLine($"helper start {startMs} ms, total {timer.ElapsedMilliseconds} ms");
    }

    private static string? FindPublishedHost()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish", "susu.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ---------- end to end from a real EDIT window ----------

    [Theory]
    [InlineData(true)]  // real SendInput Ctrl+C: needs the foreground (interactive desktop)
    [InlineData(false)] // WM_COPY sent to the EDIT instead: everything else (listener, owner evidence, read, restore) is real
    public async Task Borrow_copies_from_a_real_edit_and_restores_the_original(bool realInput)
    {
        byte[] html = Encoding.UTF8.GetBytes("Version:0.9\r\n<i>original</i>\0");
        Put((CF_UNICODETEXT, Utf16("original clipboard")), (Format("HTML Format"), html));
        using var window = ForegroundEdit.Create(realInput);
        if (realInput && !window.IsForeground) Assert.Skip("needs an interactive desktop where the test can take the foreground");
        var platform = window.Platform(realInput);
        var result = await BorrowRetryingAsync(new ClipboardBorrower(platform), window.Target);
        Assert.True(result.Status == BorrowStatus.Borrowed, $"{result.Status} {result.Reason}");
        Assert.Equal("selected text", result.Text);
        Assert.Equal(BorrowRestore.Restored, result.Restore);
        Assert.Equal("original clipboard", ReadText());
        Assert.True(Read(Format("HTML Format"))!.AsSpan(0, html.Length).SequenceEqual(html));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task C06_borrow_keeps_the_text_and_reports_when_restore_is_blocked(bool realInput)
    {
        Put((CF_UNICODETEXT, Utf16("original clipboard")));
        using var window = ForegroundEdit.Create(realInput);
        if (realInput && !window.IsForeground) Assert.Skip("needs an interactive desktop where the test can take the foreground");
        using var held = new ManualResetEventSlim();
        Thread? holder = null;
        var platform = window.Platform(realInput);
        platform.Wrap = h => new HookedHelper(h) { BeforeRestore = () => { holder = Hold(1200, held); held.Wait(2000); } };
        var timer = Stopwatch.StartNew();
        var result = await BorrowRetryingAsync(new ClipboardBorrower(platform), window.Target);
        Assert.True(timer.ElapsedMilliseconds < 1500 * 3, $"took {timer.ElapsedMilliseconds} ms");
        Assert.Equal((BorrowStatus.Borrowed, "selected text"), (result.Status, result.Text));
        Assert.Equal(BorrowRestore.Failed, result.Restore);
        Assert.Equal("未能恢复剪贴板，当前内容已保留", result.Notice);
        holder?.Join();
        Assert.Equal("selected text", ReadText()); // the current content is kept, not overwritten later
    }

    /// <summary>
    /// The EDIT tries OpenClipboard once on WM_COPY; if another process (e.g. rdpclip in an RDP session) has the clipboard
    /// open at that instant, nothing is copied and the borrow correctly ends as NoCandidate. Retry that case only.
    /// </summary>
    private static async Task<BorrowResult> BorrowRetryingAsync(ClipboardBorrower borrower, ForegroundSnapshot target)
    {
        BorrowResult result = null!;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            result = await borrower.BorrowAsync(target, Ct);
            if (result.Status != BorrowStatus.NoCandidate) break;
            await Task.Delay(100, Ct);
        }
        return result;
    }

    /// <summary>A visible top-level window with a focused EDIT ("prefix selected text suffix", "selected text" selected) on its own pumping thread.</summary>
    private sealed class ForegroundEdit : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private volatile bool _stop;
        private nint _parent, _edit;
        public ForegroundSnapshot Target => new(_parent, _edit, Environment.ProcessId, 0, "Static", false, 0);

        private ForegroundEdit()
        {
            _thread = new Thread(() =>
            {
                _parent = CreateWindowEx(0, "STATIC", "Su-Su F08.2 borrow test", 0x10CF0000, 80, 80, 520, 200, 0, 0, 0, 0);
                _edit = CreateWindowEx(0, "EDIT", "prefix selected text suffix", 0x50000000 | 0x0004, 10, 10, 460, 100, _parent, 0, 0, 0);
                TakeForeground(_parent);
                SetFocus(_edit);
                SendMessage(_edit, 0x00B1 /*EM_SETSEL*/, 7, 20);
                _ready.Set();
                while (!_stop)
                {
                    while (PeekMessage(out var msg, 0, 0, 0, 1)) { TranslateMessage(in msg); DispatchMessage(in msg); }
                    Thread.Sleep(2);
                }
                DestroyWindow(_parent);
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(5000);
        }

        public bool IsForeground => GetForegroundWindow() == _parent;

        public static ForegroundEdit Create(bool waitForForeground)
        {
            var window = new ForegroundEdit();
            for (int i = 0; waitForForeground && i < 50 && !window.IsForeground; i++) Thread.Sleep(10);
            return window;
        }

        /// <summary>Real input needs the real foreground; the simulated copy pins the foreground to this window and sends WM_COPY.</summary>
        public InProcessPlatform Platform(bool realInput) => realInput ? new InProcessPlatform()
            : new InProcessPlatform { Foreground = _parent, Copy = () => PostMessage(_edit, 0x0301 /*WM_COPY*/, 0, 0) };

        private static void TakeForeground(nint window)
        {
            // A background process may not take the foreground; a synthetic Alt tap satisfies the "last input" rule.
            Input[] inputs = [new Input { Type = 1, Vk = 0x12 }, new Input { Type = 1, Vk = 0x12, Flags = 2 }];
            SendInput(2, inputs, Marshal.SizeOf<Input>());
            SetForegroundWindow(window);
        }

        public void Dispose() { _stop = true; _thread.Join(5000); }
    }

    /// <summary>Owns the clipboard with a delayed-rendered CF_UNICODETEXT whose rendering takes <c>renderMs</c> (C03).</summary>
    private sealed class DelayedOwner : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private volatile bool _stop;
        private WndProc? _proc;

        public DelayedOwner(int renderMs, string text)
        {
            _thread = new Thread(() =>
            {
                nint window = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, -3, 0, 0, 0);
                nint previous = 0;
                _proc = (hwnd, message, w, l) =>
                {
                    if (message == 0x0305 /*WM_RENDERFORMAT*/ && w == CF_UNICODETEXT)
                    {
                        Thread.Sleep(renderMs);
                        var data = Utf16(text);
                        nint memory = GlobalAlloc(2, (nuint)data.Length);
                        Marshal.Copy(data, 0, GlobalLock(memory), data.Length);
                        GlobalUnlock(memory);
                        SetClipboardData(CF_UNICODETEXT, memory);
                        return 0;
                    }
                    if (message == 0x0306 /*WM_RENDERALLFORMATS*/) return 0;
                    return CallWindowProc(previous, hwnd, message, w, l);
                };
                previous = SetWindowLongPtr(window, -4 /*GWLP_WNDPROC*/, Marshal.GetFunctionPointerForDelegate(_proc));
                if (OpenWithRetry(window, 2000))
                {
                    EmptyClipboard();
                    SetClipboardData(CF_UNICODETEXT, 0); // delayed rendering
                    CloseClipboard();
                }
                _ready.Set();
                while (!_stop)
                {
                    while (PeekMessage(out var msg, 0, 0, 0, 1)) { TranslateMessage(in msg); DispatchMessage(in msg); }
                    Thread.Sleep(2);
                }
                SetWindowLongPtr(window, -4, previous);
                DestroyWindow(window);
            }) { IsBackground = true };
            _thread.Start();
            _ready.Wait(5000);
        }

        public void Dispose() { _stop = true; _thread.Join(5000); GC.KeepAlive(_proc); }
    }

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)] private struct Msg { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public ushort Vk; [FieldOffset(12)] public uint Flags; }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out Msg message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(in Msg message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(in Msg message);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")] private static extern nint CallWindowProc(nint previous, nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
}
