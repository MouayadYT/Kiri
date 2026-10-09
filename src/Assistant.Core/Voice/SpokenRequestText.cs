using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Core.Voice;

/// <summary>
/// Makes what a recognizer heard into the text of a request (PROJECT_SPEC §4.2, step 125): the small streaming recognizers write in lower case without
/// punctuation, and a request that is shown to the user and sent to the model reads better with a capital, an "I" and an end mark. It never changes a word
/// that was heard. It also takes the wake word off the front of a request that began with it.
/// </summary>
public static class SpokenRequestText
{
    /// <summary>The word that wakes the Assistant.</summary>
    public const string WakeWord = "Kiri";

    private static readonly HashSet<string> WakeWordSpellings = new(StringComparer.OrdinalIgnoreCase)
    {
        "kiri", "kiry", "kirie", "kirri", "kyri", "kyrie", "keri", "kerri", "kerry", "kari", "karry", "kerie", "kiree", "keeri", "keery", "kearie",
        "curry", "carrie", "cary", "carey", "kearney", "keary", "cheri", "kyree",

        // What is left of the word when the audio was cut in the middle of it, or the recognizer heard only a part: the stress falls on "Kee".
        "e", "ee", "y", "ey", "ay", "ri", "re", "ree", "ki", "ke", "kee", "kie", "ky", "rie", "carie", "urie", "uri", "kerrie",
    };

    // Words the recognizers write as two, from the old books they learned from.
    private static readonly (string Spoken, string Written)[] SplitWords =
    [
        ("to morrow", "tomorrow"),
        ("to night", "tonight"),
        ("to day", "today"),
    ];

    private static readonly HashSet<string> Greetings = new(StringComparer.OrdinalIgnoreCase) { "hey", "hi", "ok", "okay", "yo", "hello" };

    private static readonly HashSet<string> QuestionStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "what", "what's", "whats", "when", "when's", "where", "where's", "who", "who's", "whose", "whom", "why", "how", "how's", "which",
        "is", "isn't", "are", "aren't", "am", "was", "wasn't", "were", "weren't", "do", "don't", "does", "doesn't", "did", "didn't",
        "can", "can't", "could", "couldn't", "will", "won't", "would", "wouldn't", "should", "shouldn't", "shall", "may", "might",
        "have", "haven't", "has", "hasn't", "had", "hadn't",
    };

    /// <summary>
    /// Whether <paramref name="word"/> is the wake word as a recognizer may write it. The recognizer has no word "Kiri" of its own, so it writes
    /// whichever it likes best: Kerry, Curry, Keri, and the like.
    /// </summary>
    public static bool IsWakeWord(string? word) =>
        !string.IsNullOrEmpty(word) && WakeWordSpellings.Contains(word.Trim().Trim('\'', '"', '.', ',', '!', '?', ';', ':'));

    /// <summary>
    /// <paramref name="text"/> without the wake word and a greeting before it, when it starts with them ("Kiri, what's on my calendar" is "what's on my
    /// calendar"); the same text otherwise. A text that is only the wake word is empty.
    /// </summary>
    public static string StripWakeWord(string? text)
    {
        var rest = (text ?? "").Trim();
        var removed = false;
        while (true)
        {
            var (word, after) = FirstWord(rest);
            if (word.Length == 0)
            {
                break;
            }

            if (IsWakeWord(word))
            {
                rest = after;
                removed = true;
                continue;
            }

            if (Greetings.Contains(word))
            {
                var (second, afterSecond) = FirstWord(after);
                if (IsWakeWord(second))
                {
                    rest = afterSecond;
                    removed = true;
                    continue;
                }
            }

            break;
        }

        return removed ? rest.TrimStart(' ', ',', '.', '!', ':', ';', '-') : (text ?? "").Trim();
    }

    /// <summary>
    /// <paramref name="text"/> as a request: spaces tidied, the first letter and the word "I" in capitals, and a question mark at the end of a question
    /// when the recognizer wrote none (<paramref name="endMark"/>: not for words that are still being heard, which may go on). Empty for text with no letters.
    /// </summary>
    public static string Normalize(string? text, bool endMark = true)
    {
        var heard = text ?? "";
        foreach (var (spoken, written) in SplitWords)
        {
            heard = Regex.Replace(heard, $@"\b{spoken}\b", written, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        var words = heard.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !words.Any(word => word.Any(char.IsLetterOrDigit)))
        {
            return "";
        }

        var builder = new StringBuilder();
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word is "i" or "i'm" or "i'll" or "i've" or "i'd" or "i'ma")
            {
                word = "I" + word[1..];
            }

            if (i == 0)
            {
                word = char.ToUpperInvariant(word[0]) + word[1..];
            }

            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(word);
        }

        var request = builder.ToString();
        if (request[^1] is '.' or '?' or '!')
        {
            return request;
        }

        var first = words[0].Trim(',', '.', '?', '!').ToLowerInvariant();
        return endMark && words.Length >= 2 && QuestionStarts.Contains(first) ? request + "?" : request;
    }

    // The first word of a text, without its punctuation, and the rest after it.
    private static (string Word, string After) FirstWord(string text)
    {
        var start = 0;
        while (start < text.Length && !char.IsLetter(text[start]))
        {
            start++;
        }

        var end = start;
        while (end < text.Length && (char.IsLetter(text[end]) || text[end] == '\''))
        {
            end++;
        }

        if (end == start)
        {
            return ("", text);
        }

        return (text[start..end], text[end..].TrimStart(' ', ',', '.', '!', ':', ';', '-'));
    }
}
