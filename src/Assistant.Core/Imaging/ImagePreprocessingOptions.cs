namespace Assistant.Core.Imaging;

/// <summary>
/// The limits an image is prepared under before it goes to a vision model (<see cref="Contracts.IImagePreprocessor"/>):
/// how many pixels it may have, how long its longer side may be, and how many bytes it may take once encoded.
/// </summary>
/// <remarks>
/// A vision model reads an image as tokens in proportion to its pixels, so a large capture costs much of the context
/// window and a long wait while adding nothing a model can see. The defaults keep an image near the fixed share the
/// context budget allows one (<see cref="Budgeting.ContextBudgeter.ImageTokens"/>, 1024 tokens: a megapixel at the
/// 32-pixel tiles of Qwen3-VL-style encoders).
/// </remarks>
public sealed record ImagePreprocessingOptions
{
    /// <summary>The limits used unless others are given.</summary>
    public static ImagePreprocessingOptions Default { get; } = new();

    /// <summary>The most pixels (width times height) an image is sent with. Default 1,048,576 (1024 × 1024).</summary>
    public int MaxPixels { get; init; } = 1024 * 1024;

    /// <summary>The longest either side of an image may be, in pixels, however narrow it is. Default 2048.</summary>
    public int MaxLongSide { get; init; } = 2048;

    /// <summary>
    /// The most bytes an image may take once encoded (default 2 MiB). A smaller image that is larger than this is
    /// re-encoded, as a JPEG when a PNG would still be too large.
    /// </summary>
    public int MaxEncodedBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>The quality of a JPEG the preprocessor writes, from 0 to 1. Default 0.9.</summary>
    public double JpegQuality { get; init; } = 0.9;

    /// <summary>
    /// The fewest pixels a screenshot is sent with: a smaller one, whose text would be only a few pixels tall, is enlarged until it has
    /// this many (but by no more than <see cref="ScreenshotMaxUpscale"/>), since a vision model reads text from the pixels it is given.
    /// Default 262,144 (512 × 512).
    /// </summary>
    public int ScreenshotMinPixels { get; init; } = 512 * 512;

    /// <summary>The most a small screenshot is enlarged, in each direction. Default 2.5.</summary>
    public double ScreenshotMaxUpscale { get; init; } = 2.5;

    /// <summary>
    /// How much a screenshot is sharpened after it is scaled, as a share of the difference from its surroundings: 0 for none. The
    /// scaling filter softens the thin strokes of small text, and a little of it back keeps them readable. Default 0.35.
    /// </summary>
    public double ScreenshotSharpen { get; init; } = 0.35;

    /// <summary>
    /// The most pixels a screenshot may have for its empty margins to be looked for: a larger one (over a 16 megapixel capture) is
    /// scaled as it is, so a very large picture is never read into memory whole for this. Default 16,777,216.
    /// </summary>
    public int ScreenshotMaxTrimPixels { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// The size a <paramref name="width"/> × <paramref name="height"/> image is sent at: the same when it is within
    /// the limits, and otherwise scaled down, keeping its proportions, until it is.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A side is not positive.</exception>
    public (int Width, int Height) FitWithin(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (IsWithin(width, height))
        {
            return (width, height);
        }

        var scale = Math.Min(
            (double)Math.Max(1, MaxLongSide) / Math.Max(width, height),
            Math.Sqrt((double)Math.Max(1, MaxPixels) / ((long)width * height)));
        var fitted = (Width: Math.Max(1, (int)Math.Floor(width * scale)), Height: Math.Max(1, (int)Math.Floor(height * scale)));

        // Rounding can leave the product a pixel's width over; a side a pixel shorter is not a visible difference.
        while ((long)fitted.Width * fitted.Height > MaxPixels && (fitted.Width > 1 || fitted.Height > 1))
        {
            fitted = fitted.Width >= fitted.Height
                ? (fitted.Width - 1, fitted.Height)
                : (fitted.Width, fitted.Height - 1);
        }

        return fitted;
    }

    /// <summary>Whether a <paramref name="width"/> × <paramref name="height"/> image is within the pixel limits.</summary>
    public bool IsWithin(int width, int height) =>
        Math.Max(width, height) <= MaxLongSide && (long)width * height <= MaxPixels;
}
