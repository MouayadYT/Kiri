using System.Text;

namespace Assistant.Core.Domain;

/// <summary>A local app, file or folder found through Windows Search.</summary>
/// <param name="Type">Whether the item is an app, file or folder.</param>
/// <param name="DisplayName">Name shown to the user.</param>
/// <param name="Path">File-system path for files and folders; the shell parsing name for apps.</param>
public sealed record SearchResultItem(SearchResultItemType Type, string DisplayName, string Path)
{
    /// <summary>
    /// The file's extension in lower case with its leading dot (<c>.pdf</c>), or <see langword="null"/> for a folder and
    /// for a file with none.
    /// </summary>
    public string? Extension { get; init; }

    /// <summary>Size of a file in bytes, when known. A folder has none.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Last modification time, when known.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>Creation time, when known.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>What else the index knows about the item, when it knows anything.</summary>
    public SearchResultMetadata? Metadata { get; init; }

    /// <summary>Short excerpt of matching text, when available.</summary>
    public string? Snippet { get; init; }

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Type = {Type}");
        return true;
    }
}
