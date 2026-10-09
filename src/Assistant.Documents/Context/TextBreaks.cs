namespace Assistant.Documents.Context;

/// <summary>Where a long text is best cut, and where the next part of it starts: by fixed rules, so the cut is the same every time.</summary>
internal static class TextBreaks
{
    // The marks that end a sentence outside the Latin script, as numbers: a character of another script in the source is easily
    // mistaken for another, and the Write tool turns escapes into raw characters.
    private const char Ellipsis = (char)0x2026;
    private const char ArabicQuestionMark = (char)0x061F;
    private const char DevanagariDanda = (char)0x0964;
    private const char ArmenianFullStop = (char)0x0589;
    private const char IdeographicFullStop = (char)0x3002;
    private const char FullwidthExclamation = (char)0xFF01;
    private const char FullwidthQuestion = (char)0xFF1F;

    /// <summary>
    /// Where the passage that starts at <paramref name="start"/> and may be <paramref name="limit"/> characters long ends
    /// (an exclusive index): in the last third of it, the last paragraph break, else the last end of a sentence, else the last
    /// line break, else the last white space, else at <paramref name="limit"/> itself, never between the halves of a surrogate pair.
    /// </summary>
    public static int FindEnd(string text, int start, int limit)
    {
        limit = Math.Min(limit, text.Length);
        var low = start + ((limit - start) * 2 / 3);
        int paragraph = -1, sentence = -1, line = -1, word = -1;
        for (var index = limit; index > low; index--)
        {
            // A cut at `index` ends the passage with text[index - 1]; text[index] is the break itself.
            if (index >= text.Length)
            {
                continue;
            }

            var character = text[index];
            if (character == '\n')
            {
                if (paragraph < 0 && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    paragraph = index;
                }

                if (sentence < 0 && IsSentenceEnd(text[index - 1]))
                {
                    sentence = index;
                }

                if (line < 0)
                {
                    line = index;
                }
            }
            else if (char.IsWhiteSpace(character))
            {
                if (sentence < 0 && IsSentenceEnd(text[index - 1]))
                {
                    sentence = index;
                }

                if (word < 0)
                {
                    word = index;
                }
            }
            else if (sentence < 0 && IsIdeographicStop(text[index - 1]))
            {
                // Scripts that do not put a space after a full stop.
                sentence = index;
            }
        }

        if (paragraph >= 0)
        {
            return paragraph;
        }

        if (sentence >= 0)
        {
            return sentence;
        }

        if (line >= 0)
        {
            return line;
        }

        return word >= 0 ? word : NotInsidePair(text, limit, -1);
    }

    /// <summary>
    /// Where the passage after one that ended at <paramref name="end"/> starts: <paramref name="overlap"/> characters back, moved
    /// on to the start of a word, so it begins at a word and repeats the end of the passage before. Never at or before
    /// <paramref name="previousStart"/>, and <paramref name="end"/> itself (no overlap) when there is no overlap to have.
    /// </summary>
    public static int FindRestart(string text, int end, int overlap, int previousStart)
    {
        if (overlap <= 0)
        {
            return end;
        }

        var position = end - overlap;
        if (position <= previousStart)
        {
            return end;
        }

        var word = position;
        if (word > 0 && !char.IsWhiteSpace(text[word - 1]))
        {
            while (word < end && !char.IsWhiteSpace(text[word]))
            {
                word++;
            }
        }

        while (word < end && char.IsWhiteSpace(text[word]))
        {
            word++;
        }

        // A text with no spaces has no word to start at: the overlap starts where it is, not through a pair.
        return word < end ? word : Math.Min(NotInsidePair(text, position, 1), end);
    }

    // `position` moved by one character in `direction` when it falls between the halves of a surrogate pair.
    private static int NotInsidePair(string text, int position, int direction)
    {
        return position > 0 && position < text.Length && char.IsHighSurrogate(text[position - 1]) && char.IsLowSurrogate(text[position])
            ? position + direction
            : position;
    }

    private static bool IsSentenceEnd(char character) =>
        character is '.' or '!' or '?' or Ellipsis or ArabicQuestionMark or DevanagariDanda or ArmenianFullStop
            or IdeographicFullStop or FullwidthExclamation or FullwidthQuestion;

    private static bool IsIdeographicStop(char character) =>
        character is IdeographicFullStop or FullwidthExclamation or FullwidthQuestion;
}
