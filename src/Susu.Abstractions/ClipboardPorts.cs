namespace Susu.Abstractions;

/// <summary>Limits of the level-3 clipboard borrow (PLAN 3.1, TEST-PLAN C01–C07).</summary>
public static class ClipboardLimits
{
    /// <summary>Upper bound of the materialized snapshot (all formats together).</summary>
    public const long MaxSnapshotBytes = 16L << 20;
    /// <summary>Text read from the clipboard, in UTF-16 units (same bound as a selection).</summary>
    public const int MaxTextChars = 65536;
    /// <summary>Snapshot deadline; a slower snapshot (e.g. slow delayed rendering) refuses the borrow before any copy.</summary>
    public static readonly TimeSpan Snapshot = SelectionDeadlines.Snapshot;
    /// <summary>How long the helper process may take to start and install its listener (not part of <see cref="Snapshot"/>).</summary>
    public static readonly TimeSpan HelperStart = TimeSpan.FromSeconds(2);
    /// <summary>How long the hotkey's modifiers may stay down before the borrow is cancelled (Ctrl+C must not become Ctrl+Alt+C).</summary>
    public static readonly TimeSpan ModifierRelease = TimeSpan.FromMilliseconds(300);
    /// <summary>Wait for the target's clipboard update after Ctrl+C.</summary>
    public static readonly TimeSpan CopyWait = TimeSpan.FromMilliseconds(300);
    /// <summary>After the first update, racing updates are collected this long (C04/C05).</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(30);
    /// <summary>Deadline of reading the candidate text (the target may delay-render it or hang).</summary>
    public static readonly TimeSpan ReadText = TimeSpan.FromMilliseconds(300);
    /// <summary>Bounded wait for another application that holds the clipboard open during restore (C06).</summary>
    public static readonly TimeSpan RestoreOpen = TimeSpan.FromMilliseconds(250);
    /// <summary>Hard parent deadline of the restore step; the helper is terminated after it and never restores late.</summary>
    public static readonly TimeSpan Restore = TimeSpan.FromSeconds(1);
}

public enum ClipboardSnapshotStatus
{
    Ok,
    /// <summary>A format outside the restore whitelist (private or non-HGLOBAL data).</summary>
    PrivateFormat,
    /// <summary>Virtual files (FileGroupDescriptor/FileContents); their contents are never read.</summary>
    VirtualFiles,
    /// <summary>More than <see cref="ClipboardLimits.MaxSnapshotBytes"/>.</summary>
    TooLarge,
    /// <summary>The clipboard could not be opened.</summary>
    Busy,
    /// <summary>Delayed rendering refused or returned no data.</summary>
    RenderFailed,
}

/// <summary>Metadata of a snapshot held by the helper. The data itself never leaves the helper process.</summary>
public sealed record ClipboardSnapshotInfo(ClipboardSnapshotStatus Status, uint Sequence, int Formats, long Bytes, string? OffendingFormat);

/// <summary>One <c>WM_CLIPBOARDUPDATE</c> seen by the helper, with the sequence number and clipboard owner's PID at that moment.</summary>
public sealed record ClipboardUpdate(uint Sequence, int OwnerProcessId);

public enum ClipboardTextStatus { Ok, NoText, Busy, TooLong }

public sealed record ClipboardText(ClipboardTextStatus Status, string Text);

public enum ClipboardRestoreStatus
{
    Restored,
    /// <summary>The clipboard no longer holds the accepted candidate (sequence or owner changed): the newer content is kept.</summary>
    NewerContentKept,
    /// <summary>Another application held the clipboard open for the whole bounded wait (C06).</summary>
    Busy,
    Failed,
}

public sealed record ClipboardRestoreResult(ClipboardRestoreStatus Status, uint SequenceAfter);

/// <summary>
/// One short-lived, killable clipboard helper (PLAN 3.1): it listens for clipboard updates from the moment it is
/// ready, materializes the snapshot in its own memory, reads text and restores. Every call that can block on another
/// process (delayed rendering, a hung owner, a held clipboard) runs inside it, so the caller's message pump never
/// waits. Cancelling a call faults its task; the caller then disposes the helper, which terminates it.
/// </summary>
public interface IClipboardHelper : IDisposable
{
    /// <summary>Updates seen so far, in arrival order.</summary>
    int UpdateCount { get; }
    IReadOnlyList<ClipboardUpdate> UpdatesSince(int index);
    /// <summary>Completes when <see cref="UpdateCount"/> exceeds <paramref name="count"/>.</summary>
    Task WaitForUpdateAsync(int count, CancellationToken cancellationToken);
    Task<ClipboardSnapshotInfo> SnapshotAsync(CancellationToken cancellationToken);
    Task<ClipboardText> ReadTextAsync(CancellationToken cancellationToken);
    /// <summary>
    /// Restores the snapshot only while the clipboard still holds the accepted candidate: checked under the clipboard
    /// lock against <paramref name="expectedSequence"/> and <paramref name="expectedOwnerPid"/>.
    /// </summary>
    Task<ClipboardRestoreResult> RestoreAsync(uint expectedSequence, int expectedOwnerPid, TimeSpan openWait, CancellationToken cancellationToken);
}

/// <summary>Non-blocking OS queries and actions of the clipboard borrow; Win32 in Susu.Windows, faked in tests.</summary>
public interface IClipboardPlatform
{
    nint ForegroundWindow();
    /// <summary><c>GetClipboardSequenceNumber</c>.</summary>
    uint SequenceNumber();
    /// <summary>Shift, Ctrl, Alt or a Windows key is down.</summary>
    bool ModifiersDown();
    /// <summary>The process with this PID and start time (FILETIME ticks, 0 = unknown) is still running.</summary>
    bool ProcessRunning(int processId, long startTime);
    /// <summary>Injects Ctrl+C into the foreground window (<c>SendInput</c>).</summary>
    void SendCopy();
    /// <summary>Starts a helper; completes once it is listening for clipboard updates.</summary>
    Task<IClipboardHelper> StartHelperAsync(CancellationToken cancellationToken);
}
