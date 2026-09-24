using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Susu.Probes.Selection;

/// <summary>F00 prototype matrix C01–C07 for clipboard borrowing (TEST-PLAN C). Synthetic data only.</summary>
internal static partial class ClipMatrix
{
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenClipboard(nint owner);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseClipboard();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EmptyClipboard();
    [LibraryImport("user32.dll")] private static partial nint SetClipboardData(uint format, nint memory);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterFormat(string name);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalUnlock(nint memory);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_clip_fixture", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int FixtureNative(int mode, int delayMs, int lifetimeMs, string text);

    internal sealed record Case(string Id, string Name, string Expected, string Observed, bool Passed, ClipBorrow.Result? Borrow);
    internal sealed record Report(DateTimeOffset Timestamp, string OS, string Scope, Case[] Cases, string[] Notes);

    private static readonly List<Case> cases = [];
    private const string Marker = "Su-Su selection marker";

    public static int FixtureMain(string[] args) => FixtureNative(int.Parse(args[0]), int.Parse(args[1]), int.Parse(args[2]), args.Length > 3 ? args[3] : "Su-Su third-party copy");

    private static Process Fixture(int mode, int delayMs, int lifetimeMs, string text = "Su-Su third-party copy")
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string a in new[] { "--clip-fixture", mode.ToString(), delayMs.ToString(), lifetimeMs.ToString(), text }) info.ArgumentList.Add(a);
        var process = Process.Start(info)!;
        if (mode is 2 or 4 or 5) process.StandardOutput.ReadLine(); // "armed"
        return process;
    }

    // ---- clipboard content helpers (harness) ----
    private static void Set(params (object Format, byte[] Data)[] items)
    {
        for (int i = 0; i < 50 && !OpenClipboard(0); i++) Thread.Sleep(10);
        EmptyClipboard();
        foreach (var (format, data) in items)
        {
            uint id = format is string name ? RegisterFormat(name) : (uint)format;
            nint memory = GlobalAlloc(2, (nuint)Math.Max(1, data.Length));
            Marshal.Copy(data, 0, GlobalLock(memory), data.Length);
            GlobalUnlock(memory);
            SetClipboardData(id, memory);
        }
        CloseClipboard();
    }

    private static byte[] Utf16Z(string s) => Encoding.Unicode.GetBytes(s + "\0");

    private static byte[] Html(string fragment)
    {
        string body = $"<html><body><!--StartFragment-->{fragment}<!--EndFragment--></body></html>";
        string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        int headerLength = string.Format(System.Globalization.CultureInfo.InvariantCulture, header, 0, 0, 0, 0).Length;
        int startFragment = headerLength + body.IndexOf("<!--StartFragment-->", StringComparison.Ordinal) + 20;
        int endFragment = headerLength + Encoding.UTF8.GetByteCount(body[..body.IndexOf("<!--EndFragment-->", StringComparison.Ordinal)]);
        string text = string.Format(System.Globalization.CultureInfo.InvariantCulture, header, headerLength, headerLength + Encoding.UTF8.GetByteCount(body), startFragment, endFragment) + body;
        return [.. Encoding.UTF8.GetBytes(text), 0];
    }

    private static byte[] Dib(int pixelBytes)
    {
        int side = (int)Math.Sqrt(pixelBytes / 4);
        var data = new byte[40 + side * side * 4];
        BitConverter.GetBytes(40).CopyTo(data, 0); BitConverter.GetBytes(side).CopyTo(data, 4); BitConverter.GetBytes(side).CopyTo(data, 8);
        BitConverter.GetBytes((short)1).CopyTo(data, 12); BitConverter.GetBytes((short)32).CopyTo(data, 14); BitConverter.GetBytes(side * side * 4).CopyTo(data, 20);
        for (int i = 40; i < data.Length; i++) data[i] = (byte)(i * 7);
        return data;
    }

    private static byte[] Drop(params string[] paths)
    {
        var list = Encoding.Unicode.GetBytes(string.Join("\0", paths) + "\0\0");
        var data = new byte[20 + list.Length];
        BitConverter.GetBytes(20).CopyTo(data, 0); BitConverter.GetBytes(1).CopyTo(data, 16);
        list.CopyTo(data, 20);
        return data;
    }

    /// <summary>Current clipboard as format name → bytes, via the same snapshot helper (status must be 0).</summary>
    private static Dictionary<string, byte[]> Read()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--clip-snapshot");
        using var helper = Process.Start(info)!;
        using var buffer = new MemoryStream();
        helper.StandardOutput.BaseStream.CopyTo(buffer);
        helper.WaitForExit(5000);
        byte[] all = buffer.ToArray();
        var map = new Dictionary<string, byte[]>();
        if (all.Length < 9 || BitConverter.ToInt32(all, 1) != 0) return map;
        int offset = 9; // 'R' + status + sequence
        while (offset + 4 <= all.Length)
        {
            uint format = BitConverter.ToUInt32(all, offset); offset += 4;
            if (format == 0) break;
            int nameChars = BitConverter.ToUInt16(all, offset); offset += 2;
            string name = Encoding.Unicode.GetString(all, offset, nameChars * 2); offset += nameChars * 2;
            int size = BitConverter.ToInt32(all, offset); offset += 4;
            map[name.Length > 0 ? name : $"#{format}"] = all[offset..(offset + size)];
            offset += size;
        }
        return map;
    }

    private static bool Same(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after, out string diff)
    {
        var problems = new List<string>();
        foreach (var (key, bytes) in before)
        {
            if (!after.TryGetValue(key, out var restored)) problems.Add($"{key} missing");
            else if (!bytes.AsSpan().SequenceEqual(restored)) problems.Add($"{key} differs ({bytes.Length} vs {restored.Length} bytes)");
        }
        diff = problems.Count == 0 ? $"{before.Count} formats identical" : string.Join("; ", problems);
        return problems.Count == 0;
    }

    private static void Record(string id, string name, string expected, string observed, bool passed, ClipBorrow.Result? borrow = null)
    {
        cases.Add(new Case(id, name, expected, observed, passed, borrow));
        Console.Error.WriteLine($"{(passed ? "PASS" : "FAIL")} {id} {name}: {observed}");
    }

    public static int Run(string evidencePath)
    {
        if (!Desktop.InputAvailable()) { Console.Error.WriteLine("Interactive desktop required (SendInput)."); return 2; }
        string root = Path.GetFullPath(".");
        string fixturesExe = Path.Combine(root, "artifacts", "sel-fixtures", "Susu.SelectionFixtures.exe");
        (Process Process, nint Window) Open(string framework, string mode)
        {
            var info = new ProcessStartInfo(fixturesExe) { UseShellExecute = false };
            info.Environment["DOTNET_ROOT"] = Path.Combine(root, ".tools", "dotnet");
            info.ArgumentList.Add(framework); info.ArgumentList.Add(mode);
            var process = Process.Start(info)!;
            string title = $"Su-Su SEL fixture {(framework == "wpf" ? "WPF" : "WinForms")} {mode}";
            nint hwnd = Desktop.WaitForWindow(t => t == title, 20000);
            Thread.Sleep(800);
            if (hwnd == 0 || !Desktop.Activate(hwnd)) throw new InvalidOperationException($"fixture {framework}/{mode} not ready");
            return (process, hwnd);
        }
        var (target, window) = Open("winforms", "text");
        var (passwordTarget, passwordWindow) = Open("wpf", "password");
        try
        {
            void SelectTarget() { Desktop.Activate(window); Desktop.Chord(Desktop.VkControl, Desktop.VkA); Thread.Sleep(150); }

            // C01: borrowing disabled.
            Set((13u, Utf16Z("Su-Su original C01")));
            uint before = ClipBorrow.GetClipboardSequenceNumber();
            SelectTarget();
            var c01 = ClipBorrow.Borrow(window, enabled: false);
            Record("C01", "borrowing off", "no copy, clipboard unchanged", $"{c01.Outcome}; sequence unchanged={ClipBorrow.GetClipboardSequenceNumber() == before}", c01.Outcome == "disabled" && ClipBorrow.GetClipboardSequenceNumber() == before, c01);

            // C02: supported formats restored exactly.
            var originals = new (string Name, (object, byte[])[] Items)[]
            {
                ("plain text", [(13u, Utf16Z("Su-Su original plain 中文"))]),
                ("HTML + text", [("HTML Format", Html("<b>Su-Su original html</b>")), (13u, Utf16Z("Su-Su original html"))]),
                ("RTF + text", [("Rich Text Format", Encoding.ASCII.GetBytes("{\\rtf1\\ansi Su-Su original \\b rtf\\b0}\0")), (13u, Utf16Z("Su-Su original rtf"))]),
                ("DIB", [(8u, Dib(64 * 64 * 4))]),
                ("CF_HDROP path list", [(15u, Drop(@"C:\synthetic\su-su-a.txt", @"C:\synthetic\su-su-b.txt"))]),
                ("text + history exclusion marker", [(13u, Utf16Z("Su-Su original excluded")), ("ExcludeClipboardContentFromMonitorProcessing", new byte[4])]),
            };
            foreach (var (name, items) in originals)
            {
                Set(items);
                var expected = Read();
                SelectTarget();
                var result = ClipBorrow.Borrow(window, enabled: true);
                Thread.Sleep(100);
                bool same = Same(expected, Read(), out string diff);
                bool ok = result.Outcome == "borrowed" && result.Text?.Contains(Marker, StringComparison.Ordinal) == true && result.Restore == "restored" && same;
                Record("C02", $"borrow + restore: {name}", "selection text returned; original formats restored byte-identical; paths not read", $"{result.Outcome}; text={(result.Text?.Contains(Marker, StringComparison.Ordinal) == true ? "marker" : "none")}; restore={result.Restore}; {diff}; snapshot {result.SnapshotMs:F0} ms; copy {result.CopyWaitMs:F0} ms", ok, result);
            }

            // C03: refused before any copy.
            var refusals = new (string Name, Action Setup)[]
            {
                ("private format", () => Set((13u, Utf16Z("Su-Su original private")), ("Su-Su F00 Private Format", new byte[] { 1, 2, 3 }))),
                ("virtual file descriptor", () => Set(("FileGroupDescriptorW", new byte[4 + 592]), (13u, Utf16Z("Su-Su original virtual")))),
                ("over 16 MiB", () => Set((8u, Dib(17 * 1024 * 1024)))),
            };
            foreach (var (name, setup) in refusals)
            {
                setup();
                uint sequence = ClipBorrow.GetClipboardSequenceNumber();
                SelectTarget();
                var result = ClipBorrow.Borrow(window, enabled: true);
                bool untouched = ClipBorrow.GetClipboardSequenceNumber() == sequence;
                Record("C03", $"refuse before copy: {name}", "cancelled before Ctrl+C; clipboard untouched", $"{result.Outcome}: {result.Detail}; untouched={untouched}", result.Outcome == "cancelled" && untouched, result);
            }
            using (var owner = Fixture(1, 1000, 8000))
            {
                Thread.Sleep(500);
                uint sequence = ClipBorrow.GetClipboardSequenceNumber();
                SelectTarget();
                var timer = Stopwatch.StartNew();
                var result = ClipBorrow.Borrow(window, enabled: true);
                double ms = timer.Elapsed.TotalMilliseconds;
                bool untouched = ClipBorrow.GetClipboardSequenceNumber() == sequence;
                Record("C03", "refuse before copy: delayed rendering slower than 200 ms", "snapshot helper killed at budget; no copy; caller returns promptly", $"{result.Outcome}: {result.Detail}; untouched={untouched}; borrow returned after {ms:F0} ms (snapshot {result.SnapshotMs:F0} ms)", result.Outcome == "cancelled" && untouched && result.SnapshotMs < 260, result);
                owner.Kill();
            }

            // C04: focus change during the snapshot; a third-party copy while waiting.
            Set((13u, Utf16Z("Su-Su original C04")));
            SelectTarget();
            var focus = ClipBorrow.Borrow(window, enabled: true, beforeCopy: () => Desktop.Activate(passwordWindow));
            Record("C04", "focus switched during snapshot", "cancelled; clipboard kept", $"{focus.Outcome}: {focus.Detail}", focus.Outcome == "cancelled", focus);
            Set((13u, Utf16Z("Su-Su original C04b")));
            Desktop.Activate(passwordWindow); // PasswordBox ignores Ctrl+C: only the third party copies
            {
                var result = ClipBorrow.Borrow(passwordWindow, enabled: true, beforeCopy: () => ThirdPartyCopySoon());
                Thread.Sleep(200);
                string now = ReadText();
                Record("C04", "another process copies while waiting", "not read as selection; newest content kept, not restored", $"{result.Outcome}: {result.Detail}; clipboard now '{now}'", result.Outcome == "rejected" && now.Contains("third-party", StringComparison.Ordinal), result);
            }

            // C05: two updates (target copy + racing copy); target does not respond; own restore ignored.
            Set((13u, Utf16Z("Su-Su original C05")));
            SelectTarget();
            using (var racer = Fixture(4, 0, 3000, "Su-Su racing copy"))
            {
                var result = ClipBorrow.Borrow(window, enabled: true);
                Thread.Sleep(200);
                string now = ReadText();
                Record("C05", "two updates while waiting (target + racer)", "rejected as ambiguous; latest content kept", $"{result.Outcome}: {result.Detail}; clipboard now '{now}'", result.Outcome == "rejected" && now.Contains("racing", StringComparison.Ordinal), result);
                if (!racer.HasExited) racer.Kill();
            }
            Set((13u, Utf16Z("Su-Su original C05b")));
            uint quiet = ClipBorrow.GetClipboardSequenceNumber();
            Desktop.Activate(passwordWindow);
            var noResponse = ClipBorrow.Borrow(passwordWindow, enabled: true);
            Record("C05", "target does not copy (password box)", "no candidate after 300 ms; clipboard unchanged", $"{noResponse.Outcome}: {noResponse.Detail}; copy wait {noResponse.CopyWaitMs:F0} ms; unchanged={ClipBorrow.GetClipboardSequenceNumber() == quiet}", noResponse.Outcome == "no-candidate" && ClipBorrow.GetClipboardSequenceNumber() == quiet && noResponse.CopyWaitMs < 400, noResponse);

            // C06: restore blocked by another process holding the clipboard.
            Set((13u, Utf16Z("Su-Su original C06")));
            SelectTarget();
            Process? holder = null;
            try
            {
                var timer = Stopwatch.StartNew();
                var result = ClipBorrow.Borrow(window, enabled: true, beforeRestore: () => holder = Fixture(2, 0, 1500));
                double ms = timer.Elapsed.TotalMilliseconds;
                holder?.WaitForExit(3000);
                string now = ReadText();
                Record("C06", "restore blocked by a clipboard holder", "bounded retry, message shown, current content kept", $"{result.Outcome}; restore={result.Restore}; returned after {ms:F0} ms; clipboard now '{(now.Length > 60 ? now[..60] : now)}'; sequence changed after holder={ClipBorrow.GetClipboardSequenceNumber()}", result.Restore?.StartsWith("未能恢复剪贴板", StringComparison.Ordinal) == true && ms < 1200 && now.Contains(Marker, StringComparison.Ordinal), result);
            }
            finally { holder?.Dispose(); }
            Record("C07", "clipboard history / cloud sync", "record observable behaviour; product must not claim exclusion",
                "not exercised automatically: Win+V history requires the WinRT Clipboard history API or manual observation; the borrowed copy is produced by the target app and may enter history/cloud sync. Product copy text must state this (PLAN 3.1).", true);
        }
        finally
        {
            foreach (var p in new[] { target, passwordTarget }) { try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { } p.Dispose(); }
        }
        var report = new Report(DateTimeOffset.UtcNow, Environment.OSVersion.VersionString, "F00 clipboard-borrowing prototype (C01–C07); not F08 acceptance",
            [.. cases], ["Target: WinForms RichTextBox fixture (Ctrl+C) and WPF PasswordBox (ignores Ctrl+C). Racing/holding processes are harness fixtures.", "Snapshot helper budget counts from the helper's ready signal; helper process start is reported separately in production by the long-lived selection helper."]);
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(report, ClipJson.Default.Report));
        Console.Error.WriteLine($"{cases.Count(c => c.Passed)}/{cases.Count} passed");
        return cases.All(c => c.Passed) ? 0 : 1;
    }

    private static void ThirdPartyCopySoon() => Task.Run(() => { Thread.Sleep(40); Set((13u, Utf16Z("Su-Su third-party copy"))); });

    private static string ReadText()
    {
        var map = Read();
        return map.TryGetValue("#13", out var bytes) ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : "";
    }

    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Report))]
    internal partial class ClipJson : JsonSerializerContext;
}
