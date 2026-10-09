using System.Runtime.InteropServices;
using Assistant.Core.Contracts;
using Assistant.Core.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Assistant.Windows.Imaging;

/// <summary>
/// Prepares images for a vision model with the Windows imaging codecs (<c>Windows.Graphics.Imaging</c>, over WIC), in
/// memory only (PROJECT_SPEC §3.1): the <see cref="IImagePreprocessor"/> of the app.
/// </summary>
/// <remarks>
/// <para>
/// A PNG or JPEG that is upright, within the <see cref="ImagePreprocessingOptions"/> and small enough is sent as it is,
/// the very memory given. Anything else is decoded, turned upright as its EXIF orientation says, scaled down with a
/// high-quality filter to fit the limits, laid on white where it is transparent (a model reads colors, not alpha), and
/// encoded afresh into new memory: as PNG when the original kept every pixel (screenshots and drawings, whose small
/// text must stay sharp) and that PNG is small enough, and otherwise as JPEG. Formats other than PNG and JPEG that
/// Windows can decode, such as BMP, GIF, TIFF, WebP or HEIF with its extension, are always re-encoded, since an engine
/// may not read them.
/// </para>
/// <para>
/// The image given is only ever read: it is wrapped in a read-only stream and never copied back into.
/// </para>
/// </remarks>
public sealed class ImagePreprocessor : IImagePreprocessor
{
    private const string OrientationProperty = "System.Photo.Orientation";

    // When a JPEG at the configured quality is still too large, it is written once more at this quality.
    private const double FallbackJpegQuality = 0.7;

    private readonly ImagePreprocessingOptions _options;

    /// <summary>Creates a preprocessor with the <see cref="ImagePreprocessingOptions.Default"/> limits.</summary>
    public ImagePreprocessor()
        : this(ImagePreprocessingOptions.Default)
    {
    }

    /// <summary>Creates a preprocessor with the given limits.</summary>
    public ImagePreprocessor(ImagePreprocessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc/>
    public async Task<PreparedImage> PrepareAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
    {
        if (image.IsEmpty)
        {
            throw new ImagePreprocessingException("The image is empty.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var format = ImageFormats.Detect(image.Span);
        using var source = ReadOnlyStream(image).AsRandomAccessStream();
        var decoder = await DecodeAsync(source, cancellationToken).ConfigureAwait(false);

        var orientation = await OrientationOfAsync(decoder).ConfigureAwait(false);
        var (width, height) = PixelOrientation.Upright((int)decoder.PixelWidth, (int)decoder.PixelHeight, orientation);
        if (format is ImageFormat.Png or ImageFormat.Jpeg
            && orientation == PixelOrientation.Normal
            && _options.IsWithin(width, height)
            && image.Length <= _options.MaxEncodedBytes)
        {
            return new PreparedImage(image, format, width, height, width, height) { IsOriginal = true };
        }

        var (targetWidth, targetHeight) = _options.FitWithin(width, height);
        var pixels = await ReadPixelsAsync(decoder, orientation, targetWidth, targetHeight, cancellationToken)
            .ConfigureAwait(false);
        FlattenOntoWhite(pixels);
        var (data, encoded) = await EncodeAsync(pixels, targetWidth, targetHeight, ImageFormats.IsLossless(format), cancellationToken)
            .ConfigureAwait(false);
        return new PreparedImage(data, encoded, targetWidth, targetHeight, width, height);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A screenshot keeps its small text readable (<see cref="ImageContent.Screenshot"/>): the empty margins around what is on it are
    /// cut off, so the pixels go to the content; one that is small is enlarged, with a smooth filter, until its text has enough pixels to
    /// read, and one that is large is scaled down with the area filter, which keeps the most of thin strokes; a scaled screenshot is
    /// sharpened a little. It is always encoded as PNG, so that no compression blurs the text, unless that is too large to send. One
    /// that needs none of this, or is not upright, is prepared as any picture is.
    /// </remarks>
    public async Task<PreparedImage> PrepareAsync(
        ReadOnlyMemory<byte> image, ImageContent content, CancellationToken cancellationToken = default)
    {
        if (content != ImageContent.Screenshot)
        {
            return await PrepareAsync(image, cancellationToken).ConfigureAwait(false);
        }

        if (image.IsEmpty)
        {
            throw new ImagePreprocessingException("The image is empty.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var format = ImageFormats.Detect(image.Span);
        using var source = ReadOnlyStream(image).AsRandomAccessStream();
        var decoder = await DecodeAsync(source, cancellationToken).ConfigureAwait(false);
        if (await OrientationOfAsync(decoder).ConfigureAwait(false) != PixelOrientation.Normal)
        {
            return await PrepareAsync(image, cancellationToken).ConfigureAwait(false);
        }

        var width = (int)decoder.PixelWidth;
        var height = (int)decoder.PixelHeight;
        var whole = new PixelArea(0, 0, width, height);
        var area = whole;
        if ((long)width * height <= _options.ScreenshotMaxTrimPixels)
        {
            var full = await ReadPixelsAsync(decoder, PixelOrientation.Normal, width, height, cancellationToken).ConfigureAwait(false);
            FlattenOntoWhite(full);
            area = ScreenshotLayout.ContentArea(full, width, height);
        }

        var (targetWidth, targetHeight) = ScreenshotLayout.TargetSize(area.Width, area.Height, _options);
        var trimmed = area != whole;
        var resized = targetWidth != area.Width || targetHeight != area.Height;
        if (!trimmed && !resized && format is ImageFormat.Png or ImageFormat.Jpeg && image.Length <= _options.MaxEncodedBytes)
        {
            return new PreparedImage(image, format, width, height, width, height) { IsOriginal = true };
        }

        var pixels = await ReadAreaAsync(decoder, whole, area, targetWidth, targetHeight, cancellationToken).ConfigureAwait(false);
        FlattenOntoWhite(pixels);
        if (resized)
        {
            ScreenshotLayout.Sharpen(pixels, targetWidth, targetHeight, _options.ScreenshotSharpen);
        }

        var (data, encoded) = await EncodeAsync(pixels, targetWidth, targetHeight, lossless: true, cancellationToken).ConfigureAwait(false);
        return new PreparedImage(data, encoded, targetWidth, targetHeight, width, height) { IsTrimmed = trimmed };
    }

    // Premultiplied BGRA pixels of one area of the picture, scaled so that the area is target by target pixels: the whole picture is
    // scaled by the codec as it decodes, and the area cut out of the scaled picture. A smooth filter enlarges, the area filter shrinks.
    private static async Task<byte[]> ReadAreaAsync(
        BitmapDecoder decoder, PixelArea whole, PixelArea area, int targetWidth, int targetHeight, CancellationToken cancellationToken)
    {
        var scaleX = (double)targetWidth / area.Width;
        var scaleY = (double)targetHeight / area.Height;
        var scaledWidth = Math.Max(targetWidth, (int)Math.Round(whole.Width * scaleX));
        var scaledHeight = Math.Max(targetHeight, (int)Math.Round(whole.Height * scaleY));
        var left = Math.Clamp((int)Math.Round(area.X * scaleX), 0, scaledWidth - targetWidth);
        var top = Math.Clamp((int)Math.Round(area.Y * scaleY), 0, scaledHeight - targetHeight);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)scaledWidth,
            ScaledHeight = (uint)scaledHeight,
            Bounds = new BitmapBounds { X = (uint)left, Y = (uint)top, Width = (uint)targetWidth, Height = (uint)targetHeight },
            InterpolationMode = scaleX > 1 || scaleY > 1 ? BitmapInterpolationMode.Cubic : BitmapInterpolationMode.Fant,
        };

        try
        {
            var provider = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.ColorManageToSRgb)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            return provider.DetachPixelData();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ImagePreprocessingException("The image could not be decoded.", exception);
        }
    }

    // The given memory as a stream that cannot be written, so the original can never change; no copy when it is an array.
    private static MemoryStream ReadOnlyStream(ReadOnlyMemory<byte> image) =>
        MemoryMarshal.TryGetArray(image, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(image.ToArray(), writable: false);

    private static async Task<BitmapDecoder> DecodeAsync(IRandomAccessStream source, CancellationToken cancellationToken)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(source).AsTask(cancellationToken).ConfigureAwait(false);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0)
            {
                throw new ImagePreprocessingException("The image has no pixels.");
            }

            return decoder;
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or ImagePreprocessingException))
        {
            // Not an image Windows can decode (WINCODEC_ERR_COMPONENTNOTFOUND), or a damaged one.
            throw new ImagePreprocessingException("The image could not be decoded.", exception);
        }
    }

    // The EXIF orientation of a photo; formats without one, and images that do not say, are upright.
    private static async Task<int> OrientationOfAsync(BitmapDecoder decoder)
    {
        try
        {
            var properties = await decoder.BitmapProperties.GetPropertiesAsync([OrientationProperty]);
            return properties.TryGetValue(OrientationProperty, out var value) && value.Value is ushort orientation
                ? PixelOrientation.Normalize(orientation)
                : PixelOrientation.Normal;
        }
        catch (Exception exception) when (exception is COMException or NotSupportedException or ArgumentException)
        {
            return PixelOrientation.Normal;
        }
    }

    // Upright BGRA pixels, premultiplied, at the target size: scaled by the codec as it decodes (for a JPEG that is far
    // cheaper than decoding it whole), then turned upright here, so the size asked for is in the stored image's axes.
    private static async Task<byte[]> ReadPixelsAsync(
        BitmapDecoder decoder, int orientation, int targetWidth, int targetHeight, CancellationToken cancellationToken)
    {
        var (storedWidth, storedHeight) = PixelOrientation.Upright(targetWidth, targetHeight, orientation);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)storedWidth,
            ScaledHeight = (uint)storedHeight,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        try
        {
            var provider = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.ColorManageToSRgb)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            var stored = provider.DetachPixelData();
            return PixelOrientation.Apply(stored, storedWidth, storedHeight, orientation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ImagePreprocessingException("The image could not be decoded.", exception);
        }
    }

    // Transparent parts become white, as the image looks on a page. With premultiplied alpha that is adding what the
    // pixel lets through.
    internal static void FlattenOntoWhite(Span<byte> bgra)
    {
        for (var index = 0; index + 3 < bgra.Length; index += 4)
        {
            var clear = (byte)(255 - bgra[index + 3]);
            if (clear == 0)
            {
                continue;
            }

            bgra[index] += clear;
            bgra[index + 1] += clear;
            bgra[index + 2] += clear;
            bgra[index + 3] = 255;
        }
    }

    private async Task<(byte[] Data, ImageFormat Format)> EncodeAsync(
        byte[] pixels, int width, int height, bool lossless, CancellationToken cancellationToken)
    {
        if (lossless)
        {
            var png = await EncodeAsync(pixels, width, height, BitmapEncoder.PngEncoderId, null, cancellationToken)
                .ConfigureAwait(false);
            if (png.Length <= _options.MaxEncodedBytes)
            {
                return (png, ImageFormat.Png);
            }
        }

        var jpeg = await EncodeAsync(pixels, width, height, BitmapEncoder.JpegEncoderId, _options.JpegQuality, cancellationToken)
            .ConfigureAwait(false);
        if (jpeg.Length > _options.MaxEncodedBytes && _options.JpegQuality > FallbackJpegQuality)
        {
            jpeg = await EncodeAsync(pixels, width, height, BitmapEncoder.JpegEncoderId, FallbackJpegQuality, cancellationToken)
                .ConfigureAwait(false);
        }

        return (jpeg, ImageFormat.Jpeg);
    }

    /// <summary>Encodes opaque BGRA <paramref name="pixels"/>, tightly packed, with the Windows codec <paramref name="encoderId"/>.</summary>
    internal static async Task<byte[]> EncodeAsync(
        byte[] pixels, int width, int height, Guid encoderId, double? quality, CancellationToken cancellationToken)
    {
        using var output = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet();
        if (quality is { } imageQuality)
        {
            options["ImageQuality"] = new BitmapTypedValue((float)Math.Clamp(imageQuality, 0, 1), global::Windows.Foundation.PropertyType.Single);
        }

        var encoder = await BitmapEncoder.CreateAsync(encoderId, output, options).AsTask(cancellationToken).ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

        var data = new byte[output.Size];
        using var reader = output.GetInputStreamAt(0).AsStreamForRead();
        await reader.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        return data;
    }
}
