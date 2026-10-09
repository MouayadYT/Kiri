using System.Text;

namespace Assistant.Documents.Extraction;

/// <summary>
/// Makes text from any file one plain shape, so that what a model reads does not depend on the file format: one kind of line
/// break, no control characters or invisible marks, no lone surrogates (which cannot be written as UTF-8 or JSON), no space at
/// the end of a line, never more than one blank line in a row, and nothing before the first or after the last word. A line's
/// own indentation is kept: it is what makes code and lists readable.
/// </summary>
internal static class TextCleaner
{
    // Written as numbers: an invisible character in the source cannot be told from nothing, and two of these end a line in C#.
    private const char ReplacementCharacter = (char)0xFFFD;
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;
    private const char NextLine = (char)0x0085;
    private const char ByteOrderMark = (char)0xFEFF;
    private const char ZeroWidthSpace = (char)0x200B;
    private const char WordJoiner = (char)0x2060;
    private const char SoftHyphen = (char)0x00AD;

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var output = new StringBuilder(text.Length);
        var spaces = new StringBuilder();
        var newlines = 0;

        void Content(char first, char? second = null)
        {
            if (output.Length > 0)
            {
                if (newlines > 0)
                {
                    output.Append('\n', Math.Min(newlines, 2));
                }

                output.Append(spaces);
            }

            newlines = 0;
            spaces.Clear();
            output.Append(first);
            if (second is { } low)
            {
                output.Append(low);
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    Content(c, text[i + 1]);
                    i++;
                    continue;
                }

                c = ReplacementCharacter;
            }
            else if (char.IsLowSurrogate(c))
            {
                c = ReplacementCharacter;
            }

            switch (c)
            {
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    newlines++;
                    spaces.Clear();
                    break;
                case '\n' or LineSeparator or ParagraphSeparator or NextLine or '\v' or '\f':
                    newlines++;
                    spaces.Clear();
                    break;
                case '\t':
                    spaces.Append('\t');
                    break;
                case ByteOrderMark or ZeroWidthSpace or WordJoiner or SoftHyphen:
                    break;
                default:
                    if (char.IsControl(c))
                    {
                        break;
                    }

                    if (char.IsWhiteSpace(c))
                    {
                        spaces.Append(' ');
                        break;
                    }

                    Content(c);
                    break;
            }
        }

        return output.ToString();
    }
}
