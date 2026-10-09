using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Assistant.UI.Controls;

/// <summary>
/// A filled, outlined surface shape that fills its layout slot, such as a pill or a panel. The stroke is drawn inside
/// the bounds, and the fill reaches the edge, so a translucent stroke never shows what is behind the shape.
/// </summary>
public abstract class GlassShape : FrameworkElement
{
    /// <summary>Identifies the <see cref="Fill"/> property.</summary>
    public static readonly DependencyProperty FillProperty = Shape.FillProperty.AddOwner(
        typeof(GlassShape), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="Stroke"/> property.</summary>
    public static readonly DependencyProperty StrokeProperty = Shape.StrokeProperty.AddOwner(
        typeof(GlassShape), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="StrokeThickness"/> property.</summary>
    public static readonly DependencyProperty StrokeThicknessProperty = Shape.StrokeThicknessProperty.AddOwner(
        typeof(GlassShape), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The brush that paints the inside of the shape.</summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The brush that paints the outline.</summary>
    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Width of the outline, drawn inside the bounds.</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>Creates the shape's outline filling <paramref name="bounds"/>.</summary>
    protected abstract Geometry CreateOutline(Rect bounds);

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        var bounds = new Rect(RenderSize);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        drawingContext.DrawGeometry(Fill, null, CreateOutline(bounds));

        var stroke = Stroke;
        var inset = StrokeThickness / 2;
        bounds.Inflate(-inset, -inset);
        if (stroke is not null && bounds.Width > 0 && bounds.Height > 0)
        {
            drawingContext.DrawGeometry(null, new Pen(stroke, StrokeThickness), CreateOutline(bounds));
        }
    }
}

/// <summary>
/// Outlines with continuous-curvature corners, the smooth corners of the reference designs: each corner is one cubic
/// Bézier that leaves the straight edges tangentially.
/// </summary>
internal static class SmoothCorners
{
    // The control ratio that draws a quarter ellipse.
    private const double EllipseRatio = 0.5523;

    /// <summary>
    /// How much of a corner's reach an elliptical corner should have to lie as close as it can to a smooth one drawn with
    /// <paramref name="controlRatio"/>. The blur behind the glass can only be clipped to elliptical corners. A corner squarer than a quarter ellipse
    /// goes nearer to the rectangle's corner than an ellipse of the same reach does, which left a crescent of glass with no blur behind it in every
    /// corner (9% of the reach wide for the panel: four pixels). An ellipse of a smaller radius follows the smooth corner to within about 1% of the
    /// reach, a little inside it in the middle and a little outside it at its ends. The radius is fitted to the corners the designs use (found by
    /// trying every radius for each ratio); it is all of the reach for a quarter ellipse.
    /// </summary>
    public static double FittedEllipseShare(double controlRatio)
    {
        var squarer = Math.Max(0, controlRatio - EllipseRatio);
        return Math.Clamp(1 - (1.0591 * squarer) - (0.3014 * squarer * squarer), 0.5, 1);
    }

    /// <summary>
    /// Creates a rectangle outline whose corners reach <paramref name="cornerWidth"/> along the horizontal edges and
    /// <paramref name="cornerHeight"/> along the vertical ones. Each corner's control points lie
    /// <paramref name="controlRatio"/> of the way from its ends toward the rectangle's corner: 0.5523 draws a quarter
    /// ellipse, and larger values draw squarer corners.
    /// </summary>
    public static Geometry Create(Rect bounds, double cornerWidth, double cornerHeight, double controlRatio)
    {
        var (left, top, right, bottom) = (bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        var controlX = cornerWidth * controlRatio;
        var controlY = cornerHeight * controlRatio;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(left + cornerWidth, top), isFilled: true, isClosed: true);
            Edge(context, new Point(left + cornerWidth, top), new Point(right - cornerWidth, top));
            context.BezierTo(new Point(right - cornerWidth + controlX, top), new Point(right, top + cornerHeight - controlY), new Point(right, top + cornerHeight), true, true);
            Edge(context, new Point(right, top + cornerHeight), new Point(right, bottom - cornerHeight));
            context.BezierTo(new Point(right, bottom - cornerHeight + controlY), new Point(right - cornerWidth + controlX, bottom), new Point(right - cornerWidth, bottom), true, true);
            Edge(context, new Point(right - cornerWidth, bottom), new Point(left + cornerWidth, bottom));
            context.BezierTo(new Point(left + cornerWidth - controlX, bottom), new Point(left, bottom - cornerHeight + controlY), new Point(left, bottom - cornerHeight), true, true);
            Edge(context, new Point(left, bottom - cornerHeight), new Point(left, top + cornerHeight));
            context.BezierTo(new Point(left, top + cornerHeight - controlY), new Point(left + cornerWidth - controlX, top), new Point(left + cornerWidth, top), true, true);
        }

        geometry.Freeze();
        return geometry;

        // Corners that meet end to end, like the ends of a pill, leave no straight edge to draw between them.
        static void Edge(StreamGeometryContext context, Point from, Point to)
        {
            if (from != to)
            {
                context.LineTo(to, isStroked: true, isSmoothJoin: true);
            }
        }
    }
}
