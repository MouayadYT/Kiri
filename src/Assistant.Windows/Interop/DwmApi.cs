using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>Desktop Window Manager window attributes from dwmapi.dll.</summary>
internal static partial class DwmApi
{
    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_USE_HOSTBACKDROPBRUSH = 17;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    public const int DWMSBT_NONE = 1;
    public const int DWMSBT_MAINWINDOW = 2;
    public const int DWMSBT_TRANSIENTWINDOW = 3;
    public const int DWMSBT_TABBEDWINDOW = 4;

    /// <summary>Sets a 32-bit window attribute and returns the HRESULT.</summary>
    public static int SetWindowAttribute(nint window, int attribute, int value) =>
        DwmSetWindowAttribute(window, attribute, in value, sizeof(int));

    /// <summary>Extends the window frame over the whole client area, so a system backdrop shows behind it.</summary>
    public static int ExtendFrameIntoWholeClientArea(nint window)
    {
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        return DwmExtendFrameIntoClientArea(window, in margins);
    }

    /// <summary>
    /// Gets the bounds of the window as it is seen, without the invisible resize border that <c>GetWindowRect</c> includes
    /// on Windows 10 and later, and returns the HRESULT.
    /// </summary>
    public static int GetExtendedFrameBounds(nint window, out User32.Rect bounds) =>
        DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out bounds, Marshal.SizeOf<User32.Rect>());

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, out User32.Rect value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint window, in Margins margins);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, in int value, int size);
}
