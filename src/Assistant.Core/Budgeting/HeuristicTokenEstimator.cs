namespace Assistant.Core.Budgeting;

/// <summary>
/// An <see cref="ITokenEstimator"/> that reads no vocabulary: it counts runs of characters, and comes out a little
/// above what real tokenizers give for English prose, and well above for code, numbers and other scripts.
/// </summary>
/// <remarks>
/// <para>
/// The text is cut into runs of one kind of character. A run of ASCII letters costs a token for every four letters (at
/// least one) up to eight, and beyond that a token for every two, because words that long are mostly identifiers, hashes
/// and encoded data, which tokenizers split finely; a run of digits a token for every two; a run of white space nothing
/// when it is a single space, which travels with the word after it, and otherwise a token for every eight characters
/// (at least one); every other character, punctuation, symbols and letters of any script beyond ASCII, one token each.
/// </para>
/// <para>
/// Each run costs the same wherever it stands, so the estimate of a text is the sum over its runs. That is what makes
/// it never fall when text is added.
/// </para>
/// </remarks>
public sealed class HeuristicTokenEstimator : ITokenEstimator
{
    // Letters up to this many are a word, at four to a token (two tokens at most); each further two cost one more.
    private const int LongWord = 8;
    private const int LongWordTokens = 2;

    /// <inheritdoc/>
    public int Estimate(ReadOnlySpan<char> text)
    {
        long tokens = 0;
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (IsAsciiLetter(character))
            {
                var run = RunLength(text, index, IsAsciiLetter);
                tokens += run <= LongWord ? (run + 3) / 4 : LongWordTokens + (run - LongWord + 1) / 2;
                index += run;
            }
            else if (IsAsciiDigit(character))
            {
                var run = RunLength(text, index, IsAsciiDigit);
                tokens += (run + 1) / 2;
                index += run;
            }
            else if (char.IsWhiteSpace(character))
            {
                var run = RunLength(text, index, char.IsWhiteSpace);
                tokens += run == 1 && character == ' ' ? 0 : (run + 7) / 8;
                index += run;
            }
            else
            {
                tokens++;
                index++;
            }
        }

        return (int)Math.Min(tokens, int.MaxValue);
    }

    private static int RunLength(ReadOnlySpan<char> text, int start, Func<char, bool> inRun)
    {
        var end = start + 1;
        while (end < text.Length && inRun(text[end]))
        {
            end++;
        }

        return end - start;
    }

    private static bool IsAsciiLetter(char character) => character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    private static bool IsAsciiDigit(char character) => character is >= '0' and <= '9';
}
