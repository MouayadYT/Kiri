using System.Collections.Concurrent;
using System.Text;
using Assistant.Core.Contracts;

namespace Assistant.Tools;

/// <summary>
/// The words of a request, read the way a tool's topic is told by them: lower case, letters only, and a word that is a slip of the keyboard away from
/// one of five letters or more counts as it, where a topic allows it ("tiemr" and "timr" are "timer", "alram" is "alarm"): one letter missing, one
/// letter too many, or two letters side by side swapped, with the first and the last letter right. A letter that is a different letter is not a slip,
/// since that is how real words differ ("found" is not "sound", "times" is not "timer", "block" is not "clock"). The switches that run without a question
/// take no slips at all ("quite" is "quiet" with two letters swapped).
/// </summary>
internal static class RequestWords
{
    // A topic word this long or longer is matched with one slip; a shorter one only as it is ("mute" is never "muted" by a slip).
    private const int SlipFrom = 5;

    /// <summary>The words of <paramref name="text"/>, lower case.</summary>
    public static HashSet<string> Of(string? text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();
        foreach (var character in (text ?? string.Empty) + " ")
        {
            if (char.IsLetter(character))
            {
                word.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (word.Length > 0)
            {
                words.Add(word.ToString());
            }

            word.Clear();
        }

        return words;
    }

    /// <summary>
    /// Whether any word of <paramref name="said"/> is one of <paramref name="topic"/>, or, with <paramref name="slips"/>, a slip away from one that is not a
    /// plural ("times" is "timers" but for a letter, and is a word of its own).
    /// </summary>
    public static bool Names(IReadOnlySet<string> said, IReadOnlySet<string> topic, bool slips = false)
    {
        ArgumentNullException.ThrowIfNull(said);
        ArgumentNullException.ThrowIfNull(topic);
        return said.Overlaps(topic)
            || (slips && said.Any(word => topic.Any(known => known.Length >= SlipFrom && known[^1] != 's' && IsSlip(word, known))));
    }

    /// <summary>
    /// Whether <paramref name="word"/> is <paramref name="known"/>, or <paramref name="known"/> with one letter missing, one too many, or two neighbours
    /// swapped, the first letter being right.
    /// </summary>
    public static bool IsSlip(string word, string known)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(known);
        if (word == known)
        {
            return true;
        }

        if (word.Length == 0 || known.Length == 0 || word[0] != known[0] || Math.Abs(word.Length - known.Length) > 1)
        {
            return false;
        }

        var at = 0;
        while (at < word.Length && at < known.Length && word[at] == known[at])
        {
            at++;
        }

        if (word.Length == known.Length)
        {
            // Two neighbours swapped, and the rest as it is.
            return at + 1 < word.Length && word[at] == known[at + 1] && word[at + 1] == known[at]
                && word.AsSpan(at + 2).SequenceEqual(known.AsSpan(at + 2));
        }

        // One letter more or less, but not at the end ("time" is not "timer" with its last letter missing: it is a word of its own): past it, the rest is the same.
        var (longer, shorter) = word.Length > known.Length ? (word, known) : (known, word);
        return at < shorter.Length && longer.AsSpan(at + 1).SequenceEqual(shorter.AsSpan(at));
    }
}
/// <summary>
/// A topic a few tools are offered for: a request that names it (<see cref="RequestWords"/>), and the requests of the same conversation for a few minutes
/// after ("and turn it back on"). A tool that does something at once without asking (mute, Do not disturb, a note) is offered only so: a small model that is
/// handed one with every request reaches for it when nothing else fits, and once unmuted the user's speakers while it was meant to send a message.
/// </summary>
/// <param name="words">The words that name the topic.</param>
internal sealed class RequestTopic(params string[] words)
{
    private const int MaxRemembered = 64;

    // How long a conversation stays on the topic after it was last named.
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly HashSet<string> _words = new(words, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _active = new();

    /// <summary>
    /// Whether the request in <paramref name="context"/> is about the topic, or the conversation named it a few minutes ago. A call made outside a turn
    /// (no request) is about anything.
    /// </summary>
    public bool IsAbout(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request is null)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        if (RequestWords.Names(RequestWords.Of(context.Request), _words))
        {
            if (_active.Count >= MaxRemembered)
            {
                _active.Clear();
            }

            _active[context.ConversationId] = now;
            return true;
        }

        return _active.TryGetValue(context.ConversationId, out var named) && now - named < Lifetime;
    }
}
