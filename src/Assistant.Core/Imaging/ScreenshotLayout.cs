namespace Assistant.Core.Imaging;

/// <summary>A rectangle of pixels in an image.</summary>
/// <param name="X">Pixels from the left edge.</param>
/// <param name="Y">Pixels from the top edge.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct PixelArea(int X, int Y, int Width, int Height)
{
    /// <summary>The number of pixels in it.</summary>
    public long Pixels => (long)Width * Height;
}

/// <summary>
/// Decides what of a screenshot is sent to a vision model and at what size, so that the text in it stays readable (PROJECT_SPEC §5.5):
/// the bounds of what is on it, without the empty margins that only cost pixels, and the size to scale that to. It works on pixels and
/// numbers alone, so it can be tested without a screen or a codec.
/// </summary>
public static class ScreenshotLayout
{
    /// <summary>How much of the picture the empty margins must be for them to be worth cutting off: below this the picture is sent whole.</summary>
    public const double MinTrimShare = 0.15;

    /// <summary>The room left around what is on the picture when its margins are cut, in pixels, so nothing touches the edge.</summary>
    public const int TrimPadding = 8;

    /// <summary>How far a pixel's channels may be from the margin's color and still count as margin: the faint noise of a gradient or of compression.</summary>
    public const int ColorTolerance = 3;

    /// <summary>
    /// The part of a picture of <paramref name="width"/> by <paramref name="height"/> pixels (BGRA, four bytes each, no padding) that is
    /// not empty margin: the margin is the color of the corner pixels, when they agree, taken off each side while its whole row or column
    /// is that color, with <see cref="TrimPadding"/> left. The whole picture when its corners differ (there is no margin to speak of),
    /// when there is nothing in it but the margin, or when the margins are under <see cref="MinTrimShare"/> of it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="bgra"/> is not the size of the picture.</exception>
    public static PixelArea ContentArea(ReadOnlySpan<byte> bgra, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgra.Length != (long)width * height * 4)
        {
            throw new ArgumentException("The pixels are not the size of the picture.", nameof(bgra));
        }

        var whole = new PixelArea(0, 0, width, height);
        if (width < 4 || height < 4 || !CornersAgree(bgra, width, height, out var margin))
        {
            return whole;
        }

        var top = 0;
        while (top < height && RowIs(bgra, width, top, margin))
        {
            top++;
        }

        if (top == height)
        {
            return whole;
        }

        var bottom = height - 1;
        while (bottom > top && RowIs(bgra, width, bottom, margin))
        {
            bottom--;
        }

        var left = 0;
        while (left < width && ColumnIs(bgra, width, top, bottom, left, margin))
        {
            left++;
        }

        var right = width - 1;
        while (right > left && ColumnIs(bgra, width, top, bottom, right, margin))
        {
            right--;
        }

        left = Math.Max(0, left - TrimPadding);
        top = Math.Max(0, top - TrimPadding);
        right = Math.Min(width - 1, right + TrimPadding);
        bottom = Math.Min(height - 1, bottom + TrimPadding);
        var content = new PixelArea(left, top, right - left + 1, bottom - top + 1);
        return 1 - (double)content.Pixels / whole.Pixels >= MinTrimShare ? content : whole;
    }

    /// <summary>
    /// The size a <paramref name="width"/> by <paramref name="height"/> screenshot is sent at under <paramref name="options"/>: scaled down,
    /// keeping its proportions, when it is over the limits, so the text in it loses as little as it must; enlarged when it is so small
    /// that text in it would be only a few pixels tall (at most <see cref="ImagePreprocessingOptions.ScreenshotMaxUpscale"/> times, never
    /// past the limits); and as it is otherwise.
    /// </summary>
    public static (int Width, int Height) TargetSize(int width, int height, ImagePreprocessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (!options.IsWithin(width, height))
        {
            return options.FitWithin(width, height);
        }

        var pixels = (long)width * height;
        if (pixels >= options.ScreenshotMinPixels)
        {
            return (width, height);
        }

        var factor = Math.Min(options.ScreenshotMaxUpscale, Math.Sqrt((double)options.ScreenshotMinPixels / pixels));
        return options.FitWithin(Math.Max(width, (int)Math.Round(width * factor)), Math.Max(height, (int)Math.Round(height * factor)));
    }

    /// <summary>
    /// Sharpens BGRA <paramref name="bgra"/> a little, in place (an unsharp mask over the color channels, <paramref name="amount"/> times
    /// the difference from the 3 by 3 average), which gives back the edge a scaling filter took from small text. The alpha channel is left
    /// as it is. A picture under 3 pixels either way is not changed.
    /// </summary>
    public static void Sharpen(Span<byte> bgra, int width, int height, double amount)
    {
        if (width < 3 || height < 3 || amount <= 0 || bgra.Length != (long)width * height * 4)
        {
            return;
        }

        var source = bgra.ToArray();
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var at = (y * width + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    var sum = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            sum += source[at + (dy * width + dx) * 4 + channel];
                        }
                    }

                    var value = source[at + channel] + amount * (source[at + channel] - sum / 9.0);
                    bgra[at + channel] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
                }
            }
        }
    }

    // The four corners are one color, which is then the margin's.
    private static bool CornersAgree(ReadOnlySpan<byte> bgra, int width, int height, out uint margin)
    {
        var topLeft = PixelAt(bgra, 0);
        margin = topLeft;
        return Near(topLeft, PixelAt(bgra, width - 1))
            && Near(topLeft, PixelAt(bgra, (height - 1) * width))
            && Near(topLeft, PixelAt(bgra, height * width - 1));
    }

    private static bool RowIs(ReadOnlySpan<byte> bgra, int width, int row, uint margin)
    {
        for (var x = 0; x < width; x++)
        {
            if (!Near(margin, PixelAt(bgra, row * width + x)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ColumnIs(ReadOnlySpan<byte> bgra, int width, int top, int bottom, int column, uint margin)
    {
        for (var y = top; y <= bottom; y++)
        {
            if (!Near(margin, PixelAt(bgra, y * width + column)))
            {
                return false;
            }
        }

        return true;
    }

    private static uint PixelAt(ReadOnlySpan<byte> bgra, int pixel) => BitConverter.ToUInt32(bgra[(pixel * 4)..]);

    // Two colors are the same margin when none of their color channels differs by more than the tolerance.
    private static bool Near(uint first, uint second) =>
        Math.Abs((int)(first & 0xFF) - (int)(second & 0xFF)) <= ColorTolerance
        && Math.Abs((int)((first >> 8) & 0xFF) - (int)((second >> 8) & 0xFF)) <= ColorTolerance
        && Math.Abs((int)((first >> 16) & 0xFF) - (int)((second >> 16) & 0xFF)) <= ColorTolerance;
}
