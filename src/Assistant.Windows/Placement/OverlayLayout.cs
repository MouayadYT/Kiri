namespace Assistant.Windows.Placement;

/// <summary>
/// The shape of an overlay window to place, in device-independent pixels (1/96 inch): the window's size, and its
/// anchor, the visible part that placement lines up. For a surface inside a transparent shadow gutter, the anchor
/// is the surface, so the surface rather than its shadow is what appears centered.
/// </summary>
/// <param name="Width">Width of the window.</param>
/// <param name="Height">Height of the window.</param>
/// <param name="AnchorLeft">Distance from the window's left edge to the anchor's.</param>
/// <param name="AnchorTop">Distance from the window's top edge to the anchor's.</param>
/// <param name="AnchorWidth">Width of the anchor.</param>
/// <param name="AnchorHeight">Height of the anchor.</param>
/// <param name="VerticalPosition">
/// Where the anchor's top edge goes, as a fraction of the work area's height measured from its top: 0 is the top
/// edge and 1 the bottom edge.
/// </param>
public readonly record struct OverlayLayout(
    double Width, double Height,
    double AnchorLeft, double AnchorTop, double AnchorWidth, double AnchorHeight,
    double VerticalPosition);
