using Assistant.Windows.Ocr;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>How a picture is sized for the Windows OCR engine, which the real engine's own test cannot pin down: small text is enlarged to be read.</summary>
public sealed class WindowsOcrEngineTests
{
    [Theory]
    [InlineData(300, 200, 2600, 2.0)]
    [InlineData(600, 100, 2600, 1000.0 / 600)]
    [InlineData(1000, 700, 2600, 1.0)]
    [InlineData(2000, 1500, 2600, 1.0)]
    [InlineData(5200, 1000, 2600, 0.5)]
    [InlineData(3000, 3000, 2600, 2600.0 / 3000)]
    [InlineData(400, 400, 600, 1.5)]
    public void APicture_IsEnlargedWhenSmall_ScaledDownWhenLarge_AndLeftAloneBetween(int width, int height, int max, double expected) =>
        Assert.Equal(expected, WindowsOcrEngine.ScaleFor(width, height, max), 6);

    [Fact]
    public async Task WithoutAnythingToRead_TheEngineSaysSoAndNothingElse()
    {
        var engine = new WindowsOcrEngine();

        if (!engine.IsAvailable)
        {
            await Assert.ThrowsAsync<Assistant.Core.Ocr.OcrUnavailableException>(() => engine.RecognizeAsync(new byte[] { 1, 2, 3 }));
            return;
        }

        await Assert.ThrowsAsync<Assistant.Core.Ocr.OcrFailedException>(() => engine.RecognizeAsync(ReadOnlyMemory<byte>.Empty));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.RecognizeAsync(new byte[] { 1, 2, 3 }, new CancellationToken(canceled: true)));
    }
}
