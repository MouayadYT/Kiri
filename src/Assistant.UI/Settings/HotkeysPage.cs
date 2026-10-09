using Assistant.Core.Settings;

namespace Assistant.UI.Settings;

/// <summary>
/// Hotkeys (PROJECT_SPEC §4.0): the global shortcuts. The ones that open the Search or Ask bar and start Visual Intelligence can be
/// changed, and take effect the next time the Assistant starts, since their keys are registered with Windows then. The third opens a
/// feature that does not exist yet, so it shows what is saved and cannot be changed yet.
/// </summary>
public sealed class HotkeysPage : SettingsPage
{
    private static readonly HotkeySettings Defaults = new();

    internal HotkeysPage(SettingsViewModel root)
        : base(root, SettingsSection.Hotkeys)
    {
        SearchOrAsk = new HotkeyEditor(
            "Search or Ask", "Opens the Search or Ask bar from anywhere.", Defaults.SearchOrAsk,
            shortcut => Commit(settings => settings with { Hotkeys = settings.Hotkeys with { SearchOrAsk = shortcut } }),
            shortcut => UsedBy(shortcut, exceptSearchOrAsk: true));
        SelectedText = new HotkeyEditor(
            "Selected text", "Opens the Ask panel with the text you have selected.", Defaults.SelectedTextActions,
            shortcut => Commit(settings => settings with { Hotkeys = settings.Hotkeys with { SelectedTextActions = shortcut } }),
            shortcut => UsedBy(shortcut, exceptSelectedText: true));
        VisualIntelligence = new HotkeyEditor(
            "Visual Intelligence", "Captures part of the screen to ask about.", Defaults.VisualIntelligence,
            shortcut => Commit(settings => settings with { Hotkeys = settings.Hotkeys with { VisualIntelligence = shortcut } }),
            shortcut => UsedBy(shortcut, exceptVisual: true));
        SelectedTextByCopy = new HotkeyEditor(
            "Selected text by copy",
            "For apps that don't share their selection: presses Copy in the app, reads it, and puts your clipboard back. Needs Selected Text by Copy to be on.",
            Defaults.SelectedTextByCopy,
            shortcut => Commit(settings => settings with { Hotkeys = settings.Hotkeys with { SelectedTextByCopy = shortcut } }),
            shortcut => UsedBy(shortcut, exceptCopy: true));
    }

    /// <summary>Opens the Search or Ask bar.</summary>
    public HotkeyEditor SearchOrAsk { get; }

    /// <summary>Opens the Ask panel with the selected text.</summary>
    public HotkeyEditor SelectedText { get; }

    /// <summary>Captures the screen.</summary>
    public HotkeyEditor VisualIntelligence { get; }

    /// <summary>Reads the selected text by pressing Copy in the app in front (step 89).</summary>
    public HotkeyEditor SelectedTextByCopy { get; }

    internal override void Apply(AppSettings settings, bool fresh)
    {
        SearchOrAsk.Show(settings.Hotkeys.SearchOrAsk);
        SelectedText.Show(settings.Hotkeys.SelectedTextActions);
        VisualIntelligence.Show(settings.Hotkeys.VisualIntelligence);
        SelectedTextByCopy.Show(settings.Hotkeys.SelectedTextByCopy);
    }

    // What another shortcut that has these keys opens, or null when they are free.
    private string? UsedBy(Hotkey shortcut, bool exceptSearchOrAsk = false, bool exceptSelectedText = false, bool exceptVisual = false, bool exceptCopy = false)
    {
        (HotkeyEditor Editor, bool Skip)[] editors =
        [
            (SearchOrAsk, exceptSearchOrAsk),
            (SelectedText, exceptSelectedText),
            (VisualIntelligence, exceptVisual),
            (SelectedTextByCopy, exceptCopy),
        ];
        return editors.FirstOrDefault(entry => !entry.Skip && entry.Editor.Shortcut is { } other && SettingsValidator.Same(shortcut, other))
            .Editor?.Title;
    }
}
