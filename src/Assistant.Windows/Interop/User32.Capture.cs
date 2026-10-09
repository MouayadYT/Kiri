using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>Monitor enumeration, device contexts and window rendering from user32.dll, for screen capture.</summary>
internal static unsafe partial class User32
{
    /// <summary>Marks the primary monitor in <see cref="MonitorInfo.Flags"/>.</summary>
    public const uint MONITORINFOF_PRIMARY = 1;

    /// <summary>Has <see cref="PrintWindow"/> render the window's whole content, including what the window draws with DirectX.</summary>
    public const uint PW_RENDERFULLCONTENT = 2;

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint window, nint deviceContext);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(nint window, nint deviceContext, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint window);

    /// <summary>Calls <paramref name="callback"/> for each monitor; the callback returns 0 to stop and anything else to go on.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(
        nint deviceContext, nint clip, delegate* unmanaged[Stdcall]<nint, nint, Rect*, nint, int> callback, nint data);
}
