using Susu.Contracts;

namespace Susu.Domain;

/// <summary>Physical-pixel rectangle in virtual-screen coordinates.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>A display: its work area (taskbar excluded) in physical pixels and its effective DPI.</summary>
public sealed record MonitorInfo(string Hint, PixelRect WorkArea, int Dpi);

/// <summary>Per-window-kind rules (PLAN 1.4 / 1.4.1, DESIGN 9, ARCHITECTURE 9). Sizes are 96-DPI DIPs.</summary>
public sealed record WindowSpec(WindowKind Kind, int WidthDip, int HeightDip, bool RemembersPosition, string PlacementKey, bool AutoHeight, bool InTaskbar, bool Activates)
{
    public static WindowSpec For(WindowKind kind) => kind switch
    {
        WindowKind.Main => new(kind, 520, 700, true, "main", false, true, true),
        WindowKind.Selection or WindowKind.Clipboard => new(kind, 380, 420, true, "float", true, false, false),
        WindowKind.Voice => new(kind, 380, 420, true, "voice", true, false, true),
        WindowKind.Ocr => new(kind, 420, 620, true, "ocr", false, true, true),
        WindowKind.Transcribe => new(kind, 760, 580, true, "transcribe", false, true, true),
        WindowKind.Settings => new(kind, 900, 700, false, "settings", false, true, true),
        WindowKind.Tray => new(kind, 236, 360, false, "tray", true, false, true),
        _ => new(kind, 460, 34, false, "error", true, false, false),
    };

    /// <summary>Floating windows may grow with content up to the work area height minus 32 DIP.</summary>
    public const int FloatMarginDip = 32;
    /// <summary>Title bar height used to decide whether a remembered position is still reachable.</summary>
    public const int TitleBarDip = 40;
}

public static class Dip
{
    public static int ToPixels(int dip, int dpi) => (int)Math.Round(dip * dpi / 96.0, MidpointRounding.AwayFromZero);
    public static int ToDip(int pixels, int dpi) => (int)Math.Round(pixels * 96.0 / dpi, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Window placement (PLAN 1.4.1, ARCHITECTURE 9): Settings always centers on the cursor's monitor. Other
/// windows reopen at their remembered position only if the title bar midpoint still lies in some work area;
/// otherwise they center. Size always comes from the design (never remembered), converted with the target
/// monitor's DPI and shrunk to fit its work area; the whole window is then clamped inside that work area.
/// </summary>
public static class PlacementPolicy
{
    public static PixelRect Compute(WindowSpec spec, IReadOnlyList<MonitorInfo> monitors, MonitorInfo cursorMonitor, (int X, int Y)? remembered, int? contentHeightDip = null)
    {
        if (monitors.Count == 0) throw new ArgumentException("at least one monitor is required", nameof(monitors));
        MonitorInfo target = cursorMonitor;
        (int X, int Y)? origin = null;
        if (spec.RemembersPosition && remembered is { } r)
        {
            foreach (var monitor in monitors)
            {
                int titleMidX = r.X + Dip.ToPixels(spec.WidthDip, monitor.Dpi) / 2;
                int titleMidY = r.Y + Dip.ToPixels(WindowSpec.TitleBarDip, monitor.Dpi) / 2;
                if (monitor.WorkArea.Contains(titleMidX, titleMidY)) { target = monitor; origin = r; break; }
            }
        }
        var work = target.WorkArea;
        int workHeightDip = Dip.ToDip(work.Height, target.Dpi);
        int heightDip = spec.AutoHeight
            ? Math.Min(contentHeightDip ?? spec.HeightDip, Math.Max(1, workHeightDip - WindowSpec.FloatMarginDip))
            : spec.HeightDip;
        int width = Math.Min(Dip.ToPixels(spec.WidthDip, target.Dpi), work.Width);
        int height = Math.Min(Dip.ToPixels(heightDip, target.Dpi), work.Height);
        int x, y;
        if (origin is { } o) { x = o.X; y = o.Y; }
        else { x = work.X + (work.Width - width) / 2; y = work.Y + (work.Height - height) / 2; }
        x = Math.Clamp(x, work.X, work.Right - width);
        y = Math.Clamp(y, work.Y, work.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>Gap between the pointer and the failure bar, in DIPs (clear of the cursor image).</summary>
    public const int PointerGapDip = 16;

    /// <summary>
    /// The failure bar (DESIGN 9 "悬浮条", the one surface that points at its trigger): just below-right of the pointer,
    /// sized with the DPI of the monitor whose work area holds the pointer, flipped above the pointer when it would run
    /// off the bottom, then clamped inside that work area. A pointer outside every work area (an invalid position, such
    /// as a monitor that was just unplugged) centers the bar on <paramref name="fallback"/>.
    /// </summary>
    public static PixelRect NearPointer(WindowSpec spec, IReadOnlyList<MonitorInfo> monitors, MonitorInfo fallback, (int X, int Y) pointer, int? heightDip = null)
    {
        MonitorInfo? target = null;
        foreach (var monitor in monitors)
            if (monitor.WorkArea.Contains(pointer.X, pointer.Y)) { target = monitor; break; }
        var screen = target ?? fallback;
        var work = screen.WorkArea;
        int width = Math.Min(Dip.ToPixels(spec.WidthDip, screen.Dpi), work.Width);
        int height = Math.Min(Dip.ToPixels(heightDip ?? spec.HeightDip, screen.Dpi), work.Height);
        if (target is null) return new PixelRect(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height);
        int gap = Dip.ToPixels(PointerGapDip, screen.Dpi);
        int x = pointer.X + gap / 2, y = pointer.Y + gap;
        if (y + height > work.Bottom) y = pointer.Y - gap / 2 - height;
        x = Math.Clamp(x, work.X, work.Right - width);
        y = Math.Clamp(y, work.Y, work.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    /// An auto-height window reported its content height (UI01): it keeps its left/top, grows or shrinks up to the work
    /// area height minus <see cref="WindowSpec.FloatMarginDip"/> (taller content scrolls inside the page) and is clamped
    /// back inside the work area of its monitor. Width stays the design width at that monitor's DPI.
    /// </summary>
    public static PixelRect FitHeight(WindowSpec spec, PixelRect current, MonitorInfo monitor, int contentHeightDip)
    {
        var work = monitor.WorkArea;
        int maxDip = Math.Max(1, Dip.ToDip(work.Height, monitor.Dpi) - (spec.Kind == WindowKind.Error ? 0 : WindowSpec.FloatMarginDip));
        int height = Math.Min(Dip.ToPixels(Math.Clamp(contentHeightDip, 1, maxDip), monitor.Dpi), work.Height);
        int width = Math.Min(Dip.ToPixels(spec.WidthDip, monitor.Dpi), work.Width);
        int x = Math.Clamp(current.X, work.X, work.Right - width);
        int y = Math.Clamp(current.Y, work.Y, work.Bottom - height);
        return new PixelRect(x, y, width, height);
    }
}

public enum WebViewAction { None, StartReleaseTimer, CancelReleaseTimer, Release }

/// <summary>
/// Keep-warm and release (ARCHITECTURE 9): hidden windows stay warm (suspended); 10 minutes after the last
/// visible window hides, all controllers and the environment are released, and the host then waits for
/// BrowserProcessExited. Showing any window cancels a pending release. Pure state; the host owns timers.
/// </summary>
public sealed class WebViewLifecycle
{
    public static readonly TimeSpan ReleaseAfter = TimeSpan.FromMinutes(10);
    private readonly HashSet<WindowKind> visible = [];
    private long timerGeneration;

    public bool TimerPending { get; private set; }
    public long TimerGeneration => timerGeneration;
    public IReadOnlyCollection<WindowKind> Visible => visible;

    public WebViewAction Shown(WindowKind kind)
    {
        visible.Add(kind);
        if (!TimerPending) return WebViewAction.None;
        TimerPending = false;
        timerGeneration++;
        return WebViewAction.CancelReleaseTimer;
    }

    public WebViewAction Hidden(WindowKind kind)
    {
        if (!visible.Remove(kind) || visible.Count > 0) return WebViewAction.None;
        TimerPending = true;
        timerGeneration++;
        return WebViewAction.StartReleaseTimer;
    }

    /// <summary>Called when a release timer fires; stale timers (a window was shown meanwhile) do nothing.</summary>
    public WebViewAction TimerFired(long generation)
    {
        if (!TimerPending || generation != timerGeneration || visible.Count > 0) return WebViewAction.None;
        TimerPending = false;
        return WebViewAction.Release;
    }
}

/// <summary>
/// Close vs minimize (PLAN 1.4, ARCHITECTURE 5.2): minimize hides to the tray and keeps the task running;
/// close cancels the window's task and hides; with "close exits" the whole app quits.
/// </summary>
public enum WindowRequest { Minimize, Close, Escape }
public enum WindowOutcome { HideKeepTask, HideCancelTask, ExitApp }

public static class WindowSemantics
{
    public static WindowOutcome Decide(WindowKind kind, WindowRequest request, CloseAction closeAction) => request switch
    {
        WindowRequest.Minimize => WindowOutcome.HideKeepTask,
        WindowRequest.Close when closeAction == CloseAction.Exit && kind is WindowKind.Main or WindowKind.Settings => WindowOutcome.ExitApp,
        _ => WindowOutcome.HideCancelTask,
    };
}

/// <summary>Hotkey chord parsing (PLAN 1.2): modifiers plus one key, e.g. <c>Ctrl+Alt+T</c>, <c>Alt+F2</c>.</summary>
public static class Chords
{
    [Flags] public enum Modifiers : uint { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

    public static bool TryParse(string chord, out Modifiers modifiers, out uint virtualKey)
    {
        modifiers = Modifiers.None;
        virtualKey = 0;
        var parts = chord.Split('+');
        if (parts.Length < 2) return false;
        foreach (var part in parts[..^1])
        {
            var m = part switch { "Ctrl" => Modifiers.Ctrl, "Alt" => Modifiers.Alt, "Shift" => Modifiers.Shift, "Win" => Modifiers.Win, _ => Modifiers.None };
            if (m == Modifiers.None || modifiers.HasFlag(m)) return false;
            modifiers |= m;
        }
        string key = parts[^1];
        virtualKey = key switch
        {
            { Length: 1 } when key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9' => key[0],
            ['F', .. var n] when int.TryParse(n, out int f) && f is >= 1 and <= 24 => (uint)(0x70 + f - 1),
            "Space" => 0x20, "PageUp" => 0x21, "PageDown" => 0x22, "End" => 0x23, "Home" => 0x24,
            "Left" => 0x25, "Up" => 0x26, "Right" => 0x27, "Down" => 0x28, "Insert" => 0x2D, "Delete" => 0x2E,
            "OemPlus" => 0xBB, "OemComma" => 0xBC, "OemMinus" => 0xBD, "OemPeriod" => 0xBE,
            ['O', 'e', 'm', var d] when d is >= '1' and <= '8' => d switch { '1' => 0xBA, '2' => 0xBF, '3' => 0xC0, '4' => 0xDB, '5' => 0xDC, '6' => 0xDD, '7' => 0xDE, _ => 0xDF },
            _ => 0,
        };
        return virtualKey != 0;
    }
}
