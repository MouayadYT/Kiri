namespace Assistant.Windows.Placement;

/// <summary>
/// A rectangle in physical screen pixels. The right and bottom edges are exclusive, as in a Win32 RECT. Like every
/// screen coordinate, it is in the coordinate space of the calling thread's DPI awareness.
/// </summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge.</param>
/// <param name="Bottom">Bottom edge.</param>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Width, in pixels.</summary>
    public int Width => Right - Left;

    /// <summary>Height, in pixels.</summary>
    public int Height => Bottom - Top;
}
