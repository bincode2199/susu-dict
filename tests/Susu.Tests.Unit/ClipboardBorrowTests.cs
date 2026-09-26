using System.Diagnostics;
using Susu.Abstractions;
using Susu.Jobs;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>F08.2 borrow transaction (PLAN 3.1) with a fake platform and helper: C02–C06 rules, no real clipboard.</summary>
public class ClipboardBorrowTests
{
    private static ClipboardBorrowOptions Fast => new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150));

    private static Task<BorrowResult> Borrow(FakeClipboardPlatform platform) => BorrowWith(platform, TestContext.Current.CancellationToken);

    private static Task<BorrowResult> BorrowWith(FakeClipboardPlatform platform, CancellationToken cancel)
        => new ClipboardBorrower(platform, Fast).BorrowAsync(FakeClipboardPlatform.Target(), cancel);

    [Fact]
    public async Task Accepted_candidate_is_read_then_the_snapshot_restored_for_that_candidate()
    {
        var platform = new FakeClipboardPlatform();
        var result = await Borrow(platform);
        Assert.Equal(BorrowStatus.Borrowed, result.Status);
        Assert.Equal("copied", result.Text);
        Assert.Equal(BorrowRestore.Restored, result.Restore);
        Assert.Null(result.Notice);
        Assert.Equal([(2u, FakeClipboardPlatform.TargetPid)], platform.Last.RestoreCalls); // candidate sequence and owner (C02)
        Assert.True(platform.Last.Disposed);
    }

    [Theory] // C03: refused before Ctrl+C; nothing touched
    [InlineData(ClipboardSnapshotStatus.PrivateFormat, BorrowReason.PrivateFormat)]
    [InlineData(ClipboardSnapshotStatus.VirtualFiles, BorrowReason.VirtualFiles)]
    [InlineData(ClipboardSnapshotStatus.TooLarge, BorrowReason.TooLarge)]
    [InlineData(ClipboardSnapshotStatus.RenderFailed, BorrowReason.RenderFailed)]
    [InlineData(ClipboardSnapshotStatus.Busy, BorrowReason.ClipboardBusy)]
    public async Task Unsafe_snapshot_refuses_before_any_copy(ClipboardSnapshotStatus status, BorrowReason reason)
    {
        var platform = new FakeClipboardPlatform { NextHelper = () => new FakeClipboardHelper { Snapshot = _ => Task.FromResult(new ClipboardSnapshotInfo(status, 1, 0, 0, "X")) } };
        var result = await Borrow(platform);
        Assert.Equal(BorrowStatus.Refused, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(0, platform.Copies);
        Assert.Empty(platform.Last.RestoreCalls);
        Assert.Equal("", result.Text);
    }

    [Fact] // C03: slow delayed rendering; the caller is never blocked, the helper is terminated
    public async Task Slow_snapshot_is_refused_at_the_deadline_without_blocking_the_caller()
    {
        var platform = new FakeClipboardPlatform { NextHelper = () => new FakeClipboardHelper { Snapshot = _ => FakeClipboardHelper.Never<ClipboardSnapshotInfo>() } };
        var timer = Stopwatch.StartNew();
        var pending = Borrow(platform);
        Assert.True(timer.ElapsedMilliseconds < 50); // returned to the (message pump) caller at once
        var result = await pending;
        Assert.Equal((BorrowStatus.Refused, BorrowReason.SlowRender), (result.Status, result.Reason));
        Assert.InRange(timer.ElapsedMilliseconds, 80, 1000);
        Assert.True(platform.Last.Disposed);
        Assert.Equal(0, platform.Copies);
    }

    [Fact] // C04
    public async Task Copy_during_the_snapshot_cancels_and_keeps_the_new_content()
    {
        var platform = new FakeClipboardPlatform();
        platform.NextHelper = () => new FakeClipboardHelper
        {
            Snapshot = _ => { platform.Sequence = 7; return Task.FromResult(new ClipboardSnapshotInfo(ClipboardSnapshotStatus.Ok, 1, 1, 1, null)); },
        };
        var result = await Borrow(platform);
        Assert.Equal((BorrowStatus.Cancelled, BorrowReason.ClipboardChanged), (result.Status, result.Reason));
        Assert.Equal(0, platform.Copies);
        Assert.Empty(platform.Last.RestoreCalls);
    }

    [Fact] // C04
    public async Task Foreground_switch_during_the_snapshot_cancels()
    {
        var platform = new FakeClipboardPlatform();
        platform.NextHelper = () => new FakeClipboardHelper
        {
            Snapshot = _ => { platform.Foreground = 99; return Task.FromResult(new ClipboardSnapshotInfo(ClipboardSnapshotStatus.Ok, 1, 1, 1, null)); },
        };
        var result = await Borrow(platform);
        Assert.Equal((BorrowStatus.Cancelled, BorrowReason.FocusChanged), (result.Status, result.Reason));
        Assert.Equal(0, platform.Copies);
    }

    [Fact]
    public async Task Target_not_foreground_at_start_is_cancelled_without_a_helper()
    {
        var platform = new FakeClipboardPlatform { Foreground = 55 };
        var result = await Borrow(platform);
        Assert.Equal((BorrowStatus.Cancelled, BorrowReason.FocusChanged), (result.Status, result.Reason));
        Assert.Empty(platform.Helpers);
    }

    [Fact] // C04: the user copies again while we wait: two updates, newest kept
    public async Task Second_copy_while_waiting_rejects_and_restores_nothing()
    {
        var platform = new FakeClipboardPlatform
        {
            OnCopy = (p, h) => { h.AddUpdate(++p.Sequence, FakeClipboardPlatform.TargetPid); h.AddUpdate(++p.Sequence, 300); },
        };
        var result = await Borrow(platform);
        Assert.Equal((BorrowStatus.Rejected, BorrowReason.MultipleUpdates), (result.Status, result.Reason));
        Assert.Equal(BorrowRestore.NotNeeded, result.Restore);
        Assert.Empty(platform.Last.RestoreCalls);
        Assert.Equal(0, platform.Last.TextReads);
    }

    [Fact] // C04: a newer copy lands between reading the candidate and restoring it
    public async Task Newer_copy_before_restore_is_kept()
    {
        var platform = new FakeClipboardPlatform();
        platform.NextHelper = () => new FakeClipboardHelper
        {
            Text = _ => { platform.Sequence = 50; return Task.FromResult(new ClipboardText(ClipboardTextStatus.Ok, "copied")); },
        };
        var result = await Borrow(platform);
        Assert.Equal(BorrowStatus.Borrowed, result.Status);
        Assert.Equal(BorrowRestore.NewerContentKept, result.Restore);
        Assert.Empty(platform.Last.RestoreCalls);
    }

    [Fact] // C04: the helper's own check under the clipboard lock also keeps newer content
    public async Task Helper_reported_newer_content_is_kept()
    {
        var platform = new FakeClipboardPlatform
        {
            NextHelper = () => new FakeClipboardHelper { Restore = (s, _, _) => Task.FromResult(new ClipboardRestoreResult(ClipboardRestoreStatus.NewerContentKept, s)) },
        };
        var result = await Borrow(platform);
        Assert.Equal(BorrowRestore.NewerContentKept, result.Restore);
        Assert.Null(result.Notice);
    }

    [Fact] // C05: an update from an unrelated process
    public async Task Update_owned_by_another_process_is_not_a_candidate()
    {
        var platform = new FakeClipboardPlatform { OnCopy = (p, h) => h.AddUpdate(++p.Sequence, 4242) };
        var result = await Borrow(platform);
        Assert.Equal((BorrowStatus.Rejected, BorrowReason.OtherSource), (result.Status, result.Reason));
        Assert.Empty(platform.Last.RestoreCalls);
        Assert.Equal("", result.Text);
    }

    [Fact] // C05: several sequence changes behind one event
    public async Task Sequence_that_moved_again_is_ambiguous()
    {
        var platform = new FakeClipboardPlatform { OnCopy = (p, h) => { h.AddUpdate(++p.Sequence, FakeClipboardPlatform.TargetPid); p.Sequence++; } };
        var result = await Borrow(platform);
        Assert.Equal((BorrowStatus.Rejected, BorrowReason.MultipleUpdates), (result.Status, result.Reason));
        Assert.Empty(platform.Last.RestoreCalls);
    }

    [Fact] // C05: no update in time; a late one changes nothing and is never restored over
    public async Task Late_update_after_the_wait_is_ignored()
    {
        var platform = new FakeClipboardPlatform { OnCopy = (_, _) => { } };
        var result = await Borrow(platform);
        Assert.Equal((BorrowStatus.NoCandidate, BorrowReason.NoUpdate), (result.Status, result.Reason));
        platform.Last.AddUpdate(++platform.Sequence, FakeClipboardPlatform.TargetPid);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(platform.Last.RestoreCalls);
        Assert.Equal(0, platform.Last.TextReads);
    }

    [Fact] // C05: sequence changed but no listener event: cannot be attributed
    public async Task Sequence_change_without_event_is_unconfirmed()
    {
        var platform = new FakeClipboardPlatform { OnCopy = (p, _) => p.Sequence++ };
        var result = await Borrow(platform);
        Assert.Equal((BorrowStatus.Rejected, BorrowReason.Unconfirmed), (result.Status, result.Reason));
        Assert.Empty(platform.Last.RestoreCalls);
    }

    [Fact] // C05: target exits
    public async Task Target_exit_rejects_the_candidate()
    {
        var platform = new FakeClipboardPlatform();
        platform.OnCopy = (p, h) => { h.AddUpdate(++p.Sequence, FakeClipboardPlatform.TargetPid); p.Running = false; };
        var result = await Borrow(platform);
        Assert.Equal((BorrowStatus.Rejected, BorrowReason.TargetExited), (result.Status, result.Reason));
        Assert.Empty(platform.Last.RestoreCalls);

        var gone = new FakeClipboardPlatform { Running = false, OnCopy = (_, _) => { } };
        Assert.Equal(BorrowReason.TargetExited, (await Borrow(gone)).Reason);
    }

    [Fact] // C05: target hangs while rendering the text
    public async Task Hung_target_is_abandoned_without_restore()
    {
        var platform = new FakeClipboardPlatform { NextHelper = () => new FakeClipboardHelper { Text = _ => FakeClipboardHelper.Never<ClipboardText>() } };
        var result = await Borrow(platform);
        Assert.Equal((BorrowStatus.Rejected, BorrowReason.TargetHung), (result.Status, result.Reason));
        Assert.Empty(platform.Last.RestoreCalls);
        Assert.True(platform.Last.Disposed);
    }

    [Fact] // C05: a restore that misses its deadline is abandoned (helper terminated) and never retried
    public async Task Late_restore_is_abandoned_once()
    {
        var late = new TaskCompletionSource<ClipboardRestoreResult>();
        var platform = new FakeClipboardPlatform { NextHelper = () => new FakeClipboardHelper { Restore = (_, _, _) => late.Task } };
        var result = await Borrow(platform);
        Assert.Equal(BorrowRestore.Failed, result.Restore);
        Assert.Equal(CaptureMessages.RestoreFailed, result.Notice);
        Assert.True(platform.Last.Disposed);
        late.SetResult(new ClipboardRestoreResult(ClipboardRestoreStatus.Restored, 9));
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.Single(platform.Last.RestoreCalls);
    }

    [Fact] // C06: another application holds the clipboard during restore
    public async Task Held_clipboard_fails_the_restore_with_the_notice_and_keeps_the_text()
    {
        var platform = new FakeClipboardPlatform { NextHelper = () => new FakeClipboardHelper { Restore = (s, _, _) => Task.FromResult(new ClipboardRestoreResult(ClipboardRestoreStatus.Busy, s)) } };
        var events = new List<string>();
        var borrower = new ClipboardBorrower(platform, Fast);
        borrower.Completed += (status, reason, restore, _) => events.Add($"{status} {reason} {restore}");
        var timer = Stopwatch.StartNew();
        var result = await borrower.BorrowAsync(FakeClipboardPlatform.Target(), TestContext.Current.CancellationToken);
        Assert.True(timer.ElapsedMilliseconds < 1000);
        Assert.Equal(BorrowStatus.Borrowed, result.Status);
        Assert.Equal(BorrowRestore.Failed, result.Restore);
        Assert.Equal("未能恢复剪贴板，当前内容已保留", result.Notice);
        Assert.Equal(["Borrowed None Failed"], events); // diagnostics carry no text
    }

    [Fact]
    public async Task Held_modifiers_cancel_before_copy_and_released_ones_proceed()
    {
        var held = new FakeClipboardPlatform { Modifiers = () => true };
        var result = await Borrow(held);
        Assert.Equal((BorrowStatus.Cancelled, BorrowReason.ModifiersHeld), (result.Status, result.Reason));
        Assert.Equal(0, held.Copies);

        var timer = Stopwatch.StartNew();
        var releasing = new FakeClipboardPlatform { Modifiers = () => timer.ElapsedMilliseconds < 40 };
        Assert.Equal(BorrowStatus.Borrowed, (await Borrow(releasing)).Status);
        Assert.Equal(1, releasing.Copies);
    }

    [Fact]
    public async Task Cancellation_before_copy_touches_nothing_and_after_copy_still_restores()
    {
        using var before = new CancellationTokenSource();
        var platform = new FakeClipboardPlatform();
        platform.NextHelper = () => new FakeClipboardHelper
        {
            Snapshot = _ => { before.Cancel(); return Task.FromResult(new ClipboardSnapshotInfo(ClipboardSnapshotStatus.Ok, 1, 1, 1, null)); },
        };
        var early = await BorrowWith(platform, before.Token);
        Assert.Equal(BorrowStatus.Cancelled, early.Status);
        Assert.Equal(0, platform.Copies);

        using var after = new CancellationTokenSource();
        var late = new FakeClipboardPlatform();
        late.OnCopy = (p, h) => { after.Cancel(); h.AddUpdate(++p.Sequence, FakeClipboardPlatform.TargetPid); };
        var result = await BorrowWith(late, after.Token);
        Assert.Equal((BorrowStatus.Cancelled, BorrowReason.Superseded), (result.Status, result.Reason));
        Assert.Equal("", result.Text);
        Assert.Equal(BorrowRestore.Restored, result.Restore); // our copy is still undone
        Assert.Single(late.Last.RestoreCalls);
    }

    [Fact]
    public async Task Candidate_without_text_is_rejected_but_restored()
    {
        var platform = new FakeClipboardPlatform { NextHelper = () => new FakeClipboardHelper { Text = _ => Task.FromResult(new ClipboardText(ClipboardTextStatus.NoText, "")) } };
        var result = await Borrow(platform);
        Assert.Equal((BorrowStatus.Rejected, BorrowReason.NoText, BorrowRestore.Restored), (result.Status, result.Reason, result.Restore));
    }

    [Fact]
    public async Task Helper_start_failure_is_reported_without_copy()
    {
        var platform = new FakeClipboardPlatform { FailStart = true };
        var result = await Borrow(platform);
        Assert.Equal((BorrowStatus.Failed, BorrowReason.HelperFailed), (result.Status, result.Reason));
        Assert.Equal(0, platform.Copies);
    }

    [Fact]
    public async Task Borrows_are_serialized()
    {
        var gate = new TaskCompletionSource<ClipboardSnapshotInfo>();
        int helpers = 0;
        var platform = new FakeClipboardPlatform();
        platform.NextHelper = () => ++helpers == 1
            ? new FakeClipboardHelper { Snapshot = _ => gate.Task }
            : new FakeClipboardHelper { Snapshot = _ => Task.FromResult(new ClipboardSnapshotInfo(ClipboardSnapshotStatus.Ok, platform.Sequence, 1, 1, null)) };
        var borrower = new ClipboardBorrower(platform, Fast with { Snapshot = TimeSpan.FromSeconds(5) });
        var first = borrower.BorrowAsync(FakeClipboardPlatform.Target(), TestContext.Current.CancellationToken);
        var second = borrower.BorrowAsync(FakeClipboardPlatform.Target(), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, helpers); // the second waits for the first to finish
        gate.SetResult(new ClipboardSnapshotInfo(ClipboardSnapshotStatus.Ok, 1, 1, 1, null));
        Assert.Equal(BorrowStatus.Borrowed, (await first).Status);
        Assert.Equal(BorrowStatus.Borrowed, (await second).Status);
        Assert.Equal(2, platform.Copies);
    }

    [Fact]
    public async Task Reading_existing_text_never_copies()
    {
        var platform = new FakeClipboardPlatform();
        var text = await new ClipboardBorrower(platform, Fast).ReadTextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("copied", text.Text);
        Assert.Equal(0, platform.Copies);
        Assert.Empty(platform.Last.RestoreCalls);

        var hung = new FakeClipboardPlatform { NextHelper = () => new FakeClipboardHelper { Text = _ => FakeClipboardHelper.Never<ClipboardText>() } };
        Assert.Equal(ClipboardTextStatus.Busy, (await new ClipboardBorrower(hung, Fast).ReadTextAsync(TestContext.Current.CancellationToken)).Status);
        Assert.True(hung.Last.Disposed);
    }
}
