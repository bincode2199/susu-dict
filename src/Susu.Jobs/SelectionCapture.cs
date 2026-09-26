using Susu.Abstractions;

namespace Susu.Jobs;

/// <summary>User-visible capture texts (PLAN 3.1, DESIGN 7). The two failure texts must not be mixed.</summary>
public static class CaptureMessages
{
    /// <summary>Lossless capture failed and borrowing is off.</summary>
    public const string NotSupportedBorrowOff = "当前程序不支持无损取词 —— 可在设置中允许借用剪贴板取词";
    /// <summary>Capture failed although borrowing is on (e.g. an elevated window).</summary>
    public const string NotSupported = "当前程序不支持取词";
    /// <summary>The borrow copied text but could not put the original clipboard back (C06).</summary>
    public const string RestoreFailed = "未能恢复剪贴板，当前内容已保留";
}

/// <summary>What started a capture.</summary>
public enum CaptureTrigger
{
    /// <summary>The selection hotkey (not shared): three-level capture, a failure shows the failure bar.</summary>
    Selection,
    /// <summary>The chord shared by selection and clipboard translation (PLAN 6.1): selection first, then clipboard text, then an empty window.</summary>
    Shared,
    /// <summary>"Translate clipboard" (hotkey or tray): reads the text already on the clipboard; never sends Ctrl+C.</summary>
    Clipboard,
    /// <summary>
    /// The pronunciation hotkey (PLAN 6.5, F10.2): the same three-level capture as <see cref="Selection"/> (never the
    /// clipboard fallback, never shared with another chord); the text is spoken, not translated.
    /// </summary>
    Pronounce,
}

public enum CaptureStatus
{
    /// <summary><see cref="CaptureOutcome.Text"/> is ready to translate.</summary>
    Text,
    /// <summary>Nothing to translate: open the window empty and wait for input (shared hotkey / clipboard without text).</summary>
    Empty,
    /// <summary>Show the failure bar with <see cref="CaptureOutcome.Message"/>; no window.</summary>
    Failed,
    /// <summary>A newer capture replaced this one; ignore it entirely (J01).</summary>
    Superseded,
}

/// <summary>
/// Result of one capture. <see cref="Source"/> is "uia", "ia2", "borrow" or "clipboard" for text, else "none".
/// <see cref="FailureKey"/> is the stable reason for the UI and diagnostics (a <see cref="SelectionStatus"/> or
/// <see cref="BorrowReason"/> name in kebab case); <see cref="Notice"/> is an extra floating-bar text such as
/// <see cref="CaptureMessages.RestoreFailed"/>, which can accompany any status. <see cref="Rect"/>/<see cref="Dpi"/>
/// place the window next to a UIA selection.
/// </summary>
public sealed record CaptureOutcome(long Generation, CaptureTrigger Trigger, CaptureStatus Status, string Text, string Source,
    string? FailureKey, string? Message, string? Notice, ScreenRect? Rect, int Dpi, double ElapsedMs);

/// <summary>
/// The three-level capture of PLAN 3.1 and the hotkey arbitration of PLAN 6.1, above the ports: level 1 UIA and level 2
/// IA2 in the selection helper (<see cref="ISelectionReader"/>), level 3 the optional clipboard borrow
/// (<see cref="ClipboardBorrower"/>, off by default). Each capture takes a new generation; a newer one cancels the older,
/// whose result comes back <see cref="CaptureStatus.Superseded"/> and is never submitted (J01).
/// </summary>
public sealed class CaptureCoordinator(ISelectionReader reader, ClipboardBorrower borrower, Func<bool> borrowEnabled)
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _current;
    private long _generation;

    /// <summary>Raised per capture with the status, source and failure key (never text), for diagnostics.</summary>
    public event Action<CaptureTrigger, CaptureStatus, string, string?, double>? Completed;

    public long CurrentGeneration { get { lock (_gate) return _generation; } }

    /// <summary>
    /// Maps a hotkey action to its capture (PLAN 6.1, CFG05): selection with the same chord as clipboard translation is
    /// <see cref="CaptureTrigger.Shared"/> (the shell registers only the selection action for a shared chord); null for
    /// actions that are not captures.
    /// </summary>
    public static CaptureTrigger? TriggerFor(string action, Susu.Domain.HotkeySettings hotkeys)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        string Chord(string a) => hotkeys.Chords.TryGetValue(a, out var chord) ? chord : "";
        return action switch
        {
            "selectionTranslate" => Chord("selectionTranslate") is { Length: > 0 } chord && chord == Chord("clipboardTranslate") ? CaptureTrigger.Shared : CaptureTrigger.Selection,
            "clipboardTranslate" => CaptureTrigger.Clipboard,
            "pronounce" => CaptureTrigger.Pronounce,
            _ => null,
        };
    }

    public bool IsCurrent(long generation) { lock (_gate) return generation == _generation; }

    /// <summary>
    /// Foreground-change hook: warms the accessibility tree of browser/Electron windows ahead of the first hotkey
    /// (fire-and-forget, a no-op for other classes and already primed windows).
    /// </summary>
    public void OnForegroundChanged(nint window) => reader.Prime(window);

    /// <summary>
    /// Starts a capture. Call on the hotkey thread before any Su-Su window is shown: the foreground snapshot is taken
    /// synchronously, before the first await. Never throws for target, helper or clipboard failures.
    /// </summary>
    public Task<CaptureOutcome> CaptureAsync(CaptureTrigger trigger, CancellationToken cancellationToken = default)
    {
        ForegroundSnapshot? snapshot = trigger == CaptureTrigger.Clipboard ? null : reader.Snapshot();
        var mine = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long generation;
        lock (_gate)
        {
            _current?.Cancel();
            _current = mine;
            generation = ++_generation;
        }
        return RunAsync(trigger, snapshot, generation, mine);
    }

    /// <summary>Captures, then submits the text to the translation session only if this is still the current capture (J01).</summary>
    public async Task<CaptureOutcome> TranslateAsync(CaptureTrigger trigger, Func<string, Task> submit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submit);
        var outcome = await CaptureAsync(trigger, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != CaptureStatus.Text) return outcome;
        if (!IsCurrent(outcome.Generation)) return outcome with { Status = CaptureStatus.Superseded, Text = "" };
        await submit(outcome.Text).ConfigureAwait(false);
        return outcome;
    }

    private async Task<CaptureOutcome> RunAsync(CaptureTrigger trigger, ForegroundSnapshot? snapshot, long generation, CancellationTokenSource mine)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        CaptureOutcome outcome;
        try { outcome = await CaptureCoreAsync(trigger, snapshot, generation, mine.Token, timer).ConfigureAwait(false); }
        catch (OperationCanceledException) when (mine.IsCancellationRequested) { outcome = Outcome(generation, trigger, CaptureStatus.Superseded, timer, failureKey: "superseded"); }
        finally
        {
            lock (_gate) if (ReferenceEquals(_current, mine)) _current = null;
            mine.Dispose();
        }
        if (outcome.Status != CaptureStatus.Superseded && !IsCurrent(generation))
            outcome = outcome with { Status = CaptureStatus.Superseded, Text = "", Message = null, FailureKey = "superseded" };
        Completed?.Invoke(trigger, outcome.Status, outcome.Source, outcome.FailureKey, outcome.ElapsedMs);
        return outcome;
    }

    private async Task<CaptureOutcome> CaptureCoreAsync(CaptureTrigger trigger, ForegroundSnapshot? snapshot, long generation, CancellationToken cancel, System.Diagnostics.Stopwatch timer)
    {
        if (trigger == CaptureTrigger.Clipboard) return await FromClipboardAsync(trigger, generation, null, cancel, timer).ConfigureAwait(false);

        bool borrow = borrowEnabled();
        var selection = await reader.ReadAsync(snapshot!, cancel).ConfigureAwait(false);
        // A cold browser/Electron tree was only just built by this read: one lossless retry beats borrowing (F00 SEL01).
        if (selection.Status == SelectionStatus.NotReady && !cancel.IsCancellationRequested)
            selection = await reader.ReadAsync(snapshot!, cancel).ConfigureAwait(false);
        if (selection.Status == SelectionStatus.Cancelled || cancel.IsCancellationRequested)
            return Outcome(generation, trigger, CaptureStatus.Superseded, timer, failureKey: "superseded");
        if (selection.Succeeded)
            return Outcome(generation, trigger, CaptureStatus.Text, timer, selection.Text, selection.Source, rect: selection.Rect, dpi: selection.Dpi);

        string failureKey = Kebab(selection.Status.ToString());
        string? notice = null;
        if (borrow && Borrowable(selection.Status))
        {
            var borrowed = await borrower.BorrowAsync(snapshot!, cancel).ConfigureAwait(false);
            notice = borrowed.Notice;
            if (borrowed.Status == BorrowStatus.Borrowed)
                return Outcome(generation, trigger, CaptureStatus.Text, timer, borrowed.Text, "borrow", notice: notice, dpi: selection.Dpi);
            if (borrowed.Reason == BorrowReason.Superseded || cancel.IsCancellationRequested)
                return Outcome(generation, trigger, CaptureStatus.Superseded, timer, failureKey: "superseded", notice: notice);
            failureKey = "borrow-" + Kebab(borrowed.Reason.ToString());
        }

        if (trigger == CaptureTrigger.Shared) return await FromClipboardAsync(trigger, generation, notice, cancel, timer).ConfigureAwait(false);
        if (selection.Status == SelectionStatus.FocusChanged || selection.Status == SelectionStatus.NoTarget)
            return Outcome(generation, trigger, CaptureStatus.Failed, timer, failureKey: failureKey, notice: notice); // the user moved on: no bar text
        return Outcome(generation, trigger, CaptureStatus.Failed, timer, failureKey: failureKey,
            message: borrow ? CaptureMessages.NotSupported : CaptureMessages.NotSupportedBorrowOff, notice: notice, dpi: selection.Dpi);
    }

    private async Task<CaptureOutcome> FromClipboardAsync(CaptureTrigger trigger, long generation, string? notice, CancellationToken cancel, System.Diagnostics.Stopwatch timer)
    {
        var text = await borrower.ReadTextAsync(cancel).ConfigureAwait(false);
        if (text.Status == ClipboardTextStatus.Ok && text.Text.Length > 0 && !string.IsNullOrWhiteSpace(text.Text))
            return Outcome(generation, trigger, CaptureStatus.Text, timer, text.Text, "clipboard", notice: notice);
        return Outcome(generation, trigger, CaptureStatus.Empty, timer, failureKey: "clipboard-" + Kebab(text.Status.ToString()), notice: notice);
    }

    /// <summary>
    /// Level 3 is tried only when levels 1–2 could not read the control. Not for a password field, an explicitly empty
    /// selection (Ctrl+C could copy a whole line), an elevated window (UIPI blocks input too), or a focus change.
    /// </summary>
    private static bool Borrowable(SelectionStatus status) => status is SelectionStatus.Unsupported or SelectionStatus.NotReady
        or SelectionStatus.Timeout or SelectionStatus.HelperFailed or SelectionStatus.HelperUnavailable;

    private static CaptureOutcome Outcome(long generation, CaptureTrigger trigger, CaptureStatus status, System.Diagnostics.Stopwatch timer, string text = "", string source = "none",
        string? failureKey = null, string? message = null, string? notice = null, ScreenRect? rect = null, int dpi = 0)
        => new(generation, trigger, status, text, source, failureKey, message, notice, rect, dpi, Math.Round(timer.Elapsed.TotalMilliseconds, 1));

    private static string Kebab(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) builder.Append('-');
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.ToString();
    }
}
