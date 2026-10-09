using System.Diagnostics;
using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Documents.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Documents;

/// <summary>
/// The app's <see cref="IDocumentContextService"/> (PROJECT_SPEC §4.7): the reader for the file's type reads its text, the
/// <see cref="DocumentContextBuilder"/> cuts it into passages and selects those the question needs, and the result is the text
/// to put in the prompt. A file that cannot be read is a status, never an exception; only cancelling is one.
/// </summary>
/// <remarks>
/// It opens whatever file it is given, so the caller has already checked the Files permission. It logs counts, the reader's id,
/// the outcome and the time (<see cref="DocumentsLog"/>), never a path, a name, the question, a term or any text.
/// </remarks>
public sealed class DocumentContextService : IDocumentContextService
{
    private readonly IDocumentReaderRegistry _readers;
    private readonly DocumentContextBuilder _builder;
    private readonly ILogger _logger;

    /// <summary>Creates the service over <paramref name="readers"/> and <paramref name="builder"/>.</summary>
    public DocumentContextService(
        IDocumentReaderRegistry readers, DocumentContextBuilder builder, ILogger<DocumentContextService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(builder);
        _readers = readers;
        _builder = builder;
        _logger = logger ?? NullLogger<DocumentContextService>.Instance;
    }

    /// <inheritdoc/>
    public async Task<DocumentContextResult> GetContextAsync(
        string filePath, string question, DocumentContextOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(question);

        var start = Stopwatch.GetTimestamp();
        var reader = _readers.GetReader(filePath);
        var read = await reader.ReadAsync(filePath, options?.Read, cancellationToken).ConfigureAwait(false);
        if (read.Status != DocumentReadStatus.Success)
        {
            DocumentsLog.ContextFailed(_logger, reader.Id, read.Status);
            return DocumentContextResult.Failed(read.Status);
        }

        var context = _builder.Build(read, options?.Chunking);
        if (context.Passages.Count == 0)
        {
            // A reader that succeeds has text, but text that normalizes to nothing is no text.
            DocumentsLog.ContextFailed(_logger, reader.Id, DocumentReadStatus.NoText);
            return DocumentContextResult.Failed(DocumentReadStatus.NoText);
        }

        var selection = _builder.Select(context, question, options?.Selection);
        var result = new DocumentContextResult
        {
            Status = DocumentReadStatus.Success,
            Text = selection.ToText(),
            Selection = selection,
            Truncated = context.Truncated,
            Metadata = read.Metadata,
        };
        DocumentsLog.ContextBuilt(
            _logger,
            reader.Id,
            context.Passages.Count,
            context.SourceCharacters,
            selection.Passages.Count,
            selection.CharacterCount,
            selection.Reason,
            selection.QueryTermCount,
            context.Truncated,
            (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return result;
    }
}
