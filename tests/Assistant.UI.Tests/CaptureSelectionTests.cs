using System.Windows;
using Assistant.UI.Capture;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// The rectangle the user draws in the Visual Intelligence overlay (PROJECT_SPEC §4.6) and where its chips go: drawing, moving and
/// resizing it with the pointer, what the pointer is on, and that it never leaves the monitor.
/// </summary>
public sealed class CaptureSelectionTests
{
    private static readonly Size Monitor = new(1000, 600);

    private static CaptureSelection Drawn(Rect area)
    {
        var selection = new CaptureSelection(Monitor);
        selection.Begin(area.TopLeft);
        Assert.True(selection.End(area.BottomRight));
        return selection;
    }

    [Fact]
    public void DraggingOnTheBackgroundDrawsASelection_FromWhereThePointerWentDownToWhereItWasReleased()
    {
        var selection = new CaptureSelection(Monitor);
        Assert.False(selection.HasSelection);
        Assert.Equal(Rect.Empty, selection.Rect);

        selection.Begin(new Point(100, 80));
        Assert.True(selection.IsDragging);
        selection.Drag(new Point(300, 260));
        Assert.Equal(new Rect(100, 80, 200, 180), selection.Rect);
        Assert.True(selection.End(new Point(320, 280)));

        Assert.False(selection.IsDragging);
        Assert.Equal(new Rect(100, 80, 220, 200), selection.Rect);
    }

    [Theory]
    [InlineData(300, 260, 100, 80)]
    [InlineData(300, 80, 100, 260)]
    [InlineData(100, 260, 300, 80)]
    public void DraggingAnyWay_GivesTheSameRectangle(double fromX, double fromY, double toX, double toY)
    {
        var selection = new CaptureSelection(Monitor);
        selection.Begin(new Point(fromX, fromY));
        selection.End(new Point(toX, toY));

        Assert.Equal(new Rect(100, 80, 200, 180), selection.Rect);
    }

    [Fact]
    public void ASelectionNeverLeavesTheMonitor_WhateverWhereThePointerGoes()
    {
        var selection = new CaptureSelection(Monitor);
        selection.Begin(new Point(900, 500));
        selection.End(new Point(5000, -400));

        Assert.Equal(new Rect(900, 0, 100, 500), selection.Rect);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(9.9, 200)]
    [InlineData(200, 9.9)]
    [InlineData(0, 0)]
    public void ADragSmallerThanTenIsAClick_AndLeavesNoSelection(double width, double height)
    {
        var selection = new CaptureSelection(Monitor);
        selection.Begin(new Point(100, 100));

        Assert.False(selection.End(new Point(100 + width, 100 + height)));

        Assert.False(selection.HasSelection);
        Assert.Equal(Rect.Empty, selection.Rect);
    }

    [Fact]
    public void DrawingANewSelection_TakesTheOldOneAway_AsSoonAsThePointerGoesDown()
    {
        var selection = Drawn(new Rect(100, 100, 300, 200));
        var changes = 0;
        selection.Changed += (_, _) => changes++;

        selection.Begin(new Point(700, 400));

        Assert.False(selection.HasSelection);
        Assert.Equal(1, changes);
        selection.Drag(new Point(800, 500));
        Assert.Equal(new Rect(700, 400, 100, 100), selection.Rect);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void DraggingInsideMovesTheSelection_KeepingItsSize_AndItStopsAtTheMonitorsEdges()
    {
        var selection = Drawn(new Rect(100, 100, 300, 200));

        selection.Begin(new Point(200, 150));
        selection.Drag(new Point(260, 190));
        Assert.Equal(new Rect(160, 140, 300, 200), selection.Rect);

        selection.Drag(new Point(5000, 5000));
        Assert.Equal(new Rect(700, 400, 300, 200), selection.Rect);

        selection.Drag(new Point(-5000, -5000));
        Assert.Equal(new Rect(0, 0, 300, 200), selection.Rect);
        selection.End(new Point(-5000, -5000));
        Assert.Equal(new Rect(0, 0, 300, 200), selection.Rect);
    }

    [Theory]
    [InlineData("BottomRight", 500, 400, 100, 100, 400, 300)]
    [InlineData("TopLeft", 150, 120, 150, 120, 250, 180)]
    [InlineData("TopRight", 500, 120, 100, 120, 400, 180)]
    [InlineData("BottomLeft", 150, 400, 150, 100, 250, 300)]
    [InlineData("Right", 500, 999, 100, 100, 400, 200)]
    [InlineData("Left", 150, 999, 150, 100, 250, 200)]
    [InlineData("Top", 999, 120, 100, 120, 300, 180)]
    [InlineData("Bottom", 999, 400, 100, 100, 300, 300)]
    public void DraggingAHandle_MovesItsEdges_AndOnlyThose(
        string name, double pointerX, double pointerY, double left, double top, double width, double height)
    {
        var part = Enum.Parse<SelectionPart>(name);
        // The selection is (100, 100) to (400, 300). The pointer's value for an axis the handle does not move on is ignored.
        var selection = Drawn(new Rect(100, 100, 300, 200));
        var grab = selection.Handles().First(handle => handle.Part == part).Center;

        selection.Begin(grab);
        selection.Drag(new Point(part is SelectionPart.Top or SelectionPart.Bottom ? grab.X : pointerX, part is SelectionPart.Left or SelectionPart.Right ? grab.Y : pointerY));

        Assert.Equal(new Rect(left, top, width, height), selection.Rect);
    }

    [Fact]
    public void DraggingAnEdgePastTheOppositeOne_TurnsTheSelectionOver()
    {
        var selection = Drawn(new Rect(100, 100, 300, 200));

        selection.Begin(new Point(400, 200));
        Assert.Equal(SelectionPart.Right, selection.HitTest(new Point(400, 200)));
        selection.Drag(new Point(50, 200));

        Assert.Equal(new Rect(50, 100, 50, 200), selection.Rect);
    }

    [Fact]
    public void AResizeDragKeepsTheSelection_EvenWhenItEndsSmall()
    {
        var selection = Drawn(new Rect(100, 100, 300, 200));

        selection.Begin(new Point(400, 300));
        Assert.True(selection.End(new Point(103, 103)));

        Assert.Equal(new Rect(100, 100, 3, 3), selection.Rect);
    }

    [Fact]
    public void TheHandles_AreTheFourCornersAndTheMiddlesOfTheSidesLongEnough()
    {
        var large = Drawn(new Rect(100, 100, 300, 200));
        Assert.Equal(
            [SelectionPart.TopLeft, SelectionPart.TopRight, SelectionPart.BottomRight, SelectionPart.BottomLeft, SelectionPart.Top, SelectionPart.Bottom, SelectionPart.Left, SelectionPart.Right],
            large.Handles().Select(handle => handle.Part));
        Assert.Equal(new Point(250, 100), large.Handles().Single(handle => handle.Part == SelectionPart.Top).Center);
        Assert.Equal(new Point(100, 200), large.Handles().Single(handle => handle.Part == SelectionPart.Left).Center);

        // A small one is not all handles: the middles of a side under 36 are left out.
        var narrow = Drawn(new Rect(100, 100, 30, 200));
        Assert.DoesNotContain(narrow.Handles(), handle => handle.Part is SelectionPart.Top or SelectionPart.Bottom);
        Assert.Contains(narrow.Handles(), handle => handle.Part is SelectionPart.Left);
        var tiny = Drawn(new Rect(100, 100, 20, 20));
        Assert.Equal(4, tiny.Handles().Count());

        Assert.Empty(new CaptureSelection(Monitor).Handles());
    }

    [Fact]
    public void WhatThePointerIsOn_CornersFirst_ThenSides_ThenInside()
    {
        var selection = Drawn(new Rect(100, 100, 300, 200));

        Assert.Equal(SelectionPart.TopLeft, selection.HitTest(new Point(100, 100)));
        Assert.Equal(SelectionPart.TopLeft, selection.HitTest(new Point(93, 95)));
        Assert.Equal(SelectionPart.BottomRight, selection.HitTest(new Point(405, 305)));
        Assert.Equal(SelectionPart.Top, selection.HitTest(new Point(250, 98)));
        Assert.Equal(SelectionPart.Top, selection.HitTest(new Point(180, 103)));
        Assert.Equal(SelectionPart.Left, selection.HitTest(new Point(97, 160)));
        Assert.Equal(SelectionPart.Right, selection.HitTest(new Point(402, 250)));
        Assert.Equal(SelectionPart.Bottom, selection.HitTest(new Point(300, 304)));
        Assert.Equal(SelectionPart.Inside, selection.HitTest(new Point(250, 200)));
        Assert.Equal(SelectionPart.Outside, selection.HitTest(new Point(50, 50)));
        Assert.Equal(SelectionPart.Outside, selection.HitTest(new Point(250, 320)));
        Assert.Equal(SelectionPart.Outside, new CaptureSelection(Monitor).HitTest(new Point(250, 200)));
    }

    [Fact]
    public void DraggingFromTheBackgroundNextToTheSelection_DrawsANewOne_NotMovesTheOld()
    {
        var selection = Drawn(new Rect(100, 100, 300, 200));

        selection.Begin(new Point(450, 150));
        selection.End(new Point(600, 260));

        Assert.Equal(new Rect(450, 150, 150, 110), selection.Rect);
    }

    [Fact]
    public void ASelectionCanBeSetAndCleared_CutToTheMonitor()
    {
        var selection = new CaptureSelection(Monitor);
        var changes = 0;
        selection.Changed += (_, _) => changes++;

        selection.Select(new Rect(-50, 500, 300, 300));
        Assert.Equal(new Rect(0, 500, 250, 100), selection.Rect);

        selection.Clear();
        Assert.False(selection.HasSelection);
        Assert.Equal(2, changes);

        selection.Clear();
        Assert.Equal(2, changes);
    }

    [Fact]
    public void AMonitorMustHaveASize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureSelection(new Size(0, 600)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureSelection(Size.Empty));
    }

    // ---- Where the chips go ----------------------------------------------------------------------------------------------------

    private static readonly Size Chips = new(146.5, 76.5);

    [Fact]
    public void TheChipsHangToTheRightOfTheSelection_TheirMiddleLevelWithItsMiddle_ALittleWayOut()
    {
        // The reference's selection, in DIPs, on a monitor 700 wide.
        var placement = ChipPlacement.Place(new Rect(40.15, 29.85, 475.45, 368.65), Chips, new Size(700, 430));

        Assert.Equal(ChipSide.Right, placement.Side);
        Assert.Equal(40.15 + 475.45 + ChipPlacement.Gap, placement.Position.X, 3);
        Assert.Equal(29.85 + 368.65 / 2 - 76.5 / 2, placement.Position.Y, 3);
    }

    [Fact]
    public void WithoutRoomOnTheRight_TheyGoToTheLeft_WithoutRoomThere_UnderIt_ThenOverIt_ThenInside()
    {
        var bounds = new Size(700, 430);

        var left = ChipPlacement.Place(new Rect(300, 100, 380, 200), Chips, bounds);
        Assert.Equal(ChipSide.Left, left.Side);
        Assert.Equal(300 - ChipPlacement.Gap - 146.5, left.Position.X, 3);

        var below = ChipPlacement.Place(new Rect(40, 40, 620, 200), Chips, bounds);
        Assert.Equal(ChipSide.Below, below.Side);
        Assert.Equal((40.0, 240 + ChipPlacement.Gap), (below.Position.X, below.Position.Y));

        var above = ChipPlacement.Place(new Rect(40, 150, 620, 270), Chips, bounds);
        Assert.Equal(ChipSide.Above, above.Side);
        Assert.Equal(150 - ChipPlacement.Gap - 76.5, above.Position.Y, 3);

        var inside = ChipPlacement.Place(new Rect(0, 0, 700, 430), Chips, bounds);
        Assert.Equal(ChipSide.Inside, inside.Side);
        Assert.Equal((700 - ChipPlacement.Gap - 146.5, 430 - ChipPlacement.Gap - 76.5), (inside.Position.X, inside.Position.Y));
    }

    [Fact]
    public void TheChipsAreKeptOnTheMonitor_WhateverTheSelection()
    {
        var bounds = new Size(700, 430);

        // A selection at the top: the stack, level with its middle, would stick out above the monitor.
        var top = ChipPlacement.Place(new Rect(40, 0, 100, 20), Chips, bounds);
        Assert.Equal(ChipSide.Right, top.Side);
        Assert.Equal(ChipPlacement.Margin, top.Position.Y);

        var bottom = ChipPlacement.Place(new Rect(40, 410, 100, 20), Chips, bounds);
        Assert.Equal(430 - ChipPlacement.Margin - 76.5, bottom.Position.Y, 3);

        // Wherever they go, they are on the monitor.
        for (var left = 0.0; left <= 600; left += 75)
        {
            for (var topEdge = 0.0; topEdge <= 380; topEdge += 50)
            {
                var placement = ChipPlacement.Place(new Rect(left, topEdge, 100, 50), Chips, bounds);
                Assert.InRange(placement.Position.X, ChipPlacement.Margin, 700 - ChipPlacement.Margin - 146.5);
                Assert.InRange(placement.Position.Y, ChipPlacement.Margin, 430 - ChipPlacement.Margin - 76.5);
            }
        }
    }
}
