using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Assistant.UI.Messages;

/// <summary>
/// The pictures on the clipboard, for pasting into a question: a picture that was copied (from a web page, an image editor, a screenshot tool) as a
/// picture in memory, and image files that were copied in File Explorer as those files. A copied picture is encoded as PNG and stays in memory: nothing
/// is written anywhere, and it is read only when the user pastes. Text is never taken from here: where the clipboard has text, pasting pastes the text.
/// </summary>
public static class ClipboardPictures
{
    /// <summary>The most pictures one paste attaches.</summary>
    public const int MaxPictures = 8;

    // A picture larger than this is not one to hand to a model: it is left on the clipboard.
    private const long MaxPixels = 64L * 1000 * 1000;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff",
    };

    /// <summary>Whether the clipboard holds a picture, or image files, and no text that pasting would paste instead.</summary>
    public static bool HasPictures()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                return false;
            }

            return Clipboard.ContainsImage() || (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().Any(IsPicture));
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException or OutOfMemoryException)
        {
            // Another program has the clipboard open, or what is on it cannot be read: there is nothing to paste.
            return false;
        }
    }

    /// <summary>The pictures on the clipboard, ready to attach; none when it holds none, or they cannot be read.</summary>
    public static IReadOnlyList<ImageItem> Read()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(IsPicture).Take(MaxPictures)
                    .Select(path => new ImageItem(Path.GetFileName(path), path)).ToList();
                if (files.Count > 0)
                {
                    return files;
                }
            }

            return Clipboard.ContainsImage() && Clipboard.GetImage() is { } picture && FromBitmap(picture) is { } pasted ? [pasted] : [];
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException or OutOfMemoryException or NotSupportedException or IOException)
        {
            return [];
        }
    }

    /// <summary>The picture <paramref name="bitmap"/> as an image to attach: encoded as PNG, with a small copy for its chip. <see langword="null"/> when it is empty or too large.</summary>
    public static ImageItem? FromBitmap(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || (long)bitmap.PixelWidth * bitmap.PixelHeight > MaxPixels)
        {
            return null;
        }

        // What other programs put on the clipboard often has an alpha channel that means nothing (every pixel clear): the picture is taken as opaque.
        BitmapSource opaque = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        opaque.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(opaque));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        var scale = Math.Min(1.0, ImageItem.ThumbnailSize / (double)Math.Min(opaque.PixelWidth, opaque.PixelHeight));
        BitmapSource thumbnail = scale < 1 ? new TransformedBitmap(opaque, new ScaleTransform(scale, scale)) : opaque;
        if (thumbnail.CanFreeze && !thumbnail.IsFrozen)
        {
            thumbnail.Freeze();
        }

        return ImageItem.FromPasted("Pasted image", stream.ToArray(), thumbnail, opaque.PixelWidth, opaque.PixelHeight);
    }

    private static bool IsPicture(string path) => Extensions.Contains(Path.GetExtension(path)) && File.Exists(path);
}
