using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Assistant.UI.Controls;
using Assistant.UI.Orb;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The orb control ---------------------------------------------------------------------------------------

    [Fact]
    public void OrbRunsOnlyWhileItCanBeSeen() => RunSta(() =>
    {
        var (window, _, orb, frames) = CreateOrbWindow();
        try
        {
            Assert.False(frames.Running);
            Assert.False(orb.IsAnimating);
            window.Show();
            Assert.True(frames.Running);
            Assert.True(orb.IsAnimating);
            window.Hide();
            Assert.False(frames.Running);
            window.Show();
            Assert.True(frames.Running);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ACalmOrbIsDrawnAboutThirtyTimesASecondAndAListeningOneEveryFrame() => RunSta(() =>
    {
        var (window, _, orb, frames) = CreateOrbWindow();
        try
        {
            window.Show();
            frames.Tick(T0);
            frames.RunUntil(T0 + Second);
            Assert.InRange(orb.FramesDrawn, 25, 40);

            orb.State = OrbState.Listening;
            var before = orb.FramesDrawn;
            frames.RunUntil(T0 + (2 * Second));
            Assert.InRange(orb.FramesDrawn - before, 95, 101);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void OnlyAListeningOrbHearsTheAmplitudeItIsGiven() => RunSta(() =>
    {
        var (window, _, orb, frames) = CreateOrbWindow();
        try
        {
            window.Show();
            orb.Amplitude = 0.8;
            frames.Tick(T0);
            frames.RunUntil(T0 + Second);
            Assert.Equal(0, orb.Motion.Level); // Idle: no reaction.

            orb.State = OrbState.Listening;
            frames.RunUntil(T0 + (2 * Second));
            Assert.True(orb.Motion.Level > 0.5, $"A listening orb followed loudness only to {orb.Motion.Level:F2}.");

            // The sound stops: it settles.
            orb.Amplitude = 0;
            frames.RunUntil(T0 + (6 * Second));
            Assert.Equal(0, orb.Motion.Level);

            // It stops hearing when it stops listening.
            orb.Amplitude = 1;
            frames.RunUntil(T0 + (7 * Second));
            Assert.True(orb.Motion.Level > 0.5);
            orb.State = OrbState.Thinking;
            frames.RunUntil(T0 + (11 * Second));
            Assert.Equal(0, orb.Motion.Level);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ASourceIsReadOncePerFrameOnlyWhileListeningAndWinsOverTheProperty() => RunSta(() =>
    {
        var (window, _, orb, frames) = CreateOrbWindow();
        try
        {
            window.Show();
            var source = new CountingAmplitude { Value = 0.9 };
            orb.AmplitudeSource = source;
            orb.Amplitude = 0;
            frames.Tick(T0);
            frames.RunUntil(T0 + Second);
            Assert.Equal(0, source.Reads); // Not listening: the sound is not even read.

            orb.State = OrbState.Listening;
            var drawn = orb.FramesDrawn;
            frames.RunUntil(T0 + (2 * Second));
            Assert.Equal(orb.FramesDrawn - drawn, source.Reads);
            Assert.True(orb.Motion.Level > 0.5, "The source, not the property, should have driven the orb.");

            orb.AmplitudeSource = null;
            frames.RunUntil(T0 + (6 * Second));
            Assert.Equal(0, orb.Motion.Level); // Back to the property, which is 0.
        }
        finally { window.Close(); }
    });

    [Fact]
    public void AmplitudeIsClampedAndAnUnreadableReadingCountsAsSilence() => RunSta(() =>
    {
        var orb = new AssistantOrb(new FakeFrames(), () => true);
        orb.Amplitude = 3;
        Assert.Equal(1, orb.Amplitude);
        orb.Amplitude = -1;
        Assert.Equal(0, orb.Amplitude);
        orb.Amplitude = double.NaN;
        Assert.Equal(0, orb.Amplitude);

        var (window, _, shown, frames) = CreateOrbWindow();
        try
        {
            window.Show();
            shown.State = OrbState.Listening;
            shown.AmplitudeSource = new CountingAmplitude { Value = double.NaN };
            frames.Tick(T0);
            frames.RunUntil(T0 + Second);
            Assert.Equal(0, shown.Motion.Level);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WithoutAnimationEffectsTheOrbHoldsTheReferencePoseForItsStateWithoutRunning() => RunSta(() =>
    {
        var (window, _, orb, frames) = CreateOrbWindow(animations: false);
        try
        {
            window.Show();
            orb.State = OrbState.Listening;
            orb.Amplitude = 1;
            Assert.False(frames.Running);
            Assert.False(orb.IsAnimating);
            Assert.All(new[] { -0.5, 0, 0.5 }, x => Assert.Equal(new OrbShape().RestBoundary(x), orb.Motion.BoundaryAt(x), 9));

            orb.State = OrbState.Error;
            Assert.False(frames.Running);
            Assert.InRange(orb.Motion.Pose.Tint, 0.5, 1);
            orb.State = OrbState.Thinking;
            Assert.Equal(0, orb.Motion.Pose.Tint);
            Assert.True(orb.Motion.Pose.SheenStrength > 0.99);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void HidingTheOrbResetsItSoItStartsAtRestWhenShownAgain() => RunSta(() =>
    {
        var (window, _, orb, frames) = CreateOrbWindow();
        try
        {
            window.Show();
            orb.State = OrbState.Listening;
            orb.Amplitude = 1;
            frames.Tick(T0);
            frames.RunUntil(T0 + Second);
            Assert.True(orb.Motion.Level > 0.5);
            window.Hide();
            Assert.Equal(0, orb.Motion.Level);
            Assert.Equal(0, orb.Motion.SwellCount);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void OrbSizesToWhatHostsItAndRendersAtAnySize() => RunSta(() =>
    {
        foreach (var side in new[] { 24.0, 41, 103, 220 })
        {
            var (window, host, orb, _) = CreateOrbWindow(animations: false, side: side);
            try
            {
                window.Show();
                orb.State = OrbState.Listening;
                host.UpdateLayout();
                Assert.Equal(side, orb.ActualWidth);
                Assert.Equal(side, orb.ActualHeight);
                var pixels = Render(host, 2);
                Assert.True(pixels.PixelWidth > 0);
            }
            finally { window.Close(); }
        }

        // Left to size itself, it is a square.
        var free = new AssistantOrb(new FakeFrames(), () => true);
        free.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.Equal(free.DesiredSize.Width, free.DesiredSize.Height);
        Assert.True(free.DesiredSize.Width > 40);
        var squeezed = new AssistantOrb(new FakeFrames(), () => true);
        squeezed.Measure(new Size(60, 200));
        Assert.Equal(new Size(60, 60), squeezed.DesiredSize);
    });

    [Fact]
    public void OrbHasAnAccessibleNameForItsState() => RunSta(() =>
    {
        var orb = new AssistantOrb(new FakeFrames(), () => true);
        var peer = UIElementAutomationPeer.CreatePeerForElement(orb);
        Assert.Equal(AutomationControlType.Image, peer.GetAutomationControlType());
        Assert.Equal("Assistant", peer.GetName());
        orb.State = OrbState.Listening;
        Assert.Equal("Assistant, listening", peer.GetName());
        orb.State = OrbState.Thinking;
        Assert.Equal("Assistant, thinking", peer.GetName());
        orb.State = OrbState.Error;
        Assert.Equal("Assistant, something went wrong", peer.GetName());
        System.Windows.Automation.AutomationProperties.SetName(orb, "Voice");
        Assert.Equal("Voice", peer.GetName());
    });

    [Fact]
    public void OrbShapeTokenAndStyleComeFromTheTheme() => RunSta(() =>
    {
        var theme = new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) };
        var shape = Assert.IsType<OrbShape>(theme["Orb.Assistant"]);

        // The token is the shape's defaults, which are the reference's measurements.
        Assert.Equal(new OrbShape(), shape);

        // The orb's style gives it that token and keeps focus off it.
        var style = Assert.IsType<Style>(theme[typeof(AssistantOrb)]);
        var setters = style.Setters.OfType<Setter>().ToArray();
        Assert.Equal(new OrbShape(), Assert.IsType<OrbShape>(Assert.Single(setters, setter => setter.Property == AssistantOrb.ShapeProperty).Value));
        Assert.Equal(false, Assert.Single(setters, setter => setter.Property == UIElement.FocusableProperty).Value);
    });

    // Fixture only: a source whose reading is set by hand.
    private sealed class CountingAmplitude : IOrbAmplitudeSource
    {
        public double Value { get; set; }
        public int Reads { get; private set; }
        public double ReadAmplitude() { Reads++; return Value; }
    }
}
