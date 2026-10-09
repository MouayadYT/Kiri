using System.Windows.Input;
using Assistant.Core.Settings;

namespace Assistant.UI.Settings;

/// <summary>Turns the keys WPF reports into the names a shortcut's key has in the settings (<see cref="HotkeyNames"/>).</summary>
public static class HotkeyKeyMap
{
    /// <summary>
    /// The name of <paramref name="key"/> as a shortcut's main key, or <see langword="null"/> when it is a modifier or a
    /// key a shortcut cannot use, such as a media key or a numeric keypad key (which registers as another key).
    /// </summary>
    public static string? NameOf(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return ((char)('A' + (key - Key.A))).ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((char)('0' + (key - Key.D0))).ToString();
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            return $"F{(key - Key.F1) + 1}";
        }

        return key switch
        {
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Enter => "Enter",
            Key.Back => "Backspace",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.End => "End",
            Key.Home => "Home",
            Key.Left => "Left",
            Key.Up => "Up",
            Key.Right => "Right",
            Key.Down => "Down",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            _ => null,
        };
    }

    /// <summary>The modifiers a shortcut records, from the keys held down as WPF reports them.</summary>
    public static HotkeyModifiers ModifiersOf(ModifierKeys keys)
    {
        var modifiers = HotkeyModifiers.None;
        if (keys.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (keys.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (keys.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (keys.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        return modifiers;
    }
}
