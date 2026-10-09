using System.Windows.Input;

namespace Assistant.UI.ViewModels;

/// <summary>
/// One row of a list of commands (PROJECT_SPEC §4.1): a glyph, a title and, at the right, the keys that run it. The
/// launcher's categories are made of these, and so will be any later list of the same kind. What the row does is its
/// <see cref="Command"/>.
/// </summary>
public sealed class CommandItemViewModel
{
    public CommandItemViewModel(
        string title, string iconKey, ICommand command, KeyHint? hint = null, KeyGesture? shortcut = null)
    {
        Title = title;
        IconKey = iconKey;
        Command = command;
        Hint = hint;
        Shortcut = shortcut;
    }

    /// <summary>The row's words.</summary>
    public string Title { get; }

    /// <summary>
    /// The key, in the theme's resources, of the data template that draws the row's glyph (for example
    /// <c>Glyph.Applications</c> in <c>Themes/Controls/Launcher.xaml</c>).
    /// </summary>
    public string IconKey { get; }

    /// <summary>What the row does when it is activated.</summary>
    public ICommand Command { get; }

    /// <summary>The keys that run the row as drawn at its right edge, or <see langword="null"/> for none.</summary>
    public KeyHint? Hint { get; }

    /// <summary>The keys that run the row while its list is showing, or <see langword="null"/> for none.</summary>
    public KeyGesture? Shortcut { get; }

    /// <summary>The shortcut spelled the way assistive technology reads it, such as <c>Ctrl+1</c>.</summary>
    public string? ShortcutText => Shortcut?.GetDisplayStringForCulture(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="key"/> with <paramref name="modifiers"/> is this row's shortcut.</summary>
    internal bool Matches(Key key, ModifierKeys modifiers) =>
        Shortcut is { } shortcut && shortcut.Key == key && shortcut.Modifiers == modifiers;

    /// <summary>Runs the row.</summary>
    internal void Activate()
    {
        if (Command.CanExecute(null))
        {
            Command.Execute(null);
        }
    }
}
