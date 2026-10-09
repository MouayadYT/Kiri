using System.Windows;
using System.Windows.Media;

namespace Assistant.UI.Controls;

/// <summary>
/// The glass of an <see cref="ActivityChip"/>: a pill that, at its narrowest, is the reference's glass orb, a dark
/// sphere over a light lower half divided by a bright, curved highlight, and, as it widens, is the chip's plain vertical
/// glass. It fills its layout slot, and its stroke is drawn inside the bounds.
/// </summary>
public sealed class ChipGlass : GlassShape
{
    /// <summary>Identifies the <see cref="Orb"/> property.</summary>
    public static readonly DependencyProperty OrbProperty = DependencyProperty.Register(
        nameof(Orb), typeof(double), typeof(ChipGlass),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // The orb, in fractions of the pill's height, measured from the orb reference: a dark top, then, half way down,
    // a bright edge that curves down toward the middle (the highlight), and a lighter lower half that is brightest at
    // its center.
    private static readonly Brush OrbBody = CreateOrbBody();
    private static readonly Brush OrbLowerLight = CreateOrbLowerLight();
    private static readonly Brush OrbHighlight = CreateOrbHighlight();

    /// <summary>How much of the orb's look shows, from 0 (the chip's plain glass) to 1 (the orb alone).</summary>
    public double Orb
    {
        get => (double)GetValue(OrbProperty);
        set => SetValue(OrbProperty, value);
    }

    /// <inheritdoc/>
    protected override Geometry CreateOutline(Rect bounds) => PillShape.CreateGeometry(bounds);

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var orb = Math.Clamp(Orb, 0, 1);
        var bounds = new Rect(RenderSize);
        if (orb <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var (width, height) = (bounds.Width, bounds.Height);
        drawingContext.PushClip(CreateOutline(bounds));
        drawingContext.PushOpacity(orb);

        drawingContext.DrawRectangle(OrbBody, null, bounds);

        // The lower half is brightest at its center, as a lens is.
        drawingContext.DrawRectangle(OrbLowerLight, null, new Rect(0, height * 0.5, width, height * 0.5));

        // The highlight is a smile-shaped band across the middle, brightest at its center and fading out toward its
        // ends: a soft wide one and a fine bright one on it.
        var smile = CreateSmile(width, height);
        drawingContext.DrawGeometry(null, new Pen(OrbHighlight, height * 0.13) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, smile);
        drawingContext.DrawGeometry(null, new Pen(OrbHighlight, height * 0.05) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, smile);

        drawingContext.Pop();
        drawingContext.Pop();
    }

    // The curved bright edge: an arc of an ellipse as wide as the pill whose lowest point is 60 % of the way down.
    private static Geometry CreateSmile(double width, double height)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(width * 0.06, height * 0.5), isFilled: false, isClosed: false);
            context.ArcTo(new Point(width * 0.94, height * 0.5), new Size(width * 0.5, height * 0.2), 0, false,
                SweepDirection.Counterclockwise, true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Brush CreateOrbBody()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        AddStops(brush,
            (0.0, 0x23), (0.10, 0x15), (0.30, 0x23), (0.42, 0x3C), (0.48, 0x79), (0.505, 0xA8), (0.54, 0x6E),
            (0.62, 0x66), (0.75, 0x8F), (0.90, 0xA2), (1.0, 0x8A));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateOrbLowerLight()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.55), GradientOrigin = new Point(0.5, 0.55), RadiusX = 0.55, RadiusY = 0.9,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x4A, 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 1));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateOrbHighlight()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), MappingMode = BrushMappingMode.RelativeToBoundingBox };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, 255, 255, 255), 0.22));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, 255, 255, 255), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, 255, 255, 255), 0.78));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 1));
        brush.Freeze();
        return brush;
    }

    private static void AddStops(GradientBrush brush, params (double Offset, byte Gray)[] stops)
    {
        foreach (var (offset, gray) in stops)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(gray, gray, gray), offset));
        }
    }
}
