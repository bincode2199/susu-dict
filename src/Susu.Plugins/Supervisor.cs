using Susu.Abstractions;

namespace Susu.Plugins;

/// <summary>
/// Restarts a crashed plugin-host process with 1/2/4 s backoff; after three failures inside 60 s it
/// stops trying and exposes a "restart plugin service" action instead (PLAN/ARCHITECTURE 4). Already
/// sent tasks are never replayed on a restart (no re-billing/re-write after a crash).
/// </summary>
public sealed class Supervisor : IDisposable
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
    private static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(60);

    private readonly Func<HostSession.Options> optionsFactory;
    private readonly IClock clock;
    private readonly object gate = new();
    private readonly List<long> recentFailures = [];
    private HostSession? session;
    private bool stopped;
    private bool disposed;

    public Supervisor(Func<HostSession.Options> optionsFactory, IClock clock)
    {
        this.optionsFactory = optionsFactory;
        this.clock = clock;
    }

    /// <summary>True once three restarts failed inside the 60 s window; automatic restart has stopped.</summary>
    public bool Stopped { get { lock (gate) return stopped; } }

    public event Action<Exception>? RestartFailed;
    public event Action? Stalled;

    public HostSession Current { get { lock (gate) return session ?? throw new InvalidOperationException("Plugin host is not running."); } }

    public HostSession Start()
    {
        lock (gate)
        {
            if (session is not null) return session;
            session = Launch();
            return session;
        }
    }

    private HostSession Launch()
    {
        var started = HostSession.Start(optionsFactory());
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
            try { session = Launch(); }
            catch (Exception error) { RestartFailed?.Invoke(error); }
        }
    }

    /// <summary>Manual recovery after the automatic budget was exhausted (the settings "restart plugin service" action).</summary>
    public HostSession ManualRestart()
    {
        lock (gate)
        {
            stopped = false;
            recentFailures.Clear();
            session?.Dispose();
            session = Launch();
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
