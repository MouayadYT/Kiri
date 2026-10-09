using Assistant.Windows.Interop;
using Assistant.Windows.Placement;

namespace Assistant.Windows.Capture;

/// <summary>
/// What a window that covers a whole monitor needs from Windows (PROJECT_SPEC §4.6): to be put exactly over the monitor, in physical
/// pixels, above everything else, and to appear and go at once, with square corners and no frame, instead of animating as other
/// windows do.
/// </summary>
public static class ScreenOverlayWindows
{
    /// <summary>
    /// Makes the window an overlay: Windows draws it without a border, without rounded corners, and shows and hides it without the
    /// animation other windows have. Call it once the window has a handle, before it is shown.
    /// </summary>
    public static void Prepare(nint window)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);
        DwmApi.SetWindowAttribute(window, DwmApi.DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        DwmApi.SetWindowAttribute(window, DwmApi.DWMWA_WINDOW_CORNER_PREFERENCE, DwmApi.DWMWCP_DONOTROUND);
        DwmApi.SetWindowAttribute(window, DwmApi.DWMWA_BORDER_COLOR, DwmApi.DWMWA_COLOR_NONE);
    }

    /// <summary>Where the pointer is on the virtual screen, in physical pixels, or <see langword="null"/> when Windows does not say.</summary>
    public static ScreenPoint? CursorPosition() =>
        User32.GetCursorPos(out var point) ? new ScreenPoint(point.X, point.Y) : null;

    /// <summary>
    /// Puts the window exactly over <paramref name="area"/> of the virtual screen and above every other window, without activating it.
    /// A window that moves to a monitor with another DPI rescales itself, and may move; so the placement is checked and made again.
    /// </summary>
    /// <returns>Whether the window is where it was asked to be.</returns>
    public static bool Cover(nint window, ScreenRect area)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!User32.SetWindowPos(
                    window, User32.HWND_TOPMOST, area.Left, area.Top, area.Width, area.Height, User32.SWP_NOACTIVATE))
            {
                return false;
            }

            if (User32.GetWindowRect(window, out var placed)
                && placed.Left == area.Left && placed.Top == area.Top && placed.Right == area.Right && placed.Bottom == area.Bottom)
            {
                return true;
            }
        }

        return false;
    }
}
