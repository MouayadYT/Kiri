namespace Assistant.Core.Settings;

/// <summary>A global keyboard shortcut.</summary>
/// <param name="Modifiers">Modifier keys held down with <paramref name="Key"/>.</param>
/// <param name="Key">The main key, named as on the keyboard, such as <c>A</c>, <c>F1</c> or <c>Space</c>.</param>
public sealed record Hotkey(HotkeyModifiers Modifiers, string Key)
{
    private static readonly (HotkeyModifiers Flag, string Label)[] ModifierLabels =
    [
        (HotkeyModifiers.Control, "Ctrl"),
        (HotkeyModifiers.Alt, "Alt"),
        (HotkeyModifiers.Shift, "Shift"),
        (HotkeyModifiers.Windows, "Win"),
    ];

    /// <summary>Returns the shortcut as shown to the user, such as <c>Alt+Shift+W</c>.</summary>
    public override string ToString() =>
        string.Join('+', ModifierLabels.Where(m => Modifiers.HasFlag(m.Flag)).Select(m => m.Label).Append(Key));
}
