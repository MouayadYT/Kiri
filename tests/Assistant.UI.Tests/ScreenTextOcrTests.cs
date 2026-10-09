using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core.Ocr;
using Assistant.Windows.Ocr;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>A test that needs a Windows OCR language: it is skipped on a PC that has none.</summary>
internal sealed class OcrFactAttribute : FactAttribute
{
    public OcrFactAttribute()
    {
        if (!new WindowsOcrEngine().IsAvailable)
        {
            Skip = "No Windows OCR language is installed on this PC.";
        }
    }
}

public sealed partial class PromptInputControlTests
{
    // ---- Windows OCR, for real, on text drawn here (PROJECT_SPEC §4.6) ----------------------------------------------------------------

    // A picture of lines of black text on white, drawn by WPF at the given size: what a dialog on a screen looks like.
    private static byte[] TextPicture(int width, int height, double fontSize, params string[] lines)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var top = fontSize * 0.5;
            foreach (var line in lines)
            {
                var text = new FormattedText(
                    line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), fontSize, Brushes.Black, 1.0);
                context.DrawText(text, new Point(fontSize, top));
                top += fontSize * 2.2;
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static OcrResult ReadWithWindows(byte[] png) =>
        Task.Run(() => new WindowsOcrEngine().RecognizeAsync(png)).GetAwaiter().GetResult();

    [OcrFact]
    public void WindowsOcr_ReadsTheWordsOfADialog_InReadingOrder_WithWhereEachLineIs() => RunSta(() =>
    {
        var png = TextPicture(900, 420, 34, "Error 404", "The file was not found.", "Press OK to continue");

        var result = ReadWithWindows(png);

        Assert.Equal((900, 420), (result.ImageWidth, result.ImageHeight));
        Assert.False(result.IsEmpty);
        Assert.False(string.IsNullOrEmpty(result.Language));
        var text = string.Join(' ', result.Lines.Select(line => line.Text));
        Assert.Contains("404", text, StringComparison.Ordinal);
        Assert.Contains("file was not found", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("continue", text, StringComparison.OrdinalIgnoreCase);

        // Top to bottom, each line inside the picture, with its words inside the line.
        Assert.Equal(result.Lines.OrderBy(line => line.Box.Y).ToList(), result.Lines);
        foreach (var line in result.Lines)
        {
            Assert.InRange(line.Box.X, 0, 900);
            Assert.InRange(line.Box.Bottom, 0, 420 + 1);
            Assert.True(line.Box.Width > 20 && line.Box.Height > 10);
            Assert.NotEmpty(line.Words);
            Assert.All(line.Words, word => Assert.InRange(word.Box.X, line.Box.X - 1, line.Box.Right + 1));
        }

        // The first line, "Error 404", is near the top left, where it was drawn.
        var first = result.Lines.First(line => line.Text.Contains("404", StringComparison.Ordinal));
        Assert.InRange(first.Box.X, 20, 80);
        Assert.InRange(first.Box.Y, 5, 90);
    });

    [OcrFact]
    public void ASmallScreenshot_IsReadToo_WithItsBoxesInThePixelsOfThePictureGiven() => RunSta(() =>
    {
        // 360 x 150 with text 14 pixels tall: enlarged for the engine, and reported at the size it was given.
        var png = TextPicture(360, 150, 14, "Disk almost full", "Free up space");

        var result = ReadWithWindows(png);

        Assert.Equal((360, 150), (result.ImageWidth, result.ImageHeight));
        var text = string.Join(' ', result.Lines.Select(line => line.Text));
        Assert.Contains("full", text, StringComparison.OrdinalIgnoreCase);
        Assert.All(result.Lines, line =>
        {
            Assert.InRange(line.Box.Right, 0, 361);
            Assert.InRange(line.Box.Bottom, 0, 151);
        });
    });

    [OcrFact]
    public void APictureWithNoText_IsAnEmptyResult_AndNotAnImageIsAFailure() => RunSta(() =>
    {
        var blank = TextPicture(300, 200, 20);

        Assert.True(ReadWithWindows(blank).IsEmpty);
        Assert.Throws<OcrFailedException>(() => ReadWithWindows("not an image"u8.ToArray()));
        Assert.Throws<OcrFailedException>(() => ReadWithWindows(Array.Empty<byte>()));
    });
}
