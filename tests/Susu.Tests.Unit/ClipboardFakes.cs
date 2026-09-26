using Susu.Abstractions;

namespace Susu.Tests.Unit;

/// <summary>Scriptable clipboard helper for the F08.2 borrow state machine (no processes, no clipboard).</summary>
internal sealed class FakeClipboardHelper : IClipboardHelper
{
    private readonly List<ClipboardUpdate> _updates = [];
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
    public Func<CancellationToken, Task<ClipboardSnapshotInfo>> Snapshot = _ => Task.FromResult(new ClipboardSnapshotInfo(ClipboardSnapshotStatus.Ok, 1, 1, 10, null));
    public Func<CancellationToken, Task<ClipboardText>> Text = _ => Task.FromResult(new ClipboardText(ClipboardTextStatus.Ok, "copied"));
    public Func<uint, int, CancellationToken, Task<ClipboardRestoreResult>> Restore = (seq, _, _) => Task.FromResult(new ClipboardRestoreResult(ClipboardRestoreStatus.Restored, seq + 1));
    public readonly List<(uint Sequence, int Owner)> RestoreCalls = [];
    public int TextReads;
    public bool Disposed;

    public int UpdateCount { get { lock (_updates) return _updates.Count; } }

    public void AddUpdate(uint sequence, int owner)
    {
        List<TaskCompletionSource> ready;
        lock (_updates)
        {
            _updates.Add(new ClipboardUpdate(sequence, owner));
            ready = _waiters.Where(w => _updates.Count > w.Count).Select(w => w.Signal).ToList();
            _waiters.RemoveAll(w => _updates.Count > w.Count);
        }
        foreach (var signal in ready) signal.TrySetResult();
    }

    public IReadOnlyList<ClipboardUpdate> UpdatesSince(int index) { lock (_updates) return _updates.Skip(index).ToList(); }

    public Task WaitForUpdateAsync(int count, CancellationToken cancellationToken)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_updates)
        {
            if (_updates.Count > count) return Task.CompletedTask;
            _waiters.Add((count, signal));
        }
        cancellationToken.Register(() => signal.TrySetCanceled(cancellationToken));
        return signal.Task;
    }

    public Task<ClipboardSnapshotInfo> SnapshotAsync(CancellationToken cancellationToken) => WithCancel(Snapshot(cancellationToken), cancellationToken);

    public Task<ClipboardText> ReadTextAsync(CancellationToken cancellationToken) { TextReads++; return WithCancel(Text(cancellationToken), cancellationToken); }

    public Task<ClipboardRestoreResult> RestoreAsync(uint expectedSequence, int expectedOwnerPid, TimeSpan openWait, CancellationToken cancellationToken)
    {
        lock (RestoreCalls) RestoreCalls.Add((expectedSequence, expectedOwnerPid));
        return WithCancel(Restore(expectedSequence, expectedOwnerPid, cancellationToken), cancellationToken);
    }

    public void Dispose() => Disposed = true;

    private static async Task<T> WithCancel<T>(Task<T> task, CancellationToken cancel)
    {
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancel.Register(() => stop.TrySetResult()))
            if (await Task.WhenAny(task, stop.Task).ConfigureAwait(false) != task) throw new OperationCanceledException(cancel);
        return await task.ConfigureAwait(false);
    }

    public static Task<T> Never<T>() => new TaskCompletionSource<T>().Task;
}

/// <summary>Scriptable platform. By default Ctrl+C makes the target (PID 100) copy once.</summary>
internal sealed class FakeClipboardPlatform : IClipboardPlatform
{
    public const int TargetPid = 100;
    public nint Foreground = 10;
    public uint Sequence = 1;
    public bool Running = true;
    public Func<bool> Modifiers = () => false;
    public Func<FakeClipboardHelper> NextHelper = () => new FakeClipboardHelper();
    public Action<FakeClipboardPlatform, FakeClipboardHelper> OnCopy = (p, h) => { p.Sequence++; h.AddUpdate(p.Sequence, TargetPid); };
    public readonly List<FakeClipboardHelper> Helpers = [];
    public int Copies;
    public bool FailStart;

    public FakeClipboardHelper Last => Helpers[^1];
    public nint ForegroundWindow() => Foreground;
    public uint SequenceNumber() => Sequence;
    public bool ModifiersDown() => Modifiers();
    public bool ProcessRunning(int processId, long startTime) => Running;
    public void SendCopy() { Copies++; OnCopy(this, Last); }

    public Task<IClipboardHelper> StartHelperAsync(CancellationToken cancellationToken)
    {
        if (FailStart) throw new InvalidOperationException("no helper");
        var helper = NextHelper();
        lock (Helpers) Helpers.Add(helper);
        return Task.FromResult<IClipboardHelper>(helper);
    }

    public static ForegroundSnapshot Target(nint window = 10, int pid = TargetPid) => new(window, window, pid, 1234, "Notepad", false, 0);
}
