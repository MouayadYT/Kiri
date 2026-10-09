namespace Assistant.Core.Contracts;

/// <summary>Selects the <see cref="IDocumentReader"/> for a file by its extension or its MIME type.</summary>
public interface IDocumentReaderRegistry
{
    /// <summary>Every extension a registered reader handles, lower-case with the leading dot.</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>Every MIME type a registered reader handles, lower-case, without parameters.</summary>
    IReadOnlyCollection<string> SupportedMimeTypes { get; }

    /// <summary>
    /// Returns the reader for the extension of <paramref name="filePath"/>, matched case-insensitively, or
    /// <see langword="null"/> when the file type is not supported. The file is not opened.
    /// </summary>
    IDocumentReader? FindReader(string filePath);

    /// <summary>
    /// Returns the reader for a MIME type (<c>application/pdf</c>, also when written with parameters such as
    /// <c>text/plain; charset=utf-8</c>), matched case-insensitively, or <see langword="null"/> when it is not supported.
    /// </summary>
    IDocumentReader? FindReaderForMimeType(string mimeType);

    /// <summary>
    /// Returns the reader for <paramref name="filePath"/>, and never <see langword="null"/>: for a file type no reader handles
    /// it is the fallback, which reads nothing and reports <see cref="Documents.DocumentReadStatus.Unsupported"/>, so that an
    /// unknown binary format is never parsed on a guess and a caller has one path for every outcome.
    /// </summary>
    IDocumentReader GetReader(string filePath);
}
