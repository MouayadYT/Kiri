using System.Text;

namespace Assistant.Core.Domain;

/// <summary>
/// The properties the Windows Search index holds about an item, beyond its name, size and dates. Every one is optional: an
/// item has only what its file type and its handler give it.
/// </summary>
public sealed record SearchResultMetadata
{
    /// <summary>What Windows calls the type, such as <c>Adobe Acrobat Document</c>.</summary>
    public string? TypeDescription { get; init; }

    /// <summary>The broad kind Windows files the item under, such as <c>document</c>, <c>picture</c> or <c>folder</c>.</summary>
    public string? Kind { get; init; }

    /// <summary>The MIME type, such as <c>application/pdf</c>.</summary>
    public string? MimeType { get; init; }

    /// <summary>The title written inside the file, when it has one.</summary>
    public string? Title { get; init; }

    /// <summary>The authors written inside the file.</summary>
    public IReadOnlyList<string> Authors { get; init; } = [];

    /// <summary>The keywords or tags written inside the file or set on it.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>When the item was last opened, as far as Windows records it.</summary>
    public DateTimeOffset? AccessedAt { get; init; }

    // Titles, authors and keywords are private content (PROJECT_SPEC §3.2): ToString, and so a log, shows none of it.
    private bool PrintMembers(StringBuilder builder) => false;
}
