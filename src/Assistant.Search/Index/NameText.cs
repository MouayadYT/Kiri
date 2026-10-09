using System.Text;

namespace Assistant.Search.Index;

/// <summary>
/// How a typed word is held against a name that may spell it with a different apostrophe. Downloaded and typed names write
/// "Anna’s" with a curly one, what is typed or said usually has a straight one, and a word typed quickly has none: the index
/// compares characters, so without this none of the three would find the others.
/// </summary>
internal static class NameText
{
    /// <summary>Whether <paramref name="character"/> is a straight, curly, modifier, prime, acute or grave apostrophe.</summary>
    public static bool IsApostrophe(char character) =>
        character is '\'' or '’' or '‘' or 'ʼ' or '′' or '´' or '`';

    /// <summary><paramref name="text"/> without any apostrophe.</summary>
    public static string WithoutApostrophes(string text)
    {
        if (!text.Any(IsApostrophe))
        {
            return text;
        }

        var kept = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!IsApostrophe(character))
            {
                kept.Append(character);
            }
        }

        return kept.ToString();
    }

    /// <summary>
    /// The patterns, for a <c>LIKE</c> on a name, that find <paramref name="word"/> whichever way the name spells its apostrophe.
    /// A word with an apostrophe matches any one character there (whichever apostrophe the name has) or none; a word that is
    /// longer than three letters and ends in "s" also matches the same word with an apostrophe before the "s" ("annas" finds
    /// "Anna’s"). Any other word has the one pattern it always had. Nothing but the word's own characters, escaped, and the
    /// single-character wildcard is ever written.
    /// </summary>
    public static IReadOnlyList<string> LikePatterns(string word)
    {
        if (word.Any(IsApostrophe))
        {
            var bare = WithoutApostrophes(word);
            if (bare.Any(char.IsLetterOrDigit))
            {
                var loose = new StringBuilder(word.Length + 8);
                foreach (var character in word)
                {
                    loose.Append(IsApostrophe(character) ? "_" : SearchSqlBuilder.EscapeLike(character.ToString()));
                }

                return ["%" + loose + "%", "%" + SearchSqlBuilder.EscapeLike(bare) + "%"];
            }
        }
        else if (word.Length >= 4 && (word[^1] is 's' or 'S') && char.IsLetter(word[^2]))
        {
            return ["%" + SearchSqlBuilder.EscapeLike(word) + "%", "%" + SearchSqlBuilder.EscapeLike(word[..^1]) + "_" + word[^1] + "%"];
        }

        return ["%" + SearchSqlBuilder.EscapeLike(word) + "%"];
    }
}
