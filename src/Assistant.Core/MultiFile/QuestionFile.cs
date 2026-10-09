using System.Text;

namespace Assistant.Core.MultiFile;

/// <summary>A file a question is asked about: the name the user knows it by and its full path.</summary>
/// <param name="Name">The file's name, which labels it for the user and for the model.</param>
/// <param name="Path">The file's full path.</param>
public sealed record QuestionFile(string Name, string Path)
{
    /// <summary>Whether <paramref name="other"/> is the same file, however the case of its path is written.</summary>
    public bool IsSameFile(QuestionFile other) =>
        other is not null && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

    // A name and a path are the user's own (PROJECT_SPEC §3.2): ToString, and so a log, shows neither.
    private bool PrintMembers(StringBuilder builder) => false;
}
