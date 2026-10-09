namespace Assistant.Core.Imaging;

/// <summary>The encoding of an image, as its first bytes say (<see cref="ImageFormats.Detect"/>).</summary>
public enum ImageFormat
{
    /// <summary>Not one of the formats below, or not an image at all.</summary>
    Unknown = 0,

    /// <summary>PNG.</summary>
    Png = 1,

    /// <summary>JPEG.</summary>
    Jpeg = 2,

    /// <summary>GIF; only its first frame is a picture to a model.</summary>
    Gif = 3,

    /// <summary>Windows bitmap.</summary>
    Bmp = 4,

    /// <summary>WebP.</summary>
    WebP = 5,

    /// <summary>TIFF.</summary>
    Tiff = 6,
}

/// <summary>Tells image formats apart by their signatures, without decoding anything.</summary>
public static class ImageFormats
{
    /// <summary>The format <paramref name="data"/> is encoded in, from its signature.</summary>
    public static ImageFormat Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return ImageFormat.Png;
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return ImageFormat.Jpeg;
        }

        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return ImageFormat.Gif;
        }

        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return ImageFormat.WebP;
        }

        if (data.StartsWith("II*\0"u8) || data.StartsWith("MM\0*"u8))
        {
            return ImageFormat.Tiff;
        }

        return data.Length >= 14 && data.StartsWith("BM"u8) ? ImageFormat.Bmp : ImageFormat.Unknown;
    }

    /// <summary>The media type of <paramref name="format"/>, as a data URI or an HTTP header names it.</summary>
    public static string MediaType(ImageFormat format) => format switch
    {
        ImageFormat.Png => "image/png",
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Gif => "image/gif",
        ImageFormat.Bmp => "image/bmp",
        ImageFormat.WebP => "image/webp",
        ImageFormat.Tiff => "image/tiff",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Whether <paramref name="format"/> keeps every pixel exactly (PNG, GIF, BMP, TIFF), as screenshots and drawings
    /// need to keep small text sharp. JPEG and WebP are treated as photos.
    /// </summary>
    public static bool IsLossless(ImageFormat format) =>
        format is ImageFormat.Png or ImageFormat.Gif or ImageFormat.Bmp or ImageFormat.Tiff;
}
