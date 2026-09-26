using System.Diagnostics;
using Susu.Abstractions;

namespace Susu.Jobs;

public enum BorrowStatus
{
    /// <summary>Text copied from the target and the original clipboard handled (see <see cref="BorrowResult.Restore"/>).</summary>
    Borrowed,
    /// <summary>Refused before Ctrl+C: the clipboard could not be snapshotted safely (C03). Nothing was touched.</summary>
    Refused,
    /// <summary>Stopped before Ctrl+C: focus or clipboard changed, modifiers held, or a newer request (C04). Nothing was touched.</summary>
    Cancelled,
    /// <summary>Ctrl+C sent but no update arrived in time; nothing changed, nothing restored (late updates are ignored).</summary>
    NoCandidate,
    /// <summary>An update arrived but without evidence it is this attempt's copy, or without usable text (C05).</summary>
    Rejected,
    /// <summary>The helper failed; see <see cref="BorrowResult.Reason"/>.</summary>
    Failed,
}

public enum BorrowReason
{
    None,
    PrivateFormat, VirtualFiles, TooLarge, SlowRender, ClipboardBusy, RenderFailed,
    HelperFailed, NoTarget, FocusChanged, ClipboardChanged, ModifiersHeld, Superseded,
    NoUpdate, Unconfirmed, OtherSource, MultipleUpdates, TargetExited, TargetHung, NoText, TextTooLong,
}

public enum BorrowRestore
{
    /// <summary>The clipboard was never changed by this attempt (or its change could not be confirmed and is kept).</summary>
    NotNeeded,
    Restored,
    /// <summary>A newer copy replaced the candidate before restore; the newest content is kept (C04).</summary>
    NewerContentKept,
    /// <summary>Restore failed (e.g. another application holds the clipboard, C06); the current content is kept.</summary>
    Failed,
}

/// <summary>Outcome of one borrow. <see cref="Text"/> is empty unless <see cref="Status"/> is <see cref="BorrowStatus.Borrowed"/>.</summary>
public sealed record BorrowResult(BorrowStatus Status, BorrowReason Reason, string Text, BorrowRestore Restore, double SnapshotMs, double CopyWaitMs, double TotalMs)
{
    /// <summary>Shown when the restore failed (PLAN 3.1, C06).</summary>
    public string? Notice => Restore == BorrowRestore.Failed ? CaptureMessages.RestoreFailed : null;
}

public sealed record ClipboardBorrowOptions(TimeSpan HelperStart, TimeSpan Snapshot, TimeSpan ModifierRelease, TimeSpan CopyWait, TimeSpan Settle, TimeSpan ReadText, TimeSpan RestoreOpen, TimeSpan Restore)
{
    public static ClipboardBorrowOptions Default { get; } = new(ClipboardLimits.HelperStart, ClipboardLimits.Snapshot, ClipboardLimits.ModifierRelease,
        ClipboardLimits.CopyWait, ClipboardLimits.Settle, ClipboardLimits.ReadText, ClipboardLimits.RestoreOpen, ClipboardLimits.Restore);
}

/// <summary>
/// Level 3 of the selection capture: the best-effort clipboard borrow of PLAN 3.1 (C02–C06).
/// <list type="number">
/// <item>Before Ctrl+C: a helper materializes the whitelisted formats within 200 ms; anything else refuses the borrow.
/// A clipboard or focus change during the snapshot, held modifiers or a newer request cancel it. Nothing is touched.</item>
/// <item>Ctrl+C, then at most 300 ms for exactly one update whose sequence is current and whose owner is the target
/// process, with focus unchanged and the target alive. Anything else keeps the newest content and restores nothing.</item>
/// <item>The candidate text is read within a deadline, then the snapshot is restored only if the clipboard still holds
/// that candidate (checked by the helper under the clipboard lock). A restore that misses its deadline is abandoned by
/// terminating the helper, so it can never land late.</item>
/// </list>
/// After Ctrl+C a cancelled request still finishes its restore; only its text is dropped. Borrows are serialized so a
/// newer one never snapshots the older one's candidate. Nothing here logs clipboard text.
/// </summary>
public sealed class ClipboardBorrower(IClipboardPlatform platform, ClipboardBorrowOptions? options = null)
{
    private readonly ClipboardBorrowOptions _options = options ?? ClipboardBorrowOptions.Default;
    private readonly SemaphoreSlim _serial = new(1, 1);

    /// <summary>Raised after every borrow and clipboard read with the status only (never text), for diagnostics.</summary>
    public event Action<BorrowStatus, BorrowReason, BorrowRestore, double>? Completed;

    public async Task<BorrowResult> BorrowAsync(ForegroundSnapshot target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var timer = Stopwatch.StartNew();
        try { await _serial.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Done(new(BorrowStatus.Cancelled, BorrowReason.Superseded, "", BorrowRestore.NotNeeded, 0, 0, Ms(timer))); }
        try { return Done(await BorrowCoreAsync(target, cancellationToken, timer).ConfigureAwait(false)); }
        finally { _serial.Release(); }
    }

    /// <summary>
    /// Reads the text already on the clipboard (the "translate clipboard" command and the shared-hotkey fallback). Runs
    /// in a helper as well, since reading can trigger delayed rendering in another process.
    /// </summary>
    public async Task<ClipboardText> ReadTextAsync(CancellationToken cancellationToken)
    {
        IClipboardHelper helper;
        try { helper = await StartAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(ClipboardTextStatus.Busy, ""); }
        using (helper)
        {
            using var deadline = Linked(cancellationToken, _options.ReadText);
            try { return await helper.ReadTextAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { return new(ClipboardTextStatus.Busy, ""); }
        }
    }

    private async Task<BorrowResult> BorrowCoreAsync(ForegroundSnapshot target, CancellationToken cancel, Stopwatch timer)
    {
        double snapshotMs = 0, copyMs = 0;
        BorrowResult Result(BorrowStatus status, BorrowReason reason, BorrowRestore restore = BorrowRestore.NotNeeded, string text = "")
            => new(status, reason, text, restore, Math.Round(snapshotMs, 1), Math.Round(copyMs, 1), Ms(timer));

        if (target.Window == 0) return Result(BorrowStatus.Cancelled, BorrowReason.NoTarget);
        if (platform.ForegroundWindow() != target.Window) return Result(BorrowStatus.Cancelled, BorrowReason.FocusChanged);
        uint start = platform.SequenceNumber();

        IClipboardHelper helper;
        try { helper = await StartAsync(cancel).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return Result(BorrowStatus.Cancelled, BorrowReason.Superseded); }
        catch (Exception error) when (error is not OutOfMemoryException) { return Result(BorrowStatus.Failed, BorrowReason.HelperFailed); }

        using (helper)
        {
            // 1. Snapshot under the 200 ms deadline. On timeout the helper is terminated by Dispose, which releases the clipboard.
            var snapTimer = Stopwatch.StartNew();
            ClipboardSnapshotInfo snapshot;
            try
            {
                using var deadline = Linked(cancel, _options.Snapshot);
                snapshot = await helper.SnapshotAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                snapshotMs = snapTimer.Elapsed.TotalMilliseconds;
                return cancel.IsCancellationRequested ? Result(BorrowStatus.Cancelled, BorrowReason.Superseded) : Result(BorrowStatus.Refused, BorrowReason.SlowRender);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { return Result(BorrowStatus.Failed, BorrowReason.HelperFailed); }
            snapshotMs = snapTimer.Elapsed.TotalMilliseconds;
            if (snapshot.Status != ClipboardSnapshotStatus.Ok) return Result(BorrowStatus.Refused, RefusalReason(snapshot.Status));
            if (snapshot.Sequence != start || platform.SequenceNumber() != start) return Result(BorrowStatus.Cancelled, BorrowReason.ClipboardChanged);
            if (platform.ForegroundWindow() != target.Window) return Result(BorrowStatus.Cancelled, BorrowReason.FocusChanged);

            // 2. The hotkey's modifiers must be up, otherwise Ctrl+C would reach the target as e.g. Ctrl+Alt+C.
            var held = Stopwatch.StartNew();
            try
            {
                while (platform.ModifiersDown())
                {
                    if (held.Elapsed >= _options.ModifierRelease) return Result(BorrowStatus.Cancelled, BorrowReason.ModifiersHeld);
                    await Task.Delay(5, cancel).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { return Result(BorrowStatus.Cancelled, BorrowReason.Superseded); }
            if (cancel.IsCancellationRequested) return Result(BorrowStatus.Cancelled, BorrowReason.Superseded);
            if (platform.SequenceNumber() != start) return Result(BorrowStatus.Cancelled, BorrowReason.ClipboardChanged);
            if (platform.ForegroundWindow() != target.Window) return Result(BorrowStatus.Cancelled, BorrowReason.FocusChanged);

            // Point of no return: from here a cancelled request only loses its text; the transaction runs to the end.
            int before = helper.UpdateCount;
            var copyTimer = Stopwatch.StartNew();
            platform.SendCopy();
            bool updated;
            try
            {
                using var wait = new CancellationTokenSource(_options.CopyWait);
                await helper.WaitForUpdateAsync(before, wait.Token).ConfigureAwait(false);
                updated = true;
            }
            catch (OperationCanceledException) { updated = false; }
            catch (Exception error) when (error is not OutOfMemoryException) { return Result(BorrowStatus.Failed, BorrowReason.HelperFailed); }
            if (!updated)
            {
                copyMs = copyTimer.Elapsed.TotalMilliseconds;
                // A sequence change without a listener event cannot be attributed; keep it as it is.
                if (platform.SequenceNumber() != start) return Result(BorrowStatus.Rejected, BorrowReason.Unconfirmed);
                return Result(BorrowStatus.NoCandidate, platform.ProcessRunning(target.ProcessId, target.ProcessStartTime) ? BorrowReason.NoUpdate : BorrowReason.TargetExited);
            }
            await Task.Delay(_options.Settle, CancellationToken.None).ConfigureAwait(false); // collect racing updates
            copyMs = copyTimer.Elapsed.TotalMilliseconds;

            var updates = helper.UpdatesSince(before);
            if (updates.Count != 1) return Result(BorrowStatus.Rejected, BorrowReason.MultipleUpdates);
            var candidate = updates[0];
            if (candidate.Sequence == start || candidate.Sequence != platform.SequenceNumber()) return Result(BorrowStatus.Rejected, BorrowReason.MultipleUpdates);
            if (candidate.OwnerProcessId != target.ProcessId) return Result(BorrowStatus.Rejected, BorrowReason.OtherSource);
            if (!platform.ProcessRunning(target.ProcessId, target.ProcessStartTime)) return Result(BorrowStatus.Rejected, BorrowReason.TargetExited);
            if (platform.ForegroundWindow() != target.Window) return Result(BorrowStatus.Rejected, BorrowReason.FocusChanged);

            // 3. Read the candidate. A target that hangs while rendering is abandoned with the helper; nothing is restored.
            ClipboardText text;
            try
            {
                using var deadline = new CancellationTokenSource(_options.ReadText);
                text = await helper.ReadTextAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return Result(BorrowStatus.Rejected, BorrowReason.TargetHung); }
            catch (Exception error) when (error is not OutOfMemoryException) { return Result(BorrowStatus.Failed, BorrowReason.HelperFailed); }

            // 4. Conditional restore: the helper re-checks sequence and owner under the clipboard lock.
            var restore = await RestoreAsync(helper, candidate).ConfigureAwait(false);
            if (cancel.IsCancellationRequested) return Result(BorrowStatus.Cancelled, BorrowReason.Superseded, restore);
            return text.Status switch
            {
                ClipboardTextStatus.Ok when text.Text.Length > 0 => Result(BorrowStatus.Borrowed, BorrowReason.None, restore, text.Text),
                ClipboardTextStatus.TooLong => Result(BorrowStatus.Rejected, BorrowReason.TextTooLong, restore),
                ClipboardTextStatus.Busy => Result(BorrowStatus.Rejected, BorrowReason.ClipboardBusy, restore),
                _ => Result(BorrowStatus.Rejected, BorrowReason.NoText, restore),
            };
        }
    }

    private async Task<BorrowRestore> RestoreAsync(IClipboardHelper helper, ClipboardUpdate candidate)
    {
        if (platform.SequenceNumber() != candidate.Sequence) return BorrowRestore.NewerContentKept;
        try
        {
            using var deadline = new CancellationTokenSource(_options.Restore);
            var result = await helper.RestoreAsync(candidate.Sequence, candidate.OwnerProcessId, _options.RestoreOpen, deadline.Token).ConfigureAwait(false);
            return result.Status switch
            {
                ClipboardRestoreStatus.Restored => BorrowRestore.Restored,
                ClipboardRestoreStatus.NewerContentKept => BorrowRestore.NewerContentKept,
                _ => BorrowRestore.Failed,
            };
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return BorrowRestore.Failed; } // abandoned: Dispose terminates the helper
    }

    private async Task<IClipboardHelper> StartAsync(CancellationToken cancel)
    {
        using var deadline = Linked(cancel, _options.HelperStart);
        return await platform.StartHelperAsync(deadline.Token).ConfigureAwait(false);
    }

    private BorrowResult Done(BorrowResult result)
    {
        Completed?.Invoke(result.Status, result.Reason, result.Restore, result.TotalMs);
        return result;
    }

    private static BorrowReason RefusalReason(ClipboardSnapshotStatus status) => status switch
    {
        ClipboardSnapshotStatus.PrivateFormat => BorrowReason.PrivateFormat,
        ClipboardSnapshotStatus.VirtualFiles => BorrowReason.VirtualFiles,
        ClipboardSnapshotStatus.TooLarge => BorrowReason.TooLarge,
        ClipboardSnapshotStatus.Busy => BorrowReason.ClipboardBusy,
        _ => BorrowReason.RenderFailed,
    };

    private static CancellationTokenSource Linked(CancellationToken cancel, TimeSpan after)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        source.CancelAfter(after);
        return source;
    }

    private static double Ms(Stopwatch timer) => Math.Round(timer.Elapsed.TotalMilliseconds, 1);
}
