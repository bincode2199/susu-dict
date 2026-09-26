using System.Diagnostics;
using Susu.Abstractions;
using Susu.Windows.Selection;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>F08.1 SEL02/SEL03 rules of the level-1 reader, with a fake platform and helper (no processes, no COM).</summary>
public class SelectionReaderTests
{
    private sealed class FakeHelper : ISelectionHelper
    {
        public readonly TaskCompletionSource<HelperReply> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Killed;
        public bool IsReady = true;
        public bool Ready => IsReady;
        public Task<HelperReply> Completion => Reply.Task;
        public void Dispose() { Killed = true; Reply.TrySetException(new InvalidOperationException("killed")); }
    }

    private sealed class FakePlatform : ISelectionPlatform
    {
        public long Now;
        public nint Foreground = 10;
        public string Class = "Notepad";
        public int Starts;
        public bool FailStart;
        public Func<FakeHelper> NextHelper = () => new FakeHelper();
        public readonly List<FakeHelper> Helpers = [];
        public long NowMilliseconds => Now;
        public ForegroundSnapshot Snapshot() => Snap();
        public nint ForegroundWindow() => Foreground;
        public string WindowClass(nint window) => Class;
        public ISelectionHelper Start(nint window, int uiaBudgetMs)
        {
            Starts++;
            if (FailStart) throw new InvalidOperationException("no helper");
            var helper = NextHelper();
            lock (Helpers) Helpers.Add(helper);
            return helper;
        }
    }

    private static ForegroundSnapshot Snap(nint window = 10, int pid = 100, string cls = "Notepad", bool elevated = false) => new(window, window, pid, 1234, cls, elevated, 0);
    private static SelectionReaderOptions Options(int acquireMs = 150) => new(TimeSpan.FromMilliseconds(acquireMs), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(60), 3, TimeSpan.FromSeconds(30));
    private static HelperReply Reply(string reason, string text = "", string source = "uia", double[]? rect = null, int ranges = 0) => new(reason, text, source, rect, 144, ranges);

    private static FakePlatform Replying(HelperReply reply)
        => new() { NextHelper = () => { var h = new FakeHelper(); h.Reply.SetResult(reply); return h; } };

    [Fact]
    public async Task Selected_text_rect_dpi_and_ranges_pass_through()
    {
        var platform = Replying(Reply("selected", "a\nb", rect: [10, 20, 110, 40], ranges: 2));
        var result = await new SelectionReader(platform, Options()).ReadAsync(Snap(), CancellationToken.None);
        Assert.Equal(SelectionStatus.Selected, result.Status);
        Assert.Equal("a\nb", result.Text);
        Assert.Equal(new ScreenRect(10, 20, 110, 40), result.Rect);
        Assert.Equal(144, result.Dpi);
        Assert.Equal(2, result.Ranges);
    }

    [Theory]
    [InlineData("password", SelectionStatus.Password)]
    [InlineData("empty", SelectionStatus.Empty)]
    [InlineData("unsupported", SelectionStatus.Unsupported)]
    [InlineData("focus-changed", SelectionStatus.FocusChanged)]
    public async Task Failures_carry_no_text(string reason, SelectionStatus expected)
    {
        var result = await new SelectionReader(Replying(Reply(reason)), Options()).ReadAsync(Snap(), CancellationToken.None);
        Assert.Equal(expected, result.Status);
        Assert.Equal("", result.Text);
        Assert.Null(result.Rect);
    }

    [Fact]
    public async Task Selected_but_empty_text_is_an_explicit_empty_failure()
    {
        var result = await new SelectionReader(Replying(Reply("selected", "")), Options()).ReadAsync(Snap(), CancellationToken.None);
        Assert.Equal(SelectionStatus.Empty, result.Status);
    }

    [Fact]
    public async Task Foreground_moved_during_read_is_focus_changed()
    {
        var platform = Replying(Reply("selected", "x"));
        platform.Foreground = 99;
        var result = await new SelectionReader(platform, Options()).ReadAsync(Snap(), CancellationToken.None);
        Assert.Equal(SelectionStatus.FocusChanged, result.Status);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task No_target_and_elevated_target_launch_no_helper()
    {
        var platform = Replying(Reply("selected", "x"));
        var reader = new SelectionReader(platform, Options());
        Assert.Equal(SelectionStatus.NoTarget, (await reader.ReadAsync(Snap(window: 0), CancellationToken.None)).Status);
        Assert.Equal(SelectionStatus.Elevated, (await reader.ReadAsync(Snap(elevated: true), CancellationToken.None)).Status);
        Assert.Equal(0, platform.Starts);
    }

    [Fact]
    public async Task Hung_helper_is_killed_at_the_deadline_and_the_late_reply_is_dropped()
    {
        var platform = new FakePlatform();
        var reader = new SelectionReader(platform, Options(acquireMs: 150));
        var timer = Stopwatch.StartNew();
        var result = await reader.ReadAsync(Snap(), CancellationToken.None);
        Assert.Equal(SelectionStatus.Timeout, result.Status);
        Assert.InRange(timer.ElapsedMilliseconds, 100, 1000);
        Assert.True(platform.Helpers[0].Killed);
        platform.Helpers[0].Reply.TrySetResult(Reply("selected", "late")); // no effect: already failed and dropped
        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task Timeout_is_cached_only_when_the_helper_had_started()
    {
        var platform = new FakePlatform { NextHelper = () => new FakeHelper { IsReady = false } };
        var reader = new SelectionReader(platform, Options(acquireMs: 80));
        Assert.Equal(SelectionStatus.Timeout, (await reader.ReadAsync(Snap(), CancellationToken.None)).Status);
        Assert.Equal(SelectionStatus.Timeout, (await reader.ReadAsync(Snap(), CancellationToken.None)).Status);
        Assert.Equal(2, platform.Starts); // slow start: not cached, the next hotkey tries again

        platform.NextHelper = () => new FakeHelper(); // started, then ran out of time
        Assert.Equal(SelectionStatus.Timeout, (await reader.ReadAsync(Snap(pid: 101), CancellationToken.None)).Status);
        var cached = await reader.ReadAsync(Snap(pid: 101), CancellationToken.None);
        Assert.Equal("cache", cached.Source);
        Assert.Equal(3, platform.Starts);
    }

    [Fact]
    public async Task ReadAsync_never_blocks_the_calling_thread()
    {
        var platform = new FakePlatform();
        var reader = new SelectionReader(platform, Options(acquireMs: 300));
        var timer = Stopwatch.StartNew();
        var pending = reader.ReadAsync(Snap(), CancellationToken.None);
        long returned = timer.ElapsedMilliseconds;
        Assert.False(pending.IsCompleted);
        Assert.True(returned < 100, $"ReadAsync blocked the caller for {returned} ms");
        Assert.Equal(SelectionStatus.Timeout, (await pending).Status);
    }

    [Fact]
    public async Task A_new_read_supersedes_the_previous_one_and_kills_its_helper()
    {
        var platform = new FakePlatform();
        var reader = new SelectionReader(platform, Options(acquireMs: 2000));
        var first = reader.ReadAsync(Snap(), CancellationToken.None);
        while (platform.Starts == 0) await Task.Delay(5, TestContext.Current.CancellationToken);
        platform.NextHelper = () => { var h = new FakeHelper(); h.Reply.SetResult(Reply("selected", "second")); return h; };
        var second = await reader.ReadAsync(Snap(), CancellationToken.None);
        var old = await first;
        Assert.Equal(SelectionStatus.Cancelled, old.Status);
        Assert.True(platform.Helpers[0].Killed);
        Assert.Equal("second", second.Text);
    }

    [Fact]
    public async Task Caller_cancellation_returns_cancelled_without_counting_as_a_helper_failure()
    {
        var platform = new FakePlatform();
        var reader = new SelectionReader(platform, Options(acquireMs: 2000));
        for (int i = 0; i < 4; i++)
        {
            using var cancel = new CancellationTokenSource(30);
            Assert.Equal(SelectionStatus.Cancelled, (await reader.ReadAsync(Snap(pid: 200 + i), cancel.Token)).Status);
        }
        Assert.Equal(4, platform.Starts); // no cool-down was triggered by cancellations
    }

    [Fact]
    public async Task Failing_target_is_cached_for_60_seconds_by_pid_start_time_and_class()
    {
        var platform = Replying(Reply("unsupported", source: "none"));
        var reader = new SelectionReader(platform, Options());
        Assert.Equal(SelectionStatus.Unsupported, (await reader.ReadAsync(Snap(), CancellationToken.None)).Status);
        var cached = await reader.ReadAsync(Snap(), CancellationToken.None);
        Assert.Equal(SelectionStatus.Unsupported, cached.Status);
        Assert.Equal("cache", cached.Source);
        Assert.Equal(1, platform.Starts);
        await reader.ReadAsync(Snap(pid: 101), CancellationToken.None); // another process is not affected
        Assert.Equal(2, platform.Starts);
        await reader.ReadAsync(Snap() with { ProcessStartTime = 999 }, CancellationToken.None); // restarted program: new key
        Assert.Equal(3, platform.Starts);
        platform.Now = 60_001;
        await reader.ReadAsync(Snap(), CancellationToken.None);
        Assert.Equal(4, platform.Starts);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("password")]
    [InlineData("focus-changed")]
    public async Task User_state_failures_are_not_cached(string reason)
    {
        var platform = Replying(Reply(reason));
        var reader = new SelectionReader(platform, Options());
        await reader.ReadAsync(Snap(), CancellationToken.None);
        await reader.ReadAsync(Snap(), CancellationToken.None);
        Assert.Equal(2, platform.Starts);
    }

    [Fact]
    public async Task Consecutive_helper_failures_are_bounded_by_a_cool_down()
    {
        var platform = new FakePlatform { FailStart = true };
        var reader = new SelectionReader(platform, Options());
        for (int i = 0; i < 3; i++) Assert.Equal(SelectionStatus.HelperFailed, (await reader.ReadAsync(Snap(pid: 300 + i), CancellationToken.None)).Status);
        for (int i = 0; i < 5; i++) Assert.Equal(SelectionStatus.HelperUnavailable, (await reader.ReadAsync(Snap(pid: 400 + i), CancellationToken.None)).Status);
        Assert.Equal(3, platform.Starts);
        platform.Now = 30_001; // cool-down over: one new attempt is allowed
        platform.FailStart = false;
        platform.NextHelper = () => { var h = new FakeHelper(); h.Reply.SetResult(Reply("selected", "ok")); return h; };
        Assert.Equal(SelectionStatus.Selected, (await reader.ReadAsync(Snap(pid: 500), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Hangs_count_towards_the_restart_bound()
    {
        var platform = new FakePlatform();
        var reader = new SelectionReader(platform, Options(acquireMs: 60));
        for (int i = 0; i < 3; i++) Assert.Equal(SelectionStatus.Timeout, (await reader.ReadAsync(Snap(pid: 600 + i), CancellationToken.None)).Status);
        Assert.Equal(SelectionStatus.HelperUnavailable, (await reader.ReadAsync(Snap(pid: 700), CancellationToken.None)).Status);
        Assert.Equal(3, platform.Starts);
        Assert.All(platform.Helpers, h => Assert.True(h.Killed));
    }

    [Fact]
    public async Task Cold_browser_window_reports_not_ready_once_then_real_status()
    {
        var platform = Replying(Reply("empty"));
        var reader = new SelectionReader(platform, Options());
        var snap = Snap(cls: "Chrome_WidgetWin_1");
        Assert.Equal(SelectionStatus.NotReady, (await reader.ReadAsync(snap, CancellationToken.None)).Status);
        Assert.Equal(SelectionStatus.Empty, (await reader.ReadAsync(snap, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Prime_runs_one_bounded_helper_for_browser_classes_only()
    {
        var platform = new FakePlatform { Class = "Notepad" };
        var reader = new SelectionReader(platform, Options(acquireMs: 50));
        reader.Prime(10);
        Assert.Equal(0, platform.Starts);
        platform.Class = "MozillaWindowClass";
        reader.Prime(10);
        reader.Prime(10);
        await Eventually.WaitAsync(() => platform.Starts != 0 && platform.Helpers[0].Killed);
        Assert.Equal(1, platform.Starts);
        Assert.True(platform.Helpers[0].Killed);
        platform.NextHelper = () => { var h = new FakeHelper(); h.Reply.SetResult(Reply("empty")); return h; };
        // Already primed: an empty selection is a real empty selection, not NotReady.
        Assert.Equal(SelectionStatus.Empty, (await reader.ReadAsync(Snap(cls: "MozillaWindowClass"), CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("""{"Text":"hi","Reason":"selected","Source":"uia","Rect":[1,2,3,4],"Dpi":96,"Ranges":1,"UiaMs":1,"Ia2Ms":0}""", true)]
    [InlineData("""{"Text":"secret","Reason":"password","Source":"uia","Dpi":96,"Ranges":0}""", false)]
    [InlineData("""{"Text":"","Reason":"whatever","Source":"uia","Dpi":96,"Ranges":0}""", false)]
    [InlineData("""{"Text":"","Reason":"empty","Source":"http","Dpi":96,"Ranges":0}""", false)]
    [InlineData("""{"Text":"x","Reason":"selected","Source":"uia","Rect":[1,2],"Dpi":96,"Ranges":1}""", false)]
    [InlineData("not json", false)]
    public void Helper_reply_is_validated(string line, bool valid)
    {
        if (valid) Assert.Equal("hi", SelectionHost.Parse(line).Text);
        else Assert.Throws<FormatException>(() => SelectionHost.Parse(line));
    }

    [Fact]
    public void Serialized_reply_round_trips()
    {
        var reply = new HelperReply("selected", "中文 \"quoted\"\n2", "ia2", [1.5, 2, 30, 40], 120, 0);
        var parsed = SelectionHost.Parse(SelectionHost.Serialize(reply, 3.2, 1.1));
        Assert.Equal(reply.Text, parsed.Text);
        Assert.Equal(reply.Rect, parsed.Rect);
        Assert.Equal(120, parsed.Dpi);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc 300")]
    [InlineData("1 10")]
    [InlineData("1 300 uia")]
    public void Host_mode_rejects_bad_arguments(string line)
    {
        var output = new StringWriter();
        Assert.Equal(2, SelectionHost.Run(line.Split(' ', StringSplitOptions.RemoveEmptyEntries), output, new StringWriter(), processWatchdog: false));
        Assert.Equal("", output.ToString());
    }
}
