namespace Assistant.Core.Budgeting;

/// <summary>
/// Cuts a text down to a number of estimated tokens. The cut is the same every time for the same text and allowance:
/// the longest piece that fits, moved back to the nearest natural boundary when there is one close enough.
/// </summary>
internal static class TokenTrimmer
{
    /// <summary>A cut may move back over at most this fraction (one in <c>SnapDivisor</c>) of the piece it keeps.</summary>
    private const int SnapDivisor = 8;

    /// <summary>The start of <paramref name="text"/> that takes at most <paramref name="maxTokens"/> tokens.</summary>
    /// <remarks>
    /// It ends where a paragraph, a line, a sentence or a word ends, in that order of preference, when one is within
    /// the last eighth of the piece; otherwise it is cut where the allowance runs out.
    /// </remarks>
    public static string Head(ITokenEstimator estimator, string text, int maxTokens)
    {
        if (maxTokens <= 0 || text.Length == 0)
        {
            return string.Empty;
        }

        if (estimator.Estimate(text) <= maxTokens)
        {
            return text;
        }

        var length = LongestFit(text.Length, maxTokens, size => estimator.Estimate(text.AsSpan(0, size)));
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        var cut = HeadCut(text, length);
        return text[..cut].TrimEnd();
    }

    /// <summary>The end of <paramref name="text"/> that takes at most <paramref name="maxTokens"/> tokens.</summary>
    /// <remarks>It starts at the beginning of a word when one is within the first eighth of the piece.</remarks>
    public static string Tail(ITokenEstimator estimator, string text, int maxTokens)
    {
        if (maxTokens <= 0 || text.Length == 0)
        {
            return string.Empty;
        }

        if (estimator.Estimate(text) <= maxTokens)
        {
            return text;
        }

        var length = LongestFit(text.Length, maxTokens, size => estimator.Estimate(text.AsSpan(text.Length - size)));
        var start = text.Length - length;
        if (start < text.Length && char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        return text[TailCut(text, start, length)..].TrimStart();
    }

    // The most characters, up to the text's length, whose estimate is within the allowance: the estimate never falls as
    // the piece grows, so the answer can be found by halving. The search starts near the size a token usually takes and
    // doubles it while that still fits, so a huge text is not measured whole to keep a small piece of it.
    private static int LongestFit(int textLength, int maxTokens, Func<int, int> estimateOfSize)
    {
        var low = 0;
        var high = (int)Math.Min(textLength, Math.Max(16L, 4L * maxTokens));
        while (high < textLength && estimateOfSize(high) <= maxTokens)
        {
            low = high;
            high = (int)Math.Min(textLength, 2L * high);
        }

        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (estimateOfSize(middle) <= maxTokens)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    // Where to end the kept start of the text, which may be cut anywhere up to `length` characters: the last
    // paragraph break, line break, sentence end or word end in its final eighth, else `length` itself.
    private static int HeadCut(string text, int length)
    {
        var floor = length - length / SnapDivisor;
        var last = Math.Min(length, text.Length - 1);

        for (var index = last; index >= floor; index--)
        {
            if (IsParagraphBreak(text, index))
            {
                return index > 0 ? index : length;
            }
        }

        for (var index = last; index >= floor; index--)
        {
            if (text[index] == '\n')
            {
                return index > 0 ? index : length;
            }
        }

        for (var index = Math.Min(last, length - 1); index >= floor; index--)
        {
            if (text[index] is '.' or '!' or '?' && (index + 1 == text.Length || char.IsWhiteSpace(text[index + 1])))
            {
                return index + 1;
            }
        }

        for (var index = last; index >= floor; index--)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                return index > 0 ? index : length;
            }
        }

        return length;
    }

    // Where to start the kept end of the text, which may begin anywhere from `start`: just after the first white space
    // in its first eighth, or where the allowance put it.
    private static int TailCut(string text, int start, int length)
    {
        if (start == 0 || char.IsWhiteSpace(text[start - 1]))
        {
            return start;
        }

        var ceiling = Math.Min(text.Length - 1, start + length / SnapDivisor);
        for (var index = start; index <= ceiling; index++)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                return index + 1;
            }
        }

        return start;
    }

    // Two line breaks in a row, with a carriage return allowed between them, as files from Windows have.
    private static bool IsParagraphBreak(string text, int index) =>
        text[index] == '\n'
        && index + 1 < text.Length
        && (text[index + 1] == '\n' || (text[index + 1] == '\r' && index + 2 < text.Length && text[index + 2] == '\n'));
}
