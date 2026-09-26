using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Susu.Abstractions;
using Susu.Windows.Shell;
using static Susu.Windows.Clipboard.ClipboardNative;

namespace Susu.Windows.Clipboard;

/// <summary>
/// The <c>susu.exe --clipboard-host</c> mode (PLAN 3.1 level 3, C02–C06): a short-lived helper that owns one borrow's
/// clipboard work, so a hung owner or slow delayed rendering can only block this process, which the parent terminates.
/// <para>Its UI thread has a message-only window registered with <c>AddClipboardFormatListener</c> (event-driven, no
/// polling) and performs every clipboard call, so sent clipboard messages are always pumped. The snapshot bytes stay in
/// this process and only metadata goes to the parent; text is sent only on the explicit <c>text</c> command.</para>
/// <para>Protocol, UTF-8 lines. Parent → helper: <c>snapshot</c>, <c>text</c>, <c>restore &lt;seq&gt; &lt;ownerPid&gt;
/// &lt;openMs&gt;</c>, <c>quit</c>. Helper → parent: <c>ready</c>, <c>u &lt;seq&gt; &lt;ownerPid&gt;</c> per clipboard
/// update, <c>r &lt;json&gt;</c> per command in order. End of input ends the helper; so does a 10 s watchdog.</para>
/// </summary>
public static unsafe class ClipboardHost
{
    public const string Mode = "--clipboard-host";
    private const uint WM_COMMAND_READY = WM_APP + 1;
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<nint, Session> Sessions = new();
    private static int _classRegistered;

    /// <summary>Formats Windows synthesizes from a stored one (text from CF_UNICODETEXT, bitmaps from CF_DIB); not stored separately.</summary>
    private static bool Synthesized(uint format) => format is CF_TEXT or CF_OEMTEXT or CF_LOCALE or CF_BITMAP or CF_PALETTE;

    /// <summary>OLE plumbing that carries no data of its own; dropped from the snapshot (the data formats paste the same).</summary>
    private static bool Plumbing(string name) => name is "DataObject" or "Ole Private Data";

    /// <summary>
    /// The restore whitelist: Unicode text (ANSI/OEM synthesized), HTML Format, Rich Text Format, DIB/DIBV5 and CF_HDROP
    /// with the shell's small file-list companions (drop effect, PIDLs, short names — paths only, file contents are never
    /// read), plus existing clipboard-history markers, which are restored as they were (PLAN 3.1).
    /// </summary>
    public static bool Allowed(uint format, string name)
    {
        if (format is CF_UNICODETEXT or CF_DIB or CF_DIBV5 or CF_HDROP) return true;
        return name is "HTML Format" or "Rich Text Format"
            or "Preferred DropEffect" or "Shell IDList Array" or "FileName" or "FileNameW"
            or "ExcludeClipboardContentFromMonitorProcessing" or "CanIncludeInClipboardHistory" or "CanUploadToCloudClipboard";
    }

    private static bool Virtual(string name) => name is "FileGroupDescriptorW" or "FileGroupDescriptor" or "FileContents";

    /// <summary>Helper-mode entry point; returns the exit code. <paramref name="processWatchdog"/> ends the whole process after 10 s.</summary>
    public static int Run(Stream input, Stream output, bool processWatchdog = true)
    {
        if (processWatchdog) new Thread(() => { Thread.Sleep(Watchdog); Environment.Exit(3); }) { IsBackground = true, Name = "clipboard-watchdog" }.Start();
        var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        nint hwnd = CreateListenerWindow();
        if (hwnd == 0) return 1;
        var session = new Session(hwnd, writer);
        Sessions[hwnd] = session;
        try
        {
            if (!AddClipboardFormatListener(hwnd)) return 1;
            writer.WriteLine("ready");
            var reader = new Thread(() => ReadCommands(input, session)) { IsBackground = true, Name = "clipboard-commands" };
            reader.Start();
            while (Win32.GetMessageW(out var message, 0, 0, 0) > 0)
            {
                Win32.TranslateMessage(message);
                Win32.DispatchMessageW(message);
            }
            return 0;
        }
        catch (IOException) { return 1; } // parent went away
        finally
        {
            RemoveClipboardFormatListener(hwnd);
            Sessions.TryRemove(hwnd, out _);
            Win32.DestroyWindow(hwnd);
        }
    }

    private static void ReadCommands(Stream input, Session session)
    {
        try
        {
            using var reader = new StreamReader(input, new UTF8Encoding(false));
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line == "quit") break;
                session.Commands.Enqueue(line);
                Win32.PostMessageW(session.Window, WM_COMMAND_READY, 0, 0);
            }
        }
        catch (IOException) { }
        Win32.PostMessageW(session.Window, WM_CLOSE, 0, 0);
    }

    private sealed class Session(nint window, StreamWriter writer)
    {
        public readonly nint Window = window;
        public readonly StreamWriter Writer = writer;
        public readonly ConcurrentQueue<string> Commands = new();
        public List<ClipboardItem>? Snapshot;
    }

    /// <summary>One materialized clipboard format, owned by the helper.</summary>
    public sealed record ClipboardItem(uint Format, string Name, byte[] Data);

    private static nint CreateListenerWindow()
    {
        const string className = "SusuClipboardHost";
        if (Interlocked.Exchange(ref _classRegistered, 1) == 0)
        {
            fixed (char* name = className)
            {
                var cls = new Win32.WNDCLASSEXW { Size = sizeof(Win32.WNDCLASSEXW), WndProc = &WindowProc, Instance = Win32.GetModuleHandleW(0), ClassName = name };
                Win32.RegisterClassExW(cls);
            }
        }
        return Win32.CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, Win32.HWND_MESSAGE, 0, Win32.GetModuleHandleW(0), 0);
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (Sessions.TryGetValue(hwnd, out var session))
            {
                switch (message)
                {
                    case WM_CLIPBOARDUPDATE:
                        session.Writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"u {GetClipboardSequenceNumber()} {OwnerProcessId()}"));
                        return 0;
                    case WM_COMMAND_READY:
                        while (session.Commands.TryDequeue(out var command)) session.Writer.WriteLine("r " + Execute(session, command));
                        return 0;
                    case WM_CLOSE:
                        Win32.PostQuitMessage(0);
                        return 0;
                }
            }
        }
        catch (IOException) { Win32.PostQuitMessage(1); }
        return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static string Execute(Session session, string command)
    {
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.FirstOrDefault())
        {
            case "snapshot":
            {
                var (info, items) = TakeSnapshot(session.Window);
                session.Snapshot = info.Status == ClipboardSnapshotStatus.Ok ? items : null;
                return Json(w =>
                {
                    w.WriteString("status", info.Status.ToString());
                    w.WriteNumber("seq", info.Sequence);
                    w.WriteNumber("formats", info.Formats);
                    w.WriteNumber("bytes", info.Bytes);
                    if (info.OffendingFormat is { } offending) w.WriteString("offending", offending);
                });
            }
            case "text":
            {
                var text = ReadText(session.Window);
                return Json(w => { w.WriteString("status", text.Status.ToString()); w.WriteString("text", text.Text); });
            }
            case "restore" when parts.Length == 4 && uint.TryParse(parts[1], CultureInfo.InvariantCulture, out uint sequence)
                && int.TryParse(parts[2], CultureInfo.InvariantCulture, out int owner) && int.TryParse(parts[3], CultureInfo.InvariantCulture, out int openMs):
            {
                var result = session.Snapshot is { } items ? Restore(session.Window, items, sequence, owner, Math.Clamp(openMs, 0, 2000)) : new ClipboardRestoreResult(ClipboardRestoreStatus.Failed, 0);
                if (result.Status == ClipboardRestoreStatus.Restored) session.Snapshot = null; // one restore per snapshot
                return Json(w => { w.WriteString("status", result.Status.ToString()); w.WriteNumber("after", result.SequenceAfter); });
            }
            default:
                return Json(w => w.WriteString("error", "unknown command"));
        }
    }

    private static string Json(Action<Utf8JsonWriter> body)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// Materializes the whitelisted formats (C02). Any other format, virtual files, more than 16 MiB, or a delayed-rendered
    /// format that returns no data refuses the snapshot (C03); a slow render is bounded by the parent's deadline.
    /// </summary>
    public static (ClipboardSnapshotInfo Info, List<ClipboardItem> Items) TakeSnapshot(nint owner)
    {
        var items = new List<ClipboardItem>();
        ClipboardSnapshotInfo Refuse(ClipboardSnapshotStatus status, uint sequence, string? offending = null) => new(status, sequence, 0, 0, offending);
        if (!OpenWithRetry(owner, 50)) return (Refuse(ClipboardSnapshotStatus.Busy, GetClipboardSequenceNumber()), items);
        try
        {
            uint sequence = GetClipboardSequenceNumber();
            long total = 0;
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                if (Synthesized(format)) continue;
                string name = FormatName(format);
                if (Virtual(name)) return (Refuse(ClipboardSnapshotStatus.VirtualFiles, sequence, name), []);
                if (Plumbing(name)) continue;
                if (!Allowed(format, name)) return (Refuse(ClipboardSnapshotStatus.PrivateFormat, sequence, name.Length > 0 ? name : "#" + format.ToString(CultureInfo.InvariantCulture)), []);
                nint data = GetClipboardData(format); // may run the owner's delayed rendering
                if (data == 0) return (Refuse(ClipboardSnapshotStatus.RenderFailed, sequence, name.Length > 0 ? name : "#" + format.ToString(CultureInfo.InvariantCulture)), []);
                long size = (long)GlobalSize(data);
                total += size;
                if (total > ClipboardLimits.MaxSnapshotBytes) return (Refuse(ClipboardSnapshotStatus.TooLarge, sequence), []);
                nint pointer = GlobalLock(data);
                if (pointer == 0) return (Refuse(ClipboardSnapshotStatus.RenderFailed, sequence, name), []);
                try { items.Add(new ClipboardItem(format, name, new ReadOnlySpan<byte>((void*)pointer, (int)size).ToArray())); }
                finally { GlobalUnlock(data); }
            }
            return (new ClipboardSnapshotInfo(ClipboardSnapshotStatus.Ok, sequence, items.Count, total, null), items);
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Reads CF_UNICODETEXT, bounded by <see cref="ClipboardLimits.MaxTextChars"/>.</summary>
    public static ClipboardText ReadText(nint owner)
    {
        if (!OpenWithRetry(owner, 50)) return new(ClipboardTextStatus.Busy, "");
        try
        {
            nint data = GetClipboardData(CF_UNICODETEXT);
            if (data == 0) return new(ClipboardTextStatus.NoText, "");
            nint pointer = GlobalLock(data);
            if (pointer == 0) return new(ClipboardTextStatus.NoText, "");
            try
            {
                var chars = new ReadOnlySpan<char>((void*)pointer, (int)Math.Min((long)GlobalSize(data) / 2, int.MaxValue));
                int length = chars.IndexOf('\0');
                if (length < 0) length = chars.Length;
                if (length > ClipboardLimits.MaxTextChars) return new(ClipboardTextStatus.TooLong, "");
                return length == 0 ? new(ClipboardTextStatus.NoText, "") : new(ClipboardTextStatus.Ok, new string(chars[..length]));
            }
            finally { GlobalUnlock(data); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>
    /// Puts <paramref name="items"/> back only if the clipboard still holds the accepted candidate (same sequence and,
    /// when given, same owner PID), checked while the clipboard is open (PLAN 3.1 step 4). Another process holding the
    /// clipboard for <paramref name="openMs"/> gives <see cref="ClipboardRestoreStatus.Busy"/> (C06).
    /// </summary>
    public static ClipboardRestoreResult Restore(nint owner, IReadOnlyList<ClipboardItem> items, uint expectedSequence, int expectedOwnerPid, int openMs)
    {
        if (!OpenWithRetry(owner, openMs)) return new(ClipboardRestoreStatus.Busy, GetClipboardSequenceNumber());
        try
        {
            if (GetClipboardSequenceNumber() != expectedSequence || (expectedOwnerPid != 0 && OwnerProcessId() != expectedOwnerPid))
                return new(ClipboardRestoreStatus.NewerContentKept, GetClipboardSequenceNumber());
            if (!EmptyClipboard()) return new(ClipboardRestoreStatus.Failed, GetClipboardSequenceNumber());
            foreach (var item in items)
            {
                uint format = item.Name.Length == 0 ? item.Format : RegisterClipboardFormatW(item.Name);
                nint memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(item.Data.Length, 1));
                if (memory == 0) return new(ClipboardRestoreStatus.Failed, GetClipboardSequenceNumber());
                item.Data.CopyTo(new Span<byte>((void*)GlobalLock(memory), item.Data.Length));
                GlobalUnlock(memory);
                if (SetClipboardData(format, memory) == 0) { GlobalFree(memory); return new(ClipboardRestoreStatus.Failed, GetClipboardSequenceNumber()); }
            }
        }
        finally { CloseClipboard(); }
        return new(ClipboardRestoreStatus.Restored, GetClipboardSequenceNumber());
    }
}
