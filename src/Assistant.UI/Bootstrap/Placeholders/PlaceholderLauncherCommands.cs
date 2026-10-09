using System.Windows.Input;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>
/// The launcher's four categories with commands that do nothing yet (PROJECT_SPEC §4.1): each later step that can
/// list applications, files, actions or the clipboard replaces its own command here.
/// </summary>
internal static class PlaceholderLauncherCommands
{
    // The reference draws the Command key, ⌘, on its hints. Windows has no such key, so the shortcut itself is
    // Ctrl+number, and only what is drawn follows the reference.
    private const string CommandKeyGlyph = "Glyph.CommandKey";

    public static IReadOnlyList<CommandItemViewModel> Create() =>
    [
        Category("Applications", "Glyph.Applications", 1),
        Category("Files", "Glyph.Files", 2),
        Category("Actions", "Glyph.Actions", 3),
        Category("Clipboard", "Glyph.Clipboard", 4),
    ];

    private static CommandItemViewModel Category(string title, string iconKey, int number) =>
        new(title, iconKey, new RelayCommand(() => { }), new KeyHint(CommandKeyGlyph, number.ToString()),
            new KeyGesture(Key.D0 + number, ModifierKeys.Control));
}
