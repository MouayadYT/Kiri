using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Placement;

/// <summary>
/// Places overlays on the monitor that holds the foreground window, near the upper center of its work area, so they
/// never cover the taskbar. Positions are computed in physical pixels at the target monitor's DPI, so they hold across
/// monitors with different scaling and in any multi-monitor layout, including monitors left of or above the primary.
/// </summary>
/// <remarks>
/// Results depend on the process being per-monitor DPI aware (v2), as the application manifest declares. The service
/// does not depend on WPF.
/// </remarks>
public sealed class WindowPlacementService : IWindowPlacementService
{
    private readonly IPlacementNativeMethods _native;
    private readonly ILogger<WindowPlacementService> _logger;

    public WindowPlacementService(ILogger<WindowPlacementService> logger) : this(logger, new PlacementNativeMethods()) { }

    internal WindowPlacementService(ILogger<WindowPlacementService> logger, IPlacementNativeMethods native)
    {
        _logger = logger;
        _native = native;
    }

    /// <inheritdoc/>
    public WindowPlacement? PlaceOnActiveMonitor(nint window, OverlayLayout layout)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);
        if (FindActiveMonitor(window) is not (var monitor, var source))
        {
            PlacementLog.NoMonitor(_logger);
            return null;
        }

        return Move(window, monitor, source, OverlayPlacementCalculator.Calculate(monitor, layout));
    }

    public WindowPlacement? PlaceOnCursorMonitor(nint window, OverlayLayout layout)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);
        var monitor = _native.MonitorFromCursor();
        if (monitor is null && _native.GetPointer() is { } point) monitor = _native.MonitorNearest(point.X, point.Y);
        if (monitor is { } cursorMonitor)
            return Move(window, cursorMonitor, MonitorSource.Cursor, OverlayPlacementCalculator.Calculate(cursorMonitor, layout));
        return PlaceOnActiveMonitor(window, layout);
    }

    /// <inheritdoc/>
    public ScreenPoint? GetAnchorTop(nint window, OverlayLayout layout)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);
        if (_native.GetOrigin(window) is not { } origin ||
            (_native.MonitorFromWindow(window) ?? _native.MonitorNearest(origin.X, origin.Y)) is not { } monitor)
        {
            return null;
        }

        return OverlayPlacementCalculator.AnchorTop(monitor, layout, new ScreenPoint(origin.X, origin.Y));
    }

    /// <inheritdoc/>
    public WindowPlacement? PlaceAnchorTopAt(nint window, OverlayLayout layout, ScreenPoint point)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);
        if (_native.MonitorNearest(point.X, point.Y) is not { } monitor)
        {
            PlacementLog.NoMonitor(_logger);
            return null;
        }

        return Move(window, monitor, MonitorSource.Point, OverlayPlacementCalculator.CalculateAt(monitor, layout, point));
    }

    /// <inheritdoc/>
    public ScreenPoint? FitAnchorTop(OverlayLayout layout, ScreenPoint point)
    {
        if (_native.MonitorNearest(point.X, point.Y) is not { } monitor)
        {
            return null;
        }

        var bounds = OverlayPlacementCalculator.CalculateAt(monitor, layout, point);
        return OverlayPlacementCalculator.AnchorTop(monitor, layout, new ScreenPoint(bounds.Left, bounds.Top));
    }

    /// <inheritdoc/>
    public WindowPlacement? PlaceCenteredOnActiveMonitor(nint window, double width, double height, double margin)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);
        if (FindActiveMonitor(window) is not (var monitor, var source))
        {
            PlacementLog.NoMonitor(_logger);
            return null;
        }

        var bounds = OverlayPlacementCalculator.CalculateCentered(monitor, width, height, margin);
        var error = _native.SetBounds(window, bounds);

        // Arriving on a monitor with another DPI makes the window rescale itself, which may change its bounds as well.
        if (error == 0 && _native.GetBounds(window) is { } placed && placed != bounds)
        {
            error = _native.SetBounds(window, bounds);
        }

        if (error != 0)
        {
            PlacementLog.MoveFailed(_logger, error);
            return null;
        }

        PlacementLog.Placed(_logger, source, monitor.Dpi);
        return new WindowPlacement(monitor, source, bounds);
    }

    /// <inheritdoc/>
    public NearWindowTarget? DescribeForegroundWindow(int excludeProcessId)
    {
        var window = _native.GetForegroundWindow();
        if (window == 0 || _native.IsDesktop(window) || !_native.IsOnScreen(window))
        {
            return null;
        }

        var processId = _native.GetProcessId(window);
        if (processId == 0 || processId == excludeProcessId || _native.GetVisibleBounds(window) is not { } bounds)
        {
            return null;
        }

        return new NearWindowTarget(window, processId, bounds, _native.GetPointer());
    }

    /// <inheritdoc/>
    public ScreenPoint? FindAnchorTopNear(NearWindowTarget target, OverlayLayout layout)
    {
        ArgumentNullException.ThrowIfNull(target);
        var bounds = target.Bounds;
        if (_native.MonitorNearest(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2)) is not { } monitor)
        {
            PlacementLog.NoMonitor(_logger);
            return null;
        }

        return NearWindowCalculator.AnchorTop(monitor, bounds, target.Pointer, layout);
    }

    private WindowPlacement? Move(nint window, DisplayMonitor monitor, MonitorSource source, ScreenRect bounds)
    {
        var error = _native.Move(window, bounds.Left, bounds.Top);

        // A move onto a monitor with another DPI makes the window rescale itself, which may move it as well.
        if (error == 0 && _native.GetOrigin(window) is { } origin && origin != (bounds.Left, bounds.Top))
        {
            error = _native.Move(window, bounds.Left, bounds.Top);
        }

        if (error != 0)
        {
            PlacementLog.MoveFailed(_logger, error);
            return null;
        }

        PlacementLog.Placed(_logger, source, monitor.Dpi);
        return new WindowPlacement(monitor, source, bounds);
    }

    // The foreground window marks where the user is working, unless it is the overlay itself or the desktop, which
    // spans every monitor. The pointer is the next best sign, and the primary monitor the last resort.
    private (DisplayMonitor Monitor, MonitorSource Source)? FindActiveMonitor(nint overlay)
    {
        var foreground = _native.GetForegroundWindow();
        if (foreground != 0 && !_native.AreRelated(foreground, overlay) && !_native.IsDesktop(foreground) &&
            _native.MonitorFromWindow(foreground) is { } active)
        {
            return (active, MonitorSource.ForegroundWindow);
        }

        if (_native.MonitorFromCursor() is { } pointed)
        {
            return (pointed, MonitorSource.Cursor);
        }

        return _native.PrimaryMonitor() is { } primary ? (primary, MonitorSource.PrimaryMonitor) : null;
    }
}
