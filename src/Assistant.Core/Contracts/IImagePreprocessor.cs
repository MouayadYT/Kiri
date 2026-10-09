using Assistant.Core.Imaging;

namespace Assistant.Core.Contracts;

/// <summary>
/// Makes an image ready for a vision model (PROJECT_SPEC §5.5): upright as the camera meant it, no larger than the
/// <see cref="ImagePreprocessingOptions"/> allow, and encoded as PNG or JPEG, which every engine reads. The image given
/// is never changed: what is sent is either that same image, untouched, or a new copy.
/// </summary>
/// <remarks>
/// Implementations work in memory only: an image is never written to disk or logged (PROJECT_SPEC §3.1, §3.3).
/// </remarks>
public interface IImagePreprocessor
{
    /// <summary>Prepares <paramref name="image"/>, an encoded image such as a PNG screenshot or a JPEG photo.</summary>
    /// <exception cref="ImagePreprocessingException">The bytes are not an image that can be decoded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<PreparedImage> PrepareAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prepares <paramref name="image"/> knowing what it shows: a <see cref="ImageContent.Screenshot"/> keeps its small text readable
    /// (its empty margins cut off, a small one enlarged, a scaled one sharpened), where another picture is only scaled to the limits.
    /// Without an override every image is prepared as a picture.
    /// </summary>
    /// <exception cref="ImagePreprocessingException">The bytes are not an image that can be decoded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<PreparedImage> PrepareAsync(
        ReadOnlyMemory<byte> image, ImageContent content, CancellationToken cancellationToken = default) =>
        PrepareAsync(image, cancellationToken);
}
