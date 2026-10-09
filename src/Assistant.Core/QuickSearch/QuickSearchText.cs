using System.Globalization;
using System.Text;

namespace Assistant.Core.QuickSearch;

/// <summary>
/// How quick search reads text so that what is typed and what is found can be compared the way a person would: without regard to
/// case or accents, with punctuation and runs of spaces counting as one gap between words. "Brave — From iPhone" and "brave from
/// iphone" are the same words.
/// </summary>
public static class QuickSearchText
{
    /// <summary>
    /// The words of <paramref name="text"/>, lower case and without accents, joined by single spaces. Anything that is not a
    /// letter or a digit is a gap; text with no letter or digit is empty.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var gap = false;
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (gap && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                gap = false;
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                gap = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>The words of <paramref name="text"/> as <see cref="Normalize"/> reads them.</summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        var normalized = Normalize(text);
        return normalized.Length == 0 ? [] : normalized.Split(' ');
    }

    /// <summary>How many letters and digits <paramref name="text"/> has.</summary>
    public static int CountLettersAndDigits(string? text) => text?.Count(char.IsLetterOrDigit) ?? 0;
}
