using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Assistant.Windows.Interop;
using Assistant.Windows.Placement;

namespace Assistant.Windows.Capture;

/// <summary>Where a window is, as capture needs to know it.</summary>
/// <param name="Outer">The window's whole rectangle, which the window renders itself into, invisible resize border included.</param>
/// <param name="Visible">The window as it is seen, without that border.</param>
internal readonly record struct WindowFrame(ScreenRect Outer, ScreenRect Visible);

/// <summary>The Windows calls screen capture makes, apart so the service can be tested without a screen.</summary>
internal interface IScreenCaptureNativeMethods
{
    /// <summary>The monitors, from left to right and then top to bottom, numbered in that order.</summary>
    IReadOnlyList<CaptureMonitor> Monitors();

    /// <summary>
    /// Copies <paramref name="area"/> of the virtual screen as it is composed, windows in front included. Returns 0 and the pixels
    /// (four opaque bytes each, blue first, the top row first, no padding), or the Win32 error and no pixels.
    /// </summary>
    int CopyScreen(ScreenRect area, out byte[]? pixels);

    /// <summary>Where the window is, or <see langword="null"/> when it is gone, hidden or minimized, so there is nothing of it to see.</summary>
    WindowFrame? DescribeWindow(nint window);

    /// <summary>
    /// Has the window render itself, <paramref name="width"/> by <paramref name="height"/> pixels from its top left, covered or not.
    /// Returns 0 and the pixels as <see cref="CopyScreen"/> does, or the Win32 error and no pixels.
    /// </summary>
    int RenderWindow(nint window, int width, int height, out byte[]? pixels);
}

/// <summary>The real thing: GDI copies the screen into a device-independent bitmap, which is read into memory.</summary>
internal sealed unsafe class ScreenCaptureNativeMethods : IScreenCaptureNativeMethods
{
    // Win32 errors for the calls that do not say why they failed.
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotEnoughMemory = 8;

    public IReadOnlyList<CaptureMonitor> Monitors()
    {
        var handles = new List<nint>();
        var state = GCHandle.Alloc(handles);
        try
        {
            User32.EnumDisplayMonitors(0, 0, &OnMonitor, GCHandle.ToIntPtr(state));
        }
        finally
        {
            state.Free();
        }

        var found = new List<(ScreenRect Bounds, int Dpi, bool IsPrimary)>(handles.Count);
        foreach (var handle in handles)
        {
            var info = new User32.MonitorInfo { Size = (uint)sizeof(User32.MonitorInfo) };
            if (!User32.GetMonitorInfo(handle, ref info))
            {
                continue;
            }

            // The DPI is read in the same awareness as the rectangles, so the two always agree.
            var dpi = ShCore.GetDpiForMonitor(handle, ShCore.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0
                ? (int)dpiX
                : DisplayMonitor.DefaultDpi;
            found.Add((new ScreenRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom), dpi,
                (info.Flags & User32.MONITORINFOF_PRIMARY) != 0));
        }

        return [.. found.OrderBy(monitor => monitor.Bounds.Left).ThenBy(monitor => monitor.Bounds.Top)
            .Select((monitor, index) => new CaptureMonitor(index, monitor.Bounds, monitor.Dpi, monitor.IsPrimary))];
    }

    public int CopyScreen(ScreenRect area, out byte[]? pixels)
    {
        pixels = null;
        var screen = User32.GetDC(0);
        if (screen == 0)
        {
            return ErrorInvalidFunction;
        }

        try
        {
            return Paint(
                area.Width, area.Height,
                memory => Gdi32.BitBlt(
                    memory, 0, 0, area.Width, area.Height, screen, area.Left, area.Top, Gdi32.SRCCOPY | Gdi32.CAPTUREBLT),
                out pixels);
        }
        finally
        {
            User32.ReleaseDC(0, screen);
        }
    }

    public WindowFrame? DescribeWindow(nint window)
    {
        if (window == 0 || !User32.IsWindow(window) || !User32.IsWindowVisible(window) || User32.IsIconic(window)
            || !User32.GetWindowRect(window, out var outer))
        {
            return null;
        }

        var visible = DwmApi.GetExtendedFrameBounds(window, out var frame) == 0 ? frame : outer;
        return new WindowFrame(
            new ScreenRect(outer.Left, outer.Top, outer.Right, outer.Bottom),
            new ScreenRect(visible.Left, visible.Top, visible.Right, visible.Bottom));
    }

    public int RenderWindow(nint window, int width, int height, out byte[]? pixels) =>
        Paint(width, height, memory => User32.PrintWindow(window, memory, User32.PW_RENDERFULLCONTENT), out pixels);

    // Makes a memory device context with a bitmap of the size as its surface, lets the caller paint on it, and reads the bitmap.
    private static int Paint(int width, int height, Func<nint, bool> paint, out byte[]? pixels)
    {
        pixels = null;
        var header = new Gdi32.BitmapInfoHeader
        {
            Size = (uint)sizeof(Gdi32.BitmapInfoHeader),
            Width = width,

            // A negative height makes the first row the top one.
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = Gdi32.BI_RGB,
        };

        var memory = Gdi32.CreateCompatibleDC(0);
        if (memory == 0)
        {
            return ErrorNotEnoughMemory;
        }

        nint bitmap = 0;
        nint previous = 0;
        try
        {
            bitmap = Gdi32.CreateDIBSection(memory, in header, Gdi32.DIB_RGB_COLORS, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0)
            {
                return ErrorOr(Marshal.GetLastPInvokeError(), ErrorNotEnoughMemory);
            }

            previous = Gdi32.SelectObject(memory, bitmap);
            if (!paint(memory))
            {
                return ErrorOr(Marshal.GetLastPInvokeError(), ErrorInvalidFunction);
            }

            // The bitmap's memory is read directly, so GDI must have finished writing it.
            Gdi32.GdiFlush();
            var data = new byte[checked(width * height * CapturedImage.BytesPerPixel)];
            new ReadOnlySpan<byte>((void*)bits, data.Length).CopyTo(data);
            MakeOpaque(data);
            pixels = data;
            return 0;
        }
        finally
        {
            if (previous != 0)
            {
                Gdi32.SelectObject(memory, previous);
            }

            if (bitmap != 0)
            {
                Gdi32.DeleteObject(bitmap);
            }

            Gdi32.DeleteDC(memory);
        }
    }

    // GDI leaves the fourth byte of each pixel as it likes, and a screen has no transparency.
    internal static void MakeOpaque(Span<byte> bgra)
    {
        foreach (ref var pixel in MemoryMarshal.Cast<byte, uint>(bgra))
        {
            pixel |= 0xFF000000u;
        }
    }

    private static int ErrorOr(int error, int fallback) => error != 0 ? error : fallback;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnMonitor(nint monitor, nint deviceContext, User32.Rect* area, nint state)
    {
        ((List<nint>)GCHandle.FromIntPtr(state).Target!).Add(monitor);
        return 1;
    }
}
