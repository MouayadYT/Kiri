namespace Assistant.Windows.Placement;

/// <summary>
/// Computes where an overlay goes beside another application's window: inside the window, against its right edge, below its
/// toolbar, and out of the way of the pointer. Pure arithmetic, with no Win32 calls.
/// </summary>
internal static class NearWindowCalculator
{
    /// <summary>How far, in DIPs, the overlay's side edge stands in from the window's side edge.</summary>
    public const double SideInset = 24;

    /// <summary>
    /// How far, in DIPs, the overlay's top edge is below the window's top edge: past the tab strip and the toolbar of a browser, so the
    /// page, and not the address bar, is what the overlay lies over.
    /// </summary>
    public const double TopInset = 96;

    /// <summary>The least room, in DIPs, to leave under the overlay inside the window when the window is tall enough.</summary>
    public const double BottomInset = 16;

    /// <summary>
    /// How near, in DIPs, the pointer may be to the overlay before it goes to the other side. What the user pointed at, such as the text
    /// they selected, must stay readable, and the entry they chose in a menu lies a little off it.
    /// </summary>
    public const double PointerClearance = 64;

    /// <summary>
    /// Returns where the top center of the layout's anchor should go, in physical pixels, for an overlay beside
    /// <paramref name="window"/>: the right side of the part of the window that is on <paramref name="monitor"/>, or the left side when the
    /// pointer is near the right one. A window narrower than the anchor and its insets gets the anchor centered on it. The point may lie
    /// outside the monitor's work area (a short window); placement brings the anchor inside.
    /// </summary>
    /// <returns>The point, or <see langword="null"/> when no part of the window is on the monitor's work area.</returns>
    public static ScreenPoint? AnchorTop(DisplayMonitor monitor, ScreenRect window, ScreenPoint? pointer, OverlayLayout layout)
    {
        var scale = monitor.Scale;
        var work = monitor.WorkArea;
        var left = Math.Max(window.Left, work.Left);
        var top = Math.Max(window.Top, work.Top);
        var right = Math.Min(window.Right, work.Right);
        var bottom = Math.Min(window.Bottom, work.Bottom);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        var width = layout.AnchorWidth * scale;
        var height = layout.AnchorHeight * scale;
        var inset = SideInset * scale;

        // Below the toolbar, but never so low that the overlay leaves the window's bottom when it would fit above that.
        var y = Math.Min(top + (TopInset * scale), bottom - (BottomInset * scale) - height);
        y = Math.Max(y, top);

        double x;
        if (right - left < width + (2 * inset))
        {
            x = left + ((right - left - width) / 2);
        }
        else
        {
            x = ChooseSide(right - inset - width, left + inset, width, height, y, pointer, PointerClearance * scale);
        }

        return new ScreenPoint(
            (int)Math.Round(x + (width / 2), MidpointRounding.AwayFromZero), (int)Math.Round(y, MidpointRounding.AwayFromZero));
    }

    // The right side, unless the pointer is near it and not near the left; when it is near both, whichever the pointer is farther from.
    private static double ChooseSide(
        double onRight, double onLeft, double width, double height, double top, ScreenPoint? pointer, double clearance)
    {
        if (pointer is not { } at)
        {
            return onRight;
        }

        var nearRight = IsNear(onRight, width, height, top, at, clearance);
        if (!nearRight)
        {
            return onRight;
        }

        if (!IsNear(onLeft, width, height, top, at, clearance))
        {
            return onLeft;
        }

        return Math.Abs(at.X - (onRight + (width / 2))) >= Math.Abs(at.X - (onLeft + (width / 2))) ? onRight : onLeft;
    }

    // Whether the pointer is within the clearance of the rectangle that starts at (left, top).
    private static bool IsNear(double left, double width, double height, double top, ScreenPoint pointer, double clearance) =>
        pointer.X >= left - clearance && pointer.X <= left + width + clearance &&
        pointer.Y >= top - clearance && pointer.Y <= top + height + clearance;
}
