using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Assistant.UI.ViewModels;

/// <summary>
/// One result of a search (PROJECT_SPEC §4.1), of any kind: an app, a file, an action, a contact, a message. It holds
/// what its row draws (an icon, a title, a subtitle, a note at the right such as a date, badges on the icon, and the
/// action it offers while it is highlighted) and what the row does when it is activated. Nothing about it is private
/// beyond what the user typed or searched for, so it is never logged.
/// </summary>
public sealed class SearchResultViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public SearchResultViewModel(
        SearchResultKind kind, string title, ICommand command, string? subtitle = null, string? detail = null,
        SearchResultIcon? icon = null, IEnumerable<SearchResultBadge>? badges = null,
        SearchResultActionHint? actionHint = null, SearchResultRowSize? size = null,
        IEnumerable<SearchResultAction>? actions = null, IEnumerable<SearchResultAlternate>? alternates = null)
    {
        Actions = new ReadOnlyCollection<SearchResultAction>([.. actions ?? []]);
        Alternates = new ReadOnlyCollection<SearchResultAlternate>([.. alternates ?? []]);
        Kind = kind;
        Title = title;
        Command = command;
        Subtitle = subtitle ?? "";
        Detail = detail ?? "";
        Icon = icon ?? SearchResultIcon.ForKind(kind);
        Badges = new ReadOnlyCollection<SearchResultBadge>([.. badges ?? []]);
        ActionHint = actionHint;
        Size = size ?? (kind == SearchResultKind.Knowledge ? SearchResultRowSize.Large : SearchResultRowSize.Standard);
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>What the result is.</summary>
    public SearchResultKind Kind { get; }

    /// <summary>The result's name.</summary>
    public string Title { get; }

    /// <summary>A second line under the name, such as what a file is or the start of a message; empty for none.</summary>
    public string Subtitle { get; }

    /// <summary>A note at the right of the name, such as a date; empty for none.</summary>
    public string Detail { get; }

    /// <summary>The picture at the left; never <see langword="null"/>: without one of its own, the kind's default.</summary>
    public SearchResultIcon Icon { get; }

    /// <summary>The marks on the icon's corner, if any.</summary>
    public IReadOnlyList<SearchResultBadge> Badges { get; }

    /// <summary>What the row offers while it is highlighted, or <see langword="null"/> for nothing.</summary>
    public SearchResultActionHint? ActionHint { get; }

    /// <summary>How much room the row gives its icon.</summary>
    public SearchResultRowSize Size { get; }

    /// <summary>What the row does when it is activated.</summary>
    public ICommand Command { get; }

    /// <summary>What else it can do, each by a key of its own while the row is highlighted.</summary>
    public IReadOnlyList<SearchResultAction> Actions { get; }

    /// <summary>
    /// The other things the result can do, listed when the user asks for them (<c>Tab</c> on the highlighted row), so that the row itself
    /// shows only what <c>Enter</c> does. Empty for a result with nothing else to offer.
    /// </summary>
    public IReadOnlyList<SearchResultAlternate> Alternates { get; }

    /// <summary>
    /// A stable identity for the result (the quick-search result's id), so that a list that is replaced while the user is moving
    /// through it can keep the same result highlighted; <see langword="null"/> for a result with none.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// What the highlighted row says about its alternates, drawn beside what <c>Enter</c> does ("Actions" and the key that lists them);
    /// <see langword="null"/> for a row that has no alternates.
    /// </summary>
    public SearchResultActionHint? SecondaryHint { get; init; }

    /// <summary>The words assistive technology reads for the row.</summary>
    public string AutomationName => string.Join(", ", new[] { Title, Subtitle, Detail }.Where(part => part.Length > 0));

    /// <summary>Whether the row is the highlighted one. The list of results sets it; the row only draws it.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Runs the row.</summary>
    internal void Activate()
    {
        if (Command.CanExecute(null))
        {
            Command.Execute(null);
        }
    }

    /// <summary>Runs the action of the row that <paramref name="key"/> and <paramref name="modifiers"/> are the keys of.</summary>
    /// <returns><see langword="true"/> when the row has such an action, so the key is the results' and not the editor's.</returns>
    internal bool TryRunAction(Key key, ModifierKeys modifiers)
    {
        var action = Actions.FirstOrDefault(candidate => candidate.Key == key && candidate.Modifiers == modifiers);
        if (action is null)
        {
            return false;
        }

        if (action.Command.CanExecute(null))
        {
            action.Command.Execute(null);
        }

        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
