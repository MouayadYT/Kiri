using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>Global memory functions from kernel32.dll: the clipboard's data is held in global memory blocks.</summary>
internal static partial class Kernel32
{
    /// <summary>A movable block, which is what the clipboard takes.</summary>
    public const uint GMEM_MOVEABLE = 0x0002;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalFree(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint memory);

    /// <summary>The size of the block, or 0 when <paramref name="memory"/> is not a global memory block.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint GlobalSize(nint memory);
}
