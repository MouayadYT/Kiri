using Assistant.Windows.Placement;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>Where an overlay opens beside another application's window (the browser the user selected text in).</summary>
public sealed class NearWindowPlacementTests
{
    // The floating conversation: a 418 x 598 panel inside its shadow gutter.
    private static readonly OverlayLayout Panel = new(508, 690, 45, 28, 418, 598, 0.22);

    private static DisplayMonitor Monitor(int right, int bottom, int dpi, int left = 0, int top = 0) =>
        new(new ScreenRect(left, top, right, bottom), new ScreenRect(left, top, right, bottom), dpi);

    [Fact]
    public void InAMaximizedWindow_ThePanelStandsAgainstTheRightEdge_BelowTheToolbar()
    {
        var monitor = Monitor(1920, 1040, 96);

        var point = NearWindowCalculator.AnchorTop(monitor, new ScreenRect(0, 0, 1920, 1040), new ScreenPoint(300, 400), Panel);

        // 24 in from the right edge: the panel's left edge is at 1478, so its top center at 1478 + 209; 96 under the window's top.
        Assert.Equal(new ScreenPoint(1687, 96), point);
    }

    [Fact]
    public void WhenThePointerIsNearTheRightEdge_ThePanelGoesToTheLeftEdgeInstead()
    {
        var monitor = Monitor(1920, 1040, 96);
        var window = new ScreenRect(0, 0, 1920, 1040);

        // The text the user selected, or the menu entry they chose, must stay readable: left edge 24 in, so its top center at 24 + 209.
        Assert.Equal(new ScreenPoint(233, 96), NearWindowCalculator.AnchorTop(monitor, window, new ScreenPoint(1700, 300), Panel));

        // Just outside the clearance (64) of the right panel, it stays where it is.
        Assert.Equal(new ScreenPoint(1687, 96), NearWindowCalculator.AnchorTop(monitor, window, new ScreenPoint(1478 - 65, 300), Panel));
        Assert.Equal(new ScreenPoint(233, 96), NearWindowCalculator.AnchorTop(monitor, window, new ScreenPoint(1478 - 64, 300), Panel));

        // Above or below it, by more than the clearance, it is not in the way.
        Assert.Equal(new ScreenPoint(1687, 96), NearWindowCalculator.AnchorTop(monitor, window, new ScreenPoint(1700, 96 - 65), Panel));
        Assert.Equal(new ScreenPoint(1687, 96), NearWindowCalculator.AnchorTop(monitor, window, new ScreenPoint(1700, 96 + 598 + 65), Panel));
    }

    [Fact]
    public void WithNoPointer_ItIsTheRightEdge()
    {
        var monitor = Monitor(1920, 1040, 96);
        Assert.Equal(
            new ScreenPoint(1687, 96), NearWindowCalculator.AnchorTop(monitor, new ScreenRect(0, 0, 1920, 1040), null, Panel));
    }

    [Fact]
    public void OnA125PercentMonitor_EverythingScalesWithTheDpi_AndASmallerWindowIsFollowedNotTheMonitor()
    {
        var monitor = Monitor(2560, 1380, 120);

        var point = NearWindowCalculator.AnchorTop(monitor, new ScreenRect(400, 200, 1800, 1100), null, Panel);

        // Panel 522.5 x 747.5, inset 30, toolbar 120: left edge at 1800 - 30 - 522.5 = 1247.5, so its center is at 1508.75; top at 200 + 120.
        Assert.Equal(new ScreenPoint(1509, 320), point);
    }

    [Fact]
    public void AWindowTooShortForThePanel_HasItBeginAtItsTop_AndAnythingBeyondIsPlacementsToFit()
    {
        var monitor = Monitor(2560, 1380, 120);

        var point = NearWindowCalculator.AnchorTop(monitor, new ScreenRect(400, 200, 1800, 700), null, Panel);

        Assert.Equal(new ScreenPoint(1509, 200), point);
    }

    [Fact]
    public void AWindowThatIsTallEnoughButNotBelowTheToolbar_RaisesThePanelToKeepItInside()
    {
        var monitor = Monitor(1920, 1040, 96);

        // 598 + 16 under the toolbar's 96 would be 710; the window is 650 tall, so the panel rises to end 16 above its bottom.
        var point = NearWindowCalculator.AnchorTop(monitor, new ScreenRect(100, 100, 1500, 750), null, Panel);

        Assert.Equal(new ScreenPoint(1500 - 24 - 209, 750 - 16 - 598), point);
    }

    [Fact]
    public void AWindowNarrowerThanThePanelAndItsInsets_GetsThePanelCenteredOnIt()
    {
        var monitor = Monitor(1920, 1040, 96);

        var point = NearWindowCalculator.AnchorTop(monitor, new ScreenRect(500, 100, 900, 900), new ScreenPoint(700, 400), Panel);

        Assert.Equal(new ScreenPoint(700, 196), point);
    }

    [Fact]
    public void WhenThePointerIsNearBothSides_ThePanelGoesWhereThePointerIsFarther()
    {
        var monitor = Monitor(1920, 1040, 96);
        var window = new ScreenRect(0, 0, 500, 800);

        // Right edge at 58, left edge at 24: their centers are 267 and 233.
        Assert.Equal(new ScreenPoint(233, 96), NearWindowCalculator.AnchorTop(monitor, window, new ScreenPoint(270, 300), Panel));
        Assert.Equal(new ScreenPoint(267, 96), NearWindowCalculator.AnchorTop(monitor, window, new ScreenPoint(230, 300), Panel));
    }

    [Fact]
    public void OnlyThePartOfTheWindowOnTheMonitorCounts()
    {
        var monitor = Monitor(1920, 1040, 96);

        // A window that hangs over the monitor's left edge: its right edge is the one the panel stands against.
        Assert.Equal(
            new ScreenPoint(1000 - 24 - 209, 96),
            NearWindowCalculator.AnchorTop(monitor, new ScreenRect(-500, -200, 1000, 800), null, Panel));

        // One that is on another monitor entirely is not here.
        Assert.Null(NearWindowCalculator.AnchorTop(monitor, new ScreenRect(2000, 0, 2500, 500), null, Panel));
        Assert.Null(NearWindowCalculator.AnchorTop(monitor, new ScreenRect(100, 1040, 900, 1200), null, Panel));
    }

    [Fact]
    public void OnAMonitorLeftOfAndAboveThePrimary_TheWindowsOwnCoordinatesAreUsed()
    {
        var monitor = Monitor(0, 1429, 96, left: -1080, top: -491);

        var point = NearWindowCalculator.AnchorTop(monitor, new ScreenRect(-1080, -491, 0, 1429), null, Panel);

        Assert.Equal(new ScreenPoint(-24 - 209, -491 + 96), point);
    }
}
