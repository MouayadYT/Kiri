using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>Whether the index can look inside the files a query covers, for a query that asks it to.</summary>
public enum ContentSearchSupport
{
    /// <summary>The query has no content criterion (<see cref="FileSearchQuery.ContentTerm"/> or <see cref="FileSearchQuery.MatchContents"/>).</summary>
    NotRequested = 0,

    /// <summary>
    /// Nothing says the index cannot. That is not a promise: Windows Search may still hold no text for some of the files
    /// (a type the user has set to properties only, a file it has not read yet).
    /// </summary>
    Available = 1,

    /// <summary>The index can look inside some of the file types the query names but not all of them.</summary>
    Partial = 2,

    /// <summary>
    /// The index cannot look inside the files the query covers, so an empty answer says nothing about whether any of them
    /// holds the phrase.
    /// </summary>
    Unavailable = 3,
}

/// <summary>Why the index cannot look inside the files a query covers, wholly or in part.</summary>
[Flags]
public enum ContentSearchLimits
{
    /// <summary>Nothing limits it.</summary>
    None = 0,

    /// <summary>
    /// The index holds nothing under the query's folder: Windows Search does not index that location (a network share, a
    /// removable drive, a folder the user has kept out of the index) or it has not read it yet, or the folder is empty.
    /// </summary>
    LocationNotIndexed = 1,

    /// <summary>
    /// The index keeps no text for a type of file the query names: Windows has no filter to read it with (a <c>.md</c> or a
    /// <c>.log</c> file, unless the user has set one up), or it is a picture, a video or a sound, whose properties are
    /// indexed but which have no text.
    /// </summary>
    FileTypeNotContentIndexed = 2,
}

/// <summary>
/// What the index can do for a query's content criterion: the flag that keeps an empty answer honest. It explains the
/// results and never changes them: a query is answered whatever this says.
/// </summary>
public sealed record ContentSearchCapability
{
    /// <summary>Whether the index can look inside the files the query covers.</summary>
    public ContentSearchSupport Support { get; init; }

    /// <summary>What limits it, when <see cref="Support"/> is <see cref="ContentSearchSupport.Partial"/> or <see cref="ContentSearchSupport.Unavailable"/>.</summary>
    public ContentSearchLimits Limits { get; init; }

    /// <summary>The extensions the query names that the index keeps no text for, lower case with their dot.</summary>
    public IReadOnlyList<string> UnsupportedExtensions { get; init; } = [];

    /// <summary>The capability of a query that does not ask for a content search.</summary>
    public static ContentSearchCapability NotRequested { get; } = new() { Support = ContentSearchSupport.NotRequested };

    /// <summary>The capability of a content query that nothing is known to limit.</summary>
    public static ContentSearchCapability Available { get; } = new() { Support = ContentSearchSupport.Available };

    /// <summary>Whether the index cannot look inside any of the files the query covers.</summary>
    public bool IsUnavailable => Support == ContentSearchSupport.Unavailable;

    /// <summary>Whether the index cannot look inside all of the files the query covers: <see cref="Support"/> is partial or unavailable.</summary>
    public bool IsLimited => Support is ContentSearchSupport.Partial or ContentSearchSupport.Unavailable;
}

/// <summary>
/// The answer to a file search and what the index could not do for it. It carries paths and properties, never the text that
/// matched, and its <see cref="object.ToString"/> shows none of it.
/// </summary>
/// <param name="Items">The results, as <see cref="IFileSearchService.SearchAsync"/> returns them.</param>
/// <param name="ContentSearch">Whether the index can look inside the files the query covers.</param>
public sealed record FileSearchOutcome(IReadOnlyList<SearchResultItem> Items, ContentSearchCapability ContentSearch)
{
    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Items = {Items.Count}, ContentSearch = {ContentSearch.Support}");
        return true;
    }
}
