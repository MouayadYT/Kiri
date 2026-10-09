using System.Globalization;
using System.Text;

namespace Assistant.Documents.Context;

/// <summary>
/// Reads the words out of a text for ranking passages by a question (PROJECT_SPEC §4.7), the same way for the question and for
/// the passages, so that a word in one is found in the other: a word is a run of letters and digits, in lower case, without
/// accents, without the apostrophes inside it (<c>anna's</c> is <c>annas</c>), and lightly stemmed (<c>budgets</c> and
/// <c>budget</c>, <c>meeting</c> and <c>meetings</c> are one term). Words that carry no meaning of their own (<c>the</c>,
/// <c>what</c>, <c>document</c>) are left out when asked. Chinese, Japanese and Korean text has no spaces to cut it at, so a run
/// of those characters is read as pairs of neighbouring characters. Nothing depends on the culture of the machine.
/// </summary>
internal static class TermExtractor
{
    // Written as numbers, like every unusual character in these sources.
    private const char RightSingleQuote = (char)0x2019;
    private const char ModifierApostrophe = (char)0x02BC;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "about", "above", "after", "again", "against", "all", "also", "am", "an", "and", "any", "are", "arent", "as", "at",
        "attached", "attachment", "be", "because", "been", "before", "being", "below", "between", "both", "but", "by", "can",
        "cant", "could", "did", "do", "does", "doesnt", "doing", "dont", "document", "down", "during", "each", "few", "file",
        "for", "from", "further", "give", "had", "has", "have", "having", "he", "her", "here", "heres", "hers", "him", "his",
        "how", "i", "if", "im", "in", "into", "is", "isnt", "it", "its", "ive", "just", "lets", "me", "more", "most", "my", "no",
        "nor", "not", "now", "of", "off", "on", "once", "only", "or", "other", "our", "out", "over", "own", "pdf", "please",
        "same", "say", "says", "she", "should", "show", "so", "some", "such", "tell", "than", "that", "thats", "the", "their",
        "them", "then", "there", "theres", "these", "they", "this", "those", "through", "to", "too", "under", "until", "up",
        "very", "was", "wasnt", "we", "were", "what", "whats", "when", "where", "which", "while", "who", "whom", "why", "will",
        "with", "wont", "would", "you", "your", "youre",

        // What a person asks the Assistant to do with a file, which says nothing about what the file holds.
        "analyse", "analyze", "brief", "briefly", "condense", "content", "contents", "describe", "digest", "explain", "extract", "gist",
        "go", "help", "key", "know", "look", "main", "need", "ok", "okay", "outline", "overview", "paraphrase", "points", "quick", "quickly", "read",
        "recap", "rewrite", "short", "simplify", "skim", "summaries", "summarise", "summarize", "summary", "synopsis", "takeaways",
        "thank", "thanks", "tldr", "translate", "want", "yeah", "yep", "yes",
    };

    /// <summary>The words of <paramref name="text"/> in the order they stand, as terms; with <paramref name="skipStopWords"/>, without those that mean nothing alone.</summary>
    public static List<string> Extract(string? text, bool skipStopWords)
    {
        var terms = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return terms;
        }

        var word = new StringBuilder();
        var ideographs = new StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var width = char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
            if (IsIdeograph(text, index, width))
            {
                EmitWord(word, terms, skipStopWords);
                ideographs.Append(text, index, width);
                index += width - 1;
            }
            else if (IsWordCharacter(text, index, width))
            {
                EmitIdeographs(ideographs, terms);
                word.Append(text, index, width);
                index += width - 1;
            }
            else if (word.Length > 0 && IsApostrophe(character) && index + 1 < text.Length && char.IsLetter(text[index + 1]))
            {
                // Inside a word: it is left out and the word goes on.
            }
            else
            {
                EmitWord(word, terms, skipStopWords);
                EmitIdeographs(ideographs, terms);
            }
        }

        EmitWord(word, terms, skipStopWords);
        EmitIdeographs(ideographs, terms);
        return terms;
    }

    /// <summary>The term for a word: lower case, without accents, lightly stemmed.</summary>
    public static string Term(string word) => Stem(Fold(word));

    /// <summary>Whether a word (lower case, without accents) means nothing alone.</summary>
    public static bool IsStopWord(string word) => StopWords.Contains(word);

    /// <summary>
    /// Takes the ends off a lower-case word so that its forms meet: plurals (<c>ies</c>, <c>es</c> after s, x, z or h, <c>s</c>),
    /// <c>ing</c> and <c>ed</c>, and a last <c>e</c>. Only words of ASCII letters are changed, and never to fewer than three letters.
    /// It is light on purpose: two words that meet by accident cost a little noise, and two that should meet and do not cost a
    /// missed passage.
    /// </summary>
    public static string Stem(string word)
    {
        if (word.Length < 4 || !IsAsciiLetters(word))
        {
            return word;
        }

        var stem = word;
        if (stem.Length > 4 && stem.EndsWith("ies", StringComparison.Ordinal))
        {
            stem = stem[..^3] + "y";
        }
        else if (stem.Length > 4 && stem.EndsWith("es", StringComparison.Ordinal) && stem[^3] is 's' or 'x' or 'z' or 'h')
        {
            stem = stem[..^2];
        }
        else if (stem.EndsWith('s') && !stem.EndsWith("ss", StringComparison.Ordinal) && !stem.EndsWith("us", StringComparison.Ordinal)
                 && !stem.EndsWith("is", StringComparison.Ordinal))
        {
            stem = stem[..^1];
        }

        if (stem.Length > 5 && stem.EndsWith("ing", StringComparison.Ordinal))
        {
            stem = stem[..^3];
        }
        else if (stem.Length > 4 && stem.EndsWith("ed", StringComparison.Ordinal))
        {
            stem = stem[..^2];
        }

        if (stem.Length > 3 && stem.EndsWith('e'))
        {
            stem = stem[..^1];
        }

        return stem;
    }

    // Lower case and without accents: what a person means by the same word, whichever way it was typed.
    private static string Fold(string word)
    {
        var lower = word.ToLowerInvariant();
        if (IsAscii(lower))
        {
            return lower;
        }

        var builder = new StringBuilder(lower.Length);
        foreach (var character in lower.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static void EmitWord(StringBuilder word, List<string> terms, bool skipStopWords)
    {
        if (word.Length == 0)
        {
            return;
        }

        var folded = Fold(word.ToString());
        word.Clear();

        // A word of one letter is noise (so is a mark with no letter under it); one digit is not.
        if (folded.Length == 0 || (folded.Length < 2 && !char.IsDigit(folded[0])))
        {
            return;
        }

        if (skipStopWords && StopWords.Contains(folded))
        {
            return;
        }

        terms.Add(Stem(folded));
    }

    // A run of ideographs is read as every pair of neighbours, and alone when it is one character.
    private static void EmitIdeographs(StringBuilder run, List<string> terms)
    {
        if (run.Length == 0)
        {
            return;
        }

        var text = run.ToString();
        run.Clear();
        var characters = new List<string>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var width = char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;
            characters.Add(text.Substring(index, width));
            index += width - 1;
        }

        if (characters.Count == 1)
        {
            terms.Add(characters[0]);
            return;
        }

        for (var index = 0; index + 1 < characters.Count; index++)
        {
            terms.Add(characters[index] + characters[index + 1]);
        }
    }

    private static bool IsWordCharacter(string text, int index, int width)
    {
        if (char.IsLetterOrDigit(text, index))
        {
            return true;
        }

        // The marks that make a letter of another script (Arabic vowel signs, Devanagari matras) belong to the word.
        var category = CharUnicodeInfo.GetUnicodeCategory(text, index);
        return width == 1 && category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
    }

    private static bool IsApostrophe(char character) => character is '\'' or RightSingleQuote or ModifierApostrophe;

    // Han, Hiragana, Katakana and Hangul syllables: scripts written without spaces between words.
    private static bool IsIdeograph(string text, int index, int width)
    {
        var codePoint = width == 2 ? char.ConvertToUtf32(text[index], text[index + 1]) : text[index];
        return codePoint is (>= 0x3040 and <= 0x30FF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF)
            or (>= 0xAC00 and <= 0xD7AF) or (>= 0x20000 and <= 0x2FA1F);
    }

    private static bool IsAscii(string text)
    {
        foreach (var character in text)
        {
            if (character > 127)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetters(string text)
    {
        foreach (var character in text)
        {
            if (character is not (>= 'a' and <= 'z'))
            {
                return false;
            }
        }

        return true;
    }
}
