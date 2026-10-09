namespace Assistant.Windows.Backdrop;

/// <summary>
/// The part of a window to blur: a rectangle with elliptical corners, in physical pixels relative to the window's
/// top-left corner. Values may be fractional, so the blur can line up with anti-aliased content.
/// </summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
/// <param name="CornerRadiusX">Horizontal radius of each corner.</param>
/// <param name="CornerRadiusY">Vertical radius of each corner.</param>
public readonly record struct BackdropRegion(
    double X, double Y, double Width, double Height, double CornerRadiusX, double CornerRadiusY)
{
    /// <summary>Whether the region covers no area.</summary>
    public bool IsEmpty => !(Width > 0 && Height > 0);
}
