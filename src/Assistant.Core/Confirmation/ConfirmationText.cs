using System.Globalization;
using System.Text;

namespace Assistant.Core.Confirmation;

/// <summary>
/// Makes text that was written by someone else (the model, a file, a connected app) safe to put in front of the user as what they are asked to
/// approve. The user is shown exactly what will happen, so the text must not be able to look like something else: a control character, a
/// character that reverses the direction of what follows it, or one that has no width is shown as a visible mark instead of doing what it does.
/// Everything else, including emoji and text in any script, is left exactly as it is.
/// </summary>
public static class ConfirmationText
{
    /// <summary>
    /// <paramref name="text"/> as it may be shown: line breaks are made <c>\n</c>, a control character becomes its picture (<c>␀</c>), and a
    /// character that is invisible or turns text around becomes <c>⟨U+202E⟩</c>. A single line has no line break: each becomes a space.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="multiline">Whether the text may hold line breaks (what a message says), or is one line (a label, a name).</param>
    public static string Visible(string? text, bool multiline)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = new StringBuilder(text.Length);
        var previousWasReturn = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            var isReturn = value == '\r';
            if (value == '\n' && previousWasReturn)
            {
                // "\r\n" is one break, which the "\r" already made.
                previousWasReturn = false;
                continue;
            }

            previousWasReturn = isReturn;
            if (value is '\r' or '\n' or 0x85 or 0x2028 or 0x2029)
            {
                result.Append(multiline ? '\n' : ' ');
            }
            else if (value == '\t')
            {
                result.Append(multiline ? '\t' : ' ');
            }
            else if (value < 0x20)
            {
                result.Append((char)(0x2400 + value));
            }
            else if (value == 0x7F)
            {
                result.Append('␡');
            }
            else if (value is >= 0x80 and < 0xA0 || Hides(rune))
            {
                result.Append("⟨U+").Append(value.ToString("X4", CultureInfo.InvariantCulture)).Append('⟩');
            }
            else
            {
                result.Append(rune.ToString());
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="length"/> characters with an ellipsis, as one line, for a name that is put in a question's title,
    /// which is short. The lines under the title are never cut: they say everything.
    /// </summary>
    public static string Short(string? text, int length)
    {
        var line = Visible(text, multiline: false).Trim();
        if (line.Length <= length)
        {
            return line;
        }

        // Not in the middle of a pair of surrogates.
        var end = char.IsHighSurrogate(line[length - 2]) ? length - 2 : length - 1;
        return line[..end] + "…";
    }

    // The characters that make text look like something it is not: they turn what follows around, or have no width, or hide a tag in plain sight.
    private static bool Hides(Rune rune)
    {
        var value = rune.Value;
        return value is 0x00AD or 0x061C or 0x200B or 0x200E or 0x200F or 0x2060 or 0xFEFF
            || value is >= 0x202A and <= 0x202E
            || value is >= 0x2066 and <= 0x2069
            || value is >= 0xE0000 and <= 0xE007F;
    }
}
