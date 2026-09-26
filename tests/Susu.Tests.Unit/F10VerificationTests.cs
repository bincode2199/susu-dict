using Susu.Contracts;
using Susu.Domain;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F10 independent verification, SEL03 bar rectangle/DPI edge cases (PLAN 6.5, DESIGN 9): monitors at negative
/// coordinates on both axes, mixed DPI with a monitor directly below, a selection straddling two monitors, a selection
/// partly outside the work area, a selection as tall as the work area, and the pointer fallback on a scaled monitor.
/// </summary>
public class F10BarPlacementVerificationTests
{
    private static readonly MonitorInfo Primary = new("m1", new PixelRect(0, 0, 1920, 1040), 96);
    private static readonly MonitorInfo Left150 = new("m2", new PixelRect(-2880, 0, 2880, 1560), 144);
    private static readonly MonitorInfo AboveLeft125 = new("m3", new PixelRect(-1920, -1080, 1920, 1040), 120);
    private static readonly MonitorInfo Below125 = new("m4", new PixelRect(0, 1080, 2560, 1400), 120);
    private static readonly WindowSpec Bar = WindowSpec.For(WindowKind.Speech);

    private static void AssertInside(PixelRect rect, MonitorInfo monitor)
    {
        var w = monitor.WorkArea;
        Assert.True(rect.X >= w.X && rect.Right <= w.Right && rect.Y >= w.Y && rect.Bottom <= w.Bottom, $"{rect} is not inside {w}");
    }

    [Fact] // negative X and Y, 125 %: at that monitor's bottom edge the bar flips above, sized 71x34 DIP at 120 DPI with an 8 px gap
    public void Negative_coordinates_bottom_edge_flips_above_with_that_monitors_dpi()
    {
        var rect = PlacementPolicy.NearSelection(Bar, [Primary, AboveLeft125], Primary, new PixelRect(-1900, -100, 200, 40), (5, 5), 71);
        Assert.Equal(new PixelRect(-1900, -100 - 8 - 43, 89, 43), rect);
        AssertInside(rect, AboveLeft125);
    }

    [Fact] // mixed DPI: a selection at the bottom of the 96 DPI primary never spills onto the 120 DPI monitor below; it flips above at 96 DPI
    public void Bottom_edge_with_a_monitor_below_flips_above_on_the_selection_monitor()
    {
        var rect = PlacementPolicy.NearSelection(Bar, [Below125, Primary], Primary, new PixelRect(100, 1010, 200, 25), (5, 5), 71);
        Assert.Equal(new PixelRect(100, 1010 - 6 - 34, 71, 34), rect);
        AssertInside(rect, Primary);
    }

    [Fact] // a selection straddling the 150 % left monitor and the primary: its bottom-left corner picks the monitor; the bar stays whole on it
    public void Straddling_selection_uses_the_bottom_left_monitor_and_is_clamped_to_it()
    {
        var rect = PlacementPolicy.NearSelection(Bar, [Primary, Left150], Primary, new PixelRect(-100, 300, 400, 30), (5, 5), 71);
        Assert.Equal(new PixelRect(-107, 339, 107, 51), rect);
        AssertInside(rect, Left150);
    }

    [Fact] // a selection starting left of every work area: its center picks the monitor and the bar is clamped to the left edge
    public void Selection_partly_outside_the_work_area_is_clamped_to_its_edge()
    {
        var rect = PlacementPolicy.NearSelection(Bar, [Primary, Left150], Primary, new PixelRect(-2900, 500, 300, 30), (5, 5), 71);
        Assert.Equal(new PixelRect(-2880, 539, 107, 51), rect);
    }

    [Fact] // a selection as tall as the work area: neither below nor above fits, the bar is still fully visible inside the work area
    public void Work_area_tall_selection_keeps_the_bar_visible()
    {
        var rect = PlacementPolicy.NearSelection(Bar, [Primary], Primary, new PixelRect(100, 10, 500, 1020), (5, 5), 71);
        Assert.Equal(new PixelRect(100, 0, 71, 34), rect);
        AssertInside(rect, Primary);
    }

    [Fact] // no rectangle: the bar goes to the pointer, sized with the DPI of the pointer's (scaled, negative-X) monitor
    public void No_rect_goes_to_the_pointer_with_the_pointer_monitor_dpi()
    {
        var rect = PlacementPolicy.NearSelection(Bar, [Primary, Left150], Primary, null, (-1000, 200), 71);
        Assert.Equal(PlacementPolicy.NearPointer(Bar with { WidthDip = 71 }, [Primary, Left150], Primary, (-1000, 200)), rect);
        Assert.Equal((107, 51), (rect.Width, rect.Height));
        Assert.True(rect.X >= -1000 && rect.Y > 200, $"{rect} should be just below-right of the pointer");
        AssertInside(rect, Left150);
    }
}
