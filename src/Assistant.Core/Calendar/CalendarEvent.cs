using System.Text;

namespace Assistant.Core.Calendar;

/// <summary>
/// One event of a calendar, as a calendar provider hands it over (PROJECT_SPEC §4.8, step 111). The times are instants; an all-day event starts at the
/// beginning of its first day, in the user's time zone, and ends at the beginning of the day after its last (so a one-day event is exactly a day long).
/// Its words (the title, the place, the notes) were written by whoever made the event, perhaps someone who sent an invitation, so they are data and never
/// instructions: they are shown and cleaned before the model sees them, and they are private content, so none reaches <see cref="object.ToString"/>.
/// </summary>
public sealed record CalendarEvent
{
    /// <summary>What the event is called.</summary>
    public required string Title { get; init; }

    /// <summary>When it starts.</summary>
    public required DateTimeOffset Start { get; init; }

    /// <summary>When it ends (not included): the same as <see cref="Start"/> for an event that has no length.</summary>
    public required DateTimeOffset End { get; init; }

    /// <summary>Whether it lasts the whole of each day it covers, with no time of day.</summary>
    public bool IsAllDay { get; init; }

    /// <summary>Where it is, when it says.</summary>
    public string? Location { get; init; }

    /// <summary>What its notes say, when it has any.</summary>
    public string? Notes { get; init; }

    /// <summary>The calendar it is in (<c>Work</c>), when the provider has more than one.</summary>
    public string? CalendarName { get; init; }

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"IsAllDay = {IsAllDay}");
        return true;
    }
}

/// <summary>The events that start, end or run between two times (<see cref="ICalendarProvider.GetEventsAsync"/>).</summary>
/// <param name="Start">The beginning of the time asked about.</param>
/// <param name="End">The end of it, not included: an event that starts at this moment is not in it.</param>
/// <param name="MaxResults">The most events to give back.</param>
public sealed record CalendarEventQuery(DateTimeOffset Start, DateTimeOffset End, int MaxResults);

/// <summary>Events with some words in them, between two times (<see cref="ICalendarProvider.SearchEventsAsync"/>).</summary>
/// <param name="Text">The words to look for. It is what the user asked about, so it is private content and is left out of <see cref="object.ToString"/>.</param>
/// <param name="Start">The beginning of the time to look in.</param>
/// <param name="End">The end of it, not included.</param>
/// <param name="MaxResults">The most events to give back.</param>
public sealed record CalendarSearchQuery(string Text, DateTimeOffset Start, DateTimeOffset End, int MaxResults)
{
    // Keeps what the user asked about out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Start = {Start:O}, End = {End:O}, MaxResults = {MaxResults}");
        return true;
    }
}

/// <summary>What a calendar gave back: the events, soonest first, and whether they are all of them.</summary>
/// <param name="Events">The events in the time asked about, in order of when they start, at most as many as were asked for.</param>
/// <param name="IsComplete"><see langword="false"/> when there were more than were asked for, so these are only the first.</param>
public sealed record CalendarEventList(IReadOnlyList<CalendarEvent> Events, bool IsComplete = true)
{
    /// <summary>A calendar with nothing in that time.</summary>
    public static CalendarEventList Empty { get; } = new([]);
}

/// <summary>Why a calendar could not be read.</summary>
public enum CalendarFailure
{
    /// <summary>It could not be reached or read now: it is not there, or did not answer.</summary>
    Unavailable = 0,

    /// <summary>It is there, but needs the user to sign in, which the Assistant cannot do yet.</summary>
    SignInNeeded = 1,
}

/// <summary>
/// A calendar could not be read (<see cref="ICalendarProvider"/>). Nothing found is not a failure. It never carries an address, a path, a name or a text from the calendar:
/// the tool tells the model and the user what the failure was in words of its own.
/// </summary>
public sealed class CalendarProviderException(CalendarFailure failure, Exception? inner = null)
    : Exception("The calendar could not be read.", inner)
{
    /// <summary>Why.</summary>
    public CalendarFailure Failure { get; } = failure;
}

/// <summary>
/// Where the Assistant reads the user's calendar from (PROJECT_SPEC §4.8, step 111): a capability, not a particular calendar. The calendar tools (<c>get_calendar_events</c> and
/// <c>search_calendar_events</c>) ask whichever provider the app has, so a calendar service is added or replaced by registering another provider and the tools and the model
/// never change. A service that has an MCP integration of its own does not need one: the integration's tools go through the generic tool system (§4.8, step 104), which is
/// tried first, and a provider is only written when there is a clear reason it cannot be an integration. Today the only provider is a made-up one (<c>InMemoryCalendarProvider</c>) for
/// trying the architecture without an account, and no real calendar is read.
/// </summary>
/// <remarks>
/// A provider only reads. It gives the events that overlap the time asked about (an event overlaps when it starts before the end and ends after the start; an event with no length,
/// when it starts inside), soonest first, at most <c>MaxResults</c>, and says when there were more. Nothing found is an empty list. It throws only <see cref="CalendarProviderException"/>,
/// or <see cref="OperationCanceledException"/> when it is cancelled.
/// </remarks>
public interface ICalendarProvider
{
    /// <summary>What the calendar is called, as the user knows it (<c>Sample calendar</c>, <c>Outlook</c>).</summary>
    string Name { get; }

    /// <summary>Whether its events are made up for trying the app and are not the user's own: the model is told, so that it says so.</summary>
    bool IsSample { get; }

    /// <summary>The events between <see cref="CalendarEventQuery.Start"/> and <see cref="CalendarEventQuery.End"/>.</summary>
    /// <exception cref="CalendarProviderException">The calendar could not be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<CalendarEventList> GetEventsAsync(CalendarEventQuery query, CancellationToken cancellationToken = default);

    /// <summary>The events between the two times that have all of the words in their title, place, notes or calendar name.</summary>
    /// <exception cref="CalendarProviderException">The calendar could not be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<CalendarEventList> SearchEventsAsync(CalendarSearchQuery query, CancellationToken cancellationToken = default);
}
