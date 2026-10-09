using System.Runtime.InteropServices;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Placement;

internal interface IPlacementNativeMethods
{
    nint GetForegroundWindow();

    /// <summary>Whether both windows have the same root owner, so one of them belongs to the other.</summary>
    bool AreRelated(nint window, nint other);

    /// <summary>Whether the window is the shell's desktop, which spans every monitor.</summary>
    bool IsDesktop(nint window);

    DisplayMonitor? MonitorFromWindow(nint window);
    DisplayMonitor? MonitorFromCursor();
    DisplayMonitor? PrimaryMonitor();
    DisplayMonitor? MonitorNearest(int x, int y);

    /// <summary>Moves a window without resizing, restacking or activating it, and returns the Win32 error.</summary>
    int Move(nint window, int x, int y);

    (int X, int Y)? GetOrigin(nint window);

    /// <summary>Moves and sizes a window without restacking or activating it, and returns the Win32 error.</summary>
    int SetBounds(nint window, ScreenRect bounds);

    ScreenRect? GetBounds(nint window);

    /// <summary>The id of the window's process, or 0 when the window is gone.</summary>
    int GetProcessId(nint window);

    /// <summary>Whether the window is visible and not minimized, so that it is somewhere on a screen.</summary>
    bool IsOnScreen(nint window);

    /// <summary>The window as it is seen, without the invisible resize border that <c>GetWindowRect</c> includes.</summary>
    ScreenRect? GetVisibleBounds(nint window);

    /// <summary>Where the pointer is.</summary>
    ScreenPoint? GetPointer();
}

internal sealed unsafe class PlacementNativeMethods : IPlacementNativeMethods
{
    // The 7-character desktop class names, one more character so longer names cannot match, and the terminator.
    private const int ClassNameCapacity = 9;

    public nint GetForegroundWindow() => User32.GetForegroundWindow();

    public bool AreRelated(nint window, nint other)
    {
        var root = User32.GetAncestor(window, User32.GA_ROOTOWNER);
        return root != 0 && root == User32.GetAncestor(other, User32.GA_ROOTOWNER);
    }

    public bool IsDesktop(nint window)
    {
        // Clicking the desktop makes one of these shell windows the foreground window.
        var buffer = stackalloc char[ClassNameCapacity];
        var className = new ReadOnlySpan<char>(buffer, User32.GetClassName(window, buffer, ClassNameCapacity));
        return className.SequenceEqual("Progman") || className.SequenceEqual("WorkerW");
    }

    public DisplayMonitor? MonitorFromWindow(nint window) =>
        Describe(User32.MonitorFromWindow(window, User32.MONITOR_DEFAULTTONULL));

    public DisplayMonitor? MonitorFromCursor() =>
        User32.GetCursorPos(out var point) ? Describe(User32.MonitorFromPoint(point, User32.MONITOR_DEFAULTTONULL)) : null;

    public DisplayMonitor? PrimaryMonitor() =>
        Describe(User32.MonitorFromPoint(default, User32.MONITOR_DEFAULTTOPRIMARY));

    public DisplayMonitor? MonitorNearest(int x, int y) =>
        Describe(User32.MonitorFromPoint(new User32.Point { X = x, Y = y }, User32.MONITOR_DEFAULTTONEAREST));

    public int Move(nint window, int x, int y) =>
        User32.SetWindowPos(window, 0, x, y, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE)
            ? 0
            : Marshal.GetLastPInvokeError();

    public (int X, int Y)? GetOrigin(nint window) =>
        User32.GetWindowRect(window, out var bounds) ? (bounds.Left, bounds.Top) : null;

    public int SetBounds(nint window, ScreenRect bounds) =>
        User32.SetWindowPos(window, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE)
            ? 0
            : Marshal.GetLastPInvokeError();

    public ScreenRect? GetBounds(nint window) => User32.GetWindowRect(window, out var bounds) ? ToScreenRect(bounds) : null;

    public int GetProcessId(nint window) => User32.GetWindowThreadProcessId(window, out var processId) == 0 ? 0 : (int)processId;

    public bool IsOnScreen(nint window) => User32.IsWindow(window) && User32.IsWindowVisible(window) && !User32.IsIconic(window);

    public ScreenRect? GetVisibleBounds(nint window) =>
        DwmApi.GetExtendedFrameBounds(window, out var frame) == 0 && frame.Right > frame.Left && frame.Bottom > frame.Top
            ? ToScreenRect(frame)
            : GetBounds(window);

    public ScreenPoint? GetPointer() => User32.GetCursorPos(out var point) ? new ScreenPoint(point.X, point.Y) : null;

    private static DisplayMonitor? Describe(nint monitor)
    {
        var info = new User32.MonitorInfo { Size = (uint)sizeof(User32.MonitorInfo) };
        if (monitor == 0 || !User32.GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        // The DPI is read in the same DPI awareness as the rectangles, so the two always agree.
        var dpi = ShCore.GetDpiForMonitor(monitor, ShCore.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0
            ? (int)dpiX
            : DisplayMonitor.DefaultDpi;
        return new DisplayMonitor(ToScreenRect(info.Monitor), ToScreenRect(info.WorkArea), dpi);
    }

    private static ScreenRect ToScreenRect(User32.Rect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
