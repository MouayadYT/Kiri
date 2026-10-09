namespace Assistant.Core.Contracts;

/// <summary>
/// The kinds of document file the Assistant reads the text of (PROJECT_SPEC §4.7): what a conversation may attach and ask about.
/// It is the list <see cref="IDocumentReaderRegistry"/> is built from, written down where a view, which has no registry to ask,
/// can tell at once whether a file in a list can be attached; a test keeps the two equal.
/// </summary>
public static class DocumentFileTypes
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".pdf", ".docx", ".pptx", ".html", ".htm", ".mhtml", ".mht", ".csv", ".tsv", ".json", ".xml",
        ".yaml", ".yml", ".log", ".ini", ".toml",
    };

    /// <summary>The extensions, lower case with their dot.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [.. Known.Order(StringComparer.Ordinal)];

    /// <summary>The kinds of file in words, for telling a person what can be read.</summary>
    public const string Described = "text, Markdown, PDF, Word (.docx), PowerPoint (.pptx), web page (.html, .mhtml), CSV and JSON files";

    /// <summary>Whether <paramref name="extension"/> (<c>.pdf</c> or <c>pdf</c>, in any case) is a document type that is read.</summary>
    public static bool IsDocumentExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        var trimmed = extension.Trim();
        return Known.Contains(trimmed.StartsWith('.') ? trimmed : "." + trimmed);
    }

    /// <summary>Whether the file at <paramref name="path"/> has the extension of a document type that is read. The file is not opened.</summary>
    public static bool IsDocument(string? path) =>
        !string.IsNullOrWhiteSpace(path) && IsDocumentExtension(Path.GetExtension(path));
}
