using System.Text;
using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>
/// A structured query against the Windows Search index. Each criterion that is set narrows the search, and an item must meet
/// all of them: the words (<see cref="Text"/>, <see cref="Filename"/>, <see cref="ContentTerm"/>), the type of file
/// (<see cref="Extensions"/>, <see cref="Kind"/>), when it was created or modified, how large it is and where it is
/// (<see cref="Folder"/>). A criterion that is set but cannot be met (a word with no letter or digit in it, a range that ends
/// before it starts, a folder that is not a full path) makes the search find nothing, never everything, and so does a query
/// with no criterion at all. The <see cref="Order"/> and the limit decide which of the matching items come back.
/// </summary>
/// <param name="Text">
/// What the user or the model is looking for, in words: each word (or a phrase in double quotes) must be in the item's name
/// or, with <see cref="MatchContents"/>, in its text or properties. Optional.
/// </param>
public sealed record FileSearchQuery(string? Text = null)
{
    /// <summary>The most characters a <see cref="ContentTerm"/> keeps; any beyond them are cut off.</summary>
    public const int MaxContentTermLength = 200;

    /// <summary>The most <see cref="Extensions"/> a query uses; any beyond them are ignored.</summary>
    public const int MaxExtensions = 20;

    /// <summary>Kinds of items to return.</summary>
    /// <remarks>
    /// Apps are not files and are not found by this search. A criterion only a file can meet (<see cref="Extensions"/>,
    /// <see cref="Kind"/>, <see cref="Size"/>, <see cref="ContentTerm"/>) leaves out folders. Each kind is searched and
    /// limited on its own, and the results come in the order the kinds are listed here.
    /// </remarks>
    public IReadOnlyList<SearchResultItemType> Types { get; init; } =
        [SearchResultItemType.App, SearchResultItemType.File, SearchResultItemType.Folder];

    /// <summary>Maximum number of results for each kind in <see cref="Types"/>. Nothing is returned for zero or less.</summary>
    public int MaxResultsPerType { get; init; } = 5;

    /// <summary>Whether <see cref="Text"/> is also looked for in the items' text and properties, not only in their names.</summary>
    public bool MatchContents { get; init; }

    /// <summary>
    /// Words the item's file name (with its extension) must contain, each of them, in any order; a phrase in double quotes as
    /// it stands. Optional.
    /// </summary>
    public string? Filename { get; init; }

    /// <summary>
    /// A phrase that the text inside the file must contain, as words next to each other in this order, whole words and
    /// whatever their case. Optional.
    /// </summary>
    /// <remarks>
    /// Only files whose text the index holds can match, and Windows Search does not hold every file's text (a location may
    /// not be indexed, and a file type may be indexed for its properties only). What a result carries is its path and
    /// properties: the index gives no excerpt of the match. <see cref="IFileSearchService.SearchWithCapabilitiesAsync"/>
    /// says when the index cannot look inside the files a query covers, which is not the same as no file containing the phrase.
    /// </remarks>
    public string? ContentTerm { get; init; }

    /// <summary>
    /// The extensions the file may have, any of them: <c>.pdf</c> or <c>pdf</c>, in any case. An entry that cannot be an
    /// extension is ignored, and a list of only such entries finds nothing. Empty for any extension.
    /// </summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>The kind of file Windows classes it as. Optional; see <see cref="FileKind"/>.</summary>
    public FileKind? Kind { get; init; }

    /// <summary>When the item was created. Unbounded by default.</summary>
    public DateRange Created { get; init; }

    /// <summary>When the item was last modified. Unbounded by default.</summary>
    public DateRange Modified { get; init; }

    /// <summary>How large the file is. Unbounded by default. A folder has no size and is never found by a size.</summary>
    public SizeRange Size { get; init; }

    /// <summary>
    /// The folder to look in, as a full path (a relative one finds nothing). Optional; without it the whole index is searched.
    /// A folder the user has excluded finds nothing.
    /// </summary>
    public string? Folder { get; init; }

    /// <summary>Whether <see cref="Folder"/> includes the folders inside it (the default) or only what is directly in it.</summary>
    public bool IncludeSubfolders { get; init; } = true;

    /// <summary>The order of the results. <see cref="FileSearchOrder.Relevance"/> by default.</summary>
    public FileSearchOrder Order { get; init; }

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs: it names the criteria that are set,
    // never their values.
    private bool PrintMembers(StringBuilder builder)
    {
        var criteria = new List<string>();
        AddIf(criteria, Text is not null, nameof(Text));
        AddIf(criteria, Filename is not null, nameof(Filename));
        AddIf(criteria, ContentTerm is not null, nameof(ContentTerm));
        AddIf(criteria, Extensions is { Count: > 0 }, nameof(Extensions));
        AddIf(criteria, Kind is not null, nameof(Kind));
        AddIf(criteria, !Created.IsUnbounded, nameof(Created));
        AddIf(criteria, !Modified.IsUnbounded, nameof(Modified));
        AddIf(criteria, !Size.IsUnbounded, nameof(Size));
        AddIf(criteria, Folder is not null, nameof(Folder));

        builder.Append(
            $"Types = [{string.Join(", ", Types)}], MaxResultsPerType = {MaxResultsPerType}, " +
            $"MatchContents = {MatchContents}, Order = {Order}, Criteria = [{string.Join(", ", criteria)}]");
        return true;
    }

    private static void AddIf(List<string> criteria, bool isSet, string name)
    {
        if (isSet)
        {
            criteria.Add(name);
        }
    }
}
