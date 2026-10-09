using System.Globalization;
using Assistant.Core.Contracts;

namespace Assistant.Search.Planning;

/// <summary>
/// The days a request speaks of ("yesterday", "last Tuesday", "last week"), worked out from the clock in the user's own time
/// zone. A day is a whole local day, from its midnight to the next; the planner turns it into the instants the index is asked
/// about. Days come out as dates, and only the last step makes them instants, so a range never drifts across a time change.
/// </summary>
internal sealed class PlanCalendar
{
    private readonly TimeProvider _clock;
    private readonly DayOfWeek _firstDayOfWeek;

    public PlanCalendar(TimeProvider clock, DayOfWeek? firstDayOfWeek = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _firstDayOfWeek = firstDayOfWeek ?? CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
    }

    /// <summary>Today, by the clock, in the user's time zone.</summary>
    public DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    /// <summary>The day a week that holds <paramref name="day"/> begins on.</summary>
    public DateOnly StartOfWeek(DateOnly day)
    {
        var back = ((int)day.DayOfWeek - (int)_firstDayOfWeek + 7) % 7;
        return day.AddDays(-back);
    }

    /// <summary>
    /// The range of whole days from <paramref name="first"/> to <paramref name="last"/>, both included, as the instants the
    /// index compares: the first day's midnight up to the midnight after the last day's.
    /// </summary>
    public DateRange Range(DateOnly? first, DateOnly? last) => new(
        first is { } from ? Midnight(from) : null,
        last is { } to ? Midnight(to.AddDays(1)) : null);

    /// <summary>The local midnight that begins <paramref name="day"/>, as an instant.</summary>
    public DateTimeOffset Midnight(DateOnly day)
    {
        var zone = _clock.LocalTimeZone;
        var local = day.ToDateTime(TimeOnly.MinValue);

        // A zone that skips midnight at a time change starts its day an hour later.
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    /// <summary>
    /// A period named in words, as the first and last day it covers. The periods are fixed: <c>today</c>, <c>yesterday</c>,
    /// <c>this week</c>, <c>last week</c>, <c>this month</c>, <c>last month</c>, <c>this year</c> and <c>last year</c>.
    /// </summary>
    public (DateOnly First, DateOnly Last)? Period(string name)
    {
        var today = Today;
        switch (name)
        {
            case "today":
                return (today, today);
            case "yesterday":
                return (today.AddDays(-1), today.AddDays(-1));
            case "this week":
                return (StartOfWeek(today), today);
            case "last week":
                var thisWeek = StartOfWeek(today);
                return (thisWeek.AddDays(-7), thisWeek.AddDays(-1));
            case "this month":
                return (new DateOnly(today.Year, today.Month, 1), today);
            case "last month":
                var start = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
                return (start, start.AddMonths(1).AddDays(-1));
            case "this year":
                return (new DateOnly(today.Year, 1, 1), today);
            case "last year":
                return (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31));
            default:
                return null;
        }
    }

    /// <summary>The most recent <paramref name="weekday"/> before today: what "last Tuesday" means.</summary>
    public DateOnly LastWeekday(DayOfWeek weekday)
    {
        var today = Today;
        var back = ((int)today.DayOfWeek - (int)weekday + 7) % 7;
        return today.AddDays(-(back == 0 ? 7 : back));
    }

    /// <summary>The last <paramref name="days"/> days, today included: <c>past 3 days</c> is today and the two before it.</summary>
    public (DateOnly First, DateOnly Last) LastDays(int days) => (Today.AddDays(-(days - 1)), Today);

    /// <summary>The last <paramref name="weeks"/> weeks, as days: seven to a week, ending today.</summary>
    public (DateOnly First, DateOnly Last) LastWeeks(int weeks) => LastDays(weeks * 7);

    /// <summary>The last <paramref name="months"/> months, counted back from today.</summary>
    public (DateOnly First, DateOnly Last) LastMonths(int months) => (Today.AddMonths(-months).AddDays(1), Today);

    /// <summary>
    /// What a model needs to work out dates it cannot compute: today, the days of the past week by name, and the start and
    /// end of the weeks, months and years people speak of. Every date is <c>YYYY-MM-DD</c> in local time.
    /// </summary>
    public string CheatSheet()
    {
        var today = Today;
        var thisWeek = StartOfWeek(today);
        var lastMonth = Period("last month")!.Value;
        var lines = new List<string>
        {
            $"Today is {Name(today)} {Iso(today)} (local time).",
            $"Yesterday: {Iso(today.AddDays(-1))}.",
            "Days of the past week, newest first:",
        };

        for (var back = 1; back <= 7; back++)
        {
            var day = today.AddDays(-back);
            lines.Add($"  {Name(day)} {Iso(day)}{(back == 7 ? " (a week ago today)" : "")}");
        }

        lines.Add("\"last <weekday>\" is the most recent such weekday before today, from the list above.");
        lines.Add($"This week: {Iso(thisWeek)} to {Iso(today)}. Last week: {Iso(thisWeek.AddDays(-7))} to {Iso(thisWeek.AddDays(-1))}.");
        lines.Add($"This month: {Iso(new DateOnly(today.Year, today.Month, 1))} to {Iso(today)}. Last month: {Iso(lastMonth.First)} to {Iso(lastMonth.Last)}.");
        lines.Add($"This year: {Iso(new DateOnly(today.Year, 1, 1))} to {Iso(today)}. Last year: {today.Year - 1}-01-01 to {today.Year - 1}-12-31.");
        return string.Join('\n', lines);
    }

    /// <summary>Writes <paramref name="day"/> as <c>YYYY-MM-DD</c>.</summary>
    public static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The English name of the weekday of <paramref name="day"/>.</summary>
    public static string Name(DateOnly day) => day.DayOfWeek.ToString();
}
