using System.Text;
using Assistant.Search.Index;

namespace Assistant.Search.Planning;

/// <summary>How well one file name fits the words of a request, when no name holds them exactly.</summary>
/// <param name="Matched">How many of the words the name holds, in some form.</param>
/// <param name="Words">How many words there were.</param>
/// <param name="Score">The mean of how well each word fits, from 0 (none) to 1 (every word exactly).</param>
internal readonly record struct NameFit(int Matched, int Words, double Score)
{
    /// <summary>Whether the name holds every word, in some form.</summary>
    public bool IsFull => Words > 0 && Matched == Words;

    /// <summary>Whether it holds at least one word well enough to be shown at all.</summary>
    public bool IsPartial => Matched > 0;
}

/// <summary>
/// Scores a file name against the words of a request, the way a person reads a name: word by word, forgiving what people and
/// downloads do to names. "annas" fits "Anna’s" (the apostrophe), "archive" fits "Archi" and "Archiv" (a name cut short),
/// "arhciv" fits "Archiv" (letters swapped), and "biolgy" fits "Biology" (a letter left out). A word found only inside another
/// word does not count: "anna" is in "Savannah", and that name is not about Anna.
/// </summary>
internal static class NameMatch
{
    /// <summary>How well a word must fit a word of the name to count as found there.</summary>
    public const double Found = 0.6;

    /// <summary>Scores <paramref name="name"/> (with or without its extension) against <paramref name="words"/>.</summary>
    public static NameFit Fit(string name, IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(words);
        var tokens = Tokens(name);
        var wanted = words.SelectMany(Tokens).Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0)
        {
            return new NameFit(0, 0, 0);
        }

        var matched = 0;
        var total = 0.0;
        foreach (var word in wanted)
        {
            var best = 0.0;
            for (var i = 0; i < tokens.Count; i++)
            {
                best = Math.Max(best, WordFit(word, tokens[i], isLast: i == tokens.Count - 1));
            }

            if (best >= Found)
            {
                matched++;
                total += best;
            }
        }

        return new NameFit(matched, wanted.Length, total / wanted.Length);
    }

    /// <summary>
    /// How well a word of the request fits a word of the name: 1 for the same word, less for a word that begins it, a name cut
    /// short, or a mistake, and 0 when it does not fit.
    /// </summary>
    public static double WordFit(string word, string token, bool isLast = false)
    {
        if (word.Length == 0 || token.Length == 0)
        {
            return 0;
        }

        if (word == token)
        {
            return 1;
        }

        // "3" and "three", "2nd" and "second": the same number written another way.
        if (NumberForms.Same(word, token))
        {
            return 0.95;
        }

        // The word begins the name's word: "bio" and "biology", "report" and "reports".
        if (word.Length >= 3 && token.StartsWith(word, StringComparison.Ordinal))
        {
            return 0.9;
        }

        // The name's word is the word cut short: "Archi" for "archive". A name cut by a download ends that way, so its last word
        // may be as short as two letters ("Ar"); elsewhere it needs three.
        if (word.StartsWith(token, StringComparison.Ordinal) && (token.Length >= 3 || (isLast && token.Length >= 2)))
        {
            return 0.8;
        }

        // A mistake: "biolgy" and "biology", "arhciv" and "archiv".
        var allowed = Fuzzy.Allowed(word.Length);
        if (allowed > 0 && token.Length >= 3 && char.ToLowerInvariant(word[0]) == char.ToLowerInvariant(token[0]))
        {
            if (Fuzzy.Distance(word, token, allowed) <= allowed)
            {
                return 0.75;
            }

            // A mistake in a word the name has cut short: "arhciv" and "Archi", "aarhcive" and "Archiv". The mistake may have
            // added or lost a letter, so the start of the word is taken a letter shorter or longer too.
            if (token.Length >= 4 && token.Length < word.Length)
            {
                for (var length = token.Length - 1; length <= Math.Min(word.Length, token.Length + 1); length++)
                {
                    if (Fuzzy.Distance(word[..length], token, allowed) <= allowed)
                    {
                        return 0.65;
                    }
                }
            }

            // A word with a mistake that begins the name's word: "biolog" and "biologyNotes".
            if (token.Length > word.Length && Fuzzy.Distance(word, token[..word.Length], allowed) <= allowed)
            {
                return 0.7;
            }
        }

        return 0;
    }

    /// <summary>
    /// The words of a name, in lower case, without apostrophes and without the extension: "Anna’s Archi.pdf" is "annas" and
    /// "archi"; "AnnualReport_2026" is "annual", "report" and "2026".
    /// </summary>
    public static IReadOnlyList<string> Tokens(string name)
    {
        var text = NameText.WithoutApostrophes(name);
        var dot = text.LastIndexOf('.');
        if (dot > 0 && text.Length - dot <= 6 && text[(dot + 1)..].All(char.IsLetterOrDigit))
        {
            text = text[..dot];
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            var splitsCase = current.Length > 0 && char.IsUpper(character) && char.IsLower(text[i - 1]);
            if (!char.IsLetterOrDigit(character) || splitsCase)
            {
                Flush();
            }

            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
            }
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
    }
}
