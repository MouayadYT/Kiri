using System.Collections.ObjectModel;

namespace Assistant.UI.ViewModels;

/// <summary>
/// A group of search results that lie together under the search field, such as the top hit, or the applications, or
/// the messages. Sections are drawn one under another with a small gap between them; a section with a title has a
/// header over its rows, and one without has none, as in the reference.
/// </summary>
public sealed class SearchResultSectionViewModel
{
    public SearchResultSectionViewModel(string? title, IEnumerable<SearchResultViewModel> items)
    {
        Title = title ?? "";
        Items = new ReadOnlyCollection<SearchResultViewModel>([.. items]);
    }

    /// <summary>The words over the section's rows; empty for none.</summary>
    public string Title { get; }

    /// <summary>The section's results, top to bottom.</summary>
    public IReadOnlyList<SearchResultViewModel> Items { get; }
}
