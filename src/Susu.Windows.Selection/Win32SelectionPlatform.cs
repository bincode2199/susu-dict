using System.Diagnostics;
using Susu.Abstractions;

namespace Susu.Windows.Selection;

/// <summary>
/// Production <see cref="ISelectionPlatform"/>: Win32 foreground snapshot and one <c>--selection-host</c> child process per
/// read (<see cref="ProcessSelectionHelper"/>). The helper runs at the caller's integrity level; it is never elevated.
/// </summary>
public sealed class Win32SelectionPlatform(string executable, IReadOnlyList<string>? prefixArguments = null) : ISelectionPlatform
{
    private static readonly int? OwnIntegrity = SelectionNative.IntegrityOf(SelectionNative.GetCurrentProcess());

    /// <summary>The platform that re-invokes the running susu.exe in helper mode.</summary>
    public static Win32SelectionPlatform ForCurrentProcess()
        => new(Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable."), [SelectionHost.Mode]);

    public long NowMilliseconds => Environment.TickCount64;

    public nint ForegroundWindow() => SelectionNative.GetForegroundWindow();

    public string WindowClass(nint window) => SelectionNative.ClassOf(window);

    public ForegroundSnapshot Snapshot()
    {
        long now = Environment.TickCount64;
        nint window = SelectionNative.GetForegroundWindow();
        if (window == 0) return new ForegroundSnapshot(0, 0, 0, 0, "", false, now);
        uint thread = SelectionNative.GetWindowThreadProcessId(window, out int pid);
        var info = new SelectionNative.GuiThreadInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<SelectionNative.GuiThreadInfo>() };
        nint focus = thread != 0 && SelectionNative.GetGUIThreadInfo(thread, ref info) ? info.Focus : 0;
        long started = 0;
        bool elevated = false;
        nint process = SelectionNative.OpenProcess(SelectionNative.ProcessQueryLimitedInformation, false, pid);
        if (process == 0) elevated = true; // protected or higher-integrity process: not readable from this level
        else
        {
            try
            {
                if (SelectionNative.GetProcessTimes(process, out long creation, out _, out _, out _)) started = creation;
                int? target = SelectionNative.IntegrityOf(process);
                // A token we may not query belongs to a higher integrity level (UIPI would block UIA as well).
                elevated = target is null ? pid != Environment.ProcessId : OwnIntegrity is int own && target > own;
            }
            finally { SelectionNative.CloseHandle(process); }
        }
        return new ForegroundSnapshot(window, focus, pid, started, SelectionNative.ClassOf(window), elevated, now);
    }

    public ISelectionHelper Start(nint window, int uiaBudgetMs)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (string argument in prefixArguments ?? []) info.ArgumentList.Add(argument);
        foreach (string argument in SelectionHost.Arguments(window, uiaBudgetMs)) info.ArgumentList.Add(argument);
        return new ProcessSelectionHelper(Process.Start(info) ?? throw new InvalidOperationException("Selection helper did not start."));
    }
}

/// <summary>A running helper process: reads one bounded reply line; <see cref="Dispose"/> terminates it without waiting.</summary>
public sealed class ProcessSelectionHelper : ISelectionHelper
{
    /// <summary>Upper bound of a reply frame (64 Ki UTF-16 units JSON-escaped, plus metadata).</summary>
    public const int MaxReplyChars = 6 * SelectionHost.MaxText + 4096;
    private readonly Process _process;
    private int _disposed;

    public ProcessSelectionHelper(Process process)
    {
        _process = process;
        _ = DrainAsync(process.StandardError);
        Completion = ReadAsync();
    }

    public Task<HelperReply> Completion { get; }

    private async Task<HelperReply> ReadAsync()
    {
        var reader = _process.StandardOutput;
        var buffer = new char[4096];
        var text = new System.Text.StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > MaxReplyChars) throw new FormatException("Selection reply exceeds its limit.");
        }
        await _process.WaitForExitAsync().ConfigureAwait(false);
        if (_process.ExitCode != 0) throw new InvalidOperationException($"Selection helper exited with {_process.ExitCode}.");
        string line = text.ToString().Trim();
        if (line.Contains('\n')) throw new FormatException("Selection reply must be one line.");
        return SelectionHost.Parse(line);
    }

    /// <inheritdoc />
    public bool Ready => Volatile.Read(ref _ready);
    private bool _ready;

    private async Task DrainAsync(StreamReader error)
    {
        try
        {
            // Only the first line matters (the ready marker); the rest is drained unread so the pipe never fills.
            string? first = await error.ReadLineAsync().ConfigureAwait(false);
            if (first == SelectionHost.ReadyMarker) Volatile.Write(ref _ready, true);
            var sink = new char[512];
            while (await error.ReadAsync(sink).ConfigureAwait(false) > 0) { }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (!_process.HasExited) _process.Kill(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        // Handles are released once the process has exited; Kill does not wait for that.
        _ = Completion.ContinueWith(_ => _process.Dispose(), TaskScheduler.Default);
    }
}
