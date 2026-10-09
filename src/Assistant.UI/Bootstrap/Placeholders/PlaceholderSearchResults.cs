using Assistant.UI.ViewModels;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>
/// Sample sections of results under the Search or Ask bar (PROJECT_SPEC §4.1), until Windows Search feeds it (§4.7).
/// Only two queries have any, so ordinary typing is left alone. What starts with <c>brave</c> gives the Search
/// reference's sections (a top hit, a definition, and applications with badges on their icons) and then any made-up
/// conversations that mention it. What starts with <c>demo</c> gives every kind once or more: conversations and
/// contacts (<see cref="FakeMessageDirectory"/>), a file and an action, and the row that would search Messages. Every
/// command does nothing.
/// </summary>
internal sealed class PlaceholderSearchResults(TimeProvider? time = null, bool includeBraveSample = true, bool includeDemoSample = true) : ISearchResultsSource
{
    private readonly FakeMessageDirectory _messages = new(time ?? TimeProvider.System);

    public IReadOnlyList<SearchResultSectionViewModel> Search(string query)
    {
        var text = query.Trim();

        // The reference's sections for "brave" are what the real search shows for the applications that are installed; the sample is
        // only for a test that wants to see the reference's rows.
        if (includeBraveSample && text.StartsWith("brave", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                Section(
                    Result(SearchResultKind.App, "Brave Browser", actionHint: new SearchResultActionHint("Search Brave Browser", "tab"))),
                Section(
                    Result(SearchResultKind.Knowledge, "Atlanta Braves", subtitle: "MLB baseball team")),
                Section(
                    Result(SearchResultKind.App, "Brave — From iPhone", badges: [SearchResultBadge.Phone]),
                    Result(SearchResultKind.App, "Brave — From iPhone", badges: [SearchResultBadge.Phone]),
                    Result(SearchResultKind.App, "Brave Browser", badges: [SearchResultBadge.Settings])),
                .. SearchResultGrouping.GroupByKind(_messages.Search("brave"), maxPerGroup: 3),
            ];
        }

        // The made-up conversations, contacts and file are a developer's sample: the app lists them only when it was started with the samples on.
        if (includeDemoSample && text.StartsWith("demo", StringComparison.OrdinalIgnoreCase))
        {
            var results = _messages.Search("").Concat(
            [
                Result(SearchResultKind.File, "Sample document.docx", subtitle: "Documents", detail: "Yesterday"),
                Result(SearchResultKind.Action, "Sample action", actionHint: new SearchResultActionHint("Run", "enter")),
            ]);
            return
            [
                .. SearchResultGrouping.GroupByKind(
                    results, order: [SearchResultKind.Message, SearchResultKind.Contact, SearchResultKind.File, SearchResultKind.Action]),
                Section(new SearchResultViewModel(SearchResultKind.App, "Search Messages", new RelayCommand(() => { }),
                    icon: SearchResultIcon.FromGlyph("Result.Icon.Message"))),
            ];
        }

        return [];
    }

    private static SearchResultSectionViewModel Section(params SearchResultViewModel[] items) => new(null, items);

    private static SearchResultViewModel Result(
        SearchResultKind kind, string title, string? subtitle = null, string? detail = null,
        SearchResultBadge[]? badges = null, SearchResultActionHint? actionHint = null) =>
        new(kind, title, new RelayCommand(() => { }), subtitle, detail, badges: badges, actionHint: actionHint);
}
