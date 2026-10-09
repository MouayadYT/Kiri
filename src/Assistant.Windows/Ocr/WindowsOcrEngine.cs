using System.Runtime.InteropServices;
using Assistant.Core.Contracts;
using Assistant.Core.Ocr;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinOcr = global::Windows.Media.Ocr;

namespace Assistant.Windows.Ocr;

/// <summary>
/// The app's <see cref="IOcrEngine"/>: the Windows OCR engine (<c>Windows.Media.Ocr</c>), on this PC, with the OCR languages installed for
/// the user's profile (or, failing those, the first one that is installed). Nothing leaves the PC, and nothing is saved or logged: the
/// picture is decoded in memory, read, and let go of.
/// </summary>
/// <remarks>
/// The engine reads best when text is at least some twenty pixels tall, and takes no side over <c>OcrEngine.MaxImageDimension</c>:
/// a small picture is enlarged first, a very large one scaled down, and the boxes it reports are given back in pixels of the picture
/// that was passed in. One picture is read at a time.
/// </remarks>
public sealed class WindowsOcrEngine : IOcrEngine
{
    // A picture whose longer side is under this is enlarged, up to this much, before it is read.
    private const int EnlargeBelow = 1000;
    private const double MaxEnlarge = 2;

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Lazy<WinOcr.OcrEngine?> _engine = new(CreateEngine);

    /// <inheritdoc/>
    public bool IsAvailable => _engine.Value is not null;

    /// <inheritdoc/>
    public async Task<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
    {
        var engine = _engine.Value ?? throw new OcrUnavailableException();
        if (image.IsEmpty)
        {
            throw new OcrFailedException();
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var stream = ReadOnlyStream(image).AsRandomAccessStream();
            var decoder = await DecodeAsync(stream, cancellationToken).ConfigureAwait(false);
            var (width, height) = ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
            var scale = ScaleFor(width, height, (int)WinOcr.OcrEngine.MaxImageDimension);
            using var bitmap = await PixelsAsync(decoder, width, height, scale, cancellationToken).ConfigureAwait(false);

            WinOcr.OcrResult recognized;
            try
            {
                recognized = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new OcrFailedException(exception);
            }

            return new OcrResult([.. recognized.Lines.Select(line => ToLine(line, scale))], width, height, engine.RecognizerLanguage.LanguageTag);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The factor a <paramref name="width"/> by <paramref name="height"/> picture is scaled by before it is read.</summary>
    internal static double ScaleFor(int width, int height, int maxDimension)
    {
        var longest = Math.Max(width, height);
        var scale = longest < EnlargeBelow ? Math.Min(MaxEnlarge, (double)EnlargeBelow / longest) : 1;
        return longest * scale > maxDimension ? (double)maxDimension / longest : scale;
    }

    // The engine for the languages the user reads, or the first OCR language that is installed, or none.
    private static WinOcr.OcrEngine? CreateEngine()
    {
        try
        {
            return WinOcr.OcrEngine.TryCreateFromUserProfileLanguages()
                ?? (WinOcr.OcrEngine.AvailableRecognizerLanguages.Count > 0
                    ? WinOcr.OcrEngine.TryCreateFromLanguage(WinOcr.OcrEngine.AvailableRecognizerLanguages[0])
                    : null);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    // A line as the app holds it: its words with their places in the picture that was passed in, and the box round them.
    private static OcrLine ToLine(WinOcr.OcrLine line, double scale)
    {
        var words = line.Words.Select(word => new OcrWord(word.Text, Box(word.BoundingRect, scale))).ToList();
        var box = words.Count == 0 ? default : words.Select(word => word.Box).Aggregate((all, next) => all.Union(next));
        return new OcrLine(line.Text, box, words);
    }

    private static OcrBox Box(global::Windows.Foundation.Rect rect, double scale) =>
        new(rect.X / scale, rect.Y / scale, rect.Width / scale, rect.Height / scale);

    private static MemoryStream ReadOnlyStream(ReadOnlyMemory<byte> image) =>
        MemoryMarshal.TryGetArray(image, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(image.ToArray(), writable: false);

    private static async Task<BitmapDecoder> DecodeAsync(IRandomAccessStream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new OcrFailedException(exception);
        }
    }

    // The picture as the pixels the engine reads, scaled by the codec as it decodes.
    private static async Task<SoftwareBitmap> PixelsAsync(
        BitmapDecoder decoder, int width, int height, double scale, CancellationToken cancellationToken)
    {
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(width * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(height * scale)),
            InterpolationMode = scale > 1 ? BitmapInterpolationMode.Cubic : BitmapInterpolationMode.Fant,
        };

        try
        {
            return await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new OcrFailedException(exception);
        }
    }
}
