using System.Collections.Concurrent;
using Assistant.Core.Contracts;

namespace Assistant.Tools.Messaging;

/// <summary>
/// Whether a request seems to be about messaging someone (PROJECT_SPEC §4.8, step 113), by fixed word lists and nothing else: no model, no network. It keeps the two messaging
/// tools, and the rules for using them, out of the prompt of a request that has nothing to do with messages, as the calendar tools are kept out of one that has nothing to do with a
/// calendar. It errs towards offering: a word for a message, a messaging app, or a relative counts, so that "let my mom know I'm late" has the tools, at the price of offering them for
/// "what did my brother say". Offering a tool sends nothing: a message goes only after the user has seen a draft and confirmed the call.
/// </summary>
internal static class MessagingRequests
{
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "message", "messages", "msg", "text", "texts", "texting", "sms", "send", "draft", "tell", "reply", "ping", "dm", "dms", "chat", "chats", "write",
        "whatsapp", "telegram", "signal", "imessage", "messenger", "beeper", "instagram", "discord", "slack",
        "mom", "mum", "mother", "mama", "dad", "father", "papa", "brother", "bro", "sister", "sis", "wife", "husband", "son", "daughter",
        "grandma", "grandpa", "friend", "cousin", "uncle", "aunt",
    };

    // What a request may begin with before it says what it wants: "can you please", "ok now", "I want to".
    private static readonly HashSet<string> LeadIns = new(StringComparer.Ordinal)
    {
        "please", "pls", "can", "could", "would", "will", "you", "kiri", "hey", "hi", "ok", "okay", "yes", "yeah", "yep", "sure", "alright", "now", "then",
        "also", "and", "so", "just", "go", "ahead", "i", "id", "want", "need", "like", "to", "wanna", "lets", "quickly", "again",
    };

    // What asks for a message to be sent, when the request begins with it: "send ...", "text mom ...", "tell my brother ...".
    private static readonly HashSet<string> SendVerbs = new(StringComparer.Ordinal) { "send", "resend", "text", "message", "msg", "tell", "dm", "ping", "reply", "draft" };

    // After "message" or "text" at the start, what makes it the thing and not the asking: "message from mom", "text in the screenshot".
    private static readonly HashSet<string> NounFollowers = new(StringComparer.Ordinal) { "from", "about", "in", "on", "history", "count", "that", "which", "was", "is", "of" };

    // What asks for something to be looked at or done in the messaging app itself, and not for a message to be sent to a saved person.
    private static readonly HashSet<string> ReadingWords = new(StringComparer.Ordinal)
    {
        "search", "find", "look", "lookup", "show", "read", "list", "summarize", "summarise", "summary", "unread", "latest", "last", "recent", "history", "said", "says",
        "what", "whats", "when", "who", "whos", "which", "where", "did", "does", "any", "check", "open", "focus", "archive", "unarchive", "remind", "reminder", "reminders",
        "mute", "unmute", "pin", "unpin", "download", "how", "many", "get", "set", "clear", "delete", "remove",
    };

    /// <summary>Whether <paramref name="request"/> is about messaging, or <see langword="null"/> (a call outside a turn, which cannot be told).</summary>
    public static bool IsAbout(string? request)
    {
        if (request is null)
        {
            return true;
        }

        var word = new System.Text.StringBuilder();
        foreach (var character in request)
        {
            if (char.IsLetter(character))
            {
                word.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (word.Length > 0 && Words.Contains(word.ToString()))
            {
                return true;
            }

            word.Clear();
        }

        return word.Length > 0 && Words.Contains(word.ToString());
    }

    /// <summary>
    /// Whether <paramref name="request"/> asks for a message to be sent to someone: it begins with the asking ("send a message to my brother on iMessage", "text mom
    /// I'm late", "can you tell Omar what time we meet", whatever the message itself says), or it has the asking in it and nothing that asks to look ("I want to
    /// write to my brother"). Asking the Assistant itself ("tell me ...", "send me ...") is not, nor is a question ("what did my brother send?").
    /// </summary>
    public static bool IsSending(string? request)
    {
        var words = Ordered(request);
        if (words.Count == 0)
        {
            return false;
        }

        var at = 0;
        while (at < words.Count && at < 8 && LeadIns.Contains(words[at]))
        {
            at++;
        }

        if (at < words.Count && SendVerbs.Contains(words[at]))
        {
            var next = at + 1 < words.Count ? words[at + 1] : string.Empty;

            // "Tell me", "send us": the Assistant is asked for something. "Message from mom": the thing, not the asking.
            return next is not ("me" or "us") && !(words[at] is "message" or "text" or "msg" && NounFollowers.Contains(next));
        }

        // "Let my mom know I'm late."
        var letsKnow = words.IndexOf("let") is >= 0 and var let && words.IndexOf("know", let) > let;
        return (letsKnow || words.Any(word => SendVerbs.Contains(word) || word == "write")) && !words.Any(ReadingWords.Contains);
    }

    /// <summary>Whether <paramref name="request"/> asks to look at something, or to do something in the app itself, and does not ask for a message to be sent.</summary>
    public static bool IsLooking(string? request) => !IsSending(request) && Ordered(request).Any(ReadingWords.Contains);

    // The words of the request in the order they were said, lower case, letters only ("what's" is "whats").
    private static List<string> Ordered(string? request)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        foreach (var character in (request ?? string.Empty) + " ")
        {
            if (char.IsLetter(character))
            {
                word.Append(char.ToLowerInvariant(character));
            }
            else if (character is '\'' or '’')
            {
                // An apostrophe is inside its word.
            }
            else
            {
                if (word.Length > 0)
                {
                    words.Add(word.ToString());
                }

                word.Clear();
            }
        }

        return words;
    }
}

/// <summary>
/// The conversations in which a message is being sent for the user: from the request that asks for it ("send a message to my brother") until the message has gone,
/// the user asks to look at something instead, or a few minutes pass. While one is, the Assistant's own <c>send_message</c> does everything the messaging app is
/// needed for (who the person is comes from the user's own list, and their chat is found by it), so none of the app's own tools is offered beside it
/// (<see cref="ConnectedApps.McpMessagingProvider"/>): a small model that was handed the app's search next to send_message looked "my brother" up in the app about
/// every other time, found nobody, and sent nothing. The user's answers on the way ("iMessage", "Omar") name no app and ask for nothing, and stay in it.
/// </summary>
internal static class MessagingFlows
{
    private const int MaxRemembered = 64;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> Active = new();

    /// <summary>Follows the request of a turn: one that asks for a message to be sent begins it for the conversation, one that asks to look at something ends it.</summary>
    public static void Note(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (MessagingRequests.IsSending(context.Request))
        {
            if (Active.Count >= MaxRemembered)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var old in Active.Where(entry => now - entry.Value >= Lifetime).Select(entry => entry.Key).ToList())
                {
                    Active.TryRemove(old, out _);
                }

                if (Active.Count >= MaxRemembered)
                {
                    Active.TryRemove(Active.OrderBy(entry => entry.Value).Select(entry => entry.Key).First(), out _);
                }
            }

            Active[context.ConversationId] = DateTimeOffset.UtcNow;
        }
        else if (MessagingRequests.IsLooking(context.Request))
        {
            Active.TryRemove(context.ConversationId, out _);
        }
    }

    /// <summary>The message was sent: what comes next in the conversation is a request of its own.</summary>
    public static void End(Guid conversation) => Active.TryRemove(conversation, out _);

    /// <summary>
    /// Whether a message is being sent in the conversation of <paramref name="context"/>: its request asks for one, or an earlier one did, a few minutes ago at
    /// most, and this one does not ask to look at something instead. It is told from the request itself and from what earlier turns left, so it says the same
    /// whether or not <see cref="Note"/> has run for this turn yet.
    /// </summary>
    public static bool IsSending(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (MessagingRequests.IsSending(context.Request))
        {
            return true;
        }

        return !MessagingRequests.IsLooking(context.Request)
            && Active.TryGetValue(context.ConversationId, out var since) && DateTimeOffset.UtcNow - since < Lifetime;
    }
}
