using System.Globalization;

namespace Assistant.Core.Calendar;

/// <summary>
/// The structured date filters of the calendar tools (PROJECT_SPEC §4.8, step 111): a start and an end that the model writes as ISO 8601 text, read strictly and turned into one
/// concrete time window in the user's time zone, so that a provider is only ever given two instants. A date (<c>2026-10-03</c>) is the start of that day; a date and time
/// (<c>2026-10-03T09:00</c>, with a space for the T if the model writes one) is that time, in the user's time zone unless it names an offset (<c>Z</c>, <c>+02:00</c>). The end is not
/// included. When the model gives the same date as start and end it means that day, so the window is the whole day. Nothing here reads a clock but the one it is given, and nothing the model
/// wrote is repeated in a problem it reports.
/// </summary>
public static class CalendarDates
{
    /// <summary>The longest a window may be, in days: a year, with a day to spare for a leap year.</summary>
    public const int MaxWindowDays = 366;

    private static readonly string[] DateFormats = ["yyyy-MM-dd"];

    private static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFF",
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFF",
    ];

    private static readonly string[] OffsetFormats =
    [
        "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFK",
        "yyyy-MM-dd HH:mmK", "yyyy-MM-dd HH:mm:ssK", "yyyy-MM-dd HH:mm:ss.FFFK",
    ];

    /// <summary>Reads <paramref name="text"/> as a date or a date and time, in <paramref name="zone"/> when it names no offset.</summary>
    /// <param name="text">What the model wrote.</param>
    /// <param name="zone">The user's time zone.</param>
    /// <param name="instant">The time it is.</param>
    /// <param name="dateOnly">Whether it was only a date, which is the start of the day.</param>
    /// <returns><see langword="true"/> when it was a date or a date and time.</returns>
    public static bool TryParse(string? text, TimeZoneInfo zone, out DateTimeOffset instant, out bool dateOnly)
    {
        ArgumentNullException.ThrowIfNull(zone);
        instant = default;
        dateOnly = false;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 40)
        {
            return false;
        }

        if (DateTime.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            instant = InZone(day, zone);
            dateOnly = true;
            return true;
        }

        if (DateTime.TryParseExact(trimmed, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            instant = InZone(local, zone);
            return true;
        }

        if (DateTimeOffset.TryParseExact(trimmed, OffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            instant = exact;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The window the model asked for. A problem, in words the model can act on, says what was wrong and never repeats what it wrote.
    /// </summary>
    /// <param name="start">The start the model wrote, or <see langword="null"/> when it gave none.</param>
    /// <param name="end">The end the model wrote, or <see langword="null"/> when it gave none.</param>
    /// <param name="now">The time now.</param>
    /// <param name="zone">The user's time zone.</param>
    /// <param name="bothRequired">Whether the model must give both (reading a calendar); otherwise a missing one is made up from today and the longest window.</param>
    /// <param name="window">The time asked about, when there is no problem.</param>
    /// <param name="problem">What was wrong, when there is a problem.</param>
    /// <returns><see langword="true"/> when the window is usable.</returns>
    public static bool TryResolve(
        string? start, string? end, DateTimeOffset now, TimeZoneInfo zone, bool bothRequired, out (DateTimeOffset Start, DateTimeOffset End) window, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(zone);
        window = default;
        problem = null;
        var hasStart = !string.IsNullOrWhiteSpace(start);
        var hasEnd = !string.IsNullOrWhiteSpace(end);
        if (bothRequired && !(hasStart && hasEnd))
        {
            problem = "Give both a start and an end, for example start 2026-10-03 and end 2026-10-04 for the whole of that one day.";
            return false;
        }

        var startsAt = default(DateTimeOffset);
        var endsAt = default(DateTimeOffset);
        var startIsDate = false;
        var endIsDate = false;
        if (hasStart && !TryParse(start, zone, out startsAt, out startIsDate))
        {
            problem = "The start is not a date. Write it as 2026-10-03 or 2026-10-03T09:00.";
            return false;
        }

        if (hasEnd && !TryParse(end, zone, out endsAt, out endIsDate))
        {
            problem = "The end is not a date. Write it as 2026-10-03 or 2026-10-03T09:00.";
            return false;
        }

        // What is not given is made up from today: a search with no dates looks from today for a year ahead.
        var today = InZone(TimeZoneInfo.ConvertTime(now, zone).Date, zone);
        if (!hasStart)
        {
            startsAt = today;
        }

        if (!hasEnd)
        {
            endsAt = startsAt.AddDays(MaxWindowDays);
        }

        // The same date for both is that day, which is what a person means; the end is not included, so the day is a day long.
        if (hasStart && hasEnd && startIsDate && endIsDate && endsAt == startsAt)
        {
            endsAt = startsAt.AddDays(1);
        }

        if (endsAt <= startsAt)
        {
            problem = "The end must be after the start. For the whole of one day, give that day as the start and the next day as the end.";
            return false;
        }

        if (endsAt - startsAt > TimeSpan.FromDays(MaxWindowDays))
        {
            problem = $"That is longer than {MaxWindowDays} days. Ask for a shorter time, or search for words instead.";
            return false;
        }

        window = (startsAt, endsAt);
        return true;
    }

    /// <summary>The time as the model is told it: <c>2026-10-03T09:00:00+02:00</c> in <paramref name="zone"/>.</summary>
    public static string Format(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    /// <summary>The day as the model is told it, for an all-day event: <c>2026-10-03</c>.</summary>
    public static string FormatDate(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    // A time on the user's clock, in their zone: one that the clock skips (the hour a daylight saving change jumps over) is read as the same time an hour later.
    private static DateTimeOffset InZone(DateTime clockTime, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(clockTime, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }
}
