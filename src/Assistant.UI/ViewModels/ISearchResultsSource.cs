namespace Assistant.UI.ViewModels;

/// <summary>
/// Where the results listed under the Search or Ask bar come from. The bar asks it for the sections to show for what
/// is typed, on every change. The query is private content (PROJECT_SPEC §3.2): an implementation never logs it.
/// </summary>
public interface ISearchResultsSource
{
    /// <summary>The sections of results for <paramref name="query"/>, in the order they are shown; none for no results.</summary>
    IReadOnlyList<SearchResultSectionViewModel> Search(string query);
}
