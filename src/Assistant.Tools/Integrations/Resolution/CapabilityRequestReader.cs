using Assistant.Core.People;

namespace Assistant.Tools.Integrations;

/// <summary>
/// Reads a request that names no app but asks for something an integration can do (PROJECT_SPEC §4.8, step 116): "Check my calendar for exams in the next two weeks" is a need to read the
/// user's calendar, and "message my brother" is a need to send a message, however they are served. It is the counterpart of <see cref="IntegrationRequestReader"/>, which reads a request
/// for a named app ("add milk to Todoist"), and it works the same way: fixed word lists, no model, no network and no state, so the same request always reads the same, and it errs towards
/// reading nothing (a question, a request that changes the calendar, a "tell me" that is not a message to someone). What it returns holds two fixed words and no text of the request: no
/// title, no name, no date.
/// </summary>
public static class CapabilityRequestReader
{
    private static readonly HashSet<string> CalendarWords = new(StringComparer.Ordinal) { "calendar", "calendars", "agenda", "diary" };

    private static readonly HashSet<string> ReadVerbs = new(StringComparer.Ordinal)
    {
        "check", "look", "see", "show", "list", "find", "get", "read", "view", "tell", "what", "whats", "which", "when", "any", "search", "fetch", "review", "scan",
    };

    // A request that changes the calendar is not a request to read it.
    private static readonly HashSet<string> ChangeVerbs = new(StringComparer.Ordinal)
    {
        "add", "create", "put", "book", "schedule", "set", "cancel", "delete", "remove", "move", "reschedule", "update", "change", "edit", "invite", "clear", "block", "sync",
    };

    private static readonly HashSet<string> SendVerbs = new(StringComparer.Ordinal)
    {
        "message", "messages", "text", "tell", "send", "ping", "dm", "remind", "notify", "whatsapp", "let", "inform", "email",
    };

    private static readonly HashSet<string> Possessives = new(StringComparer.Ordinal) { "my", "our" };

    /// <summary>The needs <paramref name="request"/> asks for, in the order they come in the request: reading the calendar, then sending a message.</summary>
    /// <param name="request">What the user wrote. It is private content: it is read, never kept.</param>
    public static IReadOnlyList<IntegrationNeed> Read(string? request)
    {
        if (string.IsNullOrWhiteSpace(request) || request.Length > IntegrationRequestReader.MaxRequestLength)
        {
            return [];
        }

        var tokens = IntegrationRequestReader.Tokenize(request);
        if (tokens.Count == 0 || !IntegrationRequestReader.IsRequest(tokens, out var body))
        {
            return [];
        }

        var needs = new List<(int At, IntegrationNeed Need)>();
        if (CalendarAt(tokens, body) is { } calendar && ReadsCalendar(tokens, body))
        {
            needs.Add((calendar, IntegrationNeed.ForCalendar()));
        }

        if (RelativeAt(tokens, body) is { } person && SendsTo(tokens, body, person))
        {
            needs.Add((person, IntegrationNeed.ForMessaging()));
        }

        return [.. needs.OrderBy(entry => entry.At).Select(entry => entry.Need)];
    }

    /// <summary>The need to read the calendar the request asks for, if it does.</summary>
    public static IntegrationNeed? CalendarNeed(string? request) =>
        Read(request).FirstOrDefault(need => need.Capability.Object == "event");

    // Where "my calendar" is: the word for a calendar with "my" or "our" just before it (or "my" and one more word: "my work calendar").
    private static int? CalendarAt(List<string> tokens, int body)
    {
        for (var at = body; at < tokens.Count; at++)
        {
            if (CalendarWords.Contains(tokens[at]) && (at >= 1 && Possessives.Contains(tokens[at - 1]) || at >= 2 && Possessives.Contains(tokens[at - 2])))
            {
                return at;
            }
        }

        return null;
    }

    private static bool ReadsCalendar(List<string> tokens, int body)
    {
        var after = tokens.Skip(body).ToList();
        return after.Any(ReadVerbs.Contains) && !after.Any(ChangeVerbs.Contains);
    }

    // Where "my brother" is: a word for a relative with "my" or "our" just before it.
    private static int? RelativeAt(List<string> tokens, int body)
    {
        for (var at = Math.Max(body, 1); at < tokens.Count; at++)
        {
            if (Possessives.Contains(tokens[at - 1]) && RelationshipTerms.IsKnown(PersonText.Fold(tokens[at])))
            {
                return at;
            }
        }

        return null;
    }

    // Whether a word that sends something to a person stands in the request: "message", "text", "remind", "let ... know". "Tell me" and "remind me" are not a message to
    // someone; only a verb before the relative that is about them counts, or "to remind him" after it.
    private static bool SendsTo(List<string> tokens, int body, int relative)
    {
        for (var at = body; at < tokens.Count; at++)
        {
            if (!SendVerbs.Contains(tokens[at]) || at == relative)
            {
                continue;
            }

            // "tell me", "remind me", "text me": the user, not someone else.
            if (at + 1 < tokens.Count && tokens[at + 1] is "me" or "us")
            {
                continue;
            }

            return true;
        }

        return false;
    }
}
