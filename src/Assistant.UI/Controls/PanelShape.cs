using System.Windows;
using System.Windows.Media;

namespace Assistant.UI.Controls;

/// <summary>
/// A tall surface with the smooth, continuous-curvature corners of the floating conversation reference. It fills its
/// layout slot, and its stroke is drawn inside the bounds.
/// </summary>
public sealed class PanelShape : GlassShape
{
    /// <summary>Identifies the <see cref="CornerSize"/> property.</summary>
    public static readonly DependencyProperty CornerSizeProperty = DependencyProperty.Register(
        nameof(CornerSize), typeof(double), typeof(PanelShape),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender), IsValidCornerSize);

    // Measured from the floating conversation reference: a single Bézier per corner with its control points at this
    // fraction of the corner, squarer than a quarter circle, matches its corners to within half a pixel at 2x.
    internal const double ControlPointRatio = 0.725;

    /// <summary>How far each corner reaches along the edges, in DIPs.</summary>
    public double CornerSize
    {
        get => (double)GetValue(CornerSizeProperty);
        set => SetValue(CornerSizeProperty, value);
    }

    /// <summary>
    /// Gets the corner radii of the rectangle with elliptical corners that lies closest to a panel of <paramref name="size"/> with
    /// <paramref name="cornerSize"/> corners: what the blur behind the panel is clipped to. It meets the panel along its straight edges and follows
    /// its corners to within about 1% of their reach (<see cref="SmoothCorners.FittedEllipseShare"/>).
    /// </summary>
    public static Size GetBackdropCornerRadii(Size size, double cornerSize)
    {
        var share = SmoothCorners.FittedEllipseShare(ControlPointRatio);
        return new(Math.Min(cornerSize, size.Width / 2) * share, Math.Min(cornerSize, size.Height / 2) * share);
    }

    /// <summary>Creates the panel outline that fills <paramref name="bounds"/>.</summary>
    public static Geometry CreateGeometry(Rect bounds, double cornerSize) =>
        SmoothCorners.Create(bounds, Math.Min(cornerSize, bounds.Width / 2), Math.Min(cornerSize, bounds.Height / 2),
            ControlPointRatio);

    /// <inheritdoc/>
    protected override Geometry CreateOutline(Rect bounds) => CreateGeometry(bounds, CornerSize);

    private static bool IsValidCornerSize(object value) => value is double size && size >= 0 && double.IsFinite(size);
}
