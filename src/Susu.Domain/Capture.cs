namespace Susu.Domain;

/// <summary>A display as the screen capture sees it: full bounds (not the work area) in physical virtual-screen pixels, and its effective DPI.</summary>
public sealed record CaptureMonitor(string Hint, PixelRect Bounds, int Dpi, bool Primary);

/// <summary>
/// Screenshot coordinate math (F11.1; ARCHITECTURE 9 "截图坐标始终以物理像素计，再归一化给 OCR", TEST-PLAN OCR01/UI01). Every
/// rectangle is in physical pixels of the virtual screen, so a secondary monitor left of or above the primary has negative
/// coordinates and mixed DPI needs no scaling: a Per-Monitor-V2 process sees physical pixels on every monitor.
/// </summary>
public static class CaptureGeometry
{
    /// <summary>PLAN 6.2 / DESIGN: a selection smaller than 8×8 px (or a click without a drag) counts as cancel.</summary>
    public const int MinimumSide = 8;

    /// <summary>The bounding rectangle of all monitors (the frozen frame covers exactly this).</summary>
    public static PixelRect VirtualBounds(IReadOnlyList<CaptureMonitor> monitors)
    {
        if (monitors.Count == 0) throw new ArgumentException("no monitors", nameof(monitors));
        int left = monitors.Min(m => m.Bounds.X), top = monitors.Min(m => m.Bounds.Y);
        int right = monitors.Max(m => m.Bounds.Right), bottom = monitors.Max(m => m.Bounds.Bottom);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// The drag rectangle between the press point and the current point (both virtual-screen pixels), clamped to
    /// <paramref name="bounds"/>. The width is |dx|, so a press at (0,0) released at (8,8) selects 8×8 pixels starting at (0,0).
    /// </summary>
    public static PixelRect FromDrag(int anchorX, int anchorY, int currentX, int currentY, PixelRect bounds)
    {
        int ax = Math.Clamp(anchorX, bounds.X, bounds.Right), ay = Math.Clamp(anchorY, bounds.Y, bounds.Bottom);
        int cx = Math.Clamp(currentX, bounds.X, bounds.Right), cy = Math.Clamp(currentY, bounds.Y, bounds.Bottom);
        return new PixelRect(Math.Min(ax, cx), Math.Min(ay, cy), Math.Abs(cx - ax), Math.Abs(cy - ay));
    }

    /// <summary>True when the selection is at least 8×8 px (PLAN 6.2); otherwise releasing cancels.</summary>
    public static bool IsSelection(PixelRect rect) => rect.Width >= MinimumSide && rect.Height >= MinimumSide;

    public static PixelRect Intersect(PixelRect a, PixelRect b)
    {
        int left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y), right = Math.Min(a.Right, b.Right), bottom = Math.Min(a.Bottom, b.Bottom);
        return right <= left || bottom <= top ? default : new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>A virtual-screen rectangle as an offset into the frame whose top-left pixel is <paramref name="frame"/>.X/Y.</summary>
    public static PixelRect ToFrame(PixelRect rect, PixelRect frame) => new(rect.X - frame.X, rect.Y - frame.Y, rect.Width, rect.Height);

    /// <summary>A point in the overlay window's client area (the overlay covers the virtual bounds) to virtual-screen pixels.</summary>
    public static (int X, int Y) ClientToVirtual(int clientX, int clientY, PixelRect virtualBounds) => (clientX + virtualBounds.X, clientY + virtualBounds.Y);

    /// <summary>Monitors the rectangle touches, in the given order.</summary>
    public static IReadOnlyList<CaptureMonitor> MonitorsUnder(PixelRect rect, IReadOnlyList<CaptureMonitor> monitors)
        => [.. monitors.Where(m => Intersect(rect, m.Bounds).Width > 0)];

    /// <summary>
    /// The monitor holding most of the selection (ties: first listed); its DPI is the scale OCR results are normalized
    /// with. Null when the rectangle lies entirely in a gap between monitors.
    /// </summary>
    public static CaptureMonitor? DominantMonitor(PixelRect rect, IReadOnlyList<CaptureMonitor> monitors)
    {
        CaptureMonitor? best = null;
        long bestArea = 0;
        foreach (var m in monitors)
        {
            var i = Intersect(rect, m.Bounds);
            long area = (long)i.Width * i.Height;
            if (area > bestArea) { best = m; bestArea = area; }
        }
        return best;
    }

    /// <summary>The monitor containing the point, else the primary, else the first (where the overlay's hint line goes).</summary>
    public static CaptureMonitor MonitorAt(int x, int y, IReadOnlyList<CaptureMonitor> monitors)
        => monitors.FirstOrDefault(m => m.Bounds.Contains(x, y)) ?? monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];

    /// <summary>The size label at the selection's top-right corner (DESIGN "选区右上角尺寸标注"), in physical pixels.</summary>
    public static string SizeLabel(PixelRect rect) => $"{rect.Width} × {rect.Height}";
}
