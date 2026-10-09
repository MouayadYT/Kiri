namespace Assistant.Windows.Placement;

/// <summary>A display monitor, in physical screen pixels.</summary>
/// <param name="Bounds">The whole monitor.</param>
/// <param name="WorkArea">The monitor without the taskbar and docked app bars, where windows belong.</param>
/// <param name="Dpi">The monitor's effective DPI: 96 at 100 % scaling, 120 at 125 %, and so on.</param>
public sealed record DisplayMonitor(ScreenRect Bounds, ScreenRect WorkArea, int Dpi)
{
    /// <summary>The DPI at 100 % scaling, where one device-independent pixel is one physical pixel.</summary>
    public const int DefaultDpi = 96;

    /// <summary>Physical pixels per device-independent pixel.</summary>
    public double Scale => (double)Dpi / DefaultDpi;
}
