using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Assistant.UI.Messages;

/// <summary>
/// Decodes image files into small thumbnails for a gallery, upright as a camera's orientation tag says. Decoding at
/// the size that is shown keeps memory and time low however large the file is.
/// </summary>
internal static class ImageThumbnail
{
    // Where JPEG and TIFF files, and WIC's HEIF decoder, keep the EXIF orientation tag.
    private static readonly string[] OrientationQueries = ["/app1/ifd/{ushort=274}", "/ifd/{ushort=274}"];

    /// <summary>
    /// Decodes the image at <paramref name="path"/> so that its shorter side is at most <paramref name="size"/> pixels.
    /// It can be called on any thread; the result is frozen.
    /// </summary>
    /// <returns>The thumbnail, or <see langword="null"/> when the file cannot be read as an image.</returns>
    public static BitmapSource? Load(string path, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            var orientation = ReadOrientation(frame);

            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (Math.Min(frame.PixelWidth, frame.PixelHeight) > size)
            {
                // Setting one side keeps the aspect ratio; the shorter side is the one that fills a square tile.
                if (frame.PixelWidth <= frame.PixelHeight) image.DecodePixelWidth = size;
                else image.DecodePixelHeight = size;
            }

            image.EndInit();
            var thumbnail = Orient(image, orientation);
            thumbnail.Freeze();
            return thumbnail;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException
            or FormatException or ArgumentException or InvalidOperationException or ExternalException
            or OverflowException)
        {
            // An unreadable image leaves its tile empty. Nothing is logged, since paths are private (PROJECT_SPEC §3.2).
            return null;
        }
    }

    /// <summary>
    /// Turns an image stored with EXIF orientation <paramref name="orientation"/>, from 1 to 8, upright. Other values
    /// leave it as it is.
    /// </summary>
    internal static BitmapSource Orient(BitmapSource image, int orientation)
    {
        // Each orientation says how to turn the stored image to show it: whether to mirror it first, then how far to
        // rotate it clockwise.
        var (mirror, angle) = orientation switch
        {
            2 => (true, 0),
            3 => (false, 180),
            4 => (true, 180),
            5 => (true, 270),
            6 => (false, 90),
            7 => (true, 90),
            8 => (false, 270),
            _ => (false, 0),
        };

        if (!mirror && angle == 0)
        {
            return image;
        }

        var transform = new TransformGroup();
        if (mirror) transform.Children.Add(new ScaleTransform(-1, 1));
        if (angle != 0) transform.Children.Add(new RotateTransform(angle));
        return new TransformedBitmap(image, transform);
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                foreach (var query in OrientationQueries)
                {
                    if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is ushort orientation)
                    {
                        return orientation;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException
            or ArgumentException or ExternalException)
        {
            // Formats without metadata, or with metadata WIC cannot read, are shown as stored.
        }

        return 1;
    }
}
