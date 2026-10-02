using Susu.Abstractions;

namespace Susu.Plugins;

/// <summary>
/// Restarts a crashed plugin-host process with 1/2/4 s backoff; after three failures inside 60 s it
/// stops trying and exposes a "restart plugin service" action instead (PLAN/ARCHITECTURE 4). Already
/// sent tasks are never replayed on a restart: a fresh <typeparamref name="TSession"/> is created and
/// nothing from the disposed session's pending calls is resent (no re-billing/re-write after a crash).
/// Generic over <see cref="IHostSessionHandle"/> so the backoff/threshold timing can be unit-tested
/// against a fast in-memory double instead of a real AppContainer process; production uses
/// <c>Supervisor&lt;HostSession&gt;</c>.
/// </summary>
public sealed class Supervisor<TSession> : IDisposable where TSession : class, IHostSessionHandle
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
    private static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(60);

    private readonly Func<TSession> launch;
    private readonly IClock clock;
    private readonly TimeSpan? idleTimeout;
    private readonly object gate = new();
    private readonly List<long> recentFailures = [];
    private TSession? session;
    private bool stopped;
    private bool disposed;
    // True from the moment a crash nulls the session until RestartAsync's scheduled relaunch installs
    // a fresh one (or gives up). While true, Acquire() must not launch: that would bypass the 1/2/4 s
    // backoff RestartAsync already scheduled for itself and turn one crash into a launch storm.
    private bool crashBackoffPending;
    private int activeCalls;
    private long lastActivityMs;
    private CancellationTokenSource? idleCts;

    public Supervisor(Func<TSession> launch, IClock clock, TimeSpan? idleTimeout = null)
    {
        this.launch = launch;
        this.clock = clock;
        this.idleTimeout = idleTimeout;
    }

    /// <summary>True once three restarts failed inside the 60 s window; automatic restart has stopped.</summary>
    public bool Stopped { get { lock (gate) return stopped; } }

    /// <summary>How many restart attempts remain before automatic restart stops (for UI/diagnostics).</summary>
    public int AttemptsRemaining { get { lock (gate) { long now = clock.NowMilliseconds; recentFailures.RemoveAll(t => now - t > FailureWindow.TotalMilliseconds); return Math.Max(0, Backoff.Length - recentFailures.Count); } } }

    public event Action<Exception>? RestartFailed;
    public event Action? Stalled;

    public TSession Current { get { lock (gate) return session ?? throw new InvalidOperationException("Plugin host is not running."); } }

    /// <summary>
    /// Non-launching read: true with the live session, or false while a crashed session is between
    /// backoff and its scheduled relaunch (or automatic restart has stopped). Callers that only ever
    /// want to *use* a session - never to start one - should call this instead of <see cref="Start"/>,
    /// so a call arriving mid-backoff gets an explicit "not available yet" instead of racing a second
    /// concurrent launch against the one <see cref="RestartAsync"/> already scheduled.
    /// </summary>
    public bool TryGetCurrent(out TSession? current) { lock (gate) { current = session; return current is not null; } }

    public TSession Start()
    {
        lock (gate)
        {
            if (session is not null) return session;
            session = LaunchAndWatch();
            return session;
        }
    }

    /// <summary>
    /// Lazy-start entry point for ordinary callers (PluginProvider): launches on first use, or after a
    /// clean idle release, but - like <see cref="TryGetCurrent"/> - never launches while a crashed
    /// session is mid-backoff, so it cannot race the relaunch <see cref="RestartAsync"/> already
    /// scheduled for itself. Returns null when unavailable (stopped, or crashed mid-backoff) instead of
    /// launching. Every successful Acquire must be paired with <see cref="Release"/> - idle release only
    /// fires once the call count drops back to zero and stays there for the idle window, so a session
    /// is never torn down under an in-flight call.
    /// </summary>
    public TSession? Acquire()
    {
        lock (gate)
        {
            if (disposed || stopped) return null;
            idleCts?.Cancel();
            idleCts = null;
            if (session is null)
            {
                if (crashBackoffPending) return null;
                session = LaunchAndWatch();
            }
            activeCalls++;
            lastActivityMs = clock.NowMilliseconds;
            return session;
        }
    }

    /// <summary>Matches a successful <see cref="Acquire"/>. Schedules idle release once no call remains in flight.</summary>
    public void Release()
    {
        TSession target;
        CancellationTokenSource cts;
        lock (gate)
        {
            if (activeCalls > 0) activeCalls--;
            lastActivityMs = clock.NowMilliseconds;
            if (activeCalls != 0 || disposed || idleTimeout is null || session is null) return;
            target = session;
            cts = new CancellationTokenSource();
            idleCts = cts;
        }
        _ = ScheduleIdleRelease(idleTimeout!.Value, cts, target);
    }

    private async Task ScheduleIdleRelease(TimeSpan delay, CancellationTokenSource cts, TSession target)
    {
        try { await clock.Delay(delay, cts.Token); }
        catch (OperationCanceledException) { return; }
        lock (gate)
        {
            if (disposed || activeCalls != 0 || !ReferenceEquals(session, target) || !ReferenceEquals(idleCts, cts)) return;
            session = null;
            idleCts = null;
        }
        target.Dispose(); // outside the lock, same reader-thread-join reasoning as ManualRestart
    }

    private TSession LaunchAndWatch()
    {
        var started = launch();
        // Capture the specific instance: HostSession.Dispose() joins its reader thread, so a
        // just-replaced session's Disconnected can still fire after ManualRestart (or a fresh
        // automatic relaunch) has already installed a new one. Without checking identity here,
        // that stale notification would null out - and trigger a spurious restart of - the live
        // session it has nothing to do with.
        started.Disconnected += () => OnDisconnected(started);
        return started;
    }

    private void OnDisconnected(TSession source)
    {
        lock (gate)
        {
            if (disposed || stopped || !ReferenceEquals(session, source)) return;
            session = null;
            crashBackoffPending = true;
            idleCts?.Cancel(); // the session it was timing is already gone; nothing left to release
            idleCts = null;
        }
        // The crashed session still holds its container profile and the read grants on the packages folder; release them on another thread
        // (Dispose joins the reader thread, and this callback may run on it).
        _ = Task.Run(() => { try { source.Dispose(); } catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException) { } });
        _ = RestartAsync();
    }

    private async Task RestartAsync()
    {
        int attempt;
        lock (gate)
        {
            long now = clock.NowMilliseconds;
            recentFailures.RemoveAll(t => now - t > FailureWindow.TotalMilliseconds);
            attempt = recentFailures.Count;
            if (attempt >= Backoff.Length)
            {
                stopped = true;
                crashBackoffPending = false; // Acquire() is refused via `stopped` now, not this flag
                Stalled?.Invoke();
                return;
            }
            recentFailures.Add(now);
        }
        await clock.Delay(Backoff[attempt], CancellationToken.None);
        lock (gate)
        {
            if (disposed || stopped || session is not null) return;
            try { session = LaunchAndWatch(); crashBackoffPending = false; }
            catch (Exception error) { RestartFailed?.Invoke(error); }
        }
    }

    /// <summary>Manual recovery after the automatic budget was exhausted (the settings "restart plugin service" action).</summary>
    public TSession ManualRestart()
    {
        TSession? old;
        TSession fresh;
        lock (gate)
        {
            stopped = false;
            crashBackoffPending = false;
            idleCts?.Cancel();
            idleCts = null;
            recentFailures.Clear();
            old = session;
            session = fresh = LaunchAndWatch();
        }
        // Disposed outside the lock: HostSession.Dispose() joins its reader thread, and that thread's
        // own Disconnected handler needs this same lock (now guarded by the identity check above, but
        // still real work) - disposing while holding the lock would stall this call on that join.
        old?.Dispose();
        return fresh;
    }

    /// <summary>
    /// F16.2: detaches the live session without disposing it, so the next <see cref="Acquire"/> launches a fresh one that loads the packages as they
    /// are now. The caller owns the returned session (null when none was running): it interrupts what must stop, lets the rest drain, and disposes it.
    /// Detaching is not a crash: no backoff, no failure counted, and the old session's later Disconnected is ignored by the identity check.
    /// </summary>
    public TSession? Recycle()
    {
        lock (gate)
        {
            if (disposed) return null;
            stopped = false;
            crashBackoffPending = false;
            recentFailures.Clear();
            idleCts?.Cancel();
            idleCts = null;
            var old = session;
            session = null;
            return old;
        }
    }

    public void Dispose()
    {
        TSession? old;
        lock (gate)
        {
            disposed = true;
            idleCts?.Cancel();
            idleCts = null;
            old = session;
            session = null;
        }
        old?.Dispose(); // outside the lock, same reader-thread-join reasoning as ManualRestart
    }
}
