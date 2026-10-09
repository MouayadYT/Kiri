namespace Assistant.UI.ViewModels;

/// <summary>
/// Sorts a flat list of results into sections by what they are (PROJECT_SPEC §4.1): the top few of each kind, one kind
/// to a section, in a fixed order. A source that already knows its sections builds them itself; this is for the ones
/// that find results of several kinds at once.
/// </summary>
public static class SearchResultGrouping
{
    /// <summary>The order sections are listed in: applications, files, then people and what they said, then actions.</summary>
    public static IReadOnlyList<SearchResultKind> DefaultOrder { get; } =
    [
        SearchResultKind.App, SearchResultKind.File, SearchResultKind.Contact, SearchResultKind.Message,
        SearchResultKind.Action, SearchResultKind.Clipboard, SearchResultKind.Knowledge,
    ];

    /// <summary>The header words for a section of <paramref name="kind"/>.</summary>
    public static string TitleOf(SearchResultKind kind) => kind switch
    {
        SearchResultKind.App => "Applications",
        SearchResultKind.File => "Files",
        SearchResultKind.Contact => "Contacts",
        SearchResultKind.Message => "Messages",
        SearchResultKind.Action => "Actions",
        SearchResultKind.Clipboard => "Clipboard",
        _ => "Definitions",
    };

    /// <summary>
    /// One section for each kind that has results, in <paramref name="order"/>. A section keeps its results in the
    /// order they came in and stops at <paramref name="maxPerGroup"/>. Its header is shown only if
    /// <paramref name="titled"/>, as the reference draws none.
    /// </summary>
    public static IReadOnlyList<SearchResultSectionViewModel> GroupByKind(
        IEnumerable<SearchResultViewModel> results, int maxPerGroup = 5, bool titled = false,
        IReadOnlyList<SearchResultKind>? order = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerGroup, 1);
        var byKind = results.ToLookup(result => result.Kind);
        return
        [
            .. (order ?? DefaultOrder)
                .Distinct()
                .Where(kind => byKind[kind].Any())
                .Select(kind => new SearchResultSectionViewModel(titled ? TitleOf(kind) : null, byKind[kind].Take(maxPerGroup))),
        ];
    }
}
