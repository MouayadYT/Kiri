using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Assistant.UI.Animation;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(10);

    [Fact]
    public void TransitionShowsAtOnceThenAnimatesInAndRestsExactlyShown() => RunSta(() =>
    {
        var (window, surface) = CreateAnimatedWindow();
        var frames = new FakeFrames();
        var transition = new SurfaceTransition(window, surface, new SurfaceMotion(), frames, () => true);
        var applied = new List<SurfaceFrame>();
        transition.FrameApplied += (_, frame) => applied.Add(frame);
        try
        {
            transition.Show();
            Assert.True(window.IsVisible);
            AssertDrawn(surface, 0, 0.96, -8);
            Assert.True(frames.Running);

            frames.Tick(T0); // The first frame only starts the clock.
            AssertDrawn(surface, 0, 0.96, -8);
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(120));
            AssertDrawn(surface, 0.875, 0.995, -1);
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(240));
            AssertDrawn(surface, 1, 1, 0);
            Assert.False(frames.Running);
            Assert.Equal(new SurfaceFrame(1, 1, 0), applied[^1]);

            // Showing a shown surface changes nothing.
            transition.Show();
            Assert.False(frames.Running);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TransitionAnimatesOutThenHidesAndRestsShownForTheNextShow() => RunSta(() =>
    {
        var (window, surface) = CreateAnimatedWindow();
        var frames = new FakeFrames();
        var transition = new SurfaceTransition(window, surface, new SurfaceMotion(), frames, () => true);
        try
        {
            window.Show();
            transition.Hide();
            Assert.True(transition.IsHiding);

            frames.Tick(T0);
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(80));
            Assert.True(window.IsVisible);
            AssertDrawn(surface, 0.125, 0.965, -7);
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(160));

            Assert.False(window.IsVisible);
            Assert.False(transition.IsHiding);
            Assert.False(frames.Running);
            AssertDrawn(surface, 1, 1, 0);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TransitionTurnsAroundFromWhereverItIsWithoutHiding() => RunSta(() =>
    {
        var (window, surface) = CreateAnimatedWindow();
        var frames = new FakeFrames();
        var transition = new SurfaceTransition(window, surface, new SurfaceMotion(), frames, () => true);
        try
        {
            transition.Show();
            frames.Tick(T0);
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(120));

            transition.Hide();
            AssertDrawn(surface, 0.875, 0.995, -1); // No jump.
            frames.Tick(T0 + TimeSpan.FromMilliseconds(130)); // Restarts the clock.
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(200)); // Half of 0.875 x 160 ms.
            Assert.Equal(0.109375, surface.Opacity, 6);

            transition.Show();
            Assert.Equal(0.109375, surface.Opacity, 6);
            frames.Tick(T0 + TimeSpan.FromMilliseconds(210));
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(500));
            Assert.True(window.IsVisible);
            AssertDrawn(surface, 1, 1, 0);
            Assert.False(frames.Running);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void StalledFrameSlowsTheTransitionInsteadOfSkippingIt() => RunSta(() =>
    {
        var (window, surface) = CreateAnimatedWindow();
        var frames = new FakeFrames();
        var transition = new SurfaceTransition(window, surface, new SurfaceMotion(), frames, () => true);
        try
        {
            transition.Show();
            frames.Tick(T0);
            frames.Tick(T0 + TimeSpan.FromSeconds(1));

            // Counted as 50 ms of the 240 ms.
            Assert.Equal(1 - Math.Pow(1 - (50.0 / 240), 3), surface.Opacity, 6);
            Assert.True(frames.Running);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WithoutAnimationEffectsTransitionShowsAndHidesAtOnce() => RunSta(() =>
    {
        var (window, surface) = CreateAnimatedWindow();
        var frames = new FakeFrames();
        var transition = new SurfaceTransition(window, surface, new SurfaceMotion(), frames, () => false);
        try
        {
            transition.Show();
            Assert.True(window.IsVisible);
            AssertDrawn(surface, 1, 1, 0);

            transition.Hide();
            Assert.False(window.IsVisible);
            AssertDrawn(surface, 1, 1, 0);
            Assert.Equal(0, frames.Starts);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void HidingOrClosingTheWindowDirectlyStopsTheTransition() => RunSta(() =>
    {
        var (window, surface) = CreateAnimatedWindow();
        var frames = new FakeFrames();
        var transition = new SurfaceTransition(window, surface, new SurfaceMotion(), frames, () => true);

        transition.Show();
        frames.Tick(T0);
        window.Hide();
        Assert.False(frames.Running);
        AssertDrawn(surface, 1, 1, 0);

        transition.Show();
        AssertDrawn(surface, 0, 0.96, -8);
        window.Close();
        Assert.False(frames.Running);
        transition.Show();
        transition.Hide();
        Assert.False(window.IsVisible);
    });

    [Fact]
    public void MotionTokenIsTheFloatingSurfaceMotion() => RunSta(() =>
    {
        var theme = new ResourceDictionary
        {
            Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
        };

        var motion = Assert.IsType<SurfaceMotion>(theme["Motion.FloatingSurface"]);

        // Subtle, quick, and quicker to leave than to arrive.
        Assert.InRange(motion.ShowDuration, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400));
        Assert.InRange(motion.HideDuration, TimeSpan.FromMilliseconds(50), motion.ShowDuration);
        Assert.InRange(motion.HiddenScale, 0.9, 0.99);
        Assert.InRange(Math.Abs(motion.HiddenOffsetY), 2, 16);
    });

    [Fact]
    public void BackdropMatchesGlassAsDrawnThroughTransformsAndOpacity() => RunSta(() =>
    {
        var glass = new Border { Width = 200, Height = 60 };
        var scale = new ScaleTransform(1, 1);
        var holder = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = scale, Children = { glass },
        };
        var window = new Window
        {
            Left = -10000, Top = -10000, Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, SizeToContent = SizeToContent.WidthAndHeight, Content = holder,
        };
        var factory = new FakeBackdropFactory();
        var host = BackdropHost.Attach(window, glass, size => new Size(size.Height / 2, size.Height / 2), factory);
        try
        {
            window.Show();
            Pump();
            var backdrop = Assert.IsType<FakeBackdrop>(factory.Created);
            var rest = backdrop.Region;
            window.Opacity = 1;

            scale.ScaleX = scale.ScaleY = 0.5;
            holder.Opacity = 0.5;
            glass.Opacity = 0.8;
            host.MatchGlass();

            var drawn = backdrop.Region;
            Assert.Equal(rest.Width / 2, drawn.Width, 6);
            Assert.Equal(rest.Height / 2, drawn.Height, 6);
            Assert.Equal(rest.CornerRadiusX / 2, drawn.CornerRadiusX, 6);
            Assert.Equal(rest.CornerRadiusY / 2, drawn.CornerRadiusY, 6);
            Assert.Equal(0.4, backdrop.Opacity, 6);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void FocusLossDismissesButKeepsTheDraftAndWindow() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
        });
        var (window, viewModel, _) = CreateAssistant();
        var other = new Window { Left = -10000, Top = -10000, Width = 50, Height = 50, Opacity = 0, ShowInTaskbar = false };
        try
        {
            window.ShowAndFocus();
            Assert.True(window.IsActive, "The overlay must be active for this test.");
            var input = Assert.IsType<PromptInputControl>(window.FindName("PromptInput"));
            input.Text = "draft that must survive";
            var handle = new WindowInteropHelper(window).Handle;

            other.Show();
            other.Activate();
            WaitUntil(() => !window.IsVisible, "Losing focus did not dismiss the overlay.");

            Assert.Equal("draft that must survive", viewModel.Query);
            window.ShowAndFocus();
            Assert.True(window.IsVisible);
            Assert.Equal(handle, new WindowInteropHelper(window).Handle);
            Assert.Equal("draft that must survive", input.Text);
            Assert.True(input.IsKeyboardFocusWithin);
        }
        finally
        {
            other.Close();
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void InvokingWhileLeavingTurnsBackWithoutMoving() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
        });
        var placement = new FakePlacement();
        var window = CreateAssistant(placement).Window;
        var pill = Assert.IsType<Grid>(window.FindName("SurfaceHost"));
        try
        {
            window.ShowAndFocus();
            WaitUntil(() => pill.Opacity == 1, "The overlay did not finish animating in.");

            window.Dismiss();
            if (SystemParameters.ClientAreaAnimation)
            {
                // Still on its way out: invoking it again turns it around where it is.
                Assert.True(window.IsVisible);
                window.ShowAndFocus();
                Assert.Single(placement.Calls);
            }
            else
            {
                Assert.False(window.IsVisible);
                window.ShowAndFocus();
            }

            WaitUntil(() => pill.Opacity == 1, "The overlay did not come back.");
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    private static (Window Window, Border Surface) CreateAnimatedWindow()
    {
        var surface = new Border { Width = 50, Height = 20 };
        var window = new Window
        {
            Left = -10000, Top = -10000, Width = 100, Height = 100, Opacity = 0, ShowActivated = false,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = surface,
        };
        return (window, surface);
    }

    private static void AssertDrawn(UIElement surface, double opacity, double scale, double offsetY)
    {
        var transforms = Assert.IsType<TransformGroup>(surface.RenderTransform).Children;
        var scaling = Assert.IsType<ScaleTransform>(transforms[0]);
        Assert.Equal(opacity, surface.Opacity, 6);
        Assert.Equal(scale, scaling.ScaleX, 6);
        Assert.Equal(scale, scaling.ScaleY, 6);
        Assert.Equal(offsetY, Assert.IsType<TranslateTransform>(transforms[1]).Y, 6);
        Assert.Equal(new Point(0.5, 0.5), surface.RenderTransformOrigin);
    }

    private sealed class FakeFrames : IFrameSource
    {
        public event EventHandler<TimeSpan>? Frame;
        public bool Running { get; private set; }
        public int Starts { get; private set; }
        public void Start() { Running = true; Starts++; }
        public void Stop() => Running = false;
        public TimeSpan Last { get; private set; }
        public void Tick(TimeSpan time) { Last = time; if (Running) Frame?.Invoke(this, time); }

        // Frames every 10 ms after the last one, up to and including the given time.
        public void RunUntil(TimeSpan time)
        {
            while (Last < time) Tick(Last + TimeSpan.FromMilliseconds(Math.Min(10, (time - Last).TotalMilliseconds)));
        }
    }
}
