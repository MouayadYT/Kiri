using System.Text;

namespace Assistant.Core.Imaging;

/// <summary>An image made ready for a vision model (<see cref="Contracts.IImagePreprocessor"/>).</summary>
/// <param name="Data">
/// The encoded image to send. When <see cref="IsOriginal"/> it is the very memory that was given; otherwise a new copy.
/// </param>
/// <param name="Format">How <paramref name="Data"/> is encoded: PNG or JPEG.</param>
/// <param name="Width">The width of the image sent, in pixels, upright.</param>
/// <param name="Height">The height of the image sent, in pixels, upright.</param>
/// <param name="SourceWidth">The width of the image given, in pixels, upright.</param>
/// <param name="SourceHeight">The height of the image given, in pixels, upright.</param>
public sealed record PreparedImage(
    ReadOnlyMemory<byte> Data,
    ImageFormat Format,
    int Width,
    int Height,
    int SourceWidth,
    int SourceHeight)
{
    /// <summary>
    /// Whether the image is sent exactly as it was given, because it was already within the limits and in a format
    /// every engine reads.
    /// </summary>
    public bool IsOriginal { get; init; }

    /// <summary>Whether the image is sent at another size than it was given: smaller, or, for a small screenshot, larger.</summary>
    public bool IsResized => Width != SourceWidth || Height != SourceHeight;

    /// <summary>Whether the empty margins of a screenshot were cut off, so that what is sent is a part of what was given.</summary>
    public bool IsTrimmed { get; init; }

    // Pixels are private content (PROJECT_SPEC §3.2): ToString, and so any log, holds sizes only.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            $"Bytes = {Data.Length}, Format = {Format}, Width = {Width}, Height = {Height}, " +
            $"SourceWidth = {SourceWidth}, SourceHeight = {SourceHeight}, IsOriginal = {IsOriginal}, IsTrimmed = {IsTrimmed}");
        return true;
    }
}
