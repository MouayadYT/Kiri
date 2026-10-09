namespace Assistant.Search.Planning;

/// <summary>
/// How close two words are when one may be mistyped: "fidn" for "find", "pdff" for "pdf", "arhciv" for "archiv". Short words
/// are compared strictly, because a mistake in a short word makes another word ("doc" is one letter from "dog").
/// </summary>
internal static class Fuzzy
{
    /// <summary>
    /// The edit distance between <paramref name="a"/> and <paramref name="b"/>: letters inserted, deleted, replaced, or two
    /// neighbours swapped (optimal string alignment), without regard to case. Stops counting past <paramref name="limit"/>.
    /// </summary>
    public static int Distance(string a, string b, int limit = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (Math.Abs(a.Length - b.Length) > limit)
        {
            return limit + 1;
        }

        var rows = new int[3][];
        for (var i = 0; i < 3; i++)
        {
            rows[i] = new int[b.Length + 1];
        }

        for (var j = 0; j <= b.Length; j++)
        {
            rows[1][j] = j;
        }

        var previousBest = 0;
        for (var i = 1; i <= a.Length; i++)
        {
            var twoBack = rows[0];
            var previous = rows[1];
            var current = rows[2];
            current[0] = i;
            var best = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var same = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]);
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (same ? 0 : 1));
                if (i > 1 && j > 1
                    && char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 2])
                    && char.ToLowerInvariant(a[i - 2]) == char.ToLowerInvariant(b[j - 1]))
                {
                    value = Math.Min(value, twoBack[j - 2] + 1);
                }

                current[j] = value;
                best = Math.Min(best, value);
            }

            // A swap reaches back two rows, so both must be past the limit before nothing can come back under it.
            if (best > limit && previousBest > limit)
            {
                return limit + 1;
            }

            previousBest = best;

            rows[0] = previous;
            rows[1] = current;
            rows[2] = twoBack;
        }

        return rows[1][b.Length];
    }

    /// <summary>How many mistakes a word as long as <paramref name="length"/> may have and still be read as meant.</summary>
    public static int Allowed(int length) => length switch
    {
        < 4 => 0,
        < 8 => 1,
        _ => 2,
    };

    /// <summary>
    /// Whether <paramref name="typed"/> is <paramref name="word"/>, perhaps mistyped. A word of three letters or fewer must be
    /// typed as it is, or with its last letter doubled or an "s" added ("pdff", "docs"); a longer one may have one mistake, and a word of
    /// eight letters or more two. The first letter must be right: people rarely miss it, and it keeps unrelated words apart.
    /// </summary>
    public static bool IsWord(string typed, string word)
    {
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(word);
        if (string.Equals(typed, word, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (typed.Length == 0 || word.Length == 0 || char.ToLowerInvariant(typed[0]) != char.ToLowerInvariant(word[0]))
        {
            return false;
        }

        if (word.Length <= 3)
        {
            // "pdff" and "pdfs", not "rare" for "rar": the letter too many repeats the last one, or makes it plural.
            return typed.Length == word.Length + 1 && typed.StartsWith(word, StringComparison.OrdinalIgnoreCase)
                && (char.ToLowerInvariant(typed[^1]) == char.ToLowerInvariant(word[^1]) || char.ToLowerInvariant(typed[^1]) == 's');
        }

        var allowed = Allowed(word.Length);
        return Distance(typed, word, allowed) <= allowed;
    }
}
