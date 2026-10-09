using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Assistant.Core.Displays;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Displays;

/// <summary>
/// The displays of this PC as Windows has them on the desktop: each with Windows' own name for it (<c>\\.\DISPLAY2</c>, whose number is the one
/// Settings shows), where it is and whether it is the main one. Nothing is changed and nothing is kept: the list is read when it is asked for.
/// </summary>
public sealed unsafe partial class WindowsDisplays : IDisplays
{
    /// <inheritdoc/>
    public IReadOnlyList<DisplayInfo> List()
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

        var displays = new List<DisplayInfo>(handles.Count);
        foreach (var handle in handles)
        {
            if (Describe(handle) is { } display && displays.All(known => known.Id != display.Id))
            {
                displays.Add(display);
            }
        }

        return displays;
    }

    /// <summary>
    /// Lays <paramref name="window"/> over the whole of <paramref name="display"/>, above the other windows, as a mark and nothing more: it is never given the
    /// keyboard, and a click goes through it to what is underneath. Returns whether Windows placed it.
    /// </summary>
    public static bool Cover(nint window, DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (window == 0 || !User32.IsWindow(window))
        {
            return false;
        }

        const nint ClickThrough = 0x00000020;
        var style = User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE);
        User32.SetWindowLongPtr(window, User32.GWL_EXSTYLE, style | ClickThrough | (nint)User32.WS_EX_NOACTIVATE | (nint)User32.WS_EX_TOOLWINDOW);
        return User32.SetWindowPos(window, User32.HWND_TOPMOST, display.Left, display.Top, display.Width, display.Height, User32.SWP_NOACTIVATE);
    }

    /// <summary>The display <paramref name="window"/> is on, or mostly on; <see langword="null"/> when Windows does not say.</summary>
    internal static DisplayInfo? Of(nint window) => Describe(User32.MonitorFromWindow(window, User32.MONITOR_DEFAULTTONEAREST));

    /// <summary>The part of <paramref name="display"/> that windows may use: what is left of it beside the taskbar.</summary>
    internal static User32.Rect? WorkAreaOf(DisplayInfo display)
    {
        var point = new User32.Point { X = display.Left + (display.Width / 2), Y = display.Top + (display.Height / 2) };
        var handle = MonitorFromPoint(point, User32.MONITOR_DEFAULTTONEAREST);
        var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
        return handle != 0 && GetMonitorInfo(handle, &info) ? info.WorkArea : null;
    }

    private static DisplayInfo? Describe(nint handle)
    {
        var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
        if (handle == 0 || !GetMonitorInfo(handle, &info))
        {
            return null;
        }

        var id = new string(info.Device);
        if (id.Length == 0)
        {
            return null;
        }

        // "\\.\DISPLAY12" is Display 12.
        var digits = new string([.. id.Reverse().TakeWhile(char.IsAsciiDigit).Reverse()]);
        var number = int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        return new DisplayInfo(
            id, number, info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top,
            (info.Flags & User32.MONITORINFOF_PRIMARY) != 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnMonitor(nint monitor, nint deviceContext, User32.Rect* area, nint data)
    {
        if (GCHandle.FromIntPtr(data).Target is List<nint> handles)
        {
            handles.Add(monitor);
        }

        return 1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public User32.Rect Monitor;
        public User32.Rect WorkArea;
        public uint Flags;
        public fixed char Device[32];
    }

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, MonitorInfoEx* info);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(User32.Point point, uint flags);
}
