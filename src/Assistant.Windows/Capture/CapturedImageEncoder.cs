using Assistant.Windows.Imaging;
using Windows.Graphics.Imaging;

namespace Assistant.Windows.Capture;

/// <summary>
/// Encodes a <see cref="CapturedImage"/> as a PNG, in memory, with the Windows imaging codec: what goes to a model as an image
/// (PROJECT_SPEC §5.5) and to the clipboard. A PNG keeps every pixel, so small text in a screenshot stays sharp. Nothing is written
/// to disk: saving a capture is something the user asks for, and has no place here.
/// </summary>
public static class CapturedImageEncoder
{
    /// <summary>Makes a PNG of <paramref name="image"/>.</summary>
    /// <exception cref="ObjectDisposedException">The capture was disposed.</exception>
    public static Task<byte[]> EncodePngAsync(CapturedImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return ImagePreprocessor.EncodeAsync(
            image.PixelArray, image.Width, image.Height, BitmapEncoder.PngEncoderId, null, cancellationToken);
    }
}
