namespace Assistant.UI.ViewModels;

/// <summary>
/// Where the results that take time come from: a source that has to ask Windows Search, which the bar cannot wait for while
/// the user types. The results under the bar ask it after a short pause in the typing, and show what it finds when it is
/// done, unless something was typed since. The query is private content (PROJECT_SPEC §3.2): an implementation never logs it.
/// </summary>
public interface IAsyncSearchResultsSource
{
    /// <summary>
    /// The sections of results for <paramref name="query"/>, in the order they are shown; none for no results. It may run
    /// on any thread, and is cancelled as soon as the query changes. A search that fails is no results, never an exception.
    /// </summary>
    Task<IReadOnlyList<SearchResultSectionViewModel>> SearchAsync(string query, CancellationToken cancellationToken);
}
