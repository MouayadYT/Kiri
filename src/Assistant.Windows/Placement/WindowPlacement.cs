namespace Assistant.Windows.Placement;

/// <summary>Where an overlay window was placed.</summary>
/// <param name="Monitor">The monitor it was placed on.</param>
/// <param name="Source">How that monitor was chosen.</param>
/// <param name="Bounds">The window's bounds at the monitor's DPI.</param>
public sealed record WindowPlacement(DisplayMonitor Monitor, MonitorSource Source, ScreenRect Bounds);
