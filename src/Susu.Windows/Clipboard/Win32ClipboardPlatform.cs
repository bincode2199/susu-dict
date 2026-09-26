using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Susu.Abstractions;
using static Susu.Windows.Clipboard.ClipboardNative;

namespace Susu.Windows.Clipboard;

/// <summary>
/// Production <see cref="IClipboardPlatform"/>: non-blocking Win32 queries in this process, and one
/// <c>susu.exe --clipboard-host</c> child per borrow or clipboard read (<see cref="ClipboardHost"/>), which does every
/// call that can block on another process.
/// </summary>
public sealed class Win32ClipboardPlatform(string executable, IReadOnlyList<string>? prefixArguments = null) : IClipboardPlatform
{
    /// <summary>The platform that re-invokes the running susu.exe in helper mode.</summary>
    public static Win32ClipboardPlatform ForCurrentProcess()
        => new(Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable."));

    public nint ForegroundWindow() => GetForegroundWindow();

    public uint SequenceNumber() => GetClipboardSequenceNumber();

    public bool ModifiersDown()
    {
        foreach (int vk in (ReadOnlySpan<int>)[VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN])
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    public bool ProcessRunning(int processId, long startTime)
    {
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0) return false;
        try
        {
            if (!GetExitCodeProcess(process, out uint code) || code != STILL_ACTIVE) return false;
            return startTime == 0 || (GetProcessTimes(process, out long created, out _, out _, out _) && created == startTime);
        }
        finally { CloseHandle(process); }
    }

    public unsafe void SendCopy()
    {
        INPUT* inputs = stackalloc INPUT[4];
        inputs[0] = new INPUT { Type = INPUT_KEYBOARD, Vk = VK_CONTROL };
        inputs[1] = new INPUT { Type = INPUT_KEYBOARD, Vk = VK_C };
        inputs[2] = new INPUT { Type = INPUT_KEYBOARD, Vk = VK_C, Flags = KEYEVENTF_KEYUP };
        inputs[3] = new INPUT { Type = INPUT_KEYBOARD, Vk = VK_CONTROL, Flags = KEYEVENTF_KEYUP };
        SendInput(4, inputs, sizeof(INPUT));
    }

    public async Task<IClipboardHelper> StartHelperAsync(CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string argument in prefixArguments ?? []) info.ArgumentList.Add(argument);
        info.ArgumentList.Add(ClipboardHost.Mode);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Clipboard helper did not start.");
        _ = DrainAsync(process.StandardError);
        var helper = new ClipboardHelperConnection(process.StandardInput, process.StandardOutput, () =>
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            _ = process.WaitForExitAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
        });
        try
        {
            await helper.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            return helper;
        }
        catch
        {
            helper.Dispose();
            throw;
        }
    }

    private static async Task DrainAsync(StreamReader error)
    {
        try { var sink = new char[512]; while (await error.ReadAsync(sink).ConfigureAwait(false) > 0) { } }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
}

/// <summary>
/// Parent side of the <see cref="ClipboardHost"/> protocol over any pair of text streams (a child process in
/// production, an in-process thread in tests). Replies complete in command order; the reader's end faults everything
/// pending. <see cref="Dispose"/> runs the terminate action (kills the process) and never waits.
/// </summary>
public sealed class ClipboardHelperConnection : IClipboardHelper
{
    private readonly TextWriter _input;
    private readonly Action _terminate;
    private readonly Lock _gate = new();
    private readonly List<ClipboardUpdate> _updates = [];
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
    private readonly Queue<TaskCompletionSource<JsonElement>> _pending = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _closed;
    private int _disposed;

    public ClipboardHelperConnection(TextWriter input, TextReader output, Action terminate)
    {
        _input = input;
        _terminate = terminate;
        _ = Task.Run(() => ReadLoopAsync(output));
    }

    /// <summary>Completes when the helper reported <c>ready</c> (listener installed).</summary>
    public Task Ready => _ready.Task;

    public int UpdateCount { get { lock (_gate) return _updates.Count; } }

    public IReadOnlyList<ClipboardUpdate> UpdatesSince(int index) { lock (_gate) return _updates.Skip(index).ToList(); }

    public Task WaitForUpdateAsync(int count, CancellationToken cancellationToken)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_updates.Count > count) return Task.CompletedTask;
            if (_closed is { } error) return Task.FromException(error);
            _waiters.Add((count, signal));
        }
        return signal.Task.WaitAsync(cancellationToken);
    }

    public async Task<ClipboardSnapshotInfo> SnapshotAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync("snapshot", cancellationToken).ConfigureAwait(false);
        return new ClipboardSnapshotInfo(Enum.Parse<ClipboardSnapshotStatus>(reply.GetProperty("status").GetString()!), reply.GetProperty("seq").GetUInt32(),
            reply.GetProperty("formats").GetInt32(), reply.GetProperty("bytes").GetInt64(), reply.TryGetProperty("offending", out var o) ? o.GetString() : null);
    }

    public async Task<ClipboardText> ReadTextAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync("text", cancellationToken).ConfigureAwait(false);
        return new ClipboardText(Enum.Parse<ClipboardTextStatus>(reply.GetProperty("status").GetString()!), reply.GetProperty("text").GetString() ?? "");
    }

    public async Task<ClipboardRestoreResult> RestoreAsync(uint expectedSequence, int expectedOwnerPid, TimeSpan openWait, CancellationToken cancellationToken)
    {
        string command = string.Create(CultureInfo.InvariantCulture, $"restore {expectedSequence} {expectedOwnerPid} {(int)openWait.TotalMilliseconds}");
        var reply = await SendAsync(command, cancellationToken).ConfigureAwait(false);
        return new ClipboardRestoreResult(Enum.Parse<ClipboardRestoreStatus>(reply.GetProperty("status").GetString()!), reply.GetProperty("after").GetUInt32());
    }

    private async Task<JsonElement> SendAsync(string command, CancellationToken cancellationToken)
    {
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closed is { } error) throw new IOException("Clipboard helper closed.", error);
            _pending.Enqueue(reply);
        }
        await _input.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        var element = await reply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (element.TryGetProperty("error", out _)) throw new InvalidOperationException("Clipboard helper rejected the command.");
        return element;
    }

    private async Task ReadLoopAsync(TextReader output)
    {
        Exception end = new IOException("Clipboard helper exited.");
        try
        {
            string? line;
            while ((line = await output.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length > 4 * ClipboardLimits.MaxTextChars + 4096) throw new FormatException("Clipboard helper line exceeds its limit.");
                if (line == "ready") { _ready.TrySetResult(); continue; }
                if (line.StartsWith("u ", StringComparison.Ordinal)) { OnUpdate(line); continue; }
                if (line.StartsWith("r ", StringComparison.Ordinal))
                {
                    JsonElement element;
                    using (var document = JsonDocument.Parse(line.AsMemory(2))) element = document.RootElement.Clone();
                    TaskCompletionSource<JsonElement>? next;
                    lock (_gate) _pending.TryDequeue(out next);
                    next?.TrySetResult(element);
                    continue;
                }
                throw new FormatException("Unexpected clipboard helper line.");
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or FormatException or JsonException) { end = error; }
        List<TaskCompletionSource<JsonElement>> pending;
        List<TaskCompletionSource> waiters;
        lock (_gate)
        {
            _closed = end;
            pending = [.. _pending];
            _pending.Clear();
            waiters = _waiters.Select(w => w.Signal).ToList();
            _waiters.Clear();
        }
        _ready.TrySetException(end);
        foreach (var reply in pending) reply.TrySetException(end);
        foreach (var waiter in waiters) waiter.TrySetException(end);
    }

    private void OnUpdate(string line)
    {
        string[] parts = line.Split(' ');
        if (parts.Length != 3 || !uint.TryParse(parts[1], CultureInfo.InvariantCulture, out uint sequence) || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out int owner))
            throw new FormatException("Bad clipboard update line.");
        List<TaskCompletionSource> ready;
        lock (_gate)
        {
            _updates.Add(new ClipboardUpdate(sequence, owner));
            ready = _waiters.Where(w => _updates.Count > w.Count).Select(w => w.Signal).ToList();
            _waiters.RemoveAll(w => _updates.Count > w.Count);
        }
        foreach (var signal in ready) signal.TrySetResult();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _input.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        _terminate();
    }
}
