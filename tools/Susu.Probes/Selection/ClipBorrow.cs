using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Susu.Probes.Selection;

/// <summary>
/// F00 prototype of the optional clipboard-borrowing level (PLAN 3.1 level 3). The snapshot runs
/// in a killable helper process under a 200 ms budget; the copy wait is 300 ms; the candidate is
/// accepted only from the target process with exactly one update; restore is conditional.
/// </summary>
internal static partial class ClipBorrow
{
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_clip_snapshot")]
    private static unsafe partial int SnapshotNative(nint output, char* offending, uint capacity);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_clip_listen")] private static partial nint Listen();
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_clip_wait")] private static partial int Wait(nint listener, uint since, int timeoutMs, int settleMs, out uint sequence, out uint ownerPid);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_clip_unlisten")] private static partial void Unlisten(nint listener);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_clip_read_text")] private static unsafe partial int ReadText(char* text, uint capacity);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_clip_restore")] private static unsafe partial int Restore(byte* snapshot, uint length, uint expected, int retryMs, out uint after);
    [LibraryImport("user32.dll")] public static partial uint GetClipboardSequenceNumber();
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int vk);

    public sealed record Result(string Outcome, string? Text, string Detail, double SnapshotMs, double CopyWaitMs, double TotalMs, string? Restore);

    /// <summary>Helper-process entry: 'R' ready byte, then the snapshot record stream on stdout.</summary>
    public static unsafe int HelperMain()
    {
        using var stdout = Console.OpenStandardOutput();
        stdout.WriteByte((byte)'R');
        stdout.Flush();
        var handle = GetStdHandle(-11);
        char* offending = stackalloc char[128];
        int status = SnapshotNative(handle, offending, 128);
        Console.Error.Write(new string(offending));
        return status;
    }

    [LibraryImport("kernel32.dll")] private static partial nint GetStdHandle(int which);

    /// <summary>Plain CF_UNICODETEXT read for test assertions.</summary>
    public static unsafe string CurrentText()
    {
        char[] buffer = new char[1 << 16];
        fixed (char* p = buffer) return ReadText(p, (uint)buffer.Length) == 0 ? new string(p) : "";
    }

    public static Result Borrow(nint target, bool enabled, Action? beforeCopy = null, int snapshotBudgetMs = 200, Action? beforeRestore = null)
    {
        var total = Stopwatch.StartNew();
        if (!enabled) return new Result("disabled", null, "borrowing is off: no copy, clipboard untouched", 0, 0, 0, null);
        Desktop.GetWindowThreadProcessId(target, out uint targetPid);
        if (Desktop.GetForegroundWindow() != target) return new Result("cancelled", null, "target not foreground at start", 0, 0, total.Elapsed.TotalMilliseconds, null);
        uint start = GetClipboardSequenceNumber();

        // 1. Snapshot in a helper process under the 200 ms budget (killed at the deadline).
        var snapTimer = Stopwatch.StartNew();
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--clip-snapshot");
        using var helper = Process.Start(info)!;
        var stream = helper.StandardOutput.BaseStream;
        byte[] snapshot;
        int status;
        uint snapSequence;
        try
        {
            using (var ready = new CancellationTokenSource(2000))
            {
                byte[] r = new byte[1];
                if (stream.ReadAsync(r, ready.Token).AsTask().GetAwaiter().GetResult() != 1 || r[0] != 'R') throw new InvalidOperationException("helper did not start");
            }
            snapTimer.Restart(); // the 200 ms budget covers the snapshot itself, not helper startup
            using var budget = new CancellationTokenSource(snapshotBudgetMs);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[64 * 1024];
            int read;
            while ((read = stream.ReadAsync(chunk, budget.Token).AsTask().GetAwaiter().GetResult()) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > (16 << 20) + 65536) throw new InvalidDataException("snapshot exceeds 16 MiB");
            }
            helper.WaitForExit(1000);
            byte[] all = buffer.ToArray();
            status = BitConverter.ToInt32(all, 0);
            snapSequence = BitConverter.ToUInt32(all, 4);
            snapshot = all[8..];
        }
        catch (OperationCanceledException)
        {
            if (!helper.HasExited) helper.Kill();
            helper.WaitForExit(1000);
            return new Result("cancelled", null, $"snapshot exceeded {snapshotBudgetMs} ms (e.g. slow delayed rendering); helper killed before any copy", snapTimer.Elapsed.TotalMilliseconds, 0, total.Elapsed.TotalMilliseconds, null);
        }
        double snapshotMs = snapTimer.Elapsed.TotalMilliseconds;
        if (status != 0)
        {
            string offending = helper.StandardError.ReadToEnd();
            string why = status switch { 10 => $"unsupported format '{offending}'", 11 => "virtual files", 12 => "over 16 MiB", 13 => "clipboard busy", 14 => "format could not be materialized", _ => $"status {status}" };
            return new Result("cancelled", null, $"snapshot refused before copy: {why}", snapshotMs, 0, total.Elapsed.TotalMilliseconds, null);
        }
        beforeCopy?.Invoke();
        if (GetClipboardSequenceNumber() != start || snapSequence != start) return new Result("cancelled", null, "clipboard changed during snapshot", snapshotMs, 0, total.Elapsed.TotalMilliseconds, null);
        if (Desktop.GetForegroundWindow() != target) return new Result("cancelled", null, "focus changed during snapshot", snapshotMs, 0, total.Elapsed.TotalMilliseconds, null);

        // 2. Copy: modifiers released, Ctrl+C, wait ≤300 ms for exactly one update owned by the target.
        nint listener = Listen();
        if (listener == 0) return new Result("cancelled", null, "listener unavailable", snapshotMs, 0, total.Elapsed.TotalMilliseconds, null);
        try
        {
            var released = Stopwatch.StartNew();
            while (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0) && released.ElapsedMilliseconds < 300) Thread.Sleep(5);
            var copyTimer = Stopwatch.StartNew();
            Desktop.Chord(Desktop.VkControl, Desktop.VkC);
            int updates = Wait(listener, start, 300, 30, out uint sequence, out uint owner);
            double copyMs = copyTimer.Elapsed.TotalMilliseconds;
            if (updates == 0 || sequence == start) return new Result("no-candidate", null, "no clipboard update within 300 ms; nothing changed, nothing restored", snapshotMs, copyMs, total.Elapsed.TotalMilliseconds, null);
            if (updates > 1) return new Result("rejected", null, $"{updates} updates while waiting: source ambiguous; latest content kept", snapshotMs, copyMs, total.Elapsed.TotalMilliseconds, "not-restored");
            if (owner != targetPid) return new Result("rejected", null, $"update owned by pid {owner}, not the target {targetPid}; latest content kept", snapshotMs, copyMs, total.Elapsed.TotalMilliseconds, "not-restored");
            if (Desktop.GetForegroundWindow() != target) return new Result("rejected", null, "focus changed while waiting; latest content kept", snapshotMs, copyMs, total.Elapsed.TotalMilliseconds, "not-restored");
            string text;
            unsafe
            {
                char[] buffer = new char[1 << 20];
                fixed (char* p = buffer) { int r = ReadText(p, (uint)buffer.Length); text = r == 0 ? new string(p) : ""; }
            }
            // 3. Conditional restore of the accepted candidate.
            beforeRestore?.Invoke(); // test hook (C06)
            int restore;
            uint after;
            unsafe { fixed (byte* s = snapshot) restore = Restore(s, (uint)snapshot.Length, sequence, 100, out after); }
            string restoreText = restore switch { 0 => "restored", 1 => "newer content kept", 2 => "未能恢复剪贴板，当前内容已保留 (clipboard held by another process)", 3 => "bad snapshot", _ => "restore failed" };
            return new Result(text.Length > 0 ? "borrowed" : "rejected", text.Length > 0 ? text : null, text.Length > 0 ? "candidate from target accepted" : "candidate had no text", snapshotMs, copyMs, total.Elapsed.TotalMilliseconds, restoreText);
        }
        finally { Unlisten(listener); }
    }
}
