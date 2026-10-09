using Assistant.Core.Domain;
using Assistant.Core.Ocr;

namespace Assistant.Core.Contracts;

/// <summary>
/// The text of the screenshots that conversations are about (PROJECT_SPEC §4.6), read locally with OCR when it is needed and not before:
/// for a model that cannot see images, which is given the text instead of the picture, and for the model that can, when it asks for the
/// exact words (<c>read_screen_text</c>). It supplements the vision model and never replaces it. What it reads is kept with the picture,
/// in memory only, and let go of when the picture is.
/// </summary>
public interface IScreenText
{
    /// <summary>Whether text can be read at all: an OCR language is installed.</summary>
    bool IsAvailable { get; }

    /// <summary>Whether the conversation has a screenshot, with its pixels, that its text could be read from.</summary>
    bool HasScreenshot(Guid conversationId);

    /// <summary>
    /// Reads the text in <paramref name="screenshot"/>, a context item with pixels. The same item is read once; later calls are answered
    /// with what was read.
    /// </summary>
    /// <returns>What was read, or <see langword="null"/> when text cannot be read (no OCR language, no pixels, a picture the engine cannot decode).</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<OcrResult?> ReadAsync(ContextItem screenshot, CancellationToken cancellationToken = default);

    /// <summary>Reads the text in the conversation's screenshot, or <see langword="null"/> when it has none or its text cannot be read.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<OcrResult?> ReadAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Lets go of what was read from the context item <paramref name="itemId"/>, such as when the user took the screenshot off.</summary>
    void Forget(Guid itemId);
}
