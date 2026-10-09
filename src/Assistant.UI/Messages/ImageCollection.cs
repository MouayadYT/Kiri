namespace Assistant.UI.Messages;

/// <summary>
/// Images a request returned, such as photos or screenshots, shown directly as a gallery: square tiles three across,
/// continuing in rows below for as many as there are, as in the floating conversation's photo reference. The gallery
/// is as wide as a card but is not one; it is never drawn in the black card frame.
/// </summary>
public sealed class ImageCollection : MessageContent
{
    /// <summary>Creates a gallery of <paramref name="images"/>, in order.</summary>
    public ImageCollection(IEnumerable<ImageItem> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        Images = images.ToArray();
        if (Images.Any(image => image is null))
        {
            throw new ArgumentException("A gallery cannot hold a missing image.", nameof(images));
        }
    }

    /// <summary>The images, in the order they are shown: across, then down.</summary>
    public IReadOnlyList<ImageItem> Images { get; }

    /// <summary>A gallery reaches as far as a card.</summary>
    public override bool IsWide => true;
}
