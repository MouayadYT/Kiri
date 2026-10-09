using System.Globalization;
using System.Text;

namespace Assistant.Core.People;

/// <summary>
/// How the Assistant compares the words people are written and spoken in (PROJECT_SPEC §4.8, step 112): without regard to case, accents
/// or punctuation, and with the extra spaces taken out, so that "Omar", "OMAR" and "Ömar " are the same name and "my brother!" is "my brother".
/// </summary>
public static class PersonText
{
    /// <summary>
    /// Whether two folded words are one word with a slip of the keyboard in it: a letter wrong, missing, added or two next to each other swapped ("borther" for
    /// "brother"), by one slip in a word of up to seven letters and two in a longer one. Words of fewer than four letters are never close to another: "bro" and "bra" are
    /// different words.
    /// </summary>
    public static bool IsClose(string? first, string? second)
    {
        if (first is null || second is null || first.Length < 4 || second.Length < 4 || first == second)
        {
            return first is not null && first == second && first.Length >= 4;
        }

        var allowed = Math.Min(first.Length, second.Length) <= 7 ? 1 : 2;
        return Math.Abs(first.Length - second.Length) <= allowed && Distance(first, second) <= allowed;
    }

    // The fewest single-letter changes, additions, removals and swaps of neighbours that make one word the other (optimal string alignment).
    private static int Distance(string first, string second)
    {
        var rows = first.Length + 1;
        var columns = second.Length + 1;
        var table = new int[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            table[row, 0] = row;
        }

        for (var column = 0; column < columns; column++)
        {
            table[0, column] = column;
        }

        for (var row = 1; row < rows; row++)
        {
            for (var column = 1; column < columns; column++)
            {
                var cost = first[row - 1] == second[column - 1] ? 0 : 1;
                table[row, column] = Math.Min(Math.Min(table[row - 1, column] + 1, table[row, column - 1] + 1), table[row - 1, column - 1] + cost);
                if (row > 1 && column > 1 && first[row - 1] == second[column - 2] && first[row - 2] == second[column - 1])
                {
                    table[row, column] = Math.Min(table[row, column], table[row - 2, column - 2] + 1);
                }
            }
        }

        return table[rows - 1, columns - 1];
    }

    /// <summary>
    /// <paramref name="text"/> folded for comparison: decomposed, with accents taken off, in lower case, every run of anything that is not a
    /// letter or a digit made one space, and trimmed. Empty when there is no letter or digit in it.
    /// </summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (space && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                space = false;
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                space = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>The words of <paramref name="folded"/>, which <see cref="Fold"/> made.</summary>
    public static string[] Words(string folded) =>
        folded.Length == 0 ? [] : folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
