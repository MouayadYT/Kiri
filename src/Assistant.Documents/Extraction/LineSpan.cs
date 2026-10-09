namespace Assistant.Documents.Extraction;

/// <summary>Which lines of a file a piece of its text is on.</summary>
internal static class LineSpan
{
    /// <summary>
    /// The first and last line, counted from 1, that hold something other than white space in <paramref name="text"/>, which
    /// begins on line <paramref name="startLine"/> of the file and has its lines separated by <c>\n</c>. <see langword="null"/>
    /// for text with nothing in it.
    /// </summary>
    public static (int First, int Last)? Of(string text, int startLine)
    {
        var line = startLine;
        int? first = null;
        var last = startLine;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                line++;
            }
            else if (!char.IsWhiteSpace(c))
            {
                first ??= line;
                last = line;
            }
        }

        return first is { } found ? (found, last) : null;
    }

    /// <summary>The number of line breaks in <paramref name="text"/>.</summary>
    public static int CountBreaks(string text) => text.AsSpan().Count('\n');
}
