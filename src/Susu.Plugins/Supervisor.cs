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
    private readonly object gate = new();
    private readonly List<long> recentFailures = [];
    private TSession? session;
    private bool stopped;
    private bool disposed;

    public Supervisor(Func<TSession> launch, IClock clock)
    {
        this.launch = launch;
        this.clock = clock;
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

    private TSession LaunchAndWatch()
    {
        var started = launch();
        started.Disconnected += OnDisconnected;
        return started;
    }

    private void OnDisconnected()
    {
        lock (gate)
        {
            if (disposed || stopped) return;
            session = null;
        }
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
                Stalled?.Invoke();
                return;
            }
            recentFailures.Add(now);
        }
        await clock.Delay(Backoff[attempt], CancellationToken.None);
        lock (gate)
        {
            if (disposed || stopped || session is not null) return;
            try { session = LaunchAndWatch(); }
            catch (Exception error) { RestartFailed?.Invoke(error); }
        }
    }

    /// <summary>Manual recovery after the automatic budget was exhausted (the settings "restart plugin service" action).</summary>
    public TSession ManualRestart()
    {
        lock (gate)
        {
            stopped = false;
            recentFailures.Clear();
            session?.Dispose();
            session = LaunchAndWatch();
            return session;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            session?.Dispose();
            session = null;
        }
    }
}
