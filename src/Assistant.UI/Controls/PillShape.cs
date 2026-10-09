using System.Windows;
using System.Windows.Media;

namespace Assistant.UI.Controls;

/// <summary>
/// A capsule whose ends are superellipses rather than half circles, giving the smooth, continuous-curvature ends of
/// the reference designs. It fills its layout slot, and its stroke is drawn inside the bounds.
/// </summary>
public sealed class PillShape : GlassShape
{
    // Measured from the Search or Ask reference: each end reaches 0.6 of the height along the straight edges and
    // follows a superellipse with exponent 2.5. One cubic Bézier per quadrant, with control points at this fraction
    // of the quadrant, matches that curve.
    private const double EndLengthRatio = 0.6;
    internal const double ControlPointRatio = 0.6877;

    /// <summary>
    /// Gets the corner radii of the rectangle with elliptical corners that lies closest to a pill of <paramref name="size"/>: what the blur behind
    /// the pill is clipped to. It follows the pill's ends to within about 1% of their reach (<see cref="SmoothCorners.FittedEllipseShare"/>).
    /// </summary>
    public static Size GetBackdropCornerRadii(Size size)
    {
        var share = SmoothCorners.FittedEllipseShare(ControlPointRatio);
        return new(GetEndLength(size) * share, size.Height / 2 * share);
    }

    /// <summary>Creates the pill outline that fills <paramref name="bounds"/>.</summary>
    public static Geometry CreateGeometry(Rect bounds) =>
        SmoothCorners.Create(bounds, GetEndLength(bounds.Size), bounds.Height / 2, ControlPointRatio);

    /// <inheritdoc/>
    protected override Geometry CreateOutline(Rect bounds) => CreateGeometry(bounds);

    // How far each end reaches along the straight edges.
    internal static double GetEndLength(Size size) => Math.Min(size.Height * EndLengthRatio, size.Width / 2);
}
