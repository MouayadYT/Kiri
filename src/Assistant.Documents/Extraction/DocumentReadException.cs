using Assistant.Core.Documents;

namespace Assistant.Documents.Extraction;

/// <summary>
/// How a reader says a file cannot be read, from wherever it finds out: the reader base turns it into the
/// <see cref="DocumentReadResult.Status"/> and never lets it out. It carries no message, because a library's own message can
/// repeat a path or a piece of the document.
/// </summary>
internal sealed class DocumentReadException : Exception
{
    public DocumentReadException(DocumentReadStatus status)
        : base(status.ToString())
    {
        Status = status;
    }

    public DocumentReadStatus Status { get; }
}
