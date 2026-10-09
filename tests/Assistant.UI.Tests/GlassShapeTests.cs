using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Assistant.UI.Controls;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    [Theory]
    [InlineData(520, 91)]
    [InlineData(100, 91)]
    [InlineData(40, 91)]
    public void PillOutlineIsUnchangedBySharingTheCornerCode(double width, double height) => RunSta(() =>
    {
        var bounds = new Rect(0.5, 0.5, width, height);
        var shared = PillShape.CreateGeometry(bounds);
        var original = OriginalPill(bounds);
        Assert.Equal(original.Bounds, shared.Bounds);
        for (var x = -1.0; x <= width + 2; x += 0.5)
        {
            for (var y = -1.0; y <= height + 2; y += 0.5)
            {
                Assert.Equal(original.FillContains(new Point(x, y)), shared.FillContains(new Point(x, y)));
            }
        }
    });

    [Fact]
    public void PanelHasSmoothCornersAndABlurRegionFittedToThem() => RunSta(() =>
    {
        var size = new Size(418, 598);
        var outline = PanelShape.CreateGeometry(new Rect(size), 42.75);
        Assert.Equal(new Rect(size), outline.Bounds);

        // Straight edges reach the corners' 42.75-DIP extent; the corners are squarer than quarter circles.
        Assert.True(outline.FillContains(new Point(43, 0.2)));
        Assert.True(outline.FillContains(new Point(0.2, 43)));
        Assert.False(outline.FillContains(new Point(5, 5)));
        Assert.True(outline.FillContains(new Point(12.5, 12.5)));

        // The blur region's corners are ellipses, which is all a blur can be clipped to: of the radius that follows the panel's corners most closely,
        // smaller than their reach. Each of the four corners is then off by a sliver, in and out (about 7 and 14 DIP²), where an ellipse of the whole
        // reach left a crescent of 130 DIP² of glass with nothing blurred behind it.
        var radii = PanelShape.GetBackdropCornerRadii(size, 42.75);
        Assert.Equal(42.75 * 0.808, radii.Width, 1);
        Assert.Equal(radii.Width, radii.Height);
        var region = new RectangleGeometry(new Rect(size), radii.Width, radii.Height);
        var pokesOut = Geometry.Combine(region, outline, GeometryCombineMode.Exclude, null).GetArea();
        var stopsShort = Geometry.Combine(outline, region, GeometryCombineMode.Exclude, null).GetArea();
        var whole = new RectangleGeometry(new Rect(size), 42.75, 42.75);
        var stoppedShort = Geometry.Combine(outline, whole, GeometryCombineMode.Exclude, null).GetArea();
        Assert.InRange(stoppedShort, 4 * 110, 4 * 150);
        Assert.True(pokesOut < 4 * 18, $"The blur region pokes {pokesOut:F2} DIP² past the panel.");
        Assert.True(stopsShort < 4 * 10, $"The blur region stops {stopsShort:F2} DIP² short of the panel.");

        // A panel smaller than its corners has them as large as fits.
        var small = PanelShape.GetBackdropCornerRadii(new Size(20, 20), 42.75);
        Assert.Equal(10 * 0.808, small.Width, 1);
        Assert.Equal(small.Width, small.Height);
    });

    [Fact]
    public void BubbleWrapsItsTextWithPaddingAndCurlsItsTailBelow() => RunSta(() =>
    {
        var text = new TextBlock { Text = "I have a question", FontSize = 13.5 };
        var bubble = new SpeechBubble { Padding = new Thickness(12, 6, 12.5, 7.5), CornerRadius = 15.75, Child = text, Background = Brushes.White };
        bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bubble.Arrange(new Rect(bubble.DesiredSize));
        Assert.Equal(text.DesiredSize.Width + 24.5, bubble.DesiredSize.Width, 6);
        Assert.Equal(text.DesiredSize.Height + 13.5, bubble.DesiredSize.Height, 6);
        Assert.Equal(new Point(12, 6), text.TranslatePoint(new Point(), bubble));

        var size = new Size(125.5, 31.5);
        var outline = SpeechBubble.CreateGeometry(size, 15.75);
        Assert.Equal(size.Height + SpeechBubble.TailDepth, outline.Bounds.Bottom, 1);
        Assert.True(outline.FillContains(new Point(125.5 - 9.5, 31.5 + 2.5))); // Halfway down the tail.
        Assert.False(outline.FillContains(new Point(125.5 - 20, 31.5 + 2))); // Left of the tail.
        Assert.False(outline.FillContains(new Point(1, 1))); // The rounded corner.
    });

    // PillShape's outline as it was drawn before the corner code was shared with PanelShape.
    private static Geometry OriginalPill(Rect bounds)
    {
        var halfHeight = bounds.Height / 2;
        var endLength = Math.Min(bounds.Height * 0.6, bounds.Width / 2);
        var controlX = endLength * 0.6877;
        var controlY = halfHeight * 0.6877;
        var (left, top, right, bottom) = (bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        var middle = top + halfHeight;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(left + endLength, top), true, true);
            context.LineTo(new Point(right - endLength, top), true, true);
            context.BezierTo(new Point(right - endLength + controlX, top), new Point(right, middle - controlY), new Point(right, middle), true, true);
            context.BezierTo(new Point(right, middle + controlY), new Point(right - endLength + controlX, bottom), new Point(right - endLength, bottom), true, true);
            context.LineTo(new Point(left + endLength, bottom), true, true);
            context.BezierTo(new Point(left + endLength - controlX, bottom), new Point(left, middle + controlY), new Point(left, middle), true, true);
            context.BezierTo(new Point(left, middle - controlY), new Point(left + endLength - controlX, top), new Point(left + endLength, top), true, true);
        }

        geometry.Freeze();
        return geometry;
    }
}
