using Susu.Abstractions;
using Susu.Contracts;

namespace Susu.Jobs;

/// <summary>Retry boundaries of the vocabulary outbox consumer (ARCHITECTURE 8.3): attempts per row, exponential backoff, poll interval.</summary>
public sealed record VocabSyncPolicy(int MaxAttempts = 6, TimeSpan? BaseDelay = null, TimeSpan? MaxDelay = null, TimeSpan? PollInterval = null)
{
    public TimeSpan Base => BaseDelay ?? TimeSpan.FromSeconds(30);
    public TimeSpan Max => MaxDelay ?? TimeSpan.FromHours(1);
    public TimeSpan Poll => PollInterval ?? TimeSpan.FromSeconds(30);

    /// <summary>Delay before attempt <paramref name="attempts"/> + 1: the vendor's Retry-After when given, else Base * 2^(attempts-1); never above <see cref="Max"/>.</summary>
    public TimeSpan Backoff(int attempts, TimeSpan? retryAfter)
    {
        if (retryAfter is { } vendor && vendor > TimeSpan.Zero) return vendor < Max ? vendor : Max;
        double seconds = Base.TotalSeconds * Math.Pow(2, Math.Max(0, Math.Min(attempts, 30) - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, Max.TotalSeconds));
    }
}

/// <summary>What one pass over one target did. <see cref="Error"/> is set when the pass itself failed (a target that threw never stops the others).</summary>
public sealed record VocabSyncReport(string Target, int Succeeded = 0, int Confirmed = 0, int Retrying = 0, int Failed = 0, int Uncertain = 0, string? Error = null);

/// <summary>
/// F15.3: the host consumer of the F15.1 outbox. Per target it first settles Uncertain rows by lookup when the package supports one
/// (found: Succeeded; absent: back-fill write), then drains due Pending/RetryWait rows one at a time (<see cref="IFavorites.Claim"/>,
/// call, <see cref="IFavorites.Complete"/>). Rules: a write whose answer is lost is Uncertain and is never resent by a plain retry
/// (a restart turns Sending into Uncertain, <see cref="IFavorites.RecoverInterrupted"/>); only "nothing was written" outcomes are
/// RetryWait, bounded by <see cref="VocabSyncPolicy.MaxAttempts"/> with backoff on the clock; a target-wide problem (network, rate
/// limit, auth, quota) ends that target's pass without burning the remaining rows; targets are independent; the worker never deletes
/// anything on a remote.
/// </summary>
public sealed class VocabSyncWorker(IFavorites favorites, Func<string, IVocabSyncTarget?> targets, IClock clock, VocabSyncPolicy? policy = null)
{
    private readonly VocabSyncPolicy rules = policy ?? new VocabSyncPolicy();

    /// <summary>One pass over every target; each target runs on its own so one failing or hanging does not block the other.</summary>
    public async Task<IReadOnlyList<VocabSyncReport>> RunAllAsync(IReadOnlyList<string> targetIds, CancellationToken cancellationToken)
        => await Task.WhenAll(targetIds.Distinct(StringComparer.Ordinal).Select(async id =>
        {
            try { return await RunTargetAsync(id, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new VocabSyncReport(id, Error: "cancelled"); }
            catch (Exception error) { return new VocabSyncReport(id, Error: error.Message); }
        }));

    /// <summary>Runs <see cref="RunAllAsync"/> every poll interval on the clock until cancelled.</summary>
    public async Task RunAsync(Func<IReadOnlyList<string>> targetIds, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunAllAsync(targetIds(), cancellationToken);
            try { await clock.Delay(rules.Poll, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task<VocabSyncReport> RunTargetAsync(string targetId, CancellationToken cancellationToken)
    {
        var report = new VocabSyncReport(targetId);
        var target = targets(targetId);
        if (target is null) return report with { Error = "no such target" };
        bool stop = false;
        if (target.SupportsLookup)
        {
            VocabDelivery? uncertain;
            while (!stop && !cancellationToken.IsCancellationRequested && (uncertain = favorites.ClaimUncertain(targetId)) is not null)
                (report, stop) = await ProcessAsync(uncertain, target, report, resolving: true, cancellationToken);
        }
        VocabDelivery? row;
        while (!stop && !cancellationToken.IsCancellationRequested && (row = favorites.Claim(targetId)) is not null)
            (report, stop) = await ProcessAsync(row, target, report, resolving: false, cancellationToken);
        return report;
    }

    private async Task<(VocabSyncReport Report, bool Stop)> ProcessAsync(VocabDelivery row, IVocabSyncTarget target, VocabSyncReport report, bool resolving, CancellationToken cancellationToken)
    {
        var entry = favorites.Get(row.EntryId);
        if (entry is null)
        {
            favorites.Complete(row.EntryId, row.TargetInstanceId, row.EntryRevision, DeliveryState.Failed);
            return (report with { Failed = report.Failed + 1 }, false);
        }
        VocabSyncRequest Make(VocabAction action) => new(row.OperationId, action, row.EntryId, row.EntryRevision, entry.DisplayText, entry.Lang, entry.ContentJson);
        try
        {
            if (resolving)
            {
                switch (await target.SendAsync(Make(VocabAction.Lookup), cancellationToken))
                {
                    case VocabSyncOutcome.Found found:
                        favorites.Complete(row.EntryId, row.TargetInstanceId, row.EntryRevision, DeliveryState.Succeeded, found.RemoteId);
                        return (report with { Succeeded = report.Succeeded + 1, Confirmed = report.Confirmed + 1 }, false);
                    case VocabSyncOutcome.Absent:
                        break; // the write never landed: back-fill it below
                    default: // could not ask: stay Uncertain, leave the target alone until the next pass
                        favorites.Complete(row.EntryId, row.TargetInstanceId, row.EntryRevision, DeliveryState.Uncertain);
                        return (report with { Uncertain = report.Uncertain + 1 }, true);
                }
            }
            return Settle(row, await target.SendAsync(Make(VocabAction.Upsert), cancellationToken), report, resolving);
        }
        catch (Exception error)
        {
            // Cancelled or crashed mid-call: if a write was in flight nobody knows whether it landed, so the row waits for a lookup or the user.
            favorites.Complete(row.EntryId, row.TargetInstanceId, row.EntryRevision, DeliveryState.Uncertain);
            return (report with { Uncertain = report.Uncertain + 1, Error = error is OperationCanceledException ? report.Error : error.Message }, true);
        }
    }

    private (VocabSyncReport Report, bool Stop) Settle(VocabDelivery row, VocabSyncOutcome outcome, VocabSyncReport report, bool resolving)
    {
        void Done(DeliveryState state, string? remote = null, TimeSpan? after = null) => favorites.Complete(row.EntryId, row.TargetInstanceId, row.EntryRevision, state, remote, after);
        switch (outcome)
        {
            case VocabSyncOutcome.Applied applied:
                Done(DeliveryState.Succeeded, applied.RemoteId);
                return (report with { Succeeded = report.Succeeded + 1 }, false);
            case VocabSyncOutcome.Found found:
                Done(DeliveryState.Succeeded, found.RemoteId);
                return (report with { Succeeded = report.Succeeded + 1, Confirmed = report.Confirmed + (resolving ? 1 : 0) }, false);
            case VocabSyncOutcome.Unknown:
                // Never resent by a retry: a later pass settles it by lookup, or the user checks the target.
                Done(DeliveryState.Uncertain);
                return (report with { Uncertain = report.Uncertain + 1 }, resolving);
            case VocabSyncOutcome.Retry retry:
                if (row.Attempts >= rules.MaxAttempts)
                {
                    Done(DeliveryState.Failed);
                    return (report with { Failed = report.Failed + 1 }, true);
                }
                Done(DeliveryState.RetryWait, after: rules.Backoff(row.Attempts, retry.Error.RetryAfter));
                return (report with { Retrying = report.Retrying + 1 }, true);
            case VocabSyncOutcome.Failure failure:
                Done(DeliveryState.Failed);
                // The account or key is wrong for every row of this target: do not fail the rest one by one.
                return (report with { Failed = report.Failed + 1 }, failure.Error.Kind is ErrorKind.Auth or ErrorKind.Quota);
            default: // Absent for a write is a protocol error of the target
                Done(DeliveryState.Uncertain);
                return (report with { Uncertain = report.Uncertain + 1 }, true);
        }
    }
}
