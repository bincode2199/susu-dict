using Susu.Contracts;
using Susu.Domain;

namespace Susu.Abstractions;

/// <summary>
/// Saves settings and secret changes together through the journaled two-file transaction (ARCHITECTURE 8.1),
/// so an account binding and its key are never half-written.
/// </summary>
public interface IConfigService : ISettingsStore
{
    ISecretStore Secrets { get; }
    SaveResult SaveWithSecrets(AppSettings proposed, long expectedRevision, string expectedFileHash, IReadOnlyList<(string Account, string Name, string? Value)> secretChanges);
}

/// <summary>Win32/WebView side of the window coordinator. Implemented by Susu.Windows; faked in tests.</summary>
public interface IWindowPlatform
{
    /// <summary>Creates the window/WebView if needed and shows it. Returns the page session id used by that WebView.</summary>
    string Show(WindowKind kind, bool activate);
    void Hide(WindowKind kind);
    void ToggleMaximize(WindowKind kind);
    /// <summary>Auto-height windows (floats, failure bar): resize to the page's content height (PlacementPolicy.FitHeight).</summary>
    void FitHeight(WindowKind kind, int contentHeightDip);
    void SetPinned(WindowKind kind, bool pinned);
    /// <summary>
    /// The pronunciation bar's anchor (PLAN 6.5): the selection's bounds in physical pixels (null: none, use the pointer)
    /// and the bar width in DIPs. Used when the window is next placed; a visible window moves at once.
    /// </summary>
    void Anchor(WindowKind kind, PixelRect? selection, int widthDip);
    /// <summary>Posts one UI envelope (JSON) to the page of a window.</summary>
    void Post(WindowKind kind, string json);
    /// <summary>Hidden WebViews: suspend script and release memory while warm.</summary>
    void Suspend(WindowKind kind);
    /// <summary>Closes every controller and releases the environment; the platform reports browser exit later.</summary>
    void ReleaseAll();
    /// <summary>Registers global hotkeys (RegisterHotKey); returns, per action, whether registration succeeded.</summary>
    IReadOnlyDictionary<string, bool> RegisterHotkeys(IReadOnlyDictionary<string, string> chordsByAction);
    void SetClipboardText(string text);
    /// <summary>Runs <paramref name="elapsed"/> on the UI thread after the delay.</summary>
    void StartTimer(TimeSpan delay, Action elapsed);
    void Exit();
}

