using System.Windows;
using System.Windows.Media;
using Assistant.UI.Controls;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    private static readonly Point[] SampleTopFade = [new(16, 0), new(52, 0.38), new(88, 1)];
    private static readonly Point[] SampleBottomFade = [new(14, 0), new(55, 0.5), new(88, 1)];

    [Theory]
    [InlineData(0, 0)]
    [InlineData(16, 0)]
    [InlineData(34, 0.19)]
    [InlineData(52, 0.38)]
    [InlineData(70, 0.69)]
    [InlineData(88, 1)]
    [InlineData(299, 1)]
    [InlineData(510, 1)]
    [InlineData(543, 0.5)]
    [InlineData(584, 0)]
    [InlineData(598, 0)]
    public void FadesRunStraightBetweenTheirPoints(double y, double opacity) =>
        Assert.Equal(opacity, FadingScrollViewer.GetOpacity(y, 598, SampleTopFade, SampleBottomFade), 6);

    [Fact]
    public void FadesTakeTheLowerOpacityWhereTheyMeetAndNothingFadesWithoutThem()
    {
        // On a viewer too short for both, each point is as faded as either fade makes it.
        Assert.Equal(Math.Min(34.0 / 36 * 0.38, 36.0 / 41 * 0.5), FadingScrollViewer.GetOpacity(50, 100, SampleTopFade, SampleBottomFade), 6);
        Assert.Equal(1, FadingScrollViewer.GetOpacity(0, 598, null, []));
        Assert.Equal(0.19, FadingScrollViewer.GetOpacity(34, 598, SampleTopFade.Reverse(), null), 6);
    }

    [Fact]
    public void FadeMaskHasAStopAtEachPointOfEitherFade() => RunSta(() =>
    {
        Assert.Null(FadingScrollViewer.CreateMask(598, null, []));
        Assert.Null(FadingScrollViewer.CreateMask(0, SampleTopFade, SampleBottomFade));

        var mask = Assert.IsType<LinearGradientBrush>(FadingScrollViewer.CreateMask(598, SampleTopFade, SampleBottomFade));
        Assert.True(mask.IsFrozen);
        Assert.Equal(BrushMappingMode.Absolute, mask.MappingMode);
        Assert.Equal((new Point(0, 0), new Point(0, 598)), (mask.StartPoint, mask.EndPoint));
        Assert.Equal(new[] { 0.0, 16, 52, 88, 510, 543, 584, 598 }, mask.GradientStops.Select(stop => Math.Round(stop.Offset * 598, 6)));
        Assert.Equal(new[] { 0, 0, 97, 255, 255, 128, 0, 0 }, mask.GradientStops.Select(stop => (int)stop.Color.A));
    });
}
