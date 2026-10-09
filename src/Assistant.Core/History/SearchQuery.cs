using System.Text;

namespace Assistant.Core.History;

/// <summary>
/// What the user typed into the history search, read into the words a conversation must contain: each word on its own,
/// or several words in quotes as one phrase.
/// </summary>
public sealed class SearchQuery
{
    /// <summary>The most words a query has; any beyond them are ignored.</summary>
    public const int MaxTerms = 8;

    /// <summary>The most characters a word or phrase has; any beyond them are cut off.</summary>
    public const int MaxTermLength = 100;

    private SearchQuery(IReadOnlyList<SearchTerm> terms) => Terms = terms;

    /// <summary>A query with no words, which finds nothing.</summary>
    public static SearchQuery Empty { get; } = new([]);

    /// <summary>The words and phrases, in the order they were typed. A conversation must match all of them.</summary>
    public IReadOnlyList<SearchTerm> Terms { get; }

    /// <summary>Whether the query has no words.</summary>
    public bool IsEmpty => Terms.Count == 0;

    /// <summary>
    /// Reads <paramref name="text"/> into words: they are separated by spaces, and a phrase in double quotes is one term.
    /// A word with no letter or digit in it (a lone punctuation mark) is not a term, and a quote that is never closed
    /// ends at the end of the text.
    /// </summary>
    public static SearchQuery Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        var terms = new List<SearchTerm>();
        var current = new StringBuilder();
        var inQuotes = false;
        var quoted = false;

        foreach (var character in text)
        {
            if (character == '"')
            {
                if (inQuotes)
                {
                    Finish();
                }
                else
                {
                    Finish();
                    inQuotes = true;
                    quoted = true;
                }

                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                Finish();
                continue;
            }

            current.Append(char.IsWhiteSpace(character) ? ' ' : character);
        }

        Finish();
        return terms.Count == 0 ? Empty : new SearchQuery(terms);

        void Finish()
        {
            var word = Collapse(current.ToString());
            current.Clear();
            var isPhrase = quoted && word.Contains(' ', StringComparison.Ordinal);
            inQuotes = false;
            quoted = false;
            if (word.Length == 0 || !word.Any(char.IsLetterOrDigit) || terms.Count >= MaxTerms)
            {
                return;
            }

            if (word.Length > MaxTermLength)
            {
                var cut = char.IsHighSurrogate(word[MaxTermLength - 1]) ? MaxTermLength - 1 : MaxTermLength;
                word = word[..cut].TrimEnd();
            }

            terms.Add(new SearchTerm(word, isPhrase));
        }
    }

    private static string Collapse(string text) => ConversationTitle.OneLine(text);
}

/// <summary>One word, or one phrase of several words, a search looks for.</summary>
/// <param name="Text">The word or phrase as typed, single-spaced.</param>
/// <param name="IsPhrase">Whether the words were quoted and must be found together, in order.</param>
public readonly record struct SearchTerm(string Text, bool IsPhrase);
