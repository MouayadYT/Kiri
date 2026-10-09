using System.Windows;
using System.Windows.Media;
using Assistant.UI.Animation;
using Assistant.UI.Controls;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    private static readonly SurfaceForm CompactForm = SurfaceForm.Pill(520, 91);
    private static readonly SurfaceForm ConversationForm = SurfaceForm.Panel(418, 598, 42.75);

    [Fact]
    public void FormsAreThePillAndThePanelAndBlendBetweenThem()
    {
        // The pill's ends and the panel's corners are the ones the references gave.
        Assert.Equal(new SurfaceForm(520, 91, 54.6, 45.5, 0.6877), CompactForm with { CornerWidth = Math.Round(CompactForm.CornerWidth, 6) });
        Assert.Equal(new SurfaceForm(418, 598, 42.75, 42.75, 0.725), ConversationForm);

        Assert.Same(CompactForm, SurfaceForm.Blend(CompactForm, ConversationForm, 0));
        Assert.Same(ConversationForm, SurfaceForm.Blend(CompactForm, ConversationForm, 1));
        Assert.Same(CompactForm, SurfaceForm.Blend(CompactForm, ConversationForm, -3));
        Assert.Same(ConversationForm, SurfaceForm.Blend(CompactForm, ConversationForm, 7));

        var half = SurfaceForm.Blend(CompactForm, ConversationForm, 0.5);
        Assert.Equal(469, half.Width, 9);
        Assert.Equal(344.5, half.Height, 9);
        Assert.Equal((CompactForm.CornerWidth + 42.75) / 2, half.CornerWidth, 9);
        Assert.Equal((45.5 + 42.75) / 2, half.CornerHeight, 9);
        Assert.Equal((0.6877 + 0.725) / 2, half.ControlRatio, 9);

        // Every step from one to the other narrows the glass while it grows taller, and never overshoots.
        var forms = Enumerable.Range(0, 21).Select(i => SurfaceForm.Blend(CompactForm, ConversationForm, i / 20.0)).ToArray();
        Assert.All(forms.Zip(forms.Skip(1)), pair =>
        {
            Assert.True(pair.Second.Width < pair.First.Width);
            Assert.True(pair.Second.Height > pair.First.Height);
        });
    }

    [Fact]
    public void FormsDrawTheSameOutlinesAsThePillAndPanelShapes() => RunSta(() =>
    {
        foreach (var (form, expected) in new[]
        {
            (CompactForm, PillShape.CreateGeometry(new Rect(0.5, 0.5, 520, 91))),
            (ConversationForm, PanelShape.CreateGeometry(new Rect(0.5, 0.5, 418, 598), 42.75)),
        })
        {
            var drawn = form.CreateGeometry(new Rect(0.5, 0.5, form.Width, form.Height));
            Assert.Equal(expected.Bounds, drawn.Bounds);
            for (var x = -1.0; x <= form.Width + 2; x += 1.5)
            {
                for (var y = -1.0; y <= form.Height + 2; y += 1.5)
                {
                    Assert.Equal(expected.FillContains(new Point(x, y)), drawn.FillContains(new Point(x, y)));
                }
            }
        }
    });

    // The blur can only be clipped to elliptical corners, and the glass's corners are squarer than ellipses. With the corner's own reach for its radius
    // the blur stopped short of the glass in every corner: a crescent of glass with nothing blurred behind it, four pixels wide on the panel, seen as
    // "the glass and the grey under it have different corners". The radius is fitted instead, and the blur follows the glass closely, in and out.
    [Fact]
    public void TheBlursCornersFollowTheGlassClosely_InEveryFormBetween() => RunSta(() =>
    {
        Assert.Equal(PillShape.GetBackdropCornerRadii(new Size(520, 91)), CompactForm.GetBackdropCornerRadii(new Size(520, 91)));
        Assert.Equal(PanelShape.GetBackdropCornerRadii(new Size(418, 598), 42.75), ConversationForm.GetBackdropCornerRadii(new Size(418, 598)));

        for (var i = 0; i <= 20; i++)
        {
            var form = SurfaceForm.Blend(CompactForm, ConversationForm, i / 20.0);
            var size = new Size(form.Width, form.Height);
            var radii = form.GetBackdropCornerRadii(size);
            var outline = form.CreateGeometry(new Rect(size));
            var region = new RectangleGeometry(new Rect(size), radii.Width, radii.Height);
            var pokesOut = Geometry.Combine(region, outline, GeometryCombineMode.Exclude, null).GetArea();
            var stopsShort = Geometry.Combine(outline, region, GeometryCombineMode.Exclude, null).GetArea();

            // What it was: an ellipse with the corner's whole reach, which never pokes out and stops well short.
            var before = new RectangleGeometry(new Rect(size), Math.Min(form.CornerWidth, size.Width / 2), Math.Min(form.CornerHeight, size.Height / 2));
            var stoppedShort = Geometry.Combine(outline, before, GeometryCombineMode.Exclude, null).GetArea();

            Assert.True(stopsShort < stoppedShort / 4, $"The blur stops {stopsShort:F1} DIP² short of the glass at {i}/20; it was {stoppedShort:F1}.");
            Assert.True(pokesOut < stoppedShort / 4, $"The blur pokes {pokesOut:F1} DIP² past the glass at {i}/20; it stopped {stoppedShort:F1} short before.");
        }
    });

    [Theory]
    [InlineData(0.5523, 1.0)]
    [InlineData(0.6877, 0.851)]
    [InlineData(0.725, 0.808)]
    public void TheFittedRadiusIsTheWholeReachForAnEllipse_AndLessTheSquarerTheCorner(double controlRatio, double share) =>
        Assert.Equal(share, SmoothCorners.FittedEllipseShare(controlRatio), 3);

    [Fact]
    public void SurfaceShapeDrawsItsFormAndFallsBackToARectangle() => RunSta(() =>
    {
        var shape = new SurfaceShape { Width = 520, Height = 91, Fill = Brushes.Black };
        shape.Measure(new Size(520, 91));
        shape.Arrange(new Rect(0, 0, 520, 91));

        // Without a form it is a plain rectangle, filling its corners.
        Assert.Null(shape.Form);
        Assert.Equal(255, RenderAlpha(shape)[0, 0]);

        // With the pill's, its corners are cut away and its middle is filled.
        shape.Form = CompactForm;
        shape.Arrange(new Rect(0, 0, 520, 91)); // Drawn afresh.
        var alpha = RenderAlpha(shape);
        Assert.Equal((0, 255), (alpha[0, 0], alpha[45, 260]));
        Assert.Equal(0, alpha[90, 519]);
    });

    [Fact]
    public void MorphMotionFadesThePillOutBeforeTheConversationFadesIn()
    {
        var motion = new SurfaceMorphMotion();

        // The pill's contents are whole at the start and gone by 40 % of the way; the conversation's are absent until
        // then and whole by 90 %.
        foreach (var (progress, expected) in new[] { (0.0, 1.0), (0.2, 0.5), (0.4, 0.0), (1.0, 0.0) })
        {
            Assert.Equal(expected, motion.CompactOpacityAt(progress), 9);
        }

        foreach (var (progress, expected) in new[] { (0.0, 0.0), (0.4, 0.0), (0.65, 0.5), (0.9, 1.0), (1.0, 1.0) })
        {
            Assert.Equal(expected, motion.ConversationOpacityAt(progress), 9);
        }

        // Neither is ever out of range, and the two are never both partly visible, so no text lies over other text.
        for (var progress = -0.5; progress <= 1.5; progress += 0.01)
        {
            var (compact, conversation) = (motion.CompactOpacityAt(progress), motion.ConversationOpacityAt(progress));
            Assert.InRange(compact, 0, 1);
            Assert.InRange(conversation, 0, 1);
            Assert.True(compact == 0 || conversation == 0, $"Both show at {progress:F2}.");
        }
    }

    [Fact]
    public void MorphTokenIsQuickAndOrderedLikeTheReferenceMotion() => RunSta(() =>
    {
        var theme = new ResourceDictionary
        {
            Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
        };

        var motion = Assert.IsType<SurfaceMorphMotion>(theme["Motion.SurfaceMorph"]);

        Assert.InRange(motion.Duration, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(600));
        Assert.InRange(motion.CompactFadeOutEnd, 0.1, 0.6);
        Assert.InRange(motion.ConversationFadeInStart, motion.CompactFadeOutEnd, 0.9);
        Assert.InRange(motion.ConversationFadeInEnd, motion.ConversationFadeInStart + 0.1, 1);
    });

    [Fact]
    public void MorphGrowsFrameByFrameAndArrivesExactlyOnce()
    {
        var frames = new FakeFrames();
        var morph = new SurfaceMorph(new SurfaceMorphMotion(), frames, () => true);
        var changes = new List<double>();
        var arrivals = 0;
        morph.Changed += (_, _) => changes.Add(morph.Progress);
        morph.Arrived += (_, _) => arrivals++;

        morph.GrowTo(1);
        Assert.True(frames.Running);
        Assert.True(morph.IsRunning);
        Assert.Equal(0, morph.Progress);

        frames.Tick(T0); // The first frame only starts the clock.
        Assert.Equal(0, morph.Progress);
        frames.RunUntil(T0 + TimeSpan.FromMilliseconds(200)); // Half of 400 ms, after a cubic ease-out.
        Assert.Equal(0.875, morph.Progress, 6);
        Assert.Equal(0, arrivals);

        frames.RunUntil(T0 + TimeSpan.FromMilliseconds(400));
        Assert.Equal(1, morph.Progress);
        Assert.False(morph.IsRunning);
        Assert.False(frames.Running);
        Assert.Equal(1, arrivals);
        Assert.Equal(changes.Order(), changes);
        Assert.Equal(1, changes[^1]);

        // Growing to where it already is changes nothing.
        morph.GrowTo(1);
        Assert.Equal(1, arrivals);
        Assert.False(frames.Running);
    }

    [Fact]
    public void MorphTurnsAroundFromWhereverItIsAndASlowFrameSlowsIt()
    {
        var frames = new FakeFrames();
        var morph = new SurfaceMorph(new SurfaceMorphMotion(), frames, () => true);
        var arrivals = new List<double>();
        morph.Arrived += (_, _) => arrivals.Add(morph.Progress);

        morph.GrowTo(1);
        frames.Tick(T0);
        frames.RunUntil(T0 + TimeSpan.FromMilliseconds(100));
        var before = morph.Progress;

        morph.GrowTo(0);
        Assert.Equal(before, morph.Progress); // No jump.
        frames.Tick(T0 + TimeSpan.FromMilliseconds(110)); // Restarts the clock.
        frames.RunUntil(T0 + TimeSpan.FromMilliseconds(600));
        Assert.Equal(0, morph.Progress);
        Assert.Equal([0.0], arrivals);

        // A frame a second late counts as 50 ms of the 400.
        morph.GrowTo(1);
        frames.Tick(T0 + TimeSpan.FromSeconds(2));
        frames.Tick(T0 + TimeSpan.FromSeconds(3));
        Assert.Equal(1 - Math.Pow(1 - (50.0 / 400), 3), morph.Progress, 6);
        Assert.True(frames.Running);
    }

    [Fact]
    public void MorphArrivesAtOnceWithoutAnimationEffectsAndJumpsQuietly()
    {
        var frames = new FakeFrames();
        var animations = false;
        var morph = new SurfaceMorph(new SurfaceMorphMotion(), frames, () => animations);
        var changes = 0;
        var arrivals = 0;
        morph.Changed += (_, _) => changes++;
        morph.Arrived += (_, _) => arrivals++;

        morph.GrowTo(1);
        Assert.Equal((1.0, 1, 1), (morph.Progress, changes, arrivals));
        Assert.Equal(0, frames.Starts);
        Assert.False(morph.IsRunning);

        // A jump draws the new progress but is not an arrival, and stops a growth in progress.
        animations = true;
        morph.GrowTo(0);
        Assert.True(frames.Running);
        morph.JumpTo(1);
        Assert.False(frames.Running);
        Assert.Equal((1.0, 2, 1), (morph.Progress, changes, arrivals));

        // Disposed, it listens to nothing.
        morph.GrowTo(0);
        morph.Dispose();
        Assert.False(frames.Running);
        frames.Start();
        frames.Tick(T0);
        Assert.Equal(2, changes);
    }
}
