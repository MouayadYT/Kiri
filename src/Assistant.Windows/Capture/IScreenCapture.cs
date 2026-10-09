using Assistant.Windows.Placement;

namespace Assistant.Windows.Capture;

/// <summary>
/// Takes pictures of the screen (PROJECT_SPEC §4.6, §5.4), on this PC and in memory only: a monitor, a window, or a rectangle of
/// the virtual screen. Whatever asks must be answering something the user did and have been allowed to capture the screen
/// (<c>PermissionCapability.ScreenCapture</c>): the service does not ask, and nothing is ever saved or logged.
/// </summary>
/// <remarks>
/// Coordinates are physical pixels of the virtual screen, in the DPI awareness of the process, which the application manifest
/// makes per-monitor v2, so they agree with <see cref="CaptureMonitor"/> and <see cref="IWindowPlacementService"/>.
/// </remarks>
public interface IScreenCapture
{
    /// <summary>The monitors as they are now, from left to right.</summary>
    IReadOnlyList<CaptureMonitor> GetMonitors();

    /// <summary>Captures the whole of <paramref name="monitor"/>.</summary>
    /// <exception cref="ScreenCaptureException">The monitor is gone or Windows refused.</exception>
    Task<CapturedImage> CaptureMonitorAsync(CaptureMonitor monitor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures every monitor at once, one picture for each, in the order of <see cref="GetMonitors"/>: a snapshot of what is
    /// on the screen now, which the Visual Intelligence overlay shows dimmed while the user chooses what to ask about.
    /// </summary>
    /// <exception cref="ScreenCaptureException">There is no monitor or Windows refused.</exception>
    Task<IReadOnlyList<CapturedImage>> CaptureAllMonitorsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures one window as it draws itself, even when other windows cover it or part of it is off the screen, and without the
    /// invisible border Windows gives it. When the window cannot draw itself for the capture, what is on screen where it is is
    /// captured instead (<see cref="CaptureMethod.ScreenCopy"/>).
    /// </summary>
    /// <param name="window">The window's handle.</param>
    /// <exception cref="ScreenCaptureException">The window is gone or minimized, or Windows refused.</exception>
    Task<CapturedImage> CaptureWindowAsync(nint window, CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures a rectangle of the virtual screen, however many monitors it crosses. The part that is outside the virtual screen
    /// is left out, so the picture may be smaller than <paramref name="region"/> (see <see cref="CapturedImage.Bounds"/>).
    /// </summary>
    /// <exception cref="ScreenCaptureException">No part of the region is on the screen, it is too large, or Windows refused.</exception>
    Task<CapturedImage> CaptureRegionAsync(ScreenRect region, CancellationToken cancellationToken = default);
}
