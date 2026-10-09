using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Assistant.UI.ViewModels;

/// <summary>
/// The category panel under the Search or Ask bar while its field is empty (PROJECT_SPEC §4.1): a short list of
/// commands, one of which may be selected with the arrow keys. Nothing is selected when the panel appears. The list is
/// only moved through and run from the keyboard here: the bar's editor keeps the focus throughout, so typing still goes
/// to it.
/// </summary>
public sealed class LauncherViewModel : INotifyPropertyChanged
{
    private CommandItemViewModel? _selected;

    public LauncherViewModel(IEnumerable<CommandItemViewModel> items)
    {
        Items = new ReadOnlyCollection<CommandItemViewModel>([.. items]);
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The commands, top to bottom.</summary>
    public IReadOnlyList<CommandItemViewModel> Items { get; }

    /// <summary>Whether there is anything to list. A launcher without commands is never shown.</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>The highlighted command, or <see langword="null"/> when none is.</summary>
    public CommandItemViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            if (value is not null && !Items.Contains(value))
            {
                value = null;
            }

            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            _selected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Handles a key the bar received while the panel shows. <c>↓</c> and <c>↑</c> move the highlight, from none to
    /// the first or the last and around the ends; <c>Enter</c> runs the highlighted command; a command's own shortcut
    /// runs it at once.
    /// </summary>
    /// <returns><see langword="true"/> when the key was the panel's, so the editor must not see it.</returns>
    public bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (!HasItems)
        {
            return false;
        }

        if (modifiers == ModifierKeys.None)
        {
            switch (key)
            {
                case Key.Down:
                    MoveSelection(1);
                    return true;
                case Key.Up:
                    MoveSelection(-1);
                    return true;
                case Key.Enter when _selected is not null:
                    _selected.Activate();
                    return true;
            }
        }

        if (Items.FirstOrDefault(item => item.Matches(key, modifiers)) is { } shortcut)
        {
            SelectedItem = shortcut;
            shortcut.Activate();
            return true;
        }

        return false;
    }

    /// <summary>Moves the highlight <paramref name="steps"/> rows down (or up, when negative), around the ends.</summary>
    public void MoveSelection(int steps)
    {
        if (!HasItems)
        {
            return;
        }

        var count = Items.Count;
        var current = _selected is null ? -1 : Items.ToList().IndexOf(_selected);
        var next = current < 0
            ? (steps > 0 ? steps - 1 : count + steps)
            : current + steps;
        SelectedItem = Items[((next % count) + count) % count];
    }

    /// <summary>Highlights nothing.</summary>
    public void ClearSelection() => SelectedItem = null;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
