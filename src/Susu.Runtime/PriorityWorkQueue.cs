using System.Collections.Concurrent;

namespace Susu.Runtime;

/// <summary>
/// Two-lane work queue for the plugin-host engine thread (F04.3: control messages overtake data
/// under load). <see cref="Control"/> items (Cancel/Shutdown) are always drained completely before
/// a single <see cref="Data"/> item (Load/Invoke/ApiResult) runs, so a backlog of queued data work
/// never delays a pending cancellation - only the item currently executing is not preempted (the
/// engine thread runs one JS slice at a time, ARCHITECTURE 4).
/// </summary>
public sealed class PriorityWorkQueue
{
    private readonly BlockingCollection<Action> control = new(new ConcurrentQueue<Action>());
    private readonly BlockingCollection<Action> data;

    public PriorityWorkQueue(int dataBoundedCapacity = 1024)
        => data = new BlockingCollection<Action>(new ConcurrentQueue<Action>(), dataBoundedCapacity);

    public bool AddControl(Action action) => TryAdd(control, action);
    public bool AddData(Action action) => TryAdd(data, action);

    private static bool TryAdd(BlockingCollection<Action> lane, Action action)
    {
        try { lane.Add(action); return true; }
        catch (InvalidOperationException) { return false; } // completed (shutting down)
    }

    /// <summary>
    /// Blocks up to <paramref name="timeoutMs"/> for the next item, control lane first. Returns null
    /// only on timeout; call again after that to keep polling (matches a shutdown-aware run loop).
    /// </summary>
    public Action? TakeNext(int timeoutMs)
    {
        if (control.TryTake(out var immediate)) return immediate;
        var lanes = new[] { control, data };
        return BlockingCollection<Action>.TryTakeFromAny(lanes, out var action, timeoutMs) >= 0 ? action : null;
    }

    /// <summary>Drains everything still queued, control first, without blocking. Used at shutdown.</summary>
    public IEnumerable<Action> DrainRemaining()
    {
        while (control.TryTake(out var c)) yield return c;
        while (data.TryTake(out var d)) yield return d;
    }

    public void CompleteAdding() { control.CompleteAdding(); data.CompleteAdding(); }
}
