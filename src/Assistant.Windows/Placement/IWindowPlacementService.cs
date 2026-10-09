namespace Assistant.Windows.Placement;

/// <summary>Places overlay windows on the monitor where the user is working.</summary>
/// <remarks>Call it on the thread that owns the window.</remarks>
public interface IWindowPlacementService
{
    /// <summary>
    /// Moves a top-level window near the upper center of the work area of the monitor that holds the foreground
    /// window, sized for that monitor's DPI. The window keeps its size, z-order and activation. Call it while the
    /// window is hidden, so a move onto a monitor with another DPI finishes rescaling before the window is seen.
    /// </summary>
    /// <param name="window">The overlay's window handle.</param>
    /// <param name="layout">The overlay's size and the part of it to line up.</param>
    /// <returns>Where the window was placed, or <see langword="null"/> if it could not be moved.</returns>
    WindowPlacement? PlaceOnActiveMonitor(nint window, OverlayLayout layout);

    /// <summary>Places the overlay on the mouse pointer's monitor, regardless of the foreground window.</summary>
    WindowPlacement? PlaceOnCursorMonitor(nint window, OverlayLayout layout) => PlaceOnActiveMonitor(window, layout);

    /// <summary>
    /// Gets where the top center of the layout's anchor is on screen now, such as after the user dragged the window.
    /// </summary>
    /// <param name="window">The overlay's window handle.</param>
    /// <param name="layout">The overlay's size and its anchor.</param>
    /// <returns>The point, or <see langword="null"/> if the window's position is unavailable.</returns>
    ScreenPoint? GetAnchorTop(nint window, OverlayLayout layout);

    /// <summary>
    /// Moves a top-level window so the top center of the layout's anchor is at <paramref name="point"/>, as near as
    /// the work area of the monitor nearest that point allows: the whole anchor stays inside it, so it can always be
    /// reached. The window is sized for that monitor's DPI and keeps its size, z-order and activation.
    /// </summary>
    /// <param name="window">The overlay's window handle.</param>
    /// <param name="layout">The overlay's size and its anchor.</param>
    /// <param name="point">Where the anchor's top center goes.</param>
    /// <returns>Where the window was placed, or <see langword="null"/> if it could not be moved.</returns>
    WindowPlacement? PlaceAnchorTopAt(nint window, OverlayLayout layout, ScreenPoint point);

    /// <summary>
    /// Gets where the top center of the layout's anchor would end up if <see cref="PlaceAnchorTopAt"/> were called
    /// with the same layout and point, without moving anything. It lets a surface that is about to grow know how far
    /// it must move to stay on screen.
    /// </summary>
    /// <param name="layout">The overlay's size and its anchor, as it will be.</param>
    /// <param name="point">Where the anchor's top center is wanted.</param>
    /// <returns>The point, or <see langword="null"/> if no monitor was found.</returns>
    ScreenPoint? FitAnchorTop(OverlayLayout layout, ScreenPoint point);

    /// <summary>
    /// Sizes a top-level window to <paramref name="width"/> by <paramref name="height"/> DIPs, or as much of that as
    /// leaves <paramref name="margin"/> DIPs free around it, at the DPI of the monitor that holds the foreground window,
    /// and centers it in that monitor's work area. The window keeps its z-order and activation. Call it while the window
    /// is hidden.
    /// </summary>
    /// <param name="window">The window handle.</param>
    /// <param name="width">The width the window prefers.</param>
    /// <param name="height">The height the window prefers.</param>
    /// <param name="margin">The least room to leave between the window and the work area's edges.</param>
    /// <returns>Where the window was placed, or <see langword="null"/> if it could not be moved.</returns>
    WindowPlacement? PlaceCenteredOnActiveMonitor(nint window, double width, double height, double margin);

    /// <summary>
    /// Notes the window the user is working in, for an overlay to open beside it: the foreground window, unless it belongs to
    /// <paramref name="excludeProcessId"/> (the overlay's own application), is the shell's desktop, is minimized or is not visible.
    /// Safe to call from any thread; call it at the moment of the user's act, since the foreground changes.
    /// </summary>
    /// <param name="excludeProcessId">The id of the process whose windows do not count.</param>
    /// <returns>The window and the pointer as they are, or <see langword="null"/> when there is no such window.</returns>
    NearWindowTarget? DescribeForegroundWindow(int excludeProcessId);

    /// <summary>
    /// Gets where the top center of the layout's anchor should go to open beside <paramref name="target"/>: inside the window, against
    /// its right edge below its toolbar, or its left edge when the pointer is near the right one (see <c>NearWindowCalculator</c>). The
    /// monitor is the one holding the middle of the window, and the point is in its physical pixels. Nothing is moved.
    /// </summary>
    /// <param name="target">The window to open beside.</param>
    /// <param name="layout">The overlay's size and its anchor.</param>
    /// <returns>The point for <see cref="PlaceAnchorTopAt"/>, or <see langword="null"/> when the window is on no monitor.</returns>
    ScreenPoint? FindAnchorTopNear(NearWindowTarget target, OverlayLayout layout);
}
