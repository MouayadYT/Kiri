using Assistant.Core.Contracts;
using Assistant.Core.Documents;

namespace Assistant.Documents.Context;

/// <summary>
/// Makes what a reader extracted ready for a question (PROJECT_SPEC §4.7): normalizes the text, cuts it into passages that
/// overlap and know where in the document they are, and selects the passages that a question needs, so the model is given those
/// and not the document. It is pure and deterministic, with no file, no model and no logging: the same text and options always
/// give the same passages, and the same passages, question and limits the same selection.
/// </summary>
public sealed class DocumentContextBuilder
{
    private readonly IPassageSelector _selector;

    /// <summary>Creates a builder that selects passages by the words they share with the question (<see cref="LexicalPassageSelector"/>).</summary>
    public DocumentContextBuilder()
        : this(new LexicalPassageSelector())
    {
    }

    /// <summary>Creates a builder that selects passages with <paramref name="selector"/>.</summary>
    public DocumentContextBuilder(IPassageSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    /// <summary>
    /// Cuts the text of a document that was read into passages. The pieces the reader returned (pages, slides, sections) keep
    /// their places, so every passage says where it is; the text is normalized first. A document that has no text, or was not
    /// read, has no passages.
    /// </summary>
    /// <param name="document">What a reader extracted.</param>
    /// <param name="options">How to cut it, or <see langword="null"/> for the defaults.</param>
    public DocumentContext Build(DocumentReadResult document, DocumentChunkingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Status != DocumentReadStatus.Success || document.Segments.Count == 0)
        {
            return DocumentContext.Empty;
        }

        var pieces = new List<DocumentSegment>(document.Segments.Count);
        foreach (var segment in document.Segments)
        {
            var text = DocumentTextNormalizer.Normalize(segment.Text);
            if (text.Length > 0)
            {
                pieces.Add(segment with { Text = text });
            }
        }

        return pieces.Count == 0
            ? DocumentContext.Empty
            : DocumentChunker.Chunk(pieces, options ?? DocumentChunkingOptions.Default, document.Truncated);
    }

    /// <summary>
    /// Cuts <paramref name="text"/>, which has no pages or slides to tell its parts apart, into passages, each in the document as
    /// a whole.
    /// </summary>
    /// <param name="text">The text, in any state: it is normalized here.</param>
    /// <param name="options">How to cut it, or <see langword="null"/> for the defaults.</param>
    public DocumentContext Build(string? text, DocumentChunkingOptions? options = null)
    {
        var normalized = DocumentTextNormalizer.Normalize(text);
        return normalized.Length == 0
            ? DocumentContext.Empty
            : DocumentChunker.Chunk(
                [new DocumentSegment(DocumentLocation.WholeDocument(), normalized)], options ?? DocumentChunkingOptions.Default, false);
    }

    /// <summary>Selects the passages of <paramref name="context"/> that best answer <paramref name="question"/> within the limits.</summary>
    /// <param name="context">The passages of a document.</param>
    /// <param name="question">What the user asked.</param>
    /// <param name="options">The limits, or <see langword="null"/> for the defaults.</param>
    public PassageSelection Select(DocumentContext context, string question, PassageSelectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(question);
        return _selector.Select(context.Passages, question, options);
    }
}
