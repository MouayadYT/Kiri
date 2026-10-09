namespace Assistant.Core.Settings;

/// <summary>Global keyboard shortcuts (PROJECT_SPEC §4.0). <see langword="null"/> turns a shortcut off.</summary>
public sealed record HotkeySettings
{
    /// <summary>Opens the Search or Ask bar. Default <c>Alt+A</c>.</summary>
    public Hotkey? SearchOrAsk { get; init; } = new(HotkeyModifiers.Alt, "A");

    /// <summary>Reads the selected text and opens the Ask panel with it (Ask Selection). Default <c>Alt+Shift+W</c>.</summary>
    public Hotkey? SelectedTextActions { get; init; } = new(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "W");

    /// <summary>
    /// Reads the selection by pressing Copy in the app in front, for apps that do not share it (Ask Selection by copy, step 89). Default
    /// <c>Alt+Shift+C</c>. It is registered with Windows only while the Selected Text by Copy permission is on.
    /// </summary>
    public Hotkey? SelectedTextByCopy { get; init; } = new(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "C");

    /// <summary>Starts a screen capture for Visual Intelligence. Default <c>Alt+Shift+S</c>.</summary>
    public Hotkey? VisualIntelligence { get; init; } = new(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "S");
}
