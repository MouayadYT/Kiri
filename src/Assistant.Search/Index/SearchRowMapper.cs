using System.Globalization;
using System.Text;
using Assistant.Core.Domain;

namespace Assistant.Search.Index;

/// <summary>
/// Turns one row of the index into a <see cref="SearchResultItem"/>: the values the provider hands over in whatever types it
/// likes (a date as a <see cref="DateTime"/> in UTC, a size as a 64-bit number, a list as an array) become one shape, and a
/// row that must not be shown becomes nothing.
/// </summary>
internal static class SearchRowMapper
{
    /// <summary>The longest excerpt kept.</summary>
    public const int MaxSnippetLength = 240;

    private const int MaxTextLength = 200;
    private const int MaxListLength = 10;
    private const long HiddenAttribute = 0x2;
    private const long SystemAttribute = 0x4;

    /// <summary>
    /// The result <paramref name="row"/> stands for, or null when the row has no path, is hidden or a system file, or is inside
    /// an excluded folder.
    /// </summary>
    /// <param name="row">Values in the order of <see cref="SearchColumns"/>; a missing value is null.</param>
    /// <param name="requested">What the query asked for, which is what a row says nothing about is taken to be.</param>
    /// <param name="excluded">Folders whose contents are never shown.</param>
    public static SearchResultItem? Map(object?[] row, SearchResultItemType requested, ExcludedFolders excluded)
    {
        if (Whole(row, SearchColumns.Path) is not { } path || excluded.Contains(path))
        {
            return null;
        }

        // The index leaves out hidden files unless the user asked it to keep them, and this holds them back anyway.
        var attributes = Number(row, SearchColumns.Attributes) ?? 0;
        if ((attributes & (HiddenAttribute | SystemAttribute)) != 0)
        {
            return null;
        }

        var isFolder = Get(row, SearchColumns.IsFolder) is bool flag ? flag : requested == SearchResultItemType.Folder;
        var type = isFolder ? SearchResultItemType.Folder : SearchResultItemType.File;

        return new SearchResultItem(type, NameOf(row, path), path)
        {
            Extension = isFolder ? null : ExtensionOf(row, path),
            SizeBytes = isFolder ? null : Number(row, SearchColumns.Size) is >= 0 and var size ? size : null,
            ModifiedAt = Date(row, SearchColumns.Modified),
            CreatedAt = Date(row, SearchColumns.Created),
            Metadata = MetadataOf(row),
            Snippet = Snippet(Text(row, SearchColumns.Summary)),
        };
    }

    /// <summary>
    /// An excerpt for a result shown in a row: one line with the control characters, the runs of spaces and anything past
    /// <see cref="MaxSnippetLength"/> characters removed. Null when nothing readable is left.
    /// </summary>
    internal static string? Snippet(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var line = new StringBuilder(Math.Min(text.Length, MaxSnippetLength + 1));
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = line.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                line.Append(' ');
                pendingSpace = false;
            }

            line.Append(character);
            if (line.Length > MaxSnippetLength)
            {
                break;
            }
        }

        if (line.Length == 0 || !line.ToString().Any(char.IsLetterOrDigit))
        {
            return null;
        }

        if (line.Length <= MaxSnippetLength)
        {
            return line.ToString();
        }

        var cut = char.IsHighSurrogate(line[MaxSnippetLength - 1]) ? MaxSnippetLength - 1 : MaxSnippetLength;
        return line.ToString(0, cut).TrimEnd() + "…";
    }

    private static string NameOf(object?[] row, string path)
    {
        var leaf = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (!string.IsNullOrEmpty(leaf))
        {
            return leaf;
        }

        // A drive's root has no file name of its own.
        return Whole(row, SearchColumns.Name) ?? path;
    }

    private static string? ExtensionOf(object?[] row, string path)
    {
        var extension = Text(row, SearchColumns.Extension) ?? Path.GetExtension(path);
        extension = extension.Trim();
        if (extension.Length == 0 || extension == ".")
        {
            return null;
        }

        return (extension[0] == '.' ? extension : "." + extension).ToLowerInvariant();
    }

    private static SearchResultMetadata? MetadataOf(object?[] row)
    {
        var metadata = new SearchResultMetadata
        {
            TypeDescription = Text(row, SearchColumns.TypeText),
            Kind = List(row, SearchColumns.Kind).FirstOrDefault(),
            MimeType = Text(row, SearchColumns.MimeType),
            Title = Text(row, SearchColumns.Title),
            Authors = List(row, SearchColumns.Authors),
            Keywords = List(row, SearchColumns.Keywords),
            AccessedAt = Date(row, SearchColumns.Accessed),
        };

        var isEmpty = metadata.TypeDescription is null && metadata.Kind is null && metadata.MimeType is null
            && metadata.Title is null && metadata.Authors.Count == 0 && metadata.Keywords.Count == 0
            && metadata.AccessedAt is null;
        return isEmpty ? null : metadata;
    }

    private static object? Get(object?[] row, int column) =>
        column < row.Length && row[column] is not DBNull ? row[column] : null;

    private static string? Text(object?[] row, int column) => Clean(Get(row, column) as string);

    // A path or a name, whole: cutting one makes it another file, or none ("…-- Anna’s Archi.pdf" lost its end at 200).
    private static string? Whole(object?[] row, int column) =>
        (Get(row, column) as string)?.Trim() is { Length: > 0 } text ? text : null;

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaxTextLength ? trimmed : trimmed[..MaxTextLength];
    }

    /// <summary>A value the provider gives as one string or as an array of them, as a short list without blanks.</summary>
    private static IReadOnlyList<string> List(object?[] row, int column)
    {
        IEnumerable<string?> values = Get(row, column) switch
        {
            string[] array => array,
            string single => single.Split(';'),
            _ => [],
        };

        return [.. values.Select(Clean).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxListLength)];
    }

    private static long? Number(object?[] row, int column)
    {
        try
        {
            return Get(row, column) switch
            {
                null => null,
                ulong value => value <= long.MaxValue ? (long)value : null,
                IConvertible value => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The provider's dates are UTC, though they carry no kind.</summary>
    private static DateTimeOffset? Date(object?[] row, int column)
    {
        if (Get(row, column) is not DateTime value || value == default)
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
