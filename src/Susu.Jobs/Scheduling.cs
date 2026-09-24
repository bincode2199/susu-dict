using Susu.Abstractions;
using Susu.Contracts;

namespace Susu.Jobs;

public enum InvocationPriority { Interactive, Background }

/// <summary>Scheduler limits (ARCHITECTURE 5.1). Categories bound specific pipelines (video ASR 1, video translate 2).</summary>
public sealed record SchedulerLimits(int Global = 8, int QueueCapacity = 128, int PerInstance = 2, int PerLimiterKey = 2, int InteractivePerBackground = 5,
    IReadOnlyDictionary<string, int>? Categories = null)
{
    public static readonly SchedulerLimits Default = new(Categories: new Dictionary<string, int> { ["video-asr"] = 1, ["video-translate"] = 2 });
}

public sealed record InvocationTicket(string InstanceId, string LimiterKey, InvocationPriority Priority, string? Category = null);

/// <summary>The request was not accepted because the bounded queue is full (shown as "busy", never silently dropped).</summary>
public sealed class SchedulerBusyException() : Exception("invocation queue is full");

/// <summary>
/// Bounded, fair admission for capability calls: global, per-instance, per account+origin and per-category
/// limits (strictest intersection); interactive work first, but at least one waiting background call is
/// admitted after every N interactive admissions. Cancellation of a waiting call never waits for traffic.
/// </summary>
public sealed class InvocationScheduler(SchedulerLimits limits)
{
    private sealed class Waiter(InvocationTicket ticket, TaskCompletionSource<Lease> promise)
    {
        public InvocationTicket Ticket { get; } = ticket;
        public TaskCompletionSource<Lease> Promise { get; } = promise;
    }

    public sealed class Lease : IDisposable
    {
        private InvocationScheduler? owner;
        internal InvocationTicket Ticket { get; }
        internal Lease(InvocationScheduler owner, InvocationTicket ticket) { this.owner = owner; Ticket = ticket; }
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(Ticket);
    }

    private readonly object gate = new();
    private readonly LinkedList<Waiter> interactive = new(), background = new();
    private readonly Dictionary<string, int> perInstance = new(StringComparer.Ordinal), perKey = new(StringComparer.Ordinal), perCategory = new(StringComparer.Ordinal);
    private int running, interactiveStreak;

    public int Running { get { lock (gate) return running; } }
    public int Waiting { get { lock (gate) return interactive.Count + background.Count; } }

    public Task<Lease> AcquireAsync(InvocationTicket ticket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (Eligible(ticket) && interactive.Count == 0 && background.Count == 0) return Task.FromResult(Start(ticket));
            if (interactive.Count + background.Count >= limits.QueueCapacity) throw new SchedulerBusyException();
            var waiter = new Waiter(ticket, new TaskCompletionSource<Lease>(TaskCreationOptions.RunContinuationsAsynchronously));
            var node = (ticket.Priority == InvocationPriority.Interactive ? interactive : background).AddLast(waiter);
            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(() =>
                {
                    lock (gate) { if (node.List is not null) node.List.Remove(node); else return; }
                    waiter.Promise.TrySetCanceled(cancellationToken);
                });
                waiter.Promise.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            }
            Pump();
            return waiter.Promise.Task;
        }
    }

    private bool Eligible(InvocationTicket t)
        => running < limits.Global
           && Count(perInstance, t.InstanceId) < limits.PerInstance
           && Count(perKey, t.LimiterKey) < limits.PerLimiterKey
           && (t.Category is null || limits.Categories is null || !limits.Categories.TryGetValue(t.Category, out int max) || Count(perCategory, t.Category) < max);

    private static int Count(Dictionary<string, int> map, string key) => map.TryGetValue(key, out int v) ? v : 0;

    private Lease Start(InvocationTicket t)
    {
        running++;
        perInstance[t.InstanceId] = Count(perInstance, t.InstanceId) + 1;
        perKey[t.LimiterKey] = Count(perKey, t.LimiterKey) + 1;
        if (t.Category is not null) perCategory[t.Category] = Count(perCategory, t.Category) + 1;
        if (t.Priority == InvocationPriority.Interactive) interactiveStreak++; else interactiveStreak = 0;
        return new Lease(this, t);
    }

    private void Release(InvocationTicket t)
    {
        lock (gate)
        {
            running--;
            perInstance[t.InstanceId]--;
            perKey[t.LimiterKey]--;
            if (t.Category is not null) perCategory[t.Category]--;
            Pump();
        }
    }

    /// <summary>Admits waiting calls while capacity allows. Caller holds the gate.</summary>
    private void Pump()
    {
        while (running < limits.Global)
        {
            var next = Pick();
            if (next is null) return;
            next.List!.Remove(next);
            var lease = Start(next.Value.Ticket);
            if (!next.Value.Promise.TrySetResult(lease)) Release(lease.Ticket); // cancelled concurrently
        }
    }

    private LinkedListNode<Waiter>? Pick()
    {
        var firstInteractive = FirstEligible(interactive);
        var firstBackground = FirstEligible(background);
        if (firstBackground is not null && (firstInteractive is null || interactiveStreak >= limits.InteractivePerBackground)) return firstBackground;
        return firstInteractive;
    }

    private LinkedListNode<Waiter>? FirstEligible(LinkedList<Waiter> queue)
    {
        for (var node = queue.First; node is not null; node = node.Next) if (Eligible(node.Value.Ticket)) return node;
        return null;
    }
}

/// <summary>Serial mailbox: every mutation of one job runs here, one at a time (ARCHITECTURE 4/5).</summary>
public sealed class SerialMailbox
{
    private readonly object gate = new();
    private Task tail = Task.CompletedTask;

    public Task Post(Action action)
    {
        lock (gate)
        {
            tail = tail.ContinueWith(_ => action(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return tail;
        }
    }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public long NowMilliseconds => Environment.TickCount64;
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed class SystemJitter : IJitter
{
    public double Next() => Random.Shared.NextDouble();
}
