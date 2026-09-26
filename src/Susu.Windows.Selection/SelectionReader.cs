using System.Diagnostics;
using Susu.Abstractions;

namespace Susu.Windows.Selection;

/// <summary>Raw reply of one helper run (the <c>--selection-host</c> JSON line).</summary>
public sealed record HelperReply(string Reason, string Text, string Source, double[]? Rect, int Dpi, int Ranges);

/// <summary>One running helper. <see cref="IDisposable.Dispose"/> terminates it if it is still running; never blocks.</summary>
public interface ISelectionHelper : IDisposable
{
    /// <summary>Completes with the reply, or faults when the helper crashed or replied with an invalid frame.</summary>
    Task<HelperReply> Completion { get; }

    /// <summary>True once the helper reported <see cref="SelectionHost.ReadyMarker"/> (its runtime started). A timeout before that is a slow start, not a slow target.</summary>
    bool Ready => true;
}

/// <summary>OS side of <see cref="SelectionReader"/>: Win32 in production (<see cref="Win32SelectionPlatform"/>), faked in tests.</summary>
public interface ISelectionPlatform
{
    long NowMilliseconds { get; }
    ForegroundSnapshot Snapshot();
    nint ForegroundWindow();
    string WindowClass(nint window);
    /// <summary>Launches a helper for <paramref name="window"/>. May throw when the process cannot start.</summary>
    ISelectionHelper Start(nint window, int uiaBudgetMs);
}

public sealed record SelectionReaderOptions(TimeSpan Acquire, TimeSpan UiaBudget, TimeSpan FailureCache, int MaxConsecutiveHelperFailures, TimeSpan HelperCooldown)
{
    public static SelectionReaderOptions Default { get; } = new(SelectionDeadlines.Acquire, SelectionDeadlines.UiaBudget, SelectionDeadlines.FailureCache, 3, TimeSpan.FromSeconds(30));
}

/// <summary>
/// Level-1 selection capture (ARCHITECTURE 4.1, SEL02/SEL03): one bounded helper process per read, a hard parent
/// deadline that terminates the helper, generation-based dropping of late or superseded results, a 60 s per-target
/// failure cache, and a bounded number of consecutive helper failures before a cool-down (no unbounded restarts).
/// Everything awaits asynchronously; the caller's thread is never blocked.
/// </summary>
public sealed class SelectionReader(ISelectionPlatform platform, SelectionReaderOptions? options = null) : ISelectionReader
{
    /// <summary>Chromium/Electron and Gecko top-level classes: accessibility is built lazily on the first query.</summary>
    private static readonly HashSet<string> LazyAccessibilityClasses = new(StringComparer.Ordinal) { "Chrome_WidgetWin_1", "MozillaWindowClass" };
    private const int MaxCacheEntries = 64;

    private readonly SelectionReaderOptions _options = options ?? SelectionReaderOptions.Default;
    private readonly Lock _gate = new();
    private readonly Dictionary<(int Pid, long Start, string Class), (SelectionStatus Status, long Expires)> _failures = [];
    private readonly Dictionary<nint, long> _primed = [];
    private CancellationTokenSource? _current;
    private long _generation;
    private int _consecutiveHelperFailures;
    private long _cooldownUntil = long.MinValue;

    /// <summary>Raised for diagnostics (status and elapsed time only, never text).</summary>
    public event Action<SelectionStatus, string, double>? Completed;

    public ForegroundSnapshot Snapshot() => platform.Snapshot();

    public async Task<SelectionResult> ReadAsync(ForegroundSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var timer = Stopwatch.StartNew();
        CancellationTokenSource mine = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long generation;
        lock (_gate)
        {
            _current?.Cancel(); // a newer hotkey supersedes the previous read (its helper is terminated)
            _current = mine;
            generation = ++_generation;
        }
        SelectionResult result;
        try { result = await ReadCoreAsync(snapshot, generation, mine.Token, timer).ConfigureAwait(false); }
        finally
        {
            lock (_gate) if (ReferenceEquals(_current, mine)) _current = null;
            mine.Dispose();
        }
        Completed?.Invoke(result.Status, result.Source, result.ElapsedMs);
        return result;
    }

    public void Prime(nint window)
    {
        if (window == 0 || !LazyAccessibilityClasses.Contains(platform.WindowClass(window))) return;
        lock (_gate)
        {
            if (_primed.ContainsKey(window) || BreakerOpen()) return;
            Remember(_primed, window, platform.NowMilliseconds);
        }
        _ = PrimeAsync(window);
    }

    private async Task PrimeAsync(nint window)
    {
        try
        {
            using var helper = await Task.Run(() => platform.Start(window, (int)_options.UiaBudget.TotalMilliseconds)).ConfigureAwait(false);
            await Task.WhenAny(helper.Completion, Task.Delay(_options.Acquire)).ConfigureAwait(false);
            _ = helper.Completion.Exception; // observed; the result of a priming read is discarded
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    private async Task<SelectionResult> ReadCoreAsync(ForegroundSnapshot snapshot, long generation, CancellationToken cancel, Stopwatch timer)
    {
        double Ms() => Math.Round(timer.Elapsed.TotalMilliseconds, 1);
        if (snapshot.Window == 0) return SelectionResult.Failure(SelectionStatus.NoTarget);
        var key = (snapshot.ProcessId, snapshot.ProcessStartTime, snapshot.WindowClass);
        lock (_gate)
        {
            long now = platform.NowMilliseconds;
            if (_failures.TryGetValue(key, out var cached))
            {
                if (cached.Expires > now) return SelectionResult.Failure(cached.Status, Ms(), "cache");
                _failures.Remove(key);
            }
        }
        if (snapshot.TargetElevated) return Cache(key, SelectionResult.Failure(SelectionStatus.Elevated, Ms()));
        lock (_gate) if (BreakerOpen()) return SelectionResult.Failure(SelectionStatus.HelperUnavailable, Ms());

        bool lazy = LazyAccessibilityClasses.Contains(snapshot.WindowClass);
        bool coldBrowser = false;
        if (lazy) lock (_gate) { coldBrowser = !_primed.ContainsKey(snapshot.Window); Remember(_primed, snapshot.Window, platform.NowMilliseconds); }

        ISelectionHelper helper;
        try { helper = await Task.Run(() => platform.Start(snapshot.Window, (int)_options.UiaBudget.TotalMilliseconds), cancel).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return SelectionResult.Failure(SelectionStatus.Cancelled, Ms()); }
        catch (Exception error) when (error is not OutOfMemoryException) { HelperFailed(); return SelectionResult.Failure(SelectionStatus.HelperFailed, Ms()); }

        HelperReply reply;
        using (helper)
        {
            TimeSpan remaining = _options.Acquire - timer.Elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var deadline = Task.Delay(remaining, stop.Token);
            var first = await Task.WhenAny(helper.Completion, deadline).ConfigureAwait(false);
            stop.Cancel();
            if (first != helper.Completion)
            {
                // Terminated by Dispose; any late reply is dropped with the task.
                _ = helper.Completion.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                if (cancel.IsCancellationRequested) return SelectionResult.Failure(SelectionStatus.Cancelled, Ms());
                HelperFailed();
                var timeout = SelectionResult.Failure(SelectionStatus.Timeout, Ms());
                // Only a helper that started and then ran out of time says something about the target; a slow
                // process start still counts towards the restart breaker but is not cached per target.
                return helper.Ready ? Cache(key, timeout) : timeout;
            }
            try { reply = await helper.Completion.ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (cancel.IsCancellationRequested) return SelectionResult.Failure(SelectionStatus.Cancelled, Ms());
                HelperFailed();
                return Cache(key, SelectionResult.Failure(SelectionStatus.HelperFailed, Ms()));
            }
        }
        lock (_gate)
        {
            _consecutiveHelperFailures = 0;
            if (generation != _generation || cancel.IsCancellationRequested) return SelectionResult.Failure(SelectionStatus.Cancelled, Ms()); // late result of a superseded read
        }
        var status = reply.Reason switch
        {
            "selected" => SelectionStatus.Selected,
            "password" => SelectionStatus.Password,
            "empty" => SelectionStatus.Empty,
            "unsupported" => SelectionStatus.Unsupported,
            "focus-changed" => SelectionStatus.FocusChanged,
            _ => SelectionStatus.HelperFailed,
        };
        if (status == SelectionStatus.Selected && reply.Text.Length == 0) status = SelectionStatus.Empty;
        if (status == SelectionStatus.Selected && platform.ForegroundWindow() != snapshot.Window) status = SelectionStatus.FocusChanged;
        if (coldBrowser && status is SelectionStatus.Empty or SelectionStatus.Unsupported) status = SelectionStatus.NotReady;
        if (status != SelectionStatus.Selected)
        {
            var failure = SelectionResult.Failure(status, Ms(), reply.Source);
            return status is SelectionStatus.Unsupported or SelectionStatus.HelperFailed ? Cache(key, failure) : failure;
        }
        ScreenRect? rect = reply.Rect is [var l, var t, var r, var b] && r > l && b > t ? new ScreenRect(l, t, r, b) : null;
        return new SelectionResult(SelectionStatus.Selected, reply.Text, reply.Source, rect, reply.Dpi, reply.Ranges, Ms());
    }

    private bool BreakerOpen() => platform.NowMilliseconds < _cooldownUntil;

    private void HelperFailed()
    {
        lock (_gate)
        {
            if (++_consecutiveHelperFailures < _options.MaxConsecutiveHelperFailures) return;
            _consecutiveHelperFailures = 0;
            _cooldownUntil = platform.NowMilliseconds + (long)_options.HelperCooldown.TotalMilliseconds;
        }
    }

    private SelectionResult Cache((int, long, string) key, SelectionResult failure)
    {
        lock (_gate)
        {
            long now = platform.NowMilliseconds;
            if (_failures.Count >= MaxCacheEntries)
                foreach (var stale in _failures.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList()) _failures.Remove(stale);
            if (_failures.Count >= MaxCacheEntries) _failures.Remove(_failures.MinBy(e => e.Value.Expires).Key);
            _failures[key] = (failure.Status, now + (long)_options.FailureCache.TotalMilliseconds);
        }
        return failure;
    }

    private static void Remember(Dictionary<nint, long> primed, nint window, long now)
    {
        if (primed.Count >= MaxCacheEntries) primed.Remove(primed.MinBy(e => e.Value).Key);
        primed[window] = now;
    }
}
