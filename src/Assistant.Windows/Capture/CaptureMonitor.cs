using Assistant.Windows.Placement;

namespace Assistant.Windows.Capture;

/// <summary>A display monitor that can be captured, in physical pixels of the virtual screen.</summary>
/// <param name="Index">
/// Its place among the monitors, counted from 0 from left to right and, for monitors that start at the same place, from top to
/// bottom. It only means something for as long as the monitors stay as they are.
/// </param>
/// <param name="Bounds">The whole monitor, where it lies on the virtual screen (which has negative coordinates left of or above the primary).</param>
/// <param name="Dpi">The monitor's effective DPI: 96 at 100 % scaling, 120 at 125 %, and so on.</param>
/// <param name="IsPrimary">Whether it is the primary monitor.</param>
public sealed record CaptureMonitor(int Index, ScreenRect Bounds, int Dpi, bool IsPrimary)
{
    /// <summary>Physical pixels per device-independent pixel.</summary>
    public double Scale => (double)Dpi / DisplayMonitor.DefaultDpi;
}
