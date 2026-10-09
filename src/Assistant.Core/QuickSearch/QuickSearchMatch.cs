namespace Assistant.Core.QuickSearch;

/// <summary>How well a result's name matches what was typed, from none to exact. The order of the members is the order of worth.</summary>
public enum QuickSearchMatchKind
{
    /// <summary>The words are not in the name: the provider found it some other way (inside a file, by its date).</summary>
    None = 0,

    /// <summary>The typed text is somewhere inside the name ("hrome" in "Chrome"), or each of its words is.</summary>
    Contains = 1,

    /// <summary>The typed letters are the first letters of the name's words ("vsc" for "Visual Studio Code").</summary>
    Initials = 2,

    /// <summary>Each typed word begins a word of the name, in any order ("studio code" for "Visual Studio Code").</summary>
    Tokens = 3,

    /// <summary>The name begins with the typed text ("brave" for "Brave Browser").</summary>
    Prefix = 4,

    /// <summary>The name is the typed text.</summary>
    Exact = 5,
}

/// <summary>How a result matches what was typed.</summary>
/// <param name="Kind">The best way the name, or one of its keywords, matches.</param>
/// <param name="ViaKeyword">Whether the match was in a keyword and not in the title.</param>
public readonly record struct QuickSearchMatch(QuickSearchMatchKind Kind, bool ViaKeyword = false)
{
    /// <summary>No match at all.</summary>
    public static QuickSearchMatch None => new(QuickSearchMatchKind.None);

    /// <summary>
    /// How the title of <paramref name="result"/>, or else one of its keywords, matches <paramref name="query"/>. A keyword never
    /// counts for more than <see cref="QuickSearchMatchKind.Tokens"/>, so the title of another result that begins with what was
    /// typed always comes first.
    /// </summary>
    public static QuickSearchMatch Evaluate(string? query, QuickSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var typed = QuickSearchText.Normalize(query);
        if (typed.Length == 0)
        {
            return None;
        }

        var words = typed.Split(' ');
        var title = Of(typed, words, result.Title);
        foreach (var alias in result.Aliases)
        {
            var kind = Of(typed, words, alias);
            if (kind > title)
            {
                title = kind;
            }
        }

        QuickSearchMatchKind keyword = QuickSearchMatchKind.None;
        foreach (var candidate in result.Keywords)
        {
            var kind = Of(typed, words, candidate);
            if (kind > keyword)
            {
                keyword = kind;
            }
        }

        keyword = (QuickSearchMatchKind)Math.Min((int)keyword, (int)QuickSearchMatchKind.Tokens);
        return keyword > title ? new QuickSearchMatch(keyword, ViaKeyword: true) : new QuickSearchMatch(title);
    }

    private static QuickSearchMatchKind Of(string typed, string[] typedWords, string? text)
    {
        var name = QuickSearchText.Normalize(text);
        if (name.Length == 0)
        {
            return QuickSearchMatchKind.None;
        }

        if (name == typed)
        {
            return QuickSearchMatchKind.Exact;
        }

        if (name.StartsWith(typed, StringComparison.Ordinal))
        {
            return QuickSearchMatchKind.Prefix;
        }

        var nameWords = name.Split(' ');
        if (BeginsWords(typedWords, nameWords))
        {
            return QuickSearchMatchKind.Tokens;
        }

        // The first letters of the words: typed as one word of at least two letters.
        if (typedWords.Length == 1 && typed.Length >= 2 && Initials(nameWords).StartsWith(typed, StringComparison.Ordinal))
        {
            return QuickSearchMatchKind.Initials;
        }

        if (name.Contains(typed, StringComparison.Ordinal) || typedWords.All(word => name.Contains(word, StringComparison.Ordinal)))
        {
            return QuickSearchMatchKind.Contains;
        }

        return QuickSearchMatchKind.None;
    }

    // Each typed word begins a different word of the name.
    private static bool BeginsWords(string[] typedWords, string[] nameWords)
    {
        var used = new bool[nameWords.Length];
        foreach (var typed in typedWords)
        {
            var found = false;
            for (var i = 0; i < nameWords.Length; i++)
            {
                if (!used[i] && nameWords[i].StartsWith(typed, StringComparison.Ordinal))
                {
                    used[i] = true;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static string Initials(string[] words) => string.Concat(words.Select(word => word[0]));
}
