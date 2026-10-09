using Assistant.Windows.Placement;
using Xunit;

namespace Assistant.Windows.Tests;

public sealed class OverlayPlacementCalculatorTests
{
    // The Search or Ask bar: a 520 x 91 pill inside a 45,28,45,64 shadow gutter, its top 22 % down the work area.
    private static readonly OverlayLayout Bar = new(610, 183, 45, 28, 520, 91, 0.22);

    [Theory]
    // Where it was dropped, on a 100 % monitor at negative coordinates.
    [InlineData(-1080, -491, 0, 1381, 96, -540, 100, -845, 72, -235, 255)]
    // Dropped past the bottom right: the pill comes back inside, its shadow may not.
    [InlineData(-1080, -491, 0, 1381, 96, -10, 1370, -565, 1262, 45, 1445)]
    // Dropped past the top left of a 125 % monitor.
    [InlineData(0, 0, 2560, 1380, 120, 100, -50, -56, -35, 707, 194)]
    public void PlacesPillWhereItWasDroppedButKeepsItInsideTheWorkArea(
        int workLeft, int workTop, int workRight, int workBottom, int dpi, int x, int y,
        int left, int top, int right, int bottom)
    {
        var work = new ScreenRect(workLeft, workTop, workRight, workBottom);
        var monitor = new DisplayMonitor(work, work, dpi);

        var bounds = OverlayPlacementCalculator.CalculateAt(monitor, Bar, new ScreenPoint(x, y));

        Assert.Equal(new ScreenRect(left, top, right, bottom), bounds);
        var scale = dpi / 96.0;
        Assert.InRange(bounds.Left + (45 * scale), work.Left, work.Right - (520 * scale));
        Assert.InRange(bounds.Top + (28 * scale), work.Top, work.Bottom - (91 * scale));
    }

    [Fact]
    public void ReadsBackThePointThePillWasPlacedAt()
    {
        var monitor = new DisplayMonitor(new ScreenRect(0, 0, 2560, 1440), new ScreenRect(0, 0, 2560, 1380), 120);
        var bounds = OverlayPlacementCalculator.CalculateAt(monitor, Bar, new ScreenPoint(1300, 400));
        Assert.Equal(new ScreenPoint(1300, 400),
            OverlayPlacementCalculator.AnchorTop(monitor, Bar, new ScreenPoint(bounds.Left, bounds.Top)));
    }

    [Theory]
    // 100 %, taskbar at the bottom.
    [InlineData(0, 0, 1920, 1080, 0, 0, 1920, 1032, 96, 655, 199, 1265, 382)]
    // 100 %, taskbar at the top.
    [InlineData(0, 0, 1920, 1080, 0, 48, 1920, 1080, 96, 655, 247, 1265, 430)]
    // 125 % primary monitor.
    [InlineData(0, 0, 2560, 1440, 0, 0, 2560, 1380, 120, 899, 269, 1662, 498)]
    // 100 % portrait monitor left of and above the primary, at negative coordinates.
    [InlineData(-1080, -491, 0, 1429, -1080, -491, 0, 1381, 96, -845, -107, -235, 76)]
    // 125 % monitor above the primary, entirely at negative y.
    [InlineData(2807, -1200, 4727, 0, 2807, -1200, 4727, -60, 120, 3386, -984, 4149, -755)]
    // 150 %, taskbar docked on the left.
    [InlineData(0, 0, 1920, 1080, 72, 0, 1920, 1080, 144, 539, 196, 1454, 471)]
    // 300 %.
    [InlineData(0, 0, 3840, 2160, 0, 0, 3840, 2088, 288, 1005, 375, 2835, 924)]
    public void CentersPillNearTopOfWorkAreaAtMonitorDpi(
        int monitorLeft, int monitorTop, int monitorRight, int monitorBottom,
        int workLeft, int workTop, int workRight, int workBottom, int dpi,
        int left, int top, int right, int bottom)
    {
        var monitor = new DisplayMonitor(
            new ScreenRect(monitorLeft, monitorTop, monitorRight, monitorBottom),
            new ScreenRect(workLeft, workTop, workRight, workBottom), dpi);

        var bounds = OverlayPlacementCalculator.Calculate(monitor, Bar);

        Assert.Equal(new ScreenRect(left, top, right, bottom), bounds);
        var scale = dpi / 96.0;
        var pillLeft = bounds.Left + (45 * scale);
        var pillCenter = pillLeft + (520 * scale / 2);
        Assert.InRange(pillCenter - ((workLeft + workRight) / 2.0), -0.5, 0.5);
        Assert.InRange(bounds.Top + (28 * scale) - (workTop + ((workBottom - workTop) * 0.22)), -0.5, 0.5);
        AssertInside(monitor.WorkArea, bounds);
    }

    [Fact]
    public void KeepsWholeWindowOffTheTaskbarWhenPositionWouldOverlapIt()
    {
        var monitor = OnMonitor(new ScreenRect(0, 0, 1920, 1032), 96);

        var bounds = OverlayPlacementCalculator.Calculate(monitor, Bar with { VerticalPosition = 1 });

        Assert.Equal(new ScreenRect(655, 849, 1265, 1032), bounds);
    }

    [Fact]
    public void KeepsPillInsideWorkAreaTooSmallForTheShadow()
    {
        var monitor = OnMonitor(new ScreenRect(0, 0, 560, 150), 96);

        var bounds = OverlayPlacementCalculator.Calculate(monitor, Bar);

        // The window is wider and taller than the work area, so only the pill fits: it stays centered, and its top
        // stays at 22 % of the work area.
        Assert.Equal(new ScreenRect(-25, 5, 585, 188), bounds);
        AssertInside(monitor.WorkArea, new ScreenRect(bounds.Left + 45, bounds.Top + 28, bounds.Left + 565, bounds.Top + 119));
    }

    [Fact]
    public void PinsPillTopLeftToWorkAreaWhenEvenThePillDoesNotFit()
    {
        var monitor = OnMonitor(new ScreenRect(100, 100, 400, 150), 96);

        var bounds = OverlayPlacementCalculator.Calculate(monitor, Bar);

        Assert.Equal(new ScreenRect(55, 72, 665, 255), bounds);
    }

    [Fact]
    public void RoundsFractionalPillLimitsInward()
    {
        // At 150 % the pill is 67.5 px from the window's left edge and 136.5 px tall, so the limits that keep it in
        // the work area fall on half pixels. Rounding them outward would push the pill half a pixel out.
        var monitor = OnMonitor(new ScreenRect(0, 0, 700, 200), 144);

        var bounds = OverlayPlacementCalculator.Calculate(monitor, Bar with { VerticalPosition = 1 });

        Assert.Equal(new ScreenRect(-67, 21, 848, 296), bounds);
        Assert.Equal(0.5, bounds.Left + 67.5);
        Assert.Equal(199.5, bounds.Top + 42 + 136.5);
    }

    [Theory]
    [InlineData(-1, 183, 0.22)]
    [InlineData(double.NaN, 183, 0.22)]
    [InlineData(610, double.PositiveInfinity, 0.22)]
    [InlineData(610, 183, 1.5)]
    [InlineData(610, 183, -0.1)]
    [InlineData(610, 183, double.NaN)]
    public void RejectsInvalidLayouts(double width, double height, double verticalPosition)
    {
        var layout = Bar with { Width = width, Height = height, VerticalPosition = verticalPosition };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => OverlayPlacementCalculator.Calculate(OnMonitor(new ScreenRect(0, 0, 1920, 1032), 96), layout));
    }

    private static DisplayMonitor OnMonitor(ScreenRect workArea, int dpi) => new(workArea, workArea, dpi);

    private static void AssertInside(ScreenRect area, ScreenRect bounds)
    {
        Assert.InRange(bounds.Left, area.Left, area.Right);
        Assert.InRange(bounds.Right, area.Left, area.Right);
        Assert.InRange(bounds.Top, area.Top, area.Bottom);
        Assert.InRange(bounds.Bottom, area.Top, area.Bottom);
    }
}
