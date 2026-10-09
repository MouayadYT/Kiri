using Assistant.Core.Domain;
using Assistant.Core.History;

namespace Assistant.Search.Index;

/// <summary>
/// How well a result's name fits what was typed, so that of the candidates the index returns the name that is the query, or
/// begins with it, comes before one that merely contains it.
/// </summary>
internal static class NameRelevance
{
    /// <summary>The name, without its extension, is the query.</summary>
    public const int Exact = 0;

    /// <summary>The name begins with the first word and holds every other.</summary>
    public const int Prefix = 1;

    /// <summary>Every word begins a word of the name.</summary>
    public const int WordStart = 2;

    /// <summary>The name holds every word somewhere.</summary>
    public const int Contains = 3;

    /// <summary>The name holds none or only some of the words: the item matched on its text or properties.</summary>
    public const int Elsewhere = 4;

    /// <summary>Scores <paramref name="item"/> for <paramref name="terms"/>: the lower, the better.</summary>
    public static int Score(SearchResultItem item, IReadOnlyList<SearchTerm> terms)
    {
        // Apostrophes are left out on both sides, as the search finds "Anna’s" for "annas" and for "anna's".
        var name = NameText.WithoutApostrophes(item.DisplayName);
        var words = terms.Select(term => NameText.WithoutApostrophes(term.Text)).ToArray();
        if (words.Length == 0 || !words.All(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            return Elsewhere;
        }

        var stem = item.Type == SearchResultItemType.File ? Path.GetFileNameWithoutExtension(name) : name;
        var typed = string.Join(' ', words);
        if (string.Equals(stem, typed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, typed, StringComparison.OrdinalIgnoreCase))
        {
            return Exact;
        }

        if (name.StartsWith(words[0], StringComparison.OrdinalIgnoreCase))
        {
            return Prefix;
        }

        return words.All(word => BeginsAWord(name, word)) ? WordStart : Contains;
    }

    private static bool BeginsAWord(string name, string term)
    {
        var from = 0;
        while (name.IndexOf(term, from, StringComparison.OrdinalIgnoreCase) is var at and >= 0)
        {
            if (at == 0 || !char.IsLetterOrDigit(name[at - 1]) || (char.IsLower(name[at - 1]) && char.IsUpper(name[at])))
            {
                return true;
            }

            from = at + 1;
        }

        return false;
    }
}
