using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>Device context and bitmap functions from gdi32.dll, for copying pixels off the screen.</summary>
internal static partial class Gdi32
{
    /// <summary>Copies the source as it is.</summary>
    public const uint SRCCOPY = 0x00CC0020;

    /// <summary>Includes layered (translucent) windows in a copy from the screen, which a plain copy leaves out.</summary>
    public const uint CAPTUREBLT = 0x40000000;

    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    /// <summary>The header of a device-independent bitmap: 32 bits per pixel needs nothing after it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;

        /// <summary>Negative for a bitmap whose first row is the top one.</summary>
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint deviceContext);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint deviceContext);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial nint CreateDIBSection(
        nint deviceContext, in BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint deviceContext, nint gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint gdiObject);

    /// <summary>Finishes the GDI calls that are batched, so memory they write can be read.</summary>
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GdiFlush();

    /// <summary>The size of a bitmap, as GetObject tells it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static partial int GetObject(nint gdiObject, int size, out BitmapInfo bitmap);

    /// <summary>Copies a bitmap's pixels, <paramref name="lines"/> rows of them from <paramref name="start"/>, into <paramref name="bits"/>.</summary>
    [LibraryImport("gdi32.dll")]
    public static unsafe partial int GetDIBits(
        nint deviceContext, nint bitmap, uint start, uint lines, byte* bits, ref BitmapInfoHeader info, uint usage);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(
        nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
}
