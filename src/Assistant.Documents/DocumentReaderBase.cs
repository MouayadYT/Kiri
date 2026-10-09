using System.Diagnostics;
using System.Security;
using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Documents.Extraction;
using Microsoft.Extensions.Logging;

namespace Assistant.Documents;

/// <summary>What a format's reader found in a file: its properties and its text in pieces, and whether the limits cut the text.</summary>
internal sealed record ExtractedDocument(DocumentMetadata Metadata, IReadOnlyList<DocumentSegment> Segments, bool Truncated);

/// <summary>
/// What every reader does the same way, so that a format's reader is only its format: the file is checked and opened read-only
/// (sharing mode that lets the user's other programs keep it open), the work runs off the calling thread, and whatever goes
/// wrong becomes a <see cref="DocumentReadStatus"/> (PROJECT_SPEC §4.7). Only cancelling is an exception. A reader throws
/// <see cref="DocumentReadException"/> to say a file is too large, encrypted or not of its type; any other exception from a
/// parsing library means the file is damaged, and only its type is logged, because a library's message can hold a path or text.
/// </summary>
internal abstract class DocumentReaderBase : IDocumentReader
{
    private readonly ILogger _logger;

    protected DocumentReaderBase(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>The reader's logger, for what a reader knows and the base does not (never a path, a title or any text).</summary>
    protected ILogger Logger => _logger;

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public abstract IReadOnlyCollection<string> SupportedExtensions { get; }

    public abstract IReadOnlyCollection<string> SupportedMimeTypes { get; }

    /// <summary>Reads the properties of the opened file, without its text. The basics (reader, extension, size) are filled in by the base.</summary>
    protected abstract DocumentMetadata ReadMetadataCore(Stream stream, DocumentReadLimits limits, CancellationToken cancellationToken);

    /// <summary>Reads the properties and the text of the opened file, within the limits. The basics are filled in by the base.</summary>
    protected abstract ExtractedDocument ReadCore(Stream stream, DocumentReadLimits limits, CancellationToken cancellationToken);

    public async Task<DocumentMetadataResult> ReadMetadataAsync(
        string filePath, DocumentReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var limits = DocumentReadLimits.From(options);

        var result = await Task.Run(() => ReadMetadataSync(filePath, limits, cancellationToken), cancellationToken).ConfigureAwait(false);

        DocumentsLog.ReadMetadata(_logger, Id, result.Status, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    public async Task<DocumentReadResult> ReadAsync(
        string filePath, DocumentReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var limits = DocumentReadLimits.From(options);

        var result = await Task.Run(() => ReadSync(filePath, limits, cancellationToken), cancellationToken).ConfigureAwait(false);

        DocumentsLog.Read(
            _logger, Id, result.Status, result.Segments.Count, result.CharacterCount, result.Truncated,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    private DocumentMetadataResult ReadMetadataSync(string filePath, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        try
        {
            using var file = OpenFile(filePath, limits);
            var metadata = Complete(ReadInFile(() => ReadMetadataCore(file.Stream, limits, cancellationToken)), file);
            return new DocumentMetadataResult { Status = DocumentReadStatus.Success, Metadata = metadata };
        }
        catch (DocumentReadException ex)
        {
            return DocumentMetadataResult.Failed(ex.Status);
        }
    }

    private DocumentReadResult ReadSync(string filePath, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        try
        {
            using var file = OpenFile(filePath, limits);
            var extracted = ReadInFile(() => ReadCore(file.Stream, limits, cancellationToken));
            return new DocumentReadResult
            {
                Status = extracted.Segments.Count == 0 ? DocumentReadStatus.NoText : DocumentReadStatus.Success,
                Metadata = Complete(extracted.Metadata, file),
                Segments = extracted.Segments,
                Truncated = extracted.Truncated,
            };
        }
        catch (DocumentReadException ex)
        {
            return DocumentReadResult.Failed(ex.Status);
        }
    }

    /// <summary>Runs a reader's work on an opened file: anything but a cancel that is not already a status is a damaged file.</summary>
    private T ReadInFile<T>(Func<T> work)
    {
        try
        {
            return work();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DocumentReadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var status = ex is OutOfMemoryException or InsufficientMemoryException ? DocumentReadStatus.TooLarge : DocumentReadStatus.Corrupt;
            DocumentsLog.Failed(_logger, Id, ex.GetType().FullName ?? ex.GetType().Name, status);
            throw new DocumentReadException(status);
        }
    }

    private DocumentMetadata Complete(DocumentMetadata metadata, OpenedFile file) =>
        metadata with { ReaderId = Id, Extension = file.Extension, SizeBytes = file.Length };

    /// <summary>
    /// Checks that the path is a full path to a file that exists and is no larger than the limit, and opens it for reading only.
    /// It shares with writers and deleters, so a document the user has open in another program is still read.
    /// </summary>
    private static OpenedFile OpenFile(string filePath, DocumentReadLimits limits)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            throw new DocumentReadException(DocumentReadStatus.NotFound);
        }

        FileStream? stream = null;
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                throw new DocumentReadException(DocumentReadStatus.NotFound);
            }

            if (info.Length > limits.MaxFileBytes)
            {
                throw new DocumentReadException(DocumentReadStatus.TooLarge);
            }

            stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.RandomAccess);

            // Checked again on the open file: what was measured a moment ago may have grown since.
            if (stream.Length > limits.MaxFileBytes)
            {
                throw new DocumentReadException(DocumentReadStatus.TooLarge);
            }

            return new OpenedFile(stream, Path.GetExtension(filePath).ToLowerInvariant(), stream.Length);
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            throw ex switch
            {
                DocumentReadException known => known,
                FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException or PathTooLongException or ArgumentException
                    => new DocumentReadException(DocumentReadStatus.NotFound),
                NotSupportedException => new DocumentReadException(DocumentReadStatus.Unsupported),
                UnauthorizedAccessException or SecurityException or IOException => new DocumentReadException(DocumentReadStatus.Unreadable),
                _ => new DocumentReadException(DocumentReadStatus.Unreadable),
            };
        }
    }

    private sealed class OpenedFile : IDisposable
    {
        public OpenedFile(FileStream stream, string extension, long length)
        {
            Stream = stream;
            Extension = extension;
            Length = length;
        }

        public FileStream Stream { get; }

        public string Extension { get; }

        public long Length { get; }

        public void Dispose() => Stream.Dispose();
    }
}
