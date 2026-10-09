namespace Assistant.Tools.Calendar;

/// <summary>
/// Whether a request seems to be about the user's calendar (PROJECT_SPEC §4.8, step 111), by fixed word lists and nothing else: no model, no network. It is what keeps the
/// two calendar tools, and the instructions for their dates, out of the prompt of a request that has nothing to do with a calendar, as the tools of connected apps are loaded only
/// for the request they fit: a small model's window is not spent on tools it does not need. It errs towards offering: a word for a calendar, an event, or a day or a time of
/// day counts, so that a follow-up such as "and on Friday?" still has the tools, at the price of offering them for "what is the weather today".
/// </summary>
internal static class CalendarRequests
{
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "calendar", "calendars", "schedule", "scheduled", "agenda", "event", "events", "meeting", "meetings", "appointment", "appointments", "birthday", "birthdays",
        "busy", "free", "availability", "available", "reminder", "reminders", "invite", "invites", "invitation", "invitations", "booked", "diary",
        "today", "tomorrow", "tonight", "yesterday", "week", "weekend", "month", "morning", "afternoon", "evening", "next", "upcoming",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
    };

    /// <summary>Whether <paramref name="request"/> is about a calendar, or <see langword="null"/> (a call outside a turn, which cannot be told).</summary>
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
}
