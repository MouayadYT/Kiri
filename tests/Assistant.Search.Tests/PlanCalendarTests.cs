using Assistant.Search.Planning;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>The days a request speaks of, worked out in the user's own time zone.</summary>
public sealed class PlanCalendarTests
{
    [Fact]
    public void TheNamedPeriodsAreWholeLocalDaysOfTheWeeksMonthsAndYearsPeopleMean()
    {
        var calendar = PlannerDay.Calendar;

        Assert.Equal(new DateOnly(2026, 10, 2), calendar.Today);
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1)), calendar.Period("yesterday"));
        Assert.Equal((new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 2)), calendar.Period("this week"));
        Assert.Equal((new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 27)), calendar.Period("last week"));
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2)), calendar.Period("this month"));
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), calendar.Period("last month"));
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 2)), calendar.Period("this year"));
        Assert.Equal((new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)), calendar.Period("last year"));
        Assert.Null(calendar.Period("next week"));
        Assert.Equal((new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 2)), calendar.LastDays(3));
        Assert.Equal((new DateOnly(2026, 9, 26), new DateOnly(2026, 10, 2)), calendar.LastWeeks(1));
        Assert.Equal((new DateOnly(2026, 8, 3), new DateOnly(2026, 10, 2)), calendar.LastMonths(2));
    }

    [Fact]
    public void LastTuesdayIsTheMostRecentOneBeforeToday_AndTodaysOwnWeekdayIsAWeekAgo()
    {
        var calendar = PlannerDay.Calendar;

        Assert.Equal(new DateOnly(2026, 9, 29), calendar.LastWeekday(DayOfWeek.Tuesday));
        Assert.Equal(new DateOnly(2026, 10, 1), calendar.LastWeekday(DayOfWeek.Thursday));
        Assert.Equal(new DateOnly(2026, 9, 25), calendar.LastWeekday(DayOfWeek.Friday));
        Assert.Equal(new DateOnly(2026, 9, 27), calendar.LastWeekday(DayOfWeek.Sunday));
    }

    [Fact]
    public void ADayIsAnInstantRangeFromItsLocalMidnightToTheNextOnes()
    {
        var range = PlannerDay.Calendar.Range(new DateOnly(2026, 9, 29), new DateOnly(2026, 9, 29));

        // Two hours ahead of UTC: the local day starts two hours before the UTC one does.
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 22, 0, 0, TimeSpan.Zero), range.From);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 22, 0, 0, TimeSpan.Zero), range.To);
        Assert.Null(PlannerDay.Calendar.Range(null, null).From);
        Assert.True(PlannerDay.Calendar.Range(null, null).IsUnbounded);
    }

    [Fact]
    public void AWeekBeginsOnTheFirstDayOfTheUsersWeek()
    {
        var sunday = new PlanCalendar(PlannerDay.Clock, DayOfWeek.Sunday);

        Assert.Equal(new DateOnly(2026, 9, 27), sunday.StartOfWeek(new DateOnly(2026, 10, 2)));
        Assert.Equal((new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 26)), sunday.Period("last week"));
    }

    [Fact]
    public void ADayThatStartsAcrossAClockChangeIsStillItsLocalMidnight()
    {
        var zone = FindZone("Eastern Standard Time");
        if (zone is null)
        {
            return;
        }

        // The clocks in New York go forward in the early hours of Sunday 8 March 2026: that day is 23 hours long.
        var clock = new FixedZoneClock(new DateTimeOffset(2026, 3, 10, 15, 0, 0, TimeSpan.Zero), zone);
        var range = new PlanCalendar(clock, DayOfWeek.Monday).Range(new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 8));

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero), range.From);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero), range.To);
    }

    [Fact]
    public void TheCheatSheetListsTodayTheLastSevenDaysByNameAndTheNamedPeriods()
    {
        var sheet = PlannerDay.Calendar.CheatSheet();

        Assert.Contains("Today is Friday 2026-10-02", sheet);
        Assert.Contains("Yesterday: 2026-10-01", sheet);
        Assert.Contains("Thursday 2026-10-01", sheet);
        Assert.Contains("Tuesday 2026-09-29", sheet);
        Assert.Contains("Friday 2026-09-25 (a week ago today)", sheet);
        Assert.Contains("This week: 2026-09-28 to 2026-10-02. Last week: 2026-09-21 to 2026-09-27.", sheet);
        Assert.Contains("Last month: 2026-09-01 to 2026-09-30", sheet);
        Assert.Contains("Last year: 2025-01-01 to 2025-12-31", sheet);
    }

    private static TimeZoneInfo? FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
    }
}
