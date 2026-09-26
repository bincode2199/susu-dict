using Susu.Abstractions;
using Susu.Jobs;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>F08.2 three-level capture and hotkey arbitration (PLAN 3.1, 6.1): C01, J01, with fakes.</summary>
public class CaptureCoordinatorTests
{
    private sealed class FakeReader : ISelectionReader
    {
        public readonly Queue<Func<CancellationToken, Task<SelectionResult>>> Replies = new();
        public Func<CancellationToken, Task<SelectionResult>> Default = _ => Task.FromResult(SelectionResult.Failure(SelectionStatus.Unsupported));
        public int Snapshots, Reads;
        public readonly List<nint> Primed = [];
        public ForegroundSnapshot Snapshot() { Snapshots++; return FakeClipboardPlatform.Target(); }
        public Task<SelectionResult> ReadAsync(ForegroundSnapshot snapshot, CancellationToken cancellationToken)
        {
            Reads++;
            return (Replies.Count > 0 ? Replies.Dequeue() : Default)(cancellationToken);
        }
        public void Prime(nint window) => Primed.Add(window);
    }

    private static Func<CancellationToken, Task<SelectionResult>> Selected(string text) => _ => Task.FromResult(new SelectionResult(SelectionStatus.Selected, text, "uia", new ScreenRect(1, 2, 3, 4), 96, 1, 5));
    private static Func<CancellationToken, Task<SelectionResult>> Failing(SelectionStatus status) => _ => Task.FromResult(SelectionResult.Failure(status));

    private static ClipboardBorrowOptions Fast => new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150));

    private static (CaptureCoordinator, FakeReader, FakeClipboardPlatform) Make(bool borrow, string clipboardText = "clip")
    {
        var reader = new FakeReader();
        var clipboard = new FakeClipboardPlatform
        {
            NextHelper = () => new FakeClipboardHelper { Text = _ => Task.FromResult(clipboardText.Length > 0 ? new ClipboardText(ClipboardTextStatus.Ok, clipboardText) : new ClipboardText(ClipboardTextStatus.NoText, "")) },
        };
        return (new CaptureCoordinator(reader, new ClipboardBorrower(clipboard, Fast), () => borrow), reader, clipboard);
    }

    [Fact]
    public async Task Lossless_selection_is_used_directly()
    {
        var (capture, reader, clipboard) = Make(borrow: true);
        reader.Replies.Enqueue(Selected("hello"));
        var outcome = await capture.CaptureAsync(CaptureTrigger.Selection, TestContext.Current.CancellationToken);
        Assert.Equal((CaptureStatus.Text, "hello", "uia"), (outcome.Status, outcome.Text, outcome.Source));
        Assert.Equal(new ScreenRect(1, 2, 3, 4), outcome.Rect);
        Assert.Empty(clipboard.Helpers);
    }

    [Theory] // C01: borrowing off, levels 1-2 fail: no copy, clipboard untouched, the borrow-off text
    [InlineData(SelectionStatus.Unsupported)]
    [InlineData(SelectionStatus.Timeout)]
    [InlineData(SelectionStatus.HelperFailed)]
    [InlineData(SelectionStatus.Empty)]
    [InlineData(SelectionStatus.Elevated)]
    public async Task Borrow_off_failure_never_touches_the_clipboard(SelectionStatus status)
    {
        var (capture, reader, clipboard) = Make(borrow: false);
        reader.Default = Failing(status);
        var outcome = await capture.CaptureAsync(CaptureTrigger.Selection, TestContext.Current.CancellationToken);
        Assert.Equal(CaptureStatus.Failed, outcome.Status);
        Assert.Equal("当前程序不支持无损取词 —— 可在设置中允许借用剪贴板取词", outcome.Message);
        Assert.Empty(clipboard.Helpers); // no helper, no snapshot, no Ctrl+C, no read
        Assert.Equal(0, clipboard.Copies);
        Assert.Equal("", outcome.Text);
    }

    [Fact]
    public async Task Borrow_on_falls_back_to_level_three()
    {
        var (capture, reader, clipboard) = Make(borrow: true);
        var outcome = await capture.CaptureAsync(CaptureTrigger.Selection, TestContext.Current.CancellationToken);
        Assert.Equal((CaptureStatus.Text, "clip", "borrow"), (outcome.Status, outcome.Text, outcome.Source));
        Assert.Equal(1, clipboard.Copies);
    }

    [Fact]
    public async Task Borrow_on_but_elevated_or_password_does_not_copy_and_uses_the_other_text()
    {
        foreach (var status in new[] { SelectionStatus.Elevated, SelectionStatus.Password, SelectionStatus.Empty })
        {
            var (capture, reader, clipboard) = Make(borrow: true);
            reader.Default = Failing(status);
            var outcome = await capture.CaptureAsync(CaptureTrigger.Selection, TestContext.Current.CancellationToken);
            Assert.Equal(CaptureStatus.Failed, outcome.Status);
            Assert.Equal(CaptureMessages.NotSupported, outcome.Message);
            Assert.Equal(0, clipboard.Copies);
        }
    }

    [Fact]
    public async Task Refused_borrow_reports_its_reason_with_the_borrow_on_text()
    {
        var (capture, _, clipboard) = Make(borrow: true);
        clipboard.NextHelper = () => new FakeClipboardHelper { Snapshot = _ => Task.FromResult(new ClipboardSnapshotInfo(ClipboardSnapshotStatus.PrivateFormat, 1, 0, 0, "X")) };
        var outcome = await capture.CaptureAsync(CaptureTrigger.Selection, TestContext.Current.CancellationToken);
        Assert.Equal((CaptureStatus.Failed, "borrow-private-format", CaptureMessages.NotSupported), (outcome.Status, outcome.FailureKey, outcome.Message));
        Assert.Equal(0, clipboard.Copies);
    }

    [Fact]
    public async Task Restore_failure_notice_travels_with_the_text()
    {
        var (capture, _, clipboard) = Make(borrow: true);
        clipboard.NextHelper = () => new FakeClipboardHelper { Restore = (s, _, _) => Task.FromResult(new ClipboardRestoreResult(ClipboardRestoreStatus.Busy, s)) };
        var outcome = await capture.CaptureAsync(CaptureTrigger.Selection, TestContext.Current.CancellationToken);
        Assert.Equal(CaptureStatus.Text, outcome.Status);
        Assert.Equal(CaptureMessages.RestoreFailed, outcome.Notice);
    }

    [Fact]
    public async Task Cold_browser_gets_one_lossless_retry_first()
    {
        var (capture, reader, clipboard) = Make(borrow: true);
        reader.Replies.Enqueue(Failing(SelectionStatus.NotReady));
        reader.Replies.Enqueue(Selected("warm"));
        var outcome = await capture.CaptureAsync(CaptureTrigger.Selection, TestContext.Current.CancellationToken);
        Assert.Equal(("warm", 2), (outcome.Text, reader.Reads));
        Assert.Empty(clipboard.Helpers);
    }

    [Fact] // PLAN 6.1: selection, then clipboard text, then an empty window
    public async Task Shared_hotkey_falls_back_to_clipboard_text_then_empty()
    {
        var (capture, reader, clipboard) = Make(borrow: false);
        reader.Default = Failing(SelectionStatus.Empty);
        var outcome = await capture.CaptureAsync(CaptureTrigger.Shared, TestContext.Current.CancellationToken);
        Assert.Equal((CaptureStatus.Text, "clip", "clipboard"), (outcome.Status, outcome.Text, outcome.Source));
        Assert.Null(outcome.Message);
        Assert.Equal(0, clipboard.Copies);

        var (empty, emptyReader, _) = Make(borrow: false, clipboardText: "");
        emptyReader.Default = Failing(SelectionStatus.Unsupported);
        var none = await empty.CaptureAsync(CaptureTrigger.Shared, TestContext.Current.CancellationToken);
        Assert.Equal(CaptureStatus.Empty, none.Status);
        Assert.Null(none.Message);
    }

    [Fact]
    public async Task Clipboard_command_reads_existing_text_without_selection_or_copy()
    {
        var (capture, reader, clipboard) = Make(borrow: true);
        var outcome = await capture.CaptureAsync(CaptureTrigger.Clipboard, TestContext.Current.CancellationToken);
        Assert.Equal((CaptureStatus.Text, "clip", "clipboard"), (outcome.Status, outcome.Text, outcome.Source));
        Assert.Equal((0, 0, 0), (reader.Snapshots, reader.Reads, clipboard.Copies));

        var (blank, _, _) = Make(borrow: true, clipboardText: "");
        Assert.Equal(CaptureStatus.Empty, (await blank.CaptureAsync(CaptureTrigger.Clipboard, TestContext.Current.CancellationToken)).Status);
    }

    [Fact] // J01: a newer hotkey supersedes the older capture; only the current generation reaches the session
    public async Task Only_the_current_capture_is_submitted()
    {
        var (capture, reader, _) = Make(borrow: false);
        var slow = new TaskCompletionSource<SelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Replies.Enqueue(_ => slow.Task); // ignores cancellation: its result arrives late
        reader.Replies.Enqueue(Selected("second"));
        var submitted = new List<string>();
        Task Submit(string text) { lock (submitted) submitted.Add(text); return Task.CompletedTask; }

        var first = capture.TranslateAsync(CaptureTrigger.Selection, Submit, TestContext.Current.CancellationToken);
        var second = capture.TranslateAsync(CaptureTrigger.Selection, Submit, TestContext.Current.CancellationToken);
        Assert.Equal(CaptureStatus.Text, (await second).Status);
        slow.SetResult(new SelectionResult(SelectionStatus.Selected, "first", "uia", null, 96, 1, 1));
        var late = await first;
        Assert.Equal(CaptureStatus.Superseded, late.Status);
        Assert.Equal("", late.Text);
        Assert.Equal(["second"], submitted);
        Assert.True(capture.IsCurrent((await second).Generation));
    }

    [Fact]
    public async Task Snapshot_is_taken_synchronously_and_prime_is_forwarded()
    {
        var (capture, reader, _) = Make(borrow: false);
        var pending = capture.CaptureAsync(CaptureTrigger.Selection, TestContext.Current.CancellationToken);
        Assert.Equal(1, reader.Snapshots); // before the first await: no Su-Su window can have taken focus yet
        capture.OnForegroundChanged(77);
        Assert.Equal([(nint)77], reader.Primed);
        await pending;
    }
}
