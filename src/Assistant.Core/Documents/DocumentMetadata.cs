using System.Globalization;
using System.Text;

namespace Assistant.Core.Documents;

/// <summary>What the parts of a document are counted in.</summary>
public enum DocumentUnitKind
{
    /// <summary>The document has no pages or slides to count (a text file).</summary>
    None,

    /// <summary>Pages.</summary>
    Page,

    /// <summary>Slides.</summary>
    Slide,
}

/// <summary>What a reader knows about a file from the file itself, before (or without) reading its text.</summary>
public sealed record DocumentMetadata
{
    /// <summary>The <see cref="Contracts.IDocumentReader.Id"/> of the reader that read the file.</summary>
    public string ReaderId { get; init; } = "";

    /// <summary>The extension of the file, lower-case with its dot.</summary>
    public string Extension { get; init; } = "";

    /// <summary>The size of the file on disk, in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>The title written inside the file, when it has one. Never a guess from the file's name.</summary>
    public string? Title { get; init; }

    /// <summary>The author written inside the file, when it has one.</summary>
    public string? Author { get; init; }

    /// <summary>What <see cref="UnitCount"/> counts.</summary>
    public DocumentUnitKind UnitKind { get; init; }

    /// <summary>
    /// The number of pages or slides, when the file says (a PDF's and a presentation's are exact; a Word document's is the page
    /// count Word saved with it, which a reader cannot check). <see langword="null"/> when there is none.
    /// </summary>
    public int? UnitCount { get; init; }

    /// <summary>When the document was created, as it says itself.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When the document was last modified, as it says itself.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }

    // A title and an author are private content (PROJECT_SPEC §3.2): ToString, and so a log, shows none of it.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ReaderId = ").Append(ReaderId)
            .Append(", Extension = ").Append(Extension)
            .Append(", SizeBytes = ").Append(SizeBytes.ToString(CultureInfo.InvariantCulture))
            .Append(", UnitKind = ").Append(UnitKind)
            .Append(", UnitCount = ").Append(UnitCount?.ToString(CultureInfo.InvariantCulture) ?? "none");
        return true;
    }
}
