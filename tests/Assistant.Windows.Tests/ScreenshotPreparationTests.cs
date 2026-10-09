using System.Runtime.InteropServices;
using Assistant.Core.Imaging;
using Assistant.Windows.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// Making a screenshot ready for a vision model with the Windows codecs (PROJECT_SPEC §4.6, §5.5): empty margins cut off, a small one
/// enlarged until its text has pixels to read, a large one scaled down and sharpened a little, and one that needs none of it sent as it
/// is, in memory only and without ever changing the image given.
/// </summary>
public sealed class ScreenshotPreparationTests
{
    private readonly ImagePreprocessor _preprocessor = new();

    [Fact]
    public async Task TheEmptyMarginsAreCutOff_AndWhatIsOnTheScreenshotIsKept()
    {
        // A dialog's worth of text-like marks, in a frame, in the middle of a large white capture.
        var image = await PngAsync(1800, 1200, (x, y) => Marks(x - 450, y - 300, 900, 600) ? Black : White);
        var copy = image.ToArray();

        var prepared = await _preprocessor.PrepareAsync(image, ImageContent.Screenshot);

        Assert.Equal(copy, image);
        Assert.True(prepared.IsTrimmed);
        Assert.False(prepared.IsOriginal);
        Assert.Equal(ImageFormat.Png, prepared.Format);
        Assert.Equal((1800, 1200), (prepared.SourceWidth, prepared.SourceHeight));

        // The frame spans 900 x 600 plus the room left around it, which is 8 pixels on each side.
        Assert.Equal((916, 616), (prepared.Width, prepared.Height));
        var (width, height, pixels) = await DecodeAsync(prepared.Data.ToArray());
        Assert.Equal((916, 616), (width, height));

        // The picture is the part of the capture that was marked, pixel for pixel, with the margin around it.
        for (var y = 0; y < height; y += 7)
        {
            for (var x = 0; x < width; x += 5)
            {
                var expected = Marks(x - 8, y - 8, 900, 600) ? Black : White;
                Assert.Equal(expected, PixelAt(pixels, width, x, y));
            }
        }
    }

    [Fact]
    public async Task ASmallScreenshot_IsEnlarged_SoItsTextHasPixelsToRead_KeepingItsProportions()
    {
        // 300 x 200: a few lines of small text, with a tint behind it that fills the picture, so there is no margin to cut.
        var image = await PngAsync(300, 200, (x, y) => y % 12 < 4 && x % 9 < 6 ? Black : Tint);

        var prepared = await _preprocessor.PrepareAsync(image, ImageContent.Screenshot);

        Assert.False(prepared.IsTrimmed);
        Assert.Equal(ScreenshotLayout.TargetSize(300, 200, ImagePreprocessingOptions.Default), (prepared.Width, prepared.Height));
        Assert.Equal((627, 418), (prepared.Width, prepared.Height));
        Assert.True(prepared.IsResized);
        var (width, height, pixels) = await DecodeAsync(prepared.Data.ToArray());
        Assert.Equal((627, 418), (width, height));

        // Dark text stays dark and the tint stays the tint: nothing is smeared into a gray wash.
        var dark = 0;
        var tint = 0;
        for (var index = 0; index < width * height; index++)
        {
            var value = pixels[index * 4 + 2];
            dark += value < 60 ? 1 : 0;
            tint += value > 200 ? 1 : 0;
        }

        Assert.InRange(dark / (double)(width * height), 0.15, 0.30);
        Assert.InRange(tint / (double)(width * height), 0.55, 0.80);
    }

    [Fact]
    public async Task ALargeScreenshot_IsScaledToTheLimits_AndItsThinTextIsSharperThanAPhotosWouldBe()
    {
        // Lines of one-pixel text-like strokes across 3200 x 1800.
        var image = await PngAsync(3200, 1800, (x, y) => y % 10 < 2 && x % 24 < 14 ? Black : White);

        var screenshot = await _preprocessor.PrepareAsync(image, ImageContent.Screenshot);
        var picture = await _preprocessor.PrepareAsync(image, ImageContent.Picture);

        var expected = ImagePreprocessingOptions.Default.FitWithin(3200, 1800);
        Assert.Equal(expected, (screenshot.Width, screenshot.Height));
        Assert.Equal(expected, (picture.Width, picture.Height));
        Assert.Equal(ImageFormat.Png, screenshot.Format);
        var (sw, sh, screenshotPixels) = await DecodeAsync(screenshot.Data.ToArray());
        var (pw, ph, picturePixels) = await DecodeAsync(picture.Data.ToArray());
        Assert.Equal((sw, sh), (pw, ph));

        // Sharper: the strokes have more contrast against the paper than the plain scaling leaves them.
        Assert.True(Contrast(screenshotPixels, sw, sh) > Contrast(picturePixels, pw, ph) * 1.05);
    }

    [Fact]
    public async Task AScreenshotThatNeedsNone_IsSentAsItIs_TheVeryMemoryGiven()
    {
        // 800 x 600, filled edge to edge with a gradient: nothing to cut, enough pixels, within the limits.
        var image = await PngAsync(800, 600, (x, y) => new Pixel((byte)(x / 4), (byte)(y / 3), (byte)((x + y) / 6), 255));

        var prepared = await _preprocessor.PrepareAsync(image, ImageContent.Screenshot);

        Assert.True(prepared.IsOriginal);
        Assert.False(prepared.IsTrimmed);
        Assert.False(prepared.IsResized);
        Assert.True(MemoryMarshal.TryGetArray(prepared.Data, out var segment));
        Assert.Same(image, segment.Array);
    }

    [Fact]
    public async Task TransparentMarginsAreWhiteMargins_SoTheyAreCutOffToo()
    {
        var image = await PngAsync(1800, 1200, (x, y) => Marks(x - 450, y - 300, 900, 600) ? Black : new Pixel(0, 0, 0, 0));

        var prepared = await _preprocessor.PrepareAsync(image, ImageContent.Screenshot);

        Assert.True(prepared.IsTrimmed);
        Assert.Equal((916, 616), (prepared.Width, prepared.Height));
    }

    [Fact]
    public async Task AScreenshotThatIsNotAnImage_IsRefusedLikeAnyOther_AndNothingIsPrepared()
    {
        await Assert.ThrowsAsync<ImagePreprocessingException>(
            () => _preprocessor.PrepareAsync("not an image"u8.ToArray(), ImageContent.Screenshot));
        await Assert.ThrowsAsync<ImagePreprocessingException>(
            () => _preprocessor.PrepareAsync(ReadOnlyMemory<byte>.Empty, ImageContent.Screenshot));
    }

    [Fact]
    public async Task APicture_IsPreparedAsBefore_WhateverTheScreenshotPathDoes()
    {
        var image = await PngAsync(300, 200, (x, y) => y % 12 < 4 && x % 9 < 6 ? Black : Tint);

        var picture = await _preprocessor.PrepareAsync(image, ImageContent.Picture);

        Assert.True(picture.IsOriginal);
        Assert.Equal((300, 200), (picture.Width, picture.Height));
    }

    // ---- Pixels -------------------------------------------------------------------------------------------------------------------------

    private readonly record struct Pixel(byte R, byte G, byte B, byte A);

    private static readonly Pixel Black = new(20, 20, 20, 255);
    private static readonly Pixel White = new(255, 255, 255, 255);
    private static readonly Pixel Tint = new(235, 240, 250, 255);

    // A frame of a width by height block, with marks like lines of text in it: a stroke of three pixels every eight rows, in runs of nine
    // of every fourteen columns.
    private static bool Marks(int x, int y, int width, int height) =>
        x >= 0 && y >= 0 && x < width && y < height
        && (x == 0 || y == 0 || x == width - 1 || y == height - 1 || ((y % 8 < 3) && (x % 14 < 9)));

    private static Pixel PixelAt(byte[] pixels, int width, int x, int y)
    {
        var at = (y * width + x) * 4;
        return new Pixel(pixels[at + 2], pixels[at + 1], pixels[at], pixels[at + 3]);
    }

    // The mean difference between the pixels in a few rows across the strokes and the one beside them: more where the strokes are crisper.
    private static double Contrast(byte[] pixels, int width, int height)
    {
        double total = 0;
        var count = 0;
        for (var y = 0; y < height; y += 3)
        {
            for (var x = 1; x < width; x++)
            {
                total += Math.Abs(pixels[(y * width + x) * 4 + 2] - pixels[(y * width + x - 1) * 4 + 2]);
                count++;
            }
        }

        return total / count;
    }

    private static async Task<byte[]> PngAsync(int width, int height, Func<int, int, Pixel> pixel)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = pixel(x, y);
                var at = (y * width + x) * 4;
                bgra[at] = p.B;
                bgra[at + 1] = p.G;
                bgra[at + 2] = p.R;
                bgra[at + 3] = p.A;
            }
        }

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        using var reader = stream.GetInputStreamAt(0).AsStreamForRead();
        await reader.ReadExactlyAsync(bytes);
        return bytes;
    }

    private static async Task<(int Width, int Height, byte[] Pixels)> DecodeAsync(byte[] image)
    {
        using var stream = new MemoryStream(image).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        return ((int)decoder.PixelWidth, (int)decoder.PixelHeight, data.DetachPixelData());
    }
}
