using System.Collections.Frozen;
using Assistant.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Documents;

/// <summary>
/// Finds the reader for a file by its extension (or a MIME type) from what the readers declare, and falls back on
/// <see cref="UnsupportedDocumentReader"/> for everything else (PROJECT_SPEC §4.7). Two readers that claim the same extension or
/// MIME type are a mistake in the composition and fail at once, not by whichever was registered last.
/// </summary>
public sealed class DocumentReaderRegistry : IDocumentReaderRegistry
{
    private static readonly char[] MimeParameterSeparator = [';'];

    private readonly FrozenDictionary<string, IDocumentReader> _byExtension;
    private readonly FrozenDictionary<string, IDocumentReader> _byMimeType;
    private readonly IDocumentReader _fallback = new UnsupportedDocumentReader();
    private readonly ILogger _logger;

    /// <summary>Creates a registry of <paramref name="readers"/>.</summary>
    /// <exception cref="ArgumentException">A reader declares an extension that is not lower-case with its dot, or one that another reader has.</exception>
    public DocumentReaderRegistry(IEnumerable<IDocumentReader> readers, ILogger<DocumentReaderRegistry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(readers);
        _logger = logger ?? NullLogger<DocumentReaderRegistry>.Instance;

        var byExtension = new Dictionary<string, IDocumentReader>(StringComparer.OrdinalIgnoreCase);
        var byMimeType = new Dictionary<string, IDocumentReader>(StringComparer.OrdinalIgnoreCase);

        foreach (var reader in readers)
        {
            foreach (var extension in reader.SupportedExtensions)
            {
                if (extension.Length < 2 || extension[0] != '.' || extension.Contains('/') || extension.Contains('\\'))
                {
                    throw new ArgumentException($"Reader '{reader.Id}' declares '{extension}', which is not an extension with its dot.", nameof(readers));
                }

                if (!byExtension.TryAdd(extension, reader))
                {
                    throw new ArgumentException($"Readers '{byExtension[extension].Id}' and '{reader.Id}' both handle '{extension}'.", nameof(readers));
                }
            }

            foreach (var mimeType in reader.SupportedMimeTypes)
            {
                var normalized = NormalizeMimeType(mimeType);
                if (normalized.Length == 0 || !byMimeType.TryAdd(normalized, reader))
                {
                    throw new ArgumentException($"Reader '{reader.Id}' declares the MIME type '{mimeType}', which is empty or already handled.", nameof(readers));
                }
            }
        }

        _byExtension = byExtension.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _byMimeType = byMimeType.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        SupportedExtensions = byExtension.Keys.Select(static e => e.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray();
        SupportedMimeTypes = byMimeType.Keys.Select(static m => m.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedMimeTypes { get; }

    /// <inheritdoc />
    public IDocumentReader? FindReader(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var extension = Path.GetExtension(filePath);
        return extension.Length > 1 && _byExtension.TryGetValue(extension, out var reader) ? reader : null;
    }

    /// <inheritdoc />
    public IDocumentReader? FindReaderForMimeType(string mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return null;
        }

        return _byMimeType.TryGetValue(NormalizeMimeType(mimeType), out var reader) ? reader : null;
    }

    /// <inheritdoc />
    public IDocumentReader GetReader(string filePath)
    {
        var reader = FindReader(filePath);
        if (reader is not null)
        {
            return reader;
        }

        DocumentsLog.NoReader(_logger, SafeExtension(filePath));
        return _fallback;
    }

    private static string NormalizeMimeType(string mimeType) =>
        mimeType.Split(MimeParameterSeparator, 2)[0].Trim().ToLowerInvariant();

    // Only what can be an extension is logged: a short run of letters and digits after a dot.
    private static string SafeExtension(string filePath)
    {
        var extension = string.IsNullOrWhiteSpace(filePath) ? string.Empty : Path.GetExtension(filePath).ToLowerInvariant();
        return extension.Length is > 1 and <= 12 && extension.Skip(1).All(char.IsAsciiLetterOrDigit) ? extension : "(none)";
    }
}
