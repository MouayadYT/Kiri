using System.IO;

namespace Assistant.UI.Messages;

/// <summary>
/// A document the user attached to a conversation to ask about (PROJECT_SPEC §4.2): shown by its file name above the follow-up
/// composer until the question is asked, and then above the user's message. It is only the name and where the file is; the file
/// is read when the question is asked, while the Files permission is on, and what is read of it is never kept here.
/// </summary>
public sealed class DocumentAttachment
{
    // A type label longer than this would not fit its place, so such files show none.
    private const int MaxTypeLength = 4;

    /// <summary>Creates an attachment for the file at <paramref name="path"/>.</summary>
    /// <param name="name">What the file is called, such as its file name.</param>
    /// <param name="path">The file's full path.</param>
    public DocumentAttachment(string name, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Name = name;
        Path = path;
    }

    /// <summary>What the file is called: the name the chip shows, and names it for assistive technology.</summary>
    public string Name { get; }

    /// <summary>The file's full path.</summary>
    public string Path { get; }

    /// <summary>
    /// The file's type as its icon shows it, such as <c>PDF</c>, or <see langword="null"/> when its extension is none or too long.
    /// </summary>
    public string? TypeLabel
    {
        get
        {
            var extension = System.IO.Path.GetExtension(Name).TrimStart('.');
            return extension.Length is > 0 and <= MaxTypeLength ? extension.ToUpperInvariant() : null;
        }
    }

    /// <summary>Whether <paramref name="other"/> is the same file: the same path, however it is written in case.</summary>
    public bool IsSameFile(DocumentAttachment other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
    }
}
