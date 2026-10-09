using Assistant.Core.Displays;
using Assistant.Windows.Clock;
using Assistant.Windows.Displays;
using Assistant.Windows.Interop;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// Where the Clock app's window goes when the user chose a display for it: at the same place on that display as it was on its own, and always inside
/// it. And the displays of this PC, as they are read from Windows.
/// </summary>
public sealed class ClockPlaceTests
{
    private static User32.Rect Area(int left, int top, int width, int height) => new() { Left = left, Top = top, Right = left + width, Bottom = top + height };

    [Fact]
    public void AWindowKeepsItsCornerOnTheDisplayItIsMovedTo()
    {
        var main = Area(0, 0, 1920, 1040);
        var left = Area(-2560, 0, 2560, 1400);

        // The small timer on top, in the top right corner of the main display, is in the top right corner of the left one.
        var timer = Area(1920 - 360, 0, 360, 240);
        Assert.Equal((-360, 0), WindowsClockApp.PlaceOn(timer, main, left));

        // A window in the middle stays in the middle.
        var middle = Area(460, 220, 1000, 600);
        var moved = WindowsClockApp.PlaceOn(middle, main, left);
        Assert.Equal(-2560 + ((2560 - 1000) / 2), moved.X);
        Assert.Equal((1400 - 600) / 2, moved.Y);
    }

    [Fact]
    public void AWindowLargerThanTheDisplayItIsMovedToStartsAtThatDisplaysCorner()
    {
        var big = Area(100, 50, 2400, 1300);
        var from = Area(0, 0, 2560, 1400);
        var small = Area(2560, 0, 1280, 720);

        Assert.Equal((2560, 0), WindowsClockApp.PlaceOn(big, from, small));
    }

    [Fact]
    public void AWindowThatFilledItsDisplayIsCentredOnTheOther()
    {
        var from = Area(0, 0, 1000, 800);
        var window = Area(0, 0, 1000, 800);
        var to = Area(1000, 0, 2000, 1200);

        Assert.Equal((1500, 200), WindowsClockApp.PlaceOn(window, from, to));
    }

    [Fact]
    public void TheDisplaysOfThisPcAreReadWithTheirNamesAndOneOfThemIsTheMainOne()
    {
        var displays = new WindowsDisplays().List();

        // Nothing is changed by reading them. A session with no display at all (a service) has none; any other has a main one.
        if (displays.Count == 0)
        {
            return;
        }

        Assert.Single(displays, display => display.IsMain);
        Assert.All(displays, display =>
        {
            Assert.StartsWith(@"\\.\DISPLAY", display.Id, StringComparison.OrdinalIgnoreCase);
            Assert.True(display.Number > 0 && display.Width > 0 && display.Height > 0);
        });
        Assert.Equal(displays.Count, displays.Select(display => display.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(displays.Count, DisplayChoice.InOrder(displays).Count);
    }
}
