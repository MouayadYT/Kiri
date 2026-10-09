namespace Assistant.Windows.Imaging;

/// <summary>
/// Turns stored pixels upright as an EXIF orientation (1 to 8) says: the way a camera records that it was held
/// sideways or upside down without turning the pixels themselves.
/// </summary>
internal static class PixelOrientation
{
    /// <summary>Stored upright: nothing to turn.</summary>
    public const int Normal = 1;

    /// <summary>An orientation outside 1 to 8 is read as upright.</summary>
    public static int Normalize(int orientation) => orientation is >= 1 and <= 8 ? orientation : Normal;

    /// <summary>
    /// The size of a <paramref name="width"/> × <paramref name="height"/> image once turned as
    /// <paramref name="orientation"/> says; the same function turns an upright size back into the stored one.
    /// </summary>
    public static (int Width, int Height) Upright(int width, int height, int orientation) =>
        SwapsSides(orientation) ? (height, width) : (width, height);

    /// <summary>
    /// The upright copy of <paramref name="bgra"/>, 4-byte pixels stored <paramref name="width"/> ×
    /// <paramref name="height"/>. An upright image is returned as it is.
    /// </summary>
    public static byte[] Apply(byte[] bgra, int width, int height, int orientation)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        orientation = Normalize(orientation);
        if (orientation == Normal)
        {
            return bgra;
        }

        var (uprightWidth, uprightHeight) = Upright(width, height, orientation);
        var upright = new byte[bgra.Length];
        for (var y = 0; y < uprightHeight; y++)
        {
            for (var x = 0; x < uprightWidth; x++)
            {
                var (sourceX, sourceY) = orientation switch
                {
                    2 => (width - 1 - x, y), // mirrored left to right
                    3 => (width - 1 - x, height - 1 - y), // turned half round
                    4 => (x, height - 1 - y), // mirrored top to bottom
                    5 => (y, x), // mirrored along the main diagonal
                    6 => (y, height - 1 - x), // turned a quarter clockwise to stand upright
                    7 => (width - 1 - y, height - 1 - x), // mirrored along the other diagonal
                    _ => (width - 1 - y, x), // 8: turned a quarter anticlockwise to stand upright
                };

                Buffer.BlockCopy(bgra, ((sourceY * width) + sourceX) * 4, upright, ((y * uprightWidth) + x) * 4, 4);
            }
        }

        return upright;
    }

    private static bool SwapsSides(int orientation) => Normalize(orientation) >= 5;
}
