using System.Globalization;
using Assistant.Core.Calendar;

namespace Assistant.Tools.Calendar;

/// <summary>
/// The first calendar provider (PROJECT_SPEC §4.8, step 111): a calendar held in memory, made of the events it is given, so that the calendar tools and the agent that calls them can be tried and
/// tested without an account or a network. It reads nothing from the PC. The events it is given by <see cref="WithSampleEvents"/> are made up (<see cref="ICalendarProvider.IsSample"/>),
/// and the app registers no provider at all, so nothing in the app reads a calendar until one is deliberately added. A real calendar replaces it by registering another
/// <see cref="ICalendarProvider"/>, or, for a service that has an MCP integration, needs no provider: its tools come through the generic tool system.
/// </summary>
public sealed class InMemoryCalendarProvider : ICalendarProvider
{
    private readonly object _gate = new();
    private readonly List<CalendarEvent> _events;

    /// <summary>Creates a calendar with <paramref name="events"/>.</summary>
    /// <param name="events">The events it holds.</param>
    /// <param name="name">What it is called.</param>
    /// <param name="isSample">Whether its events are made up.</param>
    public InMemoryCalendarProvider(IEnumerable<CalendarEvent>? events = null, string name = "Sample calendar", bool isSample = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _events = [.. events ?? []];
        Name = name;
        IsSample = isSample;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool IsSample { get; }

    /// <summary>When set, every read fails in this way, to try what the tools do when a calendar cannot be read.</summary>
    public CalendarFailure? Failure { get; set; }

    /// <summary>How many reads were asked of it.</summary>
    public int Reads { get; private set; }

    /// <summary>A calendar with a week or two of made-up events around today: a few meetings, a dentist, a birthday and a conference.</summary>
    /// <param name="clock">What says today, and the user's time zone.</param>
    public static InMemoryCalendarProvider WithSampleEvents(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var zone = clock.LocalTimeZone;
        var today = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).Date;

        DateTimeOffset At(int days, int hour, int minute = 0)
        {
            var local = DateTime.SpecifyKind(today.AddDays(days).AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }

        DateTimeOffset Day(int days) => At(days, 0);

        return new InMemoryCalendarProvider(
        [
            new CalendarEvent { Title = "Design review (sample)", Start = At(0, 10), End = At(0, 10, 30), Location = "Room 4", Notes = "Bring the mockups.", CalendarName = "Work" },
            new CalendarEvent { Title = "Call with Anna (sample)", Start = At(0, 15), End = At(0, 16), CalendarName = "Work" },
            new CalendarEvent { Title = "Dentist (sample)", Start = At(1, 9, 30), End = At(1, 10, 15), Location = "Main Street Clinic", CalendarName = "Personal" },
            new CalendarEvent { Title = "Anna's birthday (sample)", Start = Day(1), End = Day(2), IsAllDay = true, CalendarName = "Personal" },
            new CalendarEvent { Title = "Lunch with Sam (sample)", Start = At(3, 13), End = At(3, 14), Location = "Corner Cafe", CalendarName = "Personal" },
            new CalendarEvent { Title = "Quarterly planning (sample)", Start = At(7, 11), End = At(7, 12), Location = "Room 2", Notes = "Prepare the budget.", CalendarName = "Work" },
            new CalendarEvent { Title = "Conference (sample)", Start = Day(9), End = Day(12), IsAllDay = true, Location = "Convention Centre", CalendarName = "Work" },
        ]);
    }

    /// <summary>Adds an event.</summary>
    public void Add(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        lock (_gate)
        {
            _events.Add(calendarEvent);
        }
    }

    /// <inheritdoc/>
    public Task<CalendarEventList> GetEventsAsync(CalendarEventQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Read(query.Start, query.End, query.MaxResults, _ => true, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<CalendarEventList> SearchEventsAsync(CalendarSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var words = Words(query.Text);
        return Read(query.Start, query.End, query.MaxResults, item => words.Count > 0 && words.All(word => Says(item, word)), cancellationToken);
    }

    private Task<CalendarEventList> Read(
        DateTimeOffset start, DateTimeOffset end, int maxResults, Func<CalendarEvent, bool> matches, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Reads++;
        }

        if (Failure is { } failure)
        {
            throw new CalendarProviderException(failure);
        }

        List<CalendarEvent> found;
        lock (_gate)
        {
            found = [.. _events
                .Where(item => Overlaps(item, start, end) && matches(item))
                .OrderBy(item => item.Start)
                .ThenBy(item => item.End)
                .ThenBy(item => item.Title, StringComparer.Ordinal)];
        }

        var most = Math.Max(0, maxResults);
        return Task.FromResult(new CalendarEventList(found.Take(most).ToList(), found.Count <= most));
    }

    // An event is in a time when it starts before the time ends and ends after it starts; one with no length is in it when it starts inside.
    private static bool Overlaps(CalendarEvent item, DateTimeOffset start, DateTimeOffset end) =>
        item.End > item.Start ? item.Start < end && item.End > start : item.Start >= start && item.Start < end;

    // The words of what was searched for: letters and digits, lower case, each once.
    private static List<string> Words(string? text) =>
    [
        .. (text ?? string.Empty)
            .Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '"', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('\'', '-').ToLower(CultureInfo.InvariantCulture))
            .Where(word => word.Length > 0)
            .Distinct(StringComparer.Ordinal),
    ];

    private static bool Says(CalendarEvent item, string word) =>
        Contains(item.Title, word) || Contains(item.Location, word) || Contains(item.Notes, word) || Contains(item.CalendarName, word);

    private static bool Contains(string? text, string word) =>
        text is not null && text.Contains(word, StringComparison.OrdinalIgnoreCase);
}
