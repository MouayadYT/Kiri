using Assistant.Core.Contracts;
using Assistant.Core.Documents;

namespace Assistant.Documents;

/// <summary>
/// The reader for a file no other reader handles. It opens nothing and reports <see cref="DocumentReadStatus.Unsupported"/>, so
/// that an unknown (and possibly binary) format is never parsed on a guess, and so that a caller has a reader, and one path
/// for every outcome, whatever the file is. It declares no extension and no MIME type: it is never found, only fallen back on.
/// </summary>
internal sealed class UnsupportedDocumentReader : IDocumentReader
{
    public string Id => "unsupported";

    public string DisplayName => "Unsupported file";

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [];

    public IReadOnlyCollection<string> SupportedMimeTypes { get; } = [];

    public Task<DocumentMetadataResult> ReadMetadataAsync(
        string filePath, DocumentReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DocumentMetadataResult.Failed(DocumentReadStatus.Unsupported));
    }

    public Task<DocumentReadResult> ReadAsync(
        string filePath, DocumentReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DocumentReadResult.Failed(DocumentReadStatus.Unsupported));
    }
}
