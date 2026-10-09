namespace Assistant.Windows.Placement;

/// <summary>Computes where an overlay goes on a monitor. Pure arithmetic, with no Win32 calls.</summary>
internal static class OverlayPlacementCalculator
{
    /// <summary>
    /// Returns the window bounds, at the monitor's DPI, that center the layout's anchor horizontally in the monitor's
    /// work area, with the anchor's top edge at the layout's vertical position. The whole window stays inside the work
    /// area when it fits. Otherwise the anchor does, and when even the anchor is too big, its top-left corner does.
    /// </summary>
    public static ScreenRect Calculate(DisplayMonitor monitor, OverlayLayout layout)
    {
        Validate(layout);
        var scale = monitor.Scale;
        var work = monitor.WorkArea;

        // Round the size up, so a partly covered pixel still counts as part of the window.
        var width = (int)Math.Ceiling(layout.Width * scale);
        var height = (int)Math.Ceiling(layout.Height * scale);
        var anchorLeft = layout.AnchorLeft * scale;
        var anchorTop = layout.AnchorTop * scale;
        var anchorWidth = layout.AnchorWidth * scale;
        var anchorHeight = layout.AnchorHeight * scale;

        var left = work.Left + ((work.Width - anchorWidth) / 2) - anchorLeft;
        var top = work.Top + (work.Height * layout.VerticalPosition) - anchorTop;
        var x = Fit(left, width, anchorLeft, anchorWidth, work.Left, work.Right);
        var y = Fit(top, height, anchorTop, anchorHeight, work.Top, work.Bottom);
        return new ScreenRect(x, y, x + width, y + height);
    }

    /// <summary>
    /// Returns the window bounds, at the monitor's DPI, that put the top center of the layout's anchor at
    /// <paramref name="point"/>, moved as little as keeps the whole anchor inside the monitor's work area. When the
    /// anchor is too big for it, its top-left corner stays inside.
    /// </summary>
    public static ScreenRect CalculateAt(DisplayMonitor monitor, OverlayLayout layout, ScreenPoint point)
    {
        Validate(layout);
        var scale = monitor.Scale;
        var work = monitor.WorkArea;
        var width = (int)Math.Ceiling(layout.Width * scale);
        var height = (int)Math.Ceiling(layout.Height * scale);
        var anchorLeft = layout.AnchorLeft * scale;
        var anchorTop = layout.AnchorTop * scale;
        var anchorWidth = layout.AnchorWidth * scale;
        var anchorHeight = layout.AnchorHeight * scale;

        var x = FitAnchor(point.X - anchorLeft - (anchorWidth / 2), anchorLeft, anchorWidth, work.Left, work.Right);
        var y = FitAnchor(point.Y - anchorTop, anchorTop, anchorHeight, work.Top, work.Bottom);
        return new ScreenRect(x, y, x + width, y + height);
    }

    /// <summary>
    /// Returns where the top center of the layout's anchor is for a window whose top-left corner is at
    /// <paramref name="origin"/>, sized for the monitor's DPI.
    /// </summary>
    public static ScreenPoint AnchorTop(DisplayMonitor monitor, OverlayLayout layout, ScreenPoint origin)
    {
        Validate(layout);
        var scale = monitor.Scale;
        var x = origin.X + ((layout.AnchorLeft + (layout.AnchorWidth / 2)) * scale);
        var y = origin.Y + (layout.AnchorTop * scale);
        return new ScreenPoint(
            (int)Math.Round(x, MidpointRounding.AwayFromZero), (int)Math.Round(y, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Returns the window bounds, at the monitor's DPI, of a window of <paramref name="width"/> by
    /// <paramref name="height"/> DIPs centered in the monitor's work area, shrunk as far as leaves
    /// <paramref name="margin"/> DIPs around it; a work area too small even for that is filled.
    /// </summary>
    public static ScreenRect CalculateCentered(DisplayMonitor monitor, double width, double height, double margin)
    {
        if (!IsLength(width) || !IsLength(height) || !IsLength(margin))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Window sizes and margins must be finite and non-negative.");
        }

        var scale = monitor.Scale;
        var work = monitor.WorkArea;
        var room = (int)Math.Round(margin * scale, MidpointRounding.AwayFromZero);
        var w = Math.Max(0, Math.Min((int)Math.Ceiling(width * scale), work.Width - (2 * room)));
        var h = Math.Max(0, Math.Min((int)Math.Ceiling(height * scale), work.Height - (2 * room)));
        if (w == 0) w = work.Width;
        if (h == 0) h = work.Height;
        var x = work.Left + ((work.Width - w) / 2);
        var y = work.Top + ((work.Height - h) / 2);
        return new ScreenRect(x, y, x + w, y + h);
    }

    // Rounds a window edge to a whole pixel inside the range that keeps the window, or else its anchor, in the area.
    private static int Fit(double position, int size, double anchorOffset, double anchorSize, int areaStart, int areaEnd)
    {
        return size <= areaEnd - areaStart
            ? Clamp(position, areaStart, areaEnd - size)
            : FitAnchor(position, anchorOffset, anchorSize, areaStart, areaEnd);
    }

    // Rounds a window edge to a whole pixel inside the range that keeps its anchor in the area.
    private static int FitAnchor(double position, double anchorOffset, double anchorSize, int areaStart, int areaEnd) =>
        Clamp(position, (int)Math.Ceiling(areaStart - anchorOffset), (int)Math.Floor(areaEnd - anchorOffset - anchorSize));

    // Checking the minimum last favors the leading edge when nothing fits.
    private static int Clamp(double position, int min, int max) =>
        Math.Max(min, Math.Min((int)Math.Round(position, MidpointRounding.AwayFromZero), max));

    private static void Validate(OverlayLayout layout)
    {
        if (!IsLength(layout.Width) || !IsLength(layout.Height) ||
            !IsLength(layout.AnchorWidth) || !IsLength(layout.AnchorHeight) ||
            !double.IsFinite(layout.AnchorLeft) || !double.IsFinite(layout.AnchorTop) ||
            layout.VerticalPosition is not (>= 0 and <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(layout), "Overlay sizes must be finite and non-negative, " +
                "and the vertical position must be between 0 and 1.");
        }
    }

    private static bool IsLength(double value) => double.IsFinite(value) && value >= 0;
}
