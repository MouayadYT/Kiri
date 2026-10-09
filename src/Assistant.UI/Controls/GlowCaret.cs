using System.Windows;
using System.Windows.Media;

namespace Assistant.UI.Controls;

/// <summary>
/// The look of the text insertion caret in the reference designs: a bright white core, thicker than a system caret,
/// in a soft glow that is cool blue toward its top and warm yellow toward its bottom. It is as tall as it is laid
/// out and as wide as its core; the glow spills past its bounds. <see cref="CaretTracker"/> places and blinks it.
/// </summary>
public sealed class GlowCaret : FrameworkElement
{
    /// <summary>Identifies the <see cref="CoreBrush"/> property.</summary>
    public static readonly DependencyProperty CoreBrushProperty = DependencyProperty.Register(
        nameof(CoreBrush), typeof(Brush), typeof(GlowCaret),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="UpperGlow"/> property.</summary>
    public static readonly DependencyProperty UpperGlowProperty = DependencyProperty.Register(
        nameof(UpperGlow), typeof(Color), typeof(GlowCaret),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender, OnGlowChanged));

    /// <summary>Identifies the <see cref="LowerGlow"/> property.</summary>
    public static readonly DependencyProperty LowerGlowProperty = DependencyProperty.Register(
        nameof(LowerGlow), typeof(Color), typeof(GlowCaret),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender, OnGlowChanged));

    /// <summary>Identifies the <see cref="GlowRadius"/> property.</summary>
    public static readonly DependencyProperty GlowRadiusProperty = DependencyProperty.Register(
        nameof(GlowRadius), typeof(double), typeof(GlowCaret),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender, OnGlowChanged));

    // The glow is drawn as nested rounded bands, each a little larger and fainter than the last, which together
    // fade smoothly from the core outward. Their brushes depend on the caret's height and are rebuilt when it changes.
    private const int Bands = 24;

    // The glow's opacity by distance from the caret's center, as a fraction of GlowRadius, measured from the reference.
    private static readonly (double Distance, double Alpha)[] Falloff =
    [
        (0, 0.9), (0.08, 0.82), (0.146, 0.58), (0.25, 0.4), (0.31, 0.32), (0.42, 0.18), (0.55, 0.1), (0.67, 0.06),
        (0.79, 0.03), (0.9, 0.012), (1, 0),
    ];

    // The glow wraps around the caret's top and bottom, reaching past them by these fractions of how far it reaches
    // to its sides, so both ends look round; a little further below, where it is warm, as in the reference.
    private const double TopReach = 0.8;
    private const double BottomReach = 0.9;

    private Brush[]? _bands;
    private double _bandsHeight = double.NaN;

    public GlowCaret()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>Fills the caret's core.</summary>
    public Brush? CoreBrush
    {
        get => (Brush?)GetValue(CoreBrushProperty);
        set => SetValue(CoreBrushProperty, value);
    }

    /// <summary>The glow's color around the upper part of the caret.</summary>
    public Color UpperGlow
    {
        get => (Color)GetValue(UpperGlowProperty);
        set => SetValue(UpperGlowProperty, value);
    }

    /// <summary>The glow's color around the lower part of the caret.</summary>
    public Color LowerGlow
    {
        get => (Color)GetValue(LowerGlowProperty);
        set => SetValue(LowerGlowProperty, value);
    }

    /// <summary>How far the glow reaches either side of the caret's center, in DIPs.</summary>
    public double GlowRadius
    {
        get => (double)GetValue(GlowRadiusProperty);
        set => SetValue(GlowRadiusProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        var (width, height) = (RenderSize.Width, RenderSize.Height);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_bands is null || _bandsHeight != height)
        {
            _bands = CreateBands(height);
            _bandsHeight = height;
        }

        var center = width / 2;
        for (var i = Bands; i >= 1; i--)
        {
            var side = GlowRadius * i / Bands;
            drawingContext.DrawGeometry(_bands[i - 1], null, Band(center, height, side));
        }

        // The core's ends are rounded over its own width, so it tapers rather than stopping square.
        drawingContext.DrawRoundedRectangle(CoreBrush, null, new Rect(0, 0, width, height), width / 2, Math.Min(width, height / 2));
    }

    // Each band blends from the upper color at the caret's top to the lower color at its bottom. Bands near the core
    // are paler, nearly white, as in the reference; their opacities compound to the measured falloff.
    private Brush[] CreateBands(double height)
    {
        var bands = new Brush[Bands];
        var outside = 0.0;
        for (var i = Bands; i >= 1; i--)
        {
            var within = FalloffAt((i - 0.5) / Bands);
            var alpha = Math.Clamp(1 - ((1 - within) / (1 - outside)), 0, 1);
            outside = within;
            var pale = 0.75 * Math.Max(0, 1 - ((i - 1) / (Bands * 0.42)));
            var brush = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, height * 0.15),
                EndPoint = new Point(0, height * 0.7),
                GradientStops =
                {
                    new GradientStop(WithAlpha(Mix(UpperGlow, Colors.White, pale), alpha), 0),
                    new GradientStop(WithAlpha(Mix(LowerGlow, Colors.White, pale), alpha), 1),
                },
            };
            brush.Freeze();
            bands[i - 1] = brush;
        }

        return bands;
    }

    // A band: straight along the caret's sides, with half-elliptical ends above and below it.
    private static Geometry Band(double center, double height, double side)
    {
        var (top, bottom) = (side * TopReach, side * BottomReach);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center - side, 0), isFilled: true, isClosed: true);
            context.ArcTo(new Point(center + side, 0), new Size(side, top), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(center + side, height), true, false);
            context.ArcTo(new Point(center - side, height), new Size(side, bottom), 0, false, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static double FalloffAt(double distance)
    {
        for (var i = 1; i < Falloff.Length; i++)
        {
            if (distance <= Falloff[i].Distance)
            {
                var (d0, a0) = Falloff[i - 1];
                var (d1, a1) = Falloff[i];
                return a0 + ((a1 - a0) * (distance - d0) / (d1 - d0));
            }
        }

        return 0;
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
        (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
        (byte)Math.Round(from.B + ((to.B - from.B) * amount)));

    private static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)Math.Round(255 * alpha), color.R, color.G, color.B);

    private static void OnGlowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var caret = (GlowCaret)d;
        caret._bands = null;
    }
}
