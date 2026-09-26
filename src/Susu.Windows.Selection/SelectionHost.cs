using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Susu.Windows.Selection;

/// <summary>
/// The <c>susu.exe --selection-host &lt;hwnd&gt; &lt;uiaBudgetMs&gt; [ia2]</c> mode (ARCHITECTURE 4.1, D-61): a short-lived,
/// read-only helper that runs UIA then IA2 on its own MTA thread, writes one JSON line to stdout and exits. It loads no
/// settings, accounts, secrets, plugins or WebView and does no network access: this namespace only uses Win32/COM and
/// susu_selection.dll, and this assembly references only Susu.Abstractions (asserted by SelectionHostTests).
/// The parent terminates it at the acquire deadline; a watchdog also ends it if the parent is gone.
/// </summary>
public static class SelectionHost
{
    public const string Mode = "--selection-host";
    /// <summary>First stderr line of a helper whose runtime has started and whose arguments are valid.</summary>
    public const string ReadyMarker = "selection-host: ready";
    /// <summary>Text limit in UTF-16 units (the native buffer is one larger for the terminator).</summary>
    public const int MaxText = 65536;
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(2);

    /// <summary>Command line of one helper run, after <see cref="Mode"/>.</summary>
    public static string[] Arguments(nint window, int uiaBudgetMs, bool ia2Only = false)
        => ia2Only ? [((long)window).ToString(CultureInfo.InvariantCulture), uiaBudgetMs.ToString(CultureInfo.InvariantCulture), "ia2"]
            : [((long)window).ToString(CultureInfo.InvariantCulture), uiaBudgetMs.ToString(CultureInfo.InvariantCulture)];

    /// <summary>
    /// Entry point of the helper mode; returns the process exit code (0 reply written, 2 usage, 1 read failure).
    /// <paramref name="processWatchdog"/> ends the whole process after 2 s (only for a dedicated helper process).
    /// </summary>
    public static int Run(ReadOnlySpan<string> args, TextWriter output, TextWriter error, bool processWatchdog = true)
    {
        if (args.Length is < 2 or > 3 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long handle)
            || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int budget) || budget is < 50 or > 5000
            || (args.Length == 3 && args[2] != "ia2"))
        {
            error.WriteLine("usage: susu --selection-host <hwnd> <uiaBudgetMs> [ia2]");
            return 2;
        }
        // Ready marker on stderr: the parent caches a Timeout only when the helper got this far, so a slow process
        // start (cold disk, AV scan) is not remembered as an unreadable target for 60 s (F08.2).
        error.WriteLine(ReadyMarker);
        error.Flush();
        var watchdog = new Thread(() => { Thread.Sleep(Watchdog); Environment.Exit(3); }) { IsBackground = true, Name = "selection-watchdog" };
        if (processWatchdog) watchdog.Start();
        try
        {
            var (reply, uiaMs, ia2Ms) = ReadTimed((nint)handle, budget, args.Length == 3);
            output.WriteLine(Serialize(reply, uiaMs, ia2Ms));
            output.Flush();
            return 0;
        }
        catch (Exception e) when (e is COMException or ExternalException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            error.WriteLine($"selection-host: {e.GetType().Name} 0x{e.HResult:X8}");
            return 1;
        }
    }

    /// <summary>One read on a dedicated MTA thread (UIA's threading requirement), per-monitor DPI aware so rectangles are physical pixels.</summary>
    public static HelperReply Read(nint window, int uiaBudgetMs, bool ia2Only = false) => ReadTimed(window, uiaBudgetMs, ia2Only).Reply;

    /// <summary><see cref="Read"/> plus the UIA and IA2 durations (diagnostics for the SEL01 matrix).</summary>
    public static (HelperReply Reply, double UiaMs, double Ia2Ms) ReadTimed(nint window, int uiaBudgetMs, bool ia2Only = false)
    {
        (HelperReply, double, double)? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = ReadOnThisThread(window, uiaBudgetMs, ia2Only); }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true, Name = "selection-mta" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        return result!.Value;
    }

    private static unsafe (HelperReply, double, double) ReadOnThisThread(nint window, int uiaBudgetMs, bool ia2Only)
    {
        if (!SelectionNative.IsWindow(window)) throw new InvalidOperationException("Target window does not exist.");
        SelectionNative.SetThreadDpiAwarenessContext(SelectionNative.DpiAwarenessPerMonitorV2);
        char[] buffer = new char[MaxText + 1];
        double* rect = stackalloc double[4];
        rect[0] = rect[1] = rect[2] = rect[3] = 0;
        int reason = 2, ranges = 0;
        string source = "none";
        double uiaMs = 0, ia2Ms = 0;
        fixed (char* text = buffer)
        {
            var timer = Stopwatch.StartNew();
            if (!ia2Only)
            {
                Marshal.ThrowExceptionForHR(SelectionNative.ReadUia(window, (uint)uiaBudgetMs, text, (uint)buffer.Length, out reason, rect, out ranges));
                uiaMs = timer.Elapsed.TotalMilliseconds;
                source = "uia";
            }
            if (reason is 2 or 3)
            {
                // Level 2: IA2 through the MSAA focus chain, also when UIA reports an empty selection (Firefox exposes a
                // TextPattern whose selection is empty while IA2 has it, F00 SEL01). IA2 returns only a real selection.
                int uiaReason = reason;
                timer.Restart();
                int hr = SelectionNative.ReadIa2(window, text, (uint)buffer.Length, out int ia2Reason);
                ia2Ms = timer.Elapsed.TotalMilliseconds;
                if (hr >= 0 && ia2Reason is 0 or 1)
                {
                    reason = ia2Reason; source = "ia2"; ranges = 0; // IA2 joins its ranges; the count is not reported
                    rect[0] = rect[1] = rect[2] = rect[3] = 0;
                }
                else
                {
                    if (hr < 0 && ia2Only) Marshal.ThrowExceptionForHR(hr);
                    reason = uiaReason == 3 ? 3 : 2; // an IA2 failure never masks UIA's explicit "empty"
                    source = uiaReason == 3 ? "uia" : "none";
                    buffer[0] = '\0';
                }
            }
        }
        if (reason != 0) { buffer[0] = '\0'; ranges = 0; rect[0] = rect[1] = rect[2] = rect[3] = 0; }
        string status = reason switch { 0 => "selected", 1 => "password", 2 => "unsupported", 3 => "empty", 4 => "focus-changed", _ => throw new InvalidOperationException("Unknown selection reason.") };
        double[]? bounds = rect[2] > rect[0] && rect[3] > rect[1] ? [rect[0], rect[1], rect[2], rect[3]] : null;
        var reply = new HelperReply(status, new string(buffer, 0, Array.IndexOf(buffer, '\0')), source, bounds, SelectionNative.DpiAt(window, bounds), ranges);
        return (reply, uiaMs, ia2Ms);
    }

    public static string Serialize(HelperReply reply, double uiaMs = 0, double ia2Ms = 0)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("Text", reply.Text);
            json.WriteString("Reason", reply.Reason);
            json.WriteString("Source", reply.Source);
            if (reply.Rect is { } rect) { json.WriteStartArray("Rect"); foreach (double v in rect) json.WriteNumberValue(v); json.WriteEndArray(); }
            json.WriteNumber("Dpi", reply.Dpi);
            json.WriteNumber("Ranges", reply.Ranges);
            json.WriteNumber("UiaMs", Math.Round(uiaMs, 3));
            json.WriteNumber("Ia2Ms", Math.Round(ia2Ms, 3));
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Parses and validates one reply line; throws <see cref="FormatException"/> on anything unexpected.</summary>
    public static HelperReply Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            string reason = root.GetProperty("Reason").GetString() ?? "";
            if (reason is not ("selected" or "password" or "unsupported" or "empty" or "focus-changed")) throw new FormatException("Unknown reason.");
            string text = root.GetProperty("Text").GetString() ?? "";
            if (text.Length > MaxText || (reason != "selected" && text.Length != 0)) throw new FormatException("Invalid text.");
            string source = root.GetProperty("Source").GetString() ?? "";
            if (source is not ("uia" or "ia2" or "none")) throw new FormatException("Unknown source.");
            double[]? rect = null;
            if (root.TryGetProperty("Rect", out var r))
            {
                if (r.GetArrayLength() != 4) throw new FormatException("Invalid rect.");
                rect = [.. r.EnumerateArray().Select(v => v.GetDouble())];
                if (rect.Any(v => !double.IsFinite(v))) throw new FormatException("Invalid rect.");
            }
            int dpi = root.GetProperty("Dpi").GetInt32(), ranges = root.GetProperty("Ranges").GetInt32();
            if (dpi is < 0 or > 1536 || ranges is < 0 or > 32) throw new FormatException("Invalid metrics.");
            return new HelperReply(reason, text, source, rect, dpi, ranges);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { throw new FormatException("Invalid selection reply.", e); }
    }

    /// <summary>Runs the IA2 synthetic vtable/bounds fixture of the shipped susu_selection.dll; throws on failure.</summary>
    public static void SelfTest() => Marshal.ThrowExceptionForHR(SelectionNative.SelfTest());
}
