namespace Assistant.Windows.Placement;

/// <summary>How the monitor for an overlay was chosen, from most to least preferred.</summary>
public enum MonitorSource
{
    /// <summary>The monitor that holds the foreground window, where the user is working.</summary>
    ForegroundWindow,

    /// <summary>
    /// The monitor under the mouse pointer, used when no application window is in the foreground: when there is no
    /// foreground window, it is the overlay itself or the desktop, or it is on no monitor.
    /// </summary>
    Cursor,

    /// <summary>The primary monitor, used when the pointer's position is unavailable.</summary>
    PrimaryMonitor,

    /// <summary>The monitor nearest a point the overlay was asked to go to, such as where the user dragged it.</summary>
    Point,
}
