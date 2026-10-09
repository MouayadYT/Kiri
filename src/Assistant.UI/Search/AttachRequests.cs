using Assistant.UI.Messages;

namespace Assistant.UI.Search;

/// <summary>
/// Where an image or a document that the user attaches from the bar's results (the Attach action of a picture or a readable file, or of a copied text) is handed on: the window's controller listens, starts a conversation that has it attached, and grows the bar into it.
/// The results do not know the window, and the window does not know the results.
/// </summary>
internal sealed class AttachRequests
{
    /// <summary>Raised when the user asks to attach <see cref="ImageItem"/> to a new conversation.</summary>
    public event EventHandler<ImageItem>? Requested;

    /// <summary>Raised when the user asks to attach <see cref="DocumentAttachment"/> to a new conversation.</summary>
    public event EventHandler<DocumentAttachment>? DocumentRequested;

    /// <summary>Raised when the user asks to attach <see cref="TextAttachment"/> (text from the clipboard history) to a new conversation.</summary>
    public event EventHandler<TextAttachment>? TextRequested;

    /// <summary>Asks for <paramref name="text"/> to be attached to a new conversation, to ask about it.</summary>
    public void Request(TextAttachment text)
    {
        ArgumentNullException.ThrowIfNull(text);
        TextRequested?.Invoke(this, text);
    }

    /// <summary>Asks for <paramref name="image"/> to be attached to a new conversation.</summary>
    public void Request(ImageItem image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Requested?.Invoke(this, image);
    }

    /// <summary>Asks for <paramref name="document"/> to be attached to a new conversation.</summary>
    public void Request(DocumentAttachment document)
    {
        ArgumentNullException.ThrowIfNull(document);
        DocumentRequested?.Invoke(this, document);
    }
}
