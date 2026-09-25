namespace Susu.Abstractions;

/// <summary>
/// Foreground state captured at hotkey time, before any Su-Su window is shown (ARCHITECTURE 4.1).
/// <see cref="ProcessStartTime"/> (FILETIME ticks) and <see cref="WindowClass"/> with the PID form the
/// failure-cache key, so a restarted program is never matched against an old entry.
/// </summary>
public sealed record ForegroundSnapshot(nint Window, nint FocusWindow, int ProcessId, long ProcessStartTime, string WindowClass,
    bool TargetElevated, long TakenAtMs);

/// <summary>Outcome of level 1 (UIA → IA2) of the three-level capture. Everything except <see cref="Selected"/> is a failure.</summary>
public enum SelectionStatus
{
    Selected,
    /// <summary>Focus is in a password field; nothing is read (SEL03).</summary>
    Password,
    /// <summary>The control has a selection API but nothing is selected; the caret paragraph is never substituted (SEL03).</summary>
    Empty,
    /// <summary>Neither UIA TextPattern nor IA2 text is exposed by the focused control.</summary>
    Unsupported,
    /// <summary>A browser/Electron window whose accessibility tree was only just activated by this read (F00 SEL01 priming); retrying the selection can succeed.</summary>
    NotReady,
    /// <summary>Focus or the foreground window moved away from the snapshot during the read.</summary>
    FocusChanged,
    /// <summary>The target runs at a higher integrity level; the helper is never elevated (SEL03).</summary>
    Elevated,
    /// <summary>No foreground window at hotkey time.</summary>
    NoTarget,
    /// <summary>The helper missed the acquire deadline and was terminated (SEL02).</summary>
    Timeout,
    /// <summary>The helper failed to start, crashed or returned an invalid reply.</summary>
    HelperFailed,
    /// <summary>Too many consecutive helper failures; no helper is launched until the cool-down ends (bounded restarts, SEL02).</summary>
    HelperUnavailable,
    /// <summary>A newer read (hotkey) or the caller cancelled this one; its result, if any, is dropped.</summary>
    Cancelled,
}

/// <summary>Screen rectangle in physical pixels.</summary>
public sealed record ScreenRect(double Left, double Top, double Right, double Bottom);

/// <summary>
/// Level-1 capture result. <see cref="Source"/> is "uia", "ia2", "cache" (a cached failure) or "none".
/// <see cref="Rect"/> is the union of the selection's bounds in physical pixels (UIA only) and <see cref="Dpi"/>
/// the effective DPI of the monitor it is on (of the target window when there is no rect), for placing the floating bar.
/// Multiple selections are merged in reading order, separated by "\n"; <see cref="Ranges"/> counts them (0 when unknown).
/// </summary>
public sealed record SelectionResult(SelectionStatus Status, string Text, string Source, ScreenRect? Rect, int Dpi, int Ranges, double ElapsedMs)
{
    public bool Succeeded => Status == SelectionStatus.Selected;
    public static SelectionResult Failure(SelectionStatus status, double elapsedMs = 0, string source = "none") => new(status, "", source, null, 0, 0, elapsedMs);
}

/// <summary>Deadlines of ARCHITECTURE 4.1 (SEL02).</summary>
public static class SelectionDeadlines
{
    /// <summary>Total helper deadline, including process start; enforced by the parent, which terminates the helper.</summary>
    public static readonly TimeSpan Acquire = TimeSpan.FromMilliseconds(500);
    /// <summary>UIA transaction budget inside the helper; the remainder of <see cref="Acquire"/> is left for IA2.</summary>
    public static readonly TimeSpan UiaBudget = TimeSpan.FromMilliseconds(300);
    /// <summary>Safe (clipboard) snapshot deadline, used by the level-3 clipboard borrow (F08.2).</summary>
    public static readonly TimeSpan Snapshot = TimeSpan.FromMilliseconds(200);
    /// <summary>How long a failing target (PID + start time + window class) is skipped.</summary>
    public static readonly TimeSpan FailureCache = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Level 1 of the three-level selection capture (UIA → IA2, in the temporary <c>--selection-host</c> process).
/// Implemented by Susu.Windows; faked in tests. Clipboard borrowing (level 3) is a separate, explicit step.
/// </summary>
public interface ISelectionReader
{
    /// <summary>Captures the foreground window, focus, process identity and elevation. Call on the hotkey thread before showing any window. Non-blocking.</summary>
    ForegroundSnapshot Snapshot();

    /// <summary>
    /// Reads the selection of the snapshot's target. Never throws for target or helper failures and completes by
    /// <see cref="SelectionDeadlines.Acquire"/>; the calling thread (message pump) is never blocked. Only one read is
    /// current: a new call cancels the previous one, whose late result is dropped.
    /// </summary>
    Task<SelectionResult> ReadAsync(ForegroundSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// Activates the accessibility tree of a browser/Electron window ahead of the first hotkey (F00 SEL01: Firefox and
    /// cold Electron expose a selection only after a first accessibility query). Fire-and-forget, bounded by the same
    /// deadline; a no-op for other window classes.
    /// </summary>
    void Prime(nint window);
}
