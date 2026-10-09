using System.Globalization;
using System.Text;

namespace Assistant.Core.Documents;

/// <summary>What was read from a document: its properties, and its text in pieces that say where each came from.</summary>
public sealed record DocumentReadResult
{
    /// <summary>How reading ended. Only <see cref="DocumentReadStatus.Success"/> has <see cref="Segments"/>.</summary>
    public DocumentReadStatus Status { get; init; }

    /// <summary>What the file says about itself, set when the file was read (<see cref="DocumentReadStatus.Success"/> or <see cref="DocumentReadStatus.NoText"/>).</summary>
    public DocumentMetadata? Metadata { get; init; }

    /// <summary>The text, in the order of the document, each piece with its <see cref="DocumentLocation"/>. Every piece has text.</summary>
    public IReadOnlyList<DocumentSegment> Segments { get; init; } = [];

    /// <summary>
    /// <see langword="true"/> when the document has more text than the limits of <see cref="DocumentReadOptions"/> let through, or a
    /// page that could not be parsed was left out of it: <see cref="Segments"/> is not all of the text, and whatever is said from it
    /// must say so.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>The number of characters of text in all the segments.</summary>
    public int CharacterCount
    {
        get
        {
            var count = 0;
            foreach (var segment in Segments)
            {
                count += segment.Text.Length;
            }

            return count;
        }
    }

    /// <summary>A result of a read that did not end in text.</summary>
    public static DocumentReadResult Failed(DocumentReadStatus status) => new() { Status = status };

    /// <summary>
    /// The text as one string for a prompt, with a marker line before each piece that says what it is (<c>[Page 3]</c>,
    /// <c>[Slide 4: Budget]</c>): the boundaries are kept where the model can see them. A document with no parts to tell
    /// apart is its text alone.
    /// </summary>
    public string ToText()
    {
        var builder = new StringBuilder(CharacterCount + (Segments.Count * 16));
        foreach (var segment in Segments)
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            if (segment.Header is { } header)
            {
                builder.Append(header).Append('\n');
            }

            builder.Append(segment.Text);
        }

        return builder.ToString();
    }

    // The segments are the document's own text (PROJECT_SPEC §3.2): ToString, and so a log, shows how many and how long.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Status = ").Append(Status)
            .Append(", Segments = ").Append(Segments.Count.ToString(CultureInfo.InvariantCulture))
            .Append(", Characters = ").Append(CharacterCount.ToString(CultureInfo.InvariantCulture))
            .Append(", Truncated = ").Append(Truncated);
        return true;
    }
}

/// <summary>What was read of a document's properties, without extracting its text.</summary>
public sealed record DocumentMetadataResult
{
    /// <summary>How reading ended: <see cref="DocumentReadStatus.Success"/> or why the file's properties could not be read.</summary>
    public DocumentReadStatus Status { get; init; }

    /// <summary>The properties, set with <see cref="DocumentReadStatus.Success"/>.</summary>
    public DocumentMetadata? Metadata { get; init; }

    /// <summary>A result of a read that did not end in properties.</summary>
    public static DocumentMetadataResult Failed(DocumentReadStatus status) => new() { Status = status };
}
