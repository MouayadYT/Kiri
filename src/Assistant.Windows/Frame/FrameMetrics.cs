using Assistant.Windows.Interop;

namespace Assistant.Windows.Frame;

/// <summary>Measurements of the frame Windows gives a resizable window.</summary>
public static class FrameMetrics
{
    /// <summary>
    /// How far, in physical pixels, a maximized resizable window reaches past each edge of its monitor's work area: its
    /// resize border, which Windows keeps even when the window draws its own frame. A window whose client area covers
    /// its frame must keep its content this far in while it is maximized, or the content is cut off.
    /// </summary>
    public static int MaximizedOverhang(nint window)
    {
        var dpi = User32.GetDpiForWindow(window);
        if (dpi == 0)
        {
            dpi = 96;
        }

        return User32.GetSystemMetricsForDpi(User32.SM_CXSIZEFRAME, dpi) +
            User32.GetSystemMetricsForDpi(User32.SM_CXPADDEDBORDER, dpi);
    }
}
