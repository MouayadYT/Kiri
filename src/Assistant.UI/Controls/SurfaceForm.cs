using System.Windows;
using System.Windows.Media;

namespace Assistant.UI.Controls;

/// <summary>
/// The form of the Assistant's glass at one moment: its size and how far and how squarely its corners reach. The Search
/// or Ask pill and the floating conversation panel are two forms of one surface, and every form between them is a blend
/// of the two, so the pill can grow into the panel without ever being two shapes.
/// </summary>
/// <param name="Width">Width of the surface, in DIPs.</param>
/// <param name="Height">Height of the surface, in DIPs.</param>
/// <param name="CornerWidth">How far each corner reaches along the horizontal edges.</param>
/// <param name="CornerHeight">How far each corner reaches along the vertical edges.</param>
/// <param name="ControlRatio">
/// How square the corners are: the fraction of the way from each end of a corner toward the rectangle's corner where
/// its Bézier control points lie.
/// </param>
public sealed record SurfaceForm(double Width, double Height, double CornerWidth, double CornerHeight, double ControlRatio)
{
    /// <summary>The Search or Ask pill: ends that are superellipses rather than half circles (<see cref="PillShape"/>).</summary>
    public static SurfaceForm Pill(double width, double height) =>
        new(width, height, PillShape.GetEndLength(new Size(width, height)), height / 2, PillShape.ControlPointRatio);

    /// <summary>A panel with the smooth, squarish corners of the floating conversation (<see cref="PanelShape"/>).</summary>
    public static SurfaceForm Panel(double width, double height, double cornerSize) =>
        new(width, height, cornerSize, cornerSize, PanelShape.ControlPointRatio);

    /// <summary>
    /// The form <paramref name="amount"/> of the way from <paramref name="from"/> to <paramref name="to"/>: 0 is
    /// <paramref name="from"/> and 1 is <paramref name="to"/>.
    /// </summary>
    public static SurfaceForm Blend(SurfaceForm from, SurfaceForm to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return amount switch
        {
            0 => from,
            1 => to,
            _ => new SurfaceForm(
                Along(from.Width, to.Width, amount), Along(from.Height, to.Height, amount),
                Along(from.CornerWidth, to.CornerWidth, amount), Along(from.CornerHeight, to.CornerHeight, amount),
                Along(from.ControlRatio, to.ControlRatio, amount)),
        };
    }

    /// <summary>
    /// The form <paramref name="amount"/> of the way from <paramref name="from"/> to <paramref name="to"/>, where the amount may go a little past
    /// either end, as a spring does: the form then overshoots, larger or smaller than both.
    /// </summary>
    public static SurfaceForm Interpolate(SurfaceForm from, SurfaceForm to, double amount)
    {
        if (amount == 0)
        {
            return from;
        }

        if (amount == 1)
        {
            return to;
        }

        return new SurfaceForm(
            Math.Max(1, Along(from.Width, to.Width, amount)), Math.Max(1, Along(from.Height, to.Height, amount)),
            Math.Max(0, Along(from.CornerWidth, to.CornerWidth, amount)), Math.Max(0, Along(from.CornerHeight, to.CornerHeight, amount)),
            Math.Clamp(Along(from.ControlRatio, to.ControlRatio, amount), 0, 1));
    }

    /// <summary>The outline of a surface of this form that fills <paramref name="bounds"/>.</summary>
    public Geometry CreateGeometry(Rect bounds) =>
        SmoothCorners.Create(bounds, Math.Min(CornerWidth, bounds.Width / 2), Math.Min(CornerHeight, bounds.Height / 2), ControlRatio);

    /// <summary>
    /// The corner radii, in DIPs, of the rectangle with elliptical corners that lies closest to a surface of this form and
    /// <paramref name="size"/>: the blurred backdrop takes this shape, and so follows the glass to within a fraction of a pixel
    /// (<see cref="SmoothCorners.FittedEllipseShare"/>).
    /// </summary>
    public Size GetBackdropCornerRadii(Size size)
    {
        var share = SmoothCorners.FittedEllipseShare(ControlRatio);
        return new(Math.Min(CornerWidth, size.Width / 2) * share, Math.Min(CornerHeight, size.Height / 2) * share);
    }

    private static double Along(double from, double to, double amount) => from + ((to - from) * amount);
}
