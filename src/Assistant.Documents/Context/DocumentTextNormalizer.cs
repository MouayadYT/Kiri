using System.Text;
using Assistant.Documents.Extraction;

namespace Assistant.Documents.Context;

/// <summary>
/// Makes the text of a document one plain shape before it is cut into passages: what <see cref="TextCleaner"/> does for every
/// reader (one kind of line break, no control characters or invisible marks, no lone surrogates, no space at the end of a line,
/// never more than one blank line in a row, nothing before the first or after the last word) and, on top of it, one space
/// wherever a line had a run of spaces or tabs between its words. A line's own indentation is kept. Applying it again to its
/// own output changes nothing, so text that a reader has already cleaned, and text that never went through a reader, end up
/// the same.
/// </summary>
internal static class DocumentTextNormalizer
{
    /// <summary>Normalizes <paramref name="text"/>; an empty result means there was nothing in it.</summary>
    public static string Normalize(string? text)
    {
        var clean = TextCleaner.Clean(text);
        if (clean.Length == 0 || !HasSpaceRun(clean))
        {
            return clean;
        }

        var builder = new StringBuilder(clean.Length);
        var atLineStart = true;
        var index = 0;
        while (index < clean.Length)
        {
            var character = clean[index];
            if (character == '\n')
            {
                builder.Append('\n');
                atLineStart = true;
                index++;
            }
            else if (character is ' ' or '\t')
            {
                var end = index;
                var tab = false;
                while (end < clean.Length && clean[end] is ' ' or '\t')
                {
                    tab |= clean[end] == '\t';
                    end++;
                }

                if (atLineStart)
                {
                    // Indentation is kept as it is.
                    builder.Append(clean, index, end - index);
                }
                else
                {
                    builder.Append(tab ? '\t' : ' ');
                }

                index = end;
            }
            else
            {
                builder.Append(character);
                atLineStart = false;
                index++;
            }
        }

        return builder.ToString();
    }

    // Whether any two spaces or tabs follow each other, which is the only thing this adds to the cleaning.
    private static bool HasSpaceRun(string text)
    {
        for (var index = 1; index < text.Length; index++)
        {
            if (text[index] is ' ' or '\t' && text[index - 1] is ' ' or '\t')
            {
                return true;
            }
        }

        return false;
    }
}
