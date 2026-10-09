using Assistant.Core.Ocr;

namespace Assistant.Core.Contracts;

/// <summary>
/// Reads text out of a picture, locally (PROJECT_SPEC §4.6, §5.4): the words in a screenshot and where they are, from the Windows OCR
/// engine and the OCR language packs that are installed. It supplements the vision model and never replaces it: it is used when the
/// exact words are wanted, or the model cannot see images. Nothing is sent anywhere, saved or logged.
/// </summary>
public interface IOcrEngine
{
    /// <summary>Whether an OCR language is installed that the engine can read with.</summary>
    bool IsAvailable { get; }

    /// <summary>Reads the text in <paramref name="image"/>, an encoded picture such as a PNG.</summary>
    /// <exception cref="OcrUnavailableException">No OCR language is installed.</exception>
    /// <exception cref="OcrFailedException">The picture could not be read: its bytes are not an image the engine can decode.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default);
}
