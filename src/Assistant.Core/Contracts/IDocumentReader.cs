using Assistant.Core.Documents;

namespace Assistant.Core.Contracts;

/// <summary>
/// Reads one family of file types: says which it handles, what a file says about itself, and what text is in it, in pieces
/// that say where each came from (PROJECT_SPEC §4.7).
/// </summary>
/// <remarks>
/// A reader only reads: the file is never modified, nothing in it is run or opened (no macro, no embedded object, no link is
/// followed), and what a reader cannot read is a <see cref="DocumentReadStatus"/>, never an exception, so one damaged file
/// cannot fail what asked. Implementations enforce <see cref="DocumentReadOptions"/> and stop when cancelled, and never log a
/// path, a title or any of the text (§3.3).
/// </remarks>
public interface IDocumentReader
{
    /// <summary>A short stable name for the reader, such as <c>pdf</c>. It is what a log and <see cref="DocumentMetadata.ReaderId"/> carry.</summary>
    string Id { get; }

    /// <summary>The kind of file the reader reads, in words for a person, such as <c>PDF document</c>.</summary>
    string DisplayName { get; }

    /// <summary>File extensions this reader handles, lower-case with the leading dot (for example <c>.pdf</c>).</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>MIME types this reader handles, lower-case, without parameters (for example <c>application/pdf</c>).</summary>
    IReadOnlyCollection<string> SupportedMimeTypes { get; }

    /// <summary>
    /// Reads what the file says about itself (its title, author, dates and number of pages or slides) without extracting its
    /// text, which is much cheaper for a long document.
    /// </summary>
    /// <param name="filePath">The full path of the file.</param>
    /// <param name="options">The limits to work within, or <see langword="null"/> for <see cref="DocumentReadOptions.Default"/>.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<DocumentMetadataResult> ReadMetadataAsync(
        string filePath, DocumentReadOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Extracts the text of the file at <paramref name="filePath"/>, with where each part of it is.</summary>
    /// <param name="filePath">The full path of the file.</param>
    /// <param name="options">The limits to work within, or <see langword="null"/> for <see cref="DocumentReadOptions.Default"/>.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<DocumentReadResult> ReadAsync(
        string filePath, DocumentReadOptions? options = null, CancellationToken cancellationToken = default);
}
