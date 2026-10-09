using Assistant.Core.Domain;

namespace Assistant.UI.Messages;

/// <summary>
/// Files, folders or apps a request found, such as Windows Search results, shown as a list: one row each, with an
/// icon for its kind, its name, where it is and when it changed, and any matching text. It is as wide as a card but is
/// not one, and it is never drawn in the black card frame.
/// </summary>
public sealed class FileCollection : MessageContent
{
    /// <summary>Creates a list of <paramref name="files"/>, in order.</summary>
    public FileCollection(IEnumerable<FileItem> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        Files = files.ToArray();
        if (Files.Any(file => file is null))
        {
            throw new ArgumentException("A list of files cannot hold a missing file.", nameof(files));
        }
    }

    /// <summary>The files, in the order they are listed.</summary>
    public IReadOnlyList<FileItem> Files { get; }

    /// <summary>A list of files reaches as far as a card.</summary>
    public override bool IsWide => true;

    /// <summary>Lists search results as they were found, relative to <paramref name="clock"/>'s today.</summary>
    public static FileCollection From(IEnumerable<SearchResultItem> results, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        return new FileCollection(results.Select(result => FileItem.From(result, clock)));
    }
}
