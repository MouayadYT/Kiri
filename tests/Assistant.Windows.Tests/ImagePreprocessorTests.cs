using System.Runtime.InteropServices;
using Assistant.Core.Imaging;
using Assistant.Windows.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// Preparing images for a vision model with the Windows codecs: what is sent as it is, what is scaled down and
/// re-encoded, turned upright or laid on white, and that the image given never changes.
/// </summary>
public sealed class ImagePreprocessorTests
{
    private readonly ImagePreprocessor _preprocessor = new();

    [Fact]
    public async Task ASmallPngOrJpeg_IsSentAsItIs_TheVeryMemoryGiven()
    {
        foreach (var encoder in new[] { BitmapEncoder.PngEncoderId, BitmapEncoder.JpegEncoderId })
        {
            var image = await EncodeAsync(encoder, 320, 200, Gradient);

            var prepared = await _preprocessor.PrepareAsync(image);

            Assert.True(prepared.IsOriginal);
            Assert.False(prepared.IsResized);
            Assert.True(MemoryMarshal.TryGetArray(prepared.Data, out var segment));
            Assert.Same(image, segment.Array);
            Assert.Equal((320, 200, 320, 200), (prepared.Width, prepared.Height, prepared.SourceWidth, prepared.SourceHeight));
            Assert.Equal(ImageFormats.Detect(image), prepared.Format);
        }
    }

    [Fact]
    public async Task ALargeScreenshot_IsScaledDownToTheLimits_AsAPng_AndTheOriginalIsUntouched()
    {
        var image = await EncodeAsync(BitmapEncoder.PngEncoderId, 1600, 1200, Gradient);
        var copy = image.ToArray();

        var prepared = await _preprocessor.PrepareAsync(image);

        Assert.Equal(copy, image);
        Assert.False(prepared.IsOriginal);
        Assert.True(prepared.IsResized);
        Assert.Equal(ImageFormat.Png, prepared.Format);
        Assert.Equal(ImageFormat.Png, ImageFormats.Detect(prepared.Data.Span));
        Assert.Equal((1182, 886), (prepared.Width, prepared.Height));
        Assert.Equal((1600, 1200), (prepared.SourceWidth, prepared.SourceHeight));
        var (width, height, _) = await DecodeAsync(prepared.Data.ToArray());
        Assert.Equal((1182, 886), (width, height));
    }

    [Fact]
    public async Task ALargePhoto_IsScaledDownAsAJpeg_KeepingItsPicture()
    {
        var image = await EncodeAsync(BitmapEncoder.JpegEncoderId, 4000, 3000, Quadrants);

        var prepared = await _preprocessor.PrepareAsync(image);

        Assert.Equal(ImageFormat.Jpeg, prepared.Format);
        Assert.Equal(ImagePreprocessingOptions.Default.FitWithin(4000, 3000), (prepared.Width, prepared.Height));
        Assert.True(prepared.Data.Length < image.Length);
        var (width, height, pixels) = await DecodeAsync(prepared.Data.ToArray());
        AssertQuadrants(width, height, pixels, [Red, Green, Blue, Yellow]);
    }

    [Theory]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("tiff")]
    public async Task OtherFormats_AreReEncodedAsPng_AtTheirSize(string kind)
    {
        var encoder = kind switch
        {
            "bmp" => BitmapEncoder.BmpEncoderId,
            "gif" => BitmapEncoder.GifEncoderId,
            _ => BitmapEncoder.TiffEncoderId,
        };
        var image = await EncodeAsync(encoder, 64, 48, Quadrants);

        var prepared = await _preprocessor.PrepareAsync(image);

        Assert.False(prepared.IsOriginal);
        Assert.False(prepared.IsResized);
        Assert.Equal(ImageFormat.Png, prepared.Format);
        var (width, height, pixels) = await DecodeAsync(prepared.Data.ToArray());
        AssertQuadrants(width, height, pixels, [Red, Green, Blue, Yellow]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task APhotoTakenSideways_IsTurnedUpright_AsWindowsShowsIt(int orientation)
    {
        var image = await EncodeAsync(BitmapEncoder.JpegEncoderId, 96, 48, Quadrants, orientation: (ushort)orientation);

        var prepared = await _preprocessor.PrepareAsync(image);

        // What Windows itself shows for the photo, which honors its orientation.
        var (expectedWidth, expectedHeight, expected) = await DecodeAsync(image, ExifOrientationMode.RespectExifOrientation);
        var (width, height, pixels) = await DecodeAsync(prepared.Data.ToArray());
        Assert.Equal((expectedWidth, expectedHeight), (width, height));
        Assert.Equal((expectedWidth, expectedHeight), (prepared.Width, prepared.Height));
        Assert.False(prepared.IsOriginal);
        Assert.Equal(QuadrantColors(expectedWidth, expectedHeight, expected), QuadrantColors(width, height, pixels));
    }

    [Fact]
    public async Task TransparentParts_AreLaidOnWhite_WhenTheImageIsReEncoded()
    {
        var image = await EncodeAsync(
            BitmapEncoder.PngEncoderId, 1500, 1000, (x, _) => x < 750 ? new Pixel(0, 0, 0, 0) : new Pixel(255, 0, 0, 128));

        var prepared = await _preprocessor.PrepareAsync(image);

        var (width, height, pixels) = await DecodeAsync(prepared.Data.ToArray());
        var clear = PixelAt(pixels, width, width / 4, height / 2);
        var halfRed = PixelAt(pixels, width, width * 3 / 4, height / 2);
        Assert.Equal(new Pixel(255, 255, 255, 255), clear);
        Assert.InRange(halfRed.R, 250, 255);
        Assert.InRange(halfRed.G, 120, 135);
        Assert.InRange(halfRed.B, 120, 135);
    }

    [Fact]
    public async Task AnImageTooLargeInBytes_IsCompressed_AsAJpegWhenAPngWouldStillBeTooLarge()
    {
        var random = new Random(39);
        var noise = new byte[600 * 400 * 3];
        random.NextBytes(noise);
        var image = await EncodeAsync(
            BitmapEncoder.PngEncoderId, 600, 400,
            (x, y) => new Pixel(noise[((y * 600) + x) * 3], noise[(((y * 600) + x) * 3) + 1], noise[(((y * 600) + x) * 3) + 2], 255));
        var options = new ImagePreprocessingOptions { MaxEncodedBytes = 200_000 };
        Assert.True(image.Length > options.MaxEncodedBytes);

        var prepared = await new ImagePreprocessor(options).PrepareAsync(image);

        Assert.Equal(ImageFormat.Jpeg, prepared.Format);
        Assert.False(prepared.IsResized);
        Assert.True(prepared.Data.Length <= options.MaxEncodedBytes, $"{prepared.Data.Length} bytes");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an image at all")]
    [InlineData("89504E470D0A1A0A00000000")]
    public async Task BytesThatAreNotAnImage_CannotBePrepared(string content)
    {
        var bytes = content.StartsWith("89504E47", StringComparison.Ordinal)
            ? Convert.FromHexString(content)
            : System.Text.Encoding.UTF8.GetBytes(content);

        var failure = await Assert.ThrowsAsync<ImagePreprocessingException>(() => _preprocessor.PrepareAsync(bytes));

        Assert.DoesNotContain(content.Length > 0 ? content : "\0", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_StopsIt()
    {
        var image = await EncodeAsync(BitmapEncoder.PngEncoderId, 64, 64, Gradient);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _preprocessor.PrepareAsync(image, cancelled.Token));
    }

    [Fact]
    public async Task ImagesCanBePreparedFromManyThreadsAtOnce()
    {
        var image = await EncodeAsync(BitmapEncoder.JpegEncoderId, 2400, 1600, Quadrants);

        var prepared = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _preprocessor.PrepareAsync(image))));

        Assert.All(prepared, each => Assert.Equal(prepared[0].Data.ToArray(), each.Data.ToArray()));
    }

    [Theory]
    [InlineData(1, "abc|def")]
    [InlineData(2, "cba|fed")]
    [InlineData(3, "fed|cba")]
    [InlineData(4, "def|abc")]
    [InlineData(5, "ad|be|cf")]
    [InlineData(6, "da|eb|fc")]
    [InlineData(7, "fc|eb|da")]
    [InlineData(8, "cf|be|ad")]
    public void Orientation_TurnsStoredPixelsUpright(int orientation, string expected)
    {
        // A 3 x 2 image whose pixels are the letters a to f, row by row.
        var stored = "abcdef".SelectMany(letter => new[] { (byte)letter, (byte)0, (byte)0, (byte)255 }).ToArray();

        var upright = PixelOrientation.Apply(stored, 3, 2, orientation);

        var (width, _) = PixelOrientation.Upright(3, 2, orientation);
        var letters = upright.Where((_, index) => index % 4 == 0).Select(value => (char)value).ToArray();
        Assert.Equal(expected, string.Join('|', letters.Chunk(width).Select(row => new string(row))));
    }

    [Fact]
    public void FlatteningOntoWhite_AddsWhatEachPixelLetsThrough()
    {
        byte[] premultiplied = [0, 0, 0, 0, 10, 20, 128, 128, 1, 2, 3, 255];

        ImagePreprocessor.FlattenOntoWhite(premultiplied);

        Assert.Equal([255, 255, 255, 255, 137, 147, 255, 255, 1, 2, 3, 255], premultiplied);
    }

    // ---- Test images ---------------------------------------------------------------------------------------------

    private static readonly Pixel Red = new(255, 0, 0, 255);
    private static readonly Pixel Green = new(0, 255, 0, 255);
    private static readonly Pixel Blue = new(0, 0, 255, 255);
    private static readonly Pixel Yellow = new(255, 255, 0, 255);

    private static Pixel Gradient(int x, int y) => new((byte)(x * 7), (byte)(y * 3), (byte)((x + y) / 5), 255);

    // Four colored quadrants: red, green over blue, yellow; big enough to survive JPEG and scaling.
    private static Pixel Quadrants(int x, int y, int width, int height) =>
        (x < width / 2, y < height / 2) switch
        {
            (true, true) => Red,
            (false, true) => Green,
            (true, false) => Blue,
            _ => Yellow,
        };

    private static Task<byte[]> EncodeAsync(Guid encoderId, int width, int height, Func<int, int, int, int, Pixel> pixel, ushort? orientation = null) =>
        EncodeAsync(encoderId, width, height, (x, y) => pixel(x, y, width, height), orientation);

    private static async Task<byte[]> EncodeAsync(Guid encoderId, int width, int height, Func<int, int, Pixel> pixel, ushort? orientation = null)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                var at = ((y * width) + x) * 4;
                (bgra[at], bgra[at + 1], bgra[at + 2], bgra[at + 3]) = (b, g, r, a);
            }
        }

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, bgra);
        if (orientation is { } value)
        {
            await encoder.BitmapProperties.SetPropertiesAsync(
            [
                new KeyValuePair<string, BitmapTypedValue>("System.Photo.Orientation", new BitmapTypedValue(value, PropertyType.UInt16)),
            ]);
        }

        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        using var reader = stream.GetInputStreamAt(0).AsStreamForRead();
        await reader.ReadExactlyAsync(bytes);
        return bytes;
    }

    private static async Task<(int Width, int Height, byte[] Bgra)> DecodeAsync(
        byte[] image, ExifOrientationMode orientation = ExifOrientationMode.IgnoreExifOrientation)
    {
        using var stream = new MemoryStream(image, writable: false).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(), orientation, ColorManagementMode.DoNotColorManage);
        var oriented = orientation == ExifOrientationMode.RespectExifOrientation;
        return ((int)(oriented ? decoder.OrientedPixelWidth : decoder.PixelWidth),
            (int)(oriented ? decoder.OrientedPixelHeight : decoder.PixelHeight),
            pixels.DetachPixelData());
    }

    private static Pixel PixelAt(byte[] bgra, int width, int x, int y)
    {
        var at = ((y * width) + x) * 4;
        return new Pixel(bgra[at + 2], bgra[at + 1], bgra[at], bgra[at + 3]);
    }

    // The color at the middle of each quadrant, each channel rounded to on or off: JPEG and scaling blur the rest.
    private static Pixel[] QuadrantColors(int width, int height, byte[] bgra) =>
        [
            .. new[] { (1, 1), (3, 1), (1, 3), (3, 3) }.Select(point =>
            {
                var pixel = PixelAt(bgra, width, width * point.Item1 / 4, height * point.Item2 / 4);
                return new Pixel(Snap(pixel.R), Snap(pixel.G), Snap(pixel.B), 255);
            }),
        ];

    private static void AssertQuadrants(int width, int height, byte[] bgra, Pixel[] expected) =>
        Assert.Equal(expected, QuadrantColors(width, height, bgra));

    private static byte Snap(byte channel) => channel >= 128 ? (byte)255 : (byte)0;

    private readonly record struct Pixel(byte R, byte G, byte B, byte A);
}
