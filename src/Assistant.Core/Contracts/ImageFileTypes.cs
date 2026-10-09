namespace Assistant.Core.Contracts;

/// <summary>
/// The kinds of image file the Assistant shows as pictures: the ones its gallery can decode and its model can be given.
/// A request for "screenshots", "photos" or "pictures" is a request for files with one of these extensions, so that every
/// result can be drawn as a tile.
/// </summary>
public static class ImageFileTypes
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif",
    };

    /// <summary>The extensions, lower case with their dot, in the order a search lists them.</summary>
    public static IReadOnlyList<string> Extensions { get; } =
    [
        ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif",
    ];

    /// <summary>Whether <paramref name="extension"/> (<c>.png</c> or <c>png</c>, in any case) is an image type.</summary>
    public static bool IsImageExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        var trimmed = extension.Trim();
        return Known.Contains(trimmed.StartsWith('.') ? trimmed : "." + trimmed);
    }

    /// <summary>
    /// Whether a search can only find image files: it asks for pictures, or for extensions that are all image types. That
    /// is what makes its answer a gallery rather than a list of files.
    /// </summary>
    public static bool IsImagesOnly(FileSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Kind is not (null or FileKind.Picture))
        {
            return false;
        }

        return query.Extensions.Count > 0
            ? query.Extensions.All(IsImageExtension)
            : query.Kind == FileKind.Picture;
    }
}
