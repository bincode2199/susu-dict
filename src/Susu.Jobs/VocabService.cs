using Susu.Abstractions;
using Susu.Contracts;

namespace Susu.Jobs;

/// <summary>Outbox counts of one vocabulary target plus what the last pass said. Pending includes rows being sent; Retrying is waiting for its backoff.</summary>
public sealed record VocabTargetStatus(string Target, int Pending, int Retrying, int Failed, int Uncertain, int Succeeded, ErrorKind? LastError, string? LastPassError);

/// <summary>A row that needs the user: Uncertain (a write whose answer was lost) or Failed. <see cref="Word"/> is the entry's text.</summary>
public sealed record VocabProblemRow(string EntryId, string Word, string Target, long Revision, DeliveryState State, int Attempts);

/// <summary>
/// F15.4: the host's vocabulary service. It is the one place the shell and the host use for favorites: favorite and unfavorite (the
/// outbox rows go only to the targets that can take deliveries now), the sync loop (at start, on every favorite, after a manual action
/// and on a timer; passes never overlap), the manual-check actions (Uncertain/Failed rows resolved as delivered or not delivered, retry
/// failed, queue the favorites that predate a target) and the file export. Nothing here deletes anything on a remote: unfavorite only
/// cancels what was not sent yet (F15.1). Targets are isolated by the worker; a failing one never stops the others or the loop.
/// </summary>
public sealed class VocabService : IAsyncDisposable
{
    private const int ProblemLimit = 100;
    private readonly IFavorites favorites;
    private readonly VocabSyncWorker worker;
    private readonly Func<IReadOnlyList<string>> usableTargets;
    private readonly IClock clock;
    private readonly IVocabFileExporter? exporter;
    private readonly TimeSpan poll;
    private readonly SemaphoreSlim gate = new(1, 1), kick = new(0, 1);
    private readonly Dictionary<string, (ErrorKind? Kind, string? PassError)> last = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stop = new();
    private Task? loop;

    public VocabService(IFavorites favorites, VocabSyncWorker worker, Func<IReadOnlyList<string>> usableTargets, IClock clock, IVocabFileExporter? exporter = null, VocabSyncPolicy? policy = null)
    {
        this.favorites = favorites;
        this.worker = worker;
        this.usableTargets = usableTargets;
        this.clock = clock;
        this.exporter = exporter;
        poll = (policy ?? new VocabSyncPolicy()).Poll;
    }

    /// <summary>Raised (on any thread) when counts or rows changed; the shell re-projects the settings page.</summary>
    public event Action? Changed;

    public bool HasExporter => exporter is not null;

    /// <summary>The targets a favorite made now would be queued to.</summary>
    public IReadOnlyList<string> Targets() => usableTargets();

    public bool IsFavorite(string lang, string word) => favorites.IsFavorite(lang, word);

    public int Count() => favorites.ActiveCount();

    public FavoriteResult Favorite(FavoriteCard card)
    {
        var result = favorites.Favorite(card, usableTargets());
        Changed?.Invoke();
        if (result.QueuedTargets.Count > 0) Kick();
        return result;
    }

    public bool Unfavorite(string lang, string word)
    {
        bool removed = favorites.Unfavorite(lang, word);
        if (removed) Changed?.Invoke();
        return removed;
    }

    // ---------- sync loop ----------

    /// <summary>Starts the loop: a pass now (app start), then one per signal or poll interval. A second call does nothing.</summary>
    public void Start()
    {
        if (loop is not null) return;
        loop = Task.Run(() => RunLoopAsync(stop.Token));
    }

    /// <summary>Asks for a pass soon (a favorite, a retry, a changed setting); passes never overlap and extra requests merge.</summary>
    public void Kick()
    {
        try { kick.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await PassAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { } // a pass that throws must not end the loop; the next one starts clean
            using var round = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var tick = clock.Delay(poll, round.Token);
                var signal = kick.WaitAsync(round.Token);
                await Task.WhenAny(tick, signal);
            }
            catch (OperationCanceledException) { return; }
            finally { round.Cancel(); }
        }
    }

    /// <summary>One pass over every usable target now (also used by "sync now"). Returns the reports.</summary>
    public async Task<IReadOnlyList<VocabSyncReport>> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        return await PassAsync(linked.Token);
    }

    private async Task<IReadOnlyList<VocabSyncReport>> PassAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var targets = usableTargets();
            if (targets.Count == 0) return [];
            var reports = await worker.RunAllAsync(targets, cancellationToken);
            lock (last)
                foreach (var r in reports)
                {
                    // The last error code stays until a pass delivers something for the target (failed rows are still listed meanwhile).
                    var before = last.GetValueOrDefault(r.Target);
                    string? passError = r.Error == "cancelled" ? before.PassError : r.Error;
                    last[r.Target] = r.Succeeded > 0 && r.LastError is null ? (null, passError) : (r.LastError ?? before.Kind, passError ?? (r.Succeeded > 0 ? null : before.PassError));
                }
            Changed?.Invoke();
            return reports;
        }
        finally { gate.Release(); }
    }

    /// <summary>Stops the loop; a call in flight is cancelled (its row becomes Uncertain, never resent blindly). Bounded.</summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        stop.Cancel();
        if (loop is null) return;
        // ConfigureAwait(false): the host calls this with GetResult() on the UI thread after its message loop ended; resuming on the dispatcher would deadlock the exit.
        try { await loop.WaitAsync(timeout ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // ---------- status and manual check ----------

    public IReadOnlyList<VocabTargetStatus> Status(IReadOnlyCollection<string> targetIds)
    {
        var counts = favorites.DeliveryCounts();
        int Of(string target, params DeliveryState[] states) => counts.Where(c => c.TargetInstanceId == target && states.Contains(c.State)).Sum(c => c.Count);
        lock (last)
            return [.. targetIds.Select(t =>
            {
                var (kind, passError) = last.GetValueOrDefault(t);
                return new VocabTargetStatus(t, Of(t, DeliveryState.Pending, DeliveryState.Sending), Of(t, DeliveryState.RetryWait), Of(t, DeliveryState.Failed), Of(t, DeliveryState.Uncertain),
                    Of(t, DeliveryState.Succeeded), kind, passError);
            })];
    }

    /// <summary>Uncertain and Failed rows, oldest first (at most 100), for the manual-check list.</summary>
    public IReadOnlyList<VocabProblemRow> Problems()
        => [.. favorites.DeliveriesIn([DeliveryState.Uncertain, DeliveryState.Failed], ProblemLimit)
            .Select(d => new VocabProblemRow(d.EntryId, favorites.Get(d.EntryId)?.DisplayText ?? "", d.TargetInstanceId, d.EntryRevision, d.State, d.Attempts))];

    /// <summary>The user checked the target: delivered (Succeeded) or not (queued again with the same operationId).</summary>
    public bool Resolve(string entryId, string target, long revision, bool delivered)
    {
        bool done = favorites.Resolve(entryId, target, revision, delivered);
        if (done) { Changed?.Invoke(); if (!delivered) Kick(); }
        return done;
    }

    /// <summary>Queues every Failed row of the target again (same operationIds) and asks for a pass. Returns how many.</summary>
    public int RetryFailed(string target)
    {
        int n = 0;
        foreach (var row in favorites.DeliveriesIn([DeliveryState.Failed], int.MaxValue).Where(d => d.TargetInstanceId == target))
            if (favorites.Resolve(row.EntryId, row.TargetInstanceId, row.EntryRevision, delivered: false)) n++;
        if (n > 0) { Changed?.Invoke(); Kick(); }
        return n;
    }

    /// <summary>
    /// Queues the favorites that predate the target (it was enabled later). An entry the target already has a row for keeps it;
    /// only the missing rows are added. Returns the number of entries looked at.
    /// </summary>
    public int QueueExisting(string target)
    {
        int n = 0;
        foreach (var e in favorites.List())
        {
            favorites.Favorite(new FavoriteCard(e.Lang, e.DisplayText, e.ContentJson), [target]);
            n++;
        }
        if (n > 0) { Changed?.Invoke(); Kick(); }
        return n;
    }

    // ---------- export ----------

    public VocabExportOutcome Export(VocabExportRequest request, CancellationToken cancellationToken)
        => exporter is null ? new VocabExportOutcome(false, "unavailable", false, null, 0, 0, []) : exporter.Export(request, cancellationToken);
}
