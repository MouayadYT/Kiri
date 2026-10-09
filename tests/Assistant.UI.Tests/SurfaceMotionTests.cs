using Assistant.UI.Animation;
using Xunit;

namespace Assistant.UI.Tests;

public sealed class SurfaceMotionTests
{
    private static readonly TimeSpan Show = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan Hide = TimeSpan.FromMilliseconds(160);

    [Fact]
    public void TweenEasesOutAndArrivesExactly()
    {
        var tween = new PresenceTween(0);
        tween.Start(1, Show);

        tween.Advance(TimeSpan.Zero);
        Assert.Equal(0, tween.Value);
        tween.Advance(TimeSpan.FromMilliseconds(120));
        Assert.Equal(0.875, tween.Value, 6); // Cubic ease-out: most of the way at half time.
        Assert.True(tween.IsRunning);
        tween.Advance(TimeSpan.FromMilliseconds(120));
        Assert.Equal(1, tween.Value);
        Assert.False(tween.IsRunning);
    }

    [Fact]
    public void TurningAroundContinuesFromCurrentValueInMatchingShareOfTime()
    {
        var tween = new PresenceTween(0);
        tween.Start(1, Show);
        tween.Advance(TimeSpan.FromMilliseconds(120));

        tween.Start(0, Hide);

        Assert.Equal(0.875, tween.Value, 6);
        tween.Advance(TimeSpan.FromMilliseconds(70)); // Half of 0.875 x 160 ms.
        Assert.Equal(0.875 - (0.875 * 0.875), tween.Value, 6);
        tween.Advance(TimeSpan.FromMilliseconds(70));
        Assert.Equal(0, tween.Value);
        Assert.False(tween.IsRunning);
    }

    [Fact]
    public void StartingWhereItAlreadyIsDoesNothing()
    {
        var tween = new PresenceTween(1);
        tween.Start(1, Show);

        Assert.False(tween.IsRunning);
        Assert.Equal(1, tween.Value);
        tween.JumpTo(3);
        Assert.Equal(1, tween.Value);
        tween.JumpTo(-1);
        Assert.Equal(0, tween.Value);
    }

    [Theory]
    [InlineData(0, 0, 0.96, -8)]
    [InlineData(0.5, 0.5, 0.98, -4)]
    [InlineData(1, 1, 1, 0)]
    [InlineData(2, 1, 1, 0)]
    public void FrameFadesGrowsAndSettlesDown(double presence, double opacity, double scale, double offsetY)
    {
        var frame = new SurfaceMotion().FrameAt(presence);

        Assert.Equal(opacity, frame.Opacity, 6);
        Assert.Equal(scale, frame.Scale, 6);
        Assert.Equal(offsetY, frame.OffsetY, 6);
    }
}
