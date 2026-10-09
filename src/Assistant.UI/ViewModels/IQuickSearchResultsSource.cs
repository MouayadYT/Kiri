using System.Windows.Input;
using Assistant.Core.QuickSearch;

namespace Assistant.UI.ViewModels;

/// <summary>
/// What a source of results has found so far for what is typed (PROJECT_SPEC §4.1): the sections to list, which result is highlighted
/// until the user moves, and what a narrowed list says when it has nothing in it.
/// </summary>
/// <param name="Sections">The sections, in the order they are shown.</param>
public sealed record SearchResultsSnapshot(IReadOnlyList<SearchResultSectionViewModel> Sections)
{
    /// <summary>
    /// The result that is highlighted for the user until they move the highlight themselves: the one whose name is what was typed or
    /// begins with it, so that Enter opens it. <see langword="null"/> highlights nothing, and Enter asks.
    /// </summary>
    public SearchResultViewModel? Highlighted { get; init; }

    /// <summary>What a list narrowed to one kind of result says when it has no results; empty for nothing.</summary>
    public string EmptyMessage { get; init; } = "";
}

/// <summary>
/// Where the results under the bar come from while the user types, as they come: the sections are reported again each time a provider
/// has answered, so the quick ones are on screen before the slow one. The text is private content (PROJECT_SPEC §3.2): an
/// implementation never logs it.
/// </summary>
public interface IQuickSearchResultsSource
{
    /// <summary>
    /// Looks for the results of <paramref name="query"/> and reports what has been found so far through <paramref name="report"/>, which
    /// may be called any number of times from any thread. It ends when everything has answered, and is cancelled as soon as the query
    /// changes. A search that fails reports what it has, and never throws.
    /// </summary>
    /// <param name="query">What was typed; empty when a kind of result is being browsed.</param>
    /// <param name="scope">The kind of result the list is narrowed to, or <see langword="null"/> for all of them.</param>
    /// <param name="report">Receives each snapshot.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    Task SearchAsync(
        string query, QuickSearchResultType? scope, Action<SearchResultsSnapshot> report, CancellationToken cancellationToken);
}

/// <summary>
/// One of the chips over the results that narrows them to a kind of result (PROJECT_SPEC §4.1): Applications, Files, Actions or
/// Clipboard. Choosing one lists only that kind; choosing it again lists everything.
/// </summary>
public sealed class SearchScopeChipViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isActive;

    /// <summary>Creates the chip for <paramref name="type"/>.</summary>
    public SearchScopeChipViewModel(QuickSearchResultType type, string title, ICommand command, string? shortcutText = null)
    {
        Type = type;
        Title = title;
        Command = command;
        ShortcutText = shortcutText;
    }

    /// <inheritdoc/>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The kind of result it narrows to.</summary>
    public QuickSearchResultType Type { get; }

    /// <summary>Its words.</summary>
    public string Title { get; }

    /// <summary>What it does when it is chosen.</summary>
    public ICommand Command { get; }

    /// <summary>The keys that choose it, spelled the way assistive technology reads them (<c>Ctrl+2</c>), or <see langword="null"/>.</summary>
    public string? ShortcutText { get; }

    /// <summary>The words assistive technology reads for the chip.</summary>
    public string AutomationName => ShortcutText is null ? Title : $"{Title} filter, {ShortcutText}";

    /// <summary>Whether the list is narrowed to this chip's kind.</summary>
    public bool IsActive
    {
        get => _isActive;
        internal set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsActive)));
        }
    }
}
