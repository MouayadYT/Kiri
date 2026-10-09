using Assistant.Windows.Placement;

namespace Assistant.Windows.Capture;

/// <summary>
/// A picture of part of the screen, held in memory only (PROJECT_SPEC §3.1 P7, §3.5): its pixels and where and when they were
/// taken. The pixels are 32 bits each in the order blue, green, red, alpha, the first row at the top, no padding between rows, and
/// every pixel opaque. Nothing here is ever written to disk or logged: <see cref="ToString"/> holds sizes only, and
/// <see cref="Dispose"/> wipes the pixels, for a capture that is no longer wanted.
/// </summary>
public sealed class CapturedImage : IDisposable
{
    private readonly byte[] _pixels;
    private bool _disposed;

    /// <summary>Creates a capture from <paramref name="pixels"/>, which it keeps and does not copy.</summary>
    /// <param name="kind">What it is a picture of.</param>
    /// <param name="method">How the pixels were taken.</param>
    /// <param name="bounds">
    /// Where it lies on the virtual screen, in physical pixels. A window's picture lies where the window is, even when part of the
    /// window is off the screen.
    /// </param>
    /// <param name="monitor">The monitor that holds most of it, or <see langword="null"/> when no monitor does.</param>
    /// <param name="pixels">Four bytes for each pixel of <paramref name="bounds"/>, as described above.</param>
    /// <param name="capturedAt">When the pixels were taken.</param>
    /// <exception cref="ArgumentException"><paramref name="bounds"/> is empty or <paramref name="pixels"/> is not its size.</exception>
    public CapturedImage(
        CaptureKind kind, CaptureMethod method, ScreenRect bounds, CaptureMonitor? monitor, byte[] pixels, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentException("A capture has at least one pixel.", nameof(bounds));
        }

        if (pixels.LongLength != (long)bounds.Width * bounds.Height * BytesPerPixel)
        {
            throw new ArgumentException("The pixels are not the size of the area.", nameof(pixels));
        }

        Kind = kind;
        Method = method;
        Bounds = bounds;
        Monitor = monitor;
        CapturedAt = capturedAt;
        _pixels = pixels;
    }

    /// <summary>The bytes a pixel takes.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>What the picture is of.</summary>
    public CaptureKind Kind { get; }

    /// <summary>How the pixels were taken.</summary>
    public CaptureMethod Method { get; }

    /// <summary>Where the picture lies on the virtual screen, in physical pixels (negative to the left of and above the primary monitor).</summary>
    public ScreenRect Bounds { get; }

    /// <summary>The monitor that holds most of the picture, or <see langword="null"/> when no monitor does.</summary>
    public CaptureMonitor? Monitor { get; }

    /// <summary>When the pixels were taken.</summary>
    public DateTimeOffset CapturedAt { get; }

    /// <summary>The picture's width, in pixels.</summary>
    public int Width => Bounds.Width;

    /// <summary>The picture's height, in pixels.</summary>
    public int Height => Bounds.Height;

    /// <summary>The bytes from one row to the next.</summary>
    public int Stride => Width * BytesPerPixel;

    /// <summary>The pixels. Do not change them.</summary>
    /// <exception cref="ObjectDisposedException">The capture was disposed.</exception>
    public ReadOnlyMemory<byte> Pixels
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pixels;
        }
    }

    /// <summary>
    /// Makes a picture of the part of this one that lies inside <paramref name="region"/>, a rectangle on the virtual screen like
    /// <see cref="Bounds"/>: what the user selected of a snapshot, taken without capturing the screen again, so it shows what the
    /// snapshot did. The part outside the picture is left out. It is a copy, which outlives this one.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The region has no part inside the picture.</exception>
    /// <exception cref="ObjectDisposedException">The capture was disposed.</exception>
    public CapturedImage Crop(ScreenRect region)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var part = new ScreenRect(
            Math.Max(region.Left, Bounds.Left), Math.Max(region.Top, Bounds.Top),
            Math.Min(region.Right, Bounds.Right), Math.Min(region.Bottom, Bounds.Bottom));
        if (part.Width <= 0 || part.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "The region is outside the picture.");
        }

        return new CapturedImage(CaptureKind.Region, Method, part, Monitor, CopyPart(_pixels, Bounds, part), CapturedAt);
    }

    /// <summary>
    /// Copies the rows of <paramref name="part"/> out of <paramref name="pixels"/>, which hold <paramref name="whole"/>; the part
    /// lies inside it.
    /// </summary>
    internal static byte[] CopyPart(byte[] pixels, ScreenRect whole, ScreenRect part)
    {
        var copy = new byte[checked(part.Width * part.Height * BytesPerPixel)];
        var rowBytes = part.Width * BytesPerPixel;
        var stride = whole.Width * BytesPerPixel;
        var from = (part.Top - whole.Top) * stride + (part.Left - whole.Left) * BytesPerPixel;
        for (var row = 0; row < part.Height; row++)
        {
            Buffer.BlockCopy(pixels, from + row * stride, copy, row * rowBytes, rowBytes);
        }

        return copy;
    }

    /// <summary>Wipes the pixels, so that a capture nobody wants any more does not linger in memory.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Array.Clear(_pixels);
        }
    }

    // Pixels are private content (PROJECT_SPEC §3.2): ToString, and so any log, holds sizes only.
    /// <inheritdoc/>
    public override string ToString() => $"CapturedImage {{ Kind = {Kind}, Width = {Width}, Height = {Height} }}";

    /// <summary>The bytes of the pixels for code that must hand an array on, such as an encoder. Do not change them.</summary>
    internal byte[] PixelArray
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pixels;
        }
    }
}
