using System.Globalization;

namespace Assistant.Core.Settings;

/// <summary>
/// The names a <see cref="Hotkey"/>'s main key may have: a letter, a digit, a function key F1 to F24, or one of a few
/// named keys. They are the names the global hotkey registration understands, so a shortcut that passes here can be
/// registered (unless another app already holds it).
/// </summary>
public static class HotkeyNames
{
    private static readonly string[] Named =
    [
        "Backspace", "Tab", "Enter", "Escape", "Space", "PageUp", "PageDown", "End", "Home", "Left", "Up", "Right", "Down",
        "Insert", "Delete",
    ];

    /// <summary>The named keys, in the order they are listed to the user.</summary>
    public static IReadOnlyList<string> NamedKeys => Named;

    /// <summary>Whether <paramref name="name"/> is a key name a shortcut can use (letters in either case).</summary>
    public static bool IsValid(string? name) => Normalize(name) is not null;

    /// <summary>
    /// The canonical spelling of the key <paramref name="name"/>: capital letters and <c>F</c> for a function key, and the
    /// named keys as listed. Returns <see langword="null"/> when there is no such key.
    /// </summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var text = name.Trim();
        if (text.Length == 1 && (char.IsAsciiLetter(text[0]) || char.IsAsciiDigit(text[0])))
        {
            return char.ToUpperInvariant(text[0]).ToString();
        }

        if ((text[0] is 'F' or 'f') && text.Length is 2 or 3
            && int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= 24)
        {
            return $"F{number.ToString(CultureInfo.InvariantCulture)}";
        }

        return Array.Find(Named, known => string.Equals(known, text, StringComparison.OrdinalIgnoreCase));
    }
}
