using System.Globalization;
using System.Text;

namespace Assistant.Core.Documents;

/// <summary>
/// A document's text made ready for a prompt (PROJECT_SPEC §4.7): normalized and cut into <see cref="Passages"/>, each with
/// where it is in the document. Which of them a question needs is decided by an <see cref="Contracts.IPassageSelector"/>.
/// </summary>
public sealed record DocumentContext
{
    /// <summary>A document with no text.</summary>
    public static DocumentContext Empty { get; } = new();

    /// <summary>The passages, in the order of the document. Every part of its text is in at least one of them.</summary>
    public IReadOnlyList<DocumentPassage> Passages { get; init; } = [];

    /// <summary>
    /// <see langword="true"/> when the document has more text than the passages hold: the reader's limits left some out
    /// (<see cref="DocumentReadResult.Truncated"/>), or there were more passages than the chunking options allow.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>The number of characters of the document's normalized text that the passages were cut from.</summary>
    public int SourceCharacters { get; init; }

    // The passages are the document's own text (PROJECT_SPEC §3.2): ToString, and so a log, shows how many and how long.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Passages = ").Append(Passages.Count.ToString(CultureInfo.InvariantCulture))
            .Append(", SourceCharacters = ").Append(SourceCharacters.ToString(CultureInfo.InvariantCulture))
            .Append(", Truncated = ").Append(Truncated);
        return true;
    }
}
