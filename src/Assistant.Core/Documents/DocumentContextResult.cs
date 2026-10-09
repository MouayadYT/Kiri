using System.Globalization;
using System.Text;

namespace Assistant.Core.Documents;

/// <summary>The limits <see cref="Contracts.IDocumentContextService"/> works within; each part not set has its default.</summary>
public sealed record DocumentContextOptions
{
    /// <summary>The limits on reading the file (its size, the characters and pages read), or <see langword="null"/> for the defaults.</summary>
    public DocumentReadOptions? Read { get; init; }

    /// <summary>How the text is cut into passages, or <see langword="null"/> for the defaults.</summary>
    public DocumentChunkingOptions? Chunking { get; init; }

    /// <summary>How many passages go into the prompt, or <see langword="null"/> for the defaults.</summary>
    public PassageSelectionOptions? Selection { get; init; }
}

/// <summary>What a question about a document came to: the text to put in the prompt, or why there is none.</summary>
public sealed record DocumentContextResult
{
    /// <summary>How reading the document ended. Only <see cref="DocumentReadStatus.Success"/> has a <see cref="Text"/>.</summary>
    public DocumentReadStatus Status { get; init; }

    /// <summary>
    /// The selected passages laid out for the prompt, each under a line that says where in the document it is
    /// (<see cref="PassageSelection.ToText"/>). Empty unless <see cref="Status"/> is <see cref="DocumentReadStatus.Success"/>.
    /// </summary>
    public string Text { get; init; } = "";

    /// <summary>Which passages were selected and why.</summary>
    public PassageSelection Selection { get; init; } = PassageSelection.Empty;

    /// <summary>
    /// <see langword="true"/> when the document has text that was not read at all, before any passage was chosen: a limit of the
    /// reader or of the passages was reached, or a page could not be parsed.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>What the file says about itself, when it was read.</summary>
    public DocumentMetadata? Metadata { get; init; }

    /// <summary>A result of a document that did not end in text.</summary>
    public static DocumentContextResult Failed(DocumentReadStatus status) => new() { Status = status };

    // The text is the document's own (PROJECT_SPEC §3.2): ToString, and so a log, shows how it ended and how long it is.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Status = ").Append(Status)
            .Append(", Length = ").Append(Text.Length.ToString(CultureInfo.InvariantCulture))
            .Append(", Selection = ").Append(Selection)
            .Append(", Truncated = ").Append(Truncated);
        return true;
    }
}
