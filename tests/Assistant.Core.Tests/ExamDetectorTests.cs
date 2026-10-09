using System.Text.Json;
using Assistant.Core.Calendar;
using Assistant.Core.Tools;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>The Assistant's own check of whether a calendar event is an exam (PROJECT_SPEC §4.8, step 116): fixed word lists, the same answer every time.</summary>
public sealed class ExamDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Physics final exam")]
    [InlineData("Final Exam: History (Hall A)")]
    [InlineData("Calculus midterm")]
    [InlineData("Midterm")]
    [InlineData("Mid-term: Biology")]
    [InlineData("Mid term biology")]
    [InlineData("FINALS WEEK")]
    [InlineData("MATH 101 EXAM")]
    [InlineData("EXAM!!!")]
    [InlineData("Chemistry quiz")]
    [InlineData("Biology quiz (sample)")]
    [InlineData("Spanish test")]
    [InlineData("Driving test")]
    [InlineData("Physics final")]
    [InlineData("Organic chemistry assessment")]
    [InlineData("Oral viva")]
    public void AnEventWhoseTitleSaysItIsAnExamIsLikely(string title)
    {
        var assessment = ExamDetector.Assess(title);

        Assert.Equal(ExamLikelihood.Likely, assessment.Likelihood);
        Assert.True(assessment.IsCandidate);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Reason));
    }

    [Fact]
    public void AnExamHallInThePlaceMakesAnEventLikely()
    {
        Assert.Equal(ExamLikelihood.Likely, ExamDetector.Assess("Physics", "Exam Hall B").Likelihood);
        Assert.Equal("the place says exam", ExamDetector.Assess("Physics", "Exam Hall B").Reason);
    }

    [Theory]
    [InlineData("Exam review session")]
    [InlineData("Study session for the chemistry midterm")]
    [InlineData("Revision for finals")]
    [InlineData("Study for the chemistry test")]
    [InlineData("Eye exam")]
    [InlineData("Annual physical exam")]
    [InlineData("Dental exam")]
    [InlineData("Final presentation")]
    [InlineData("Mock exam practice")]
    public void AnEventThatMayBeAnExamButIsNotClearIsOnlyPossible(string title)
    {
        var assessment = ExamDetector.Assess(title);

        Assert.Equal(ExamLikelihood.Possible, assessment.Likelihood);
        Assert.True(assessment.IsCandidate);
    }

    [Fact]
    public void AnExamOnlyInTheNotesIsOnlyPossible()
    {
        var assessment = ExamDetector.Assess("Team meeting", null, "Bring your exam notes");

        Assert.Equal(ExamLikelihood.Possible, assessment.Likelihood);
        Assert.Equal("the notes mention an exam", assessment.Reason);
    }

    [Theory]
    [InlineData("Dentist appointment")]
    [InlineData("Lunch with Sam")]
    [InlineData("Test drive at the dealership")]
    [InlineData("Blood test")]
    [InlineData("Covid test")]
    [InlineData("Pub quiz")]
    [InlineData("Quiz night")]
    [InlineData("Unit test review")]
    [InlineData("Testing meeting")]
    [InlineData("Examine the budget")]
    [InlineData("")]
    [InlineData(null)]
    public void AnEventThatSaysNothingOfAnExamIsNot(string? title)
    {
        var assessment = ExamDetector.Assess(title);

        Assert.Equal(ExamLikelihood.NotAnExam, assessment.Likelihood);
        Assert.False(assessment.IsCandidate);
        Assert.Equal(string.Empty, assessment.Reason);
    }

    [Fact]
    public void AnEventIsReadFromItsThreeTexts()
    {
        var item = new CalendarEvent { Title = "Physics final exam", Start = Now, End = Now.AddHours(2), Location = "Hall B", Notes = "Bring a calculator" };

        Assert.Equal(ExamLikelihood.Likely, ExamDetector.Assess(item).Likelihood);
        Assert.Throws<ArgumentNullException>(() => ExamDetector.Assess((CalendarEvent)null!));
    }

    [Fact]
    public void TheCalendarToolMarksAnEventItThinksIsAnExamAndSaysWhatTheMarkIs()
    {
        var events = new CalendarEventList(
        [
            new CalendarEvent { Title = "Dentist", Start = Now.AddDays(1), End = Now.AddDays(1).AddHours(1) },
            new CalendarEvent { Title = "Physics final exam", Start = Now.AddDays(5), End = Now.AddDays(5).AddHours(2), Location = "Hall B" },
            new CalendarEvent { Title = "Exam review session", Start = Now.AddDays(4), End = Now.AddDays(4).AddHours(1) },
        ]);

        var json = CalendarToolResults.Events("Sample", isSample: false, Now, Now.AddDays(14), TimeZoneInfo.Utc, events);

        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.GetProperty("events").EnumerateArray().ToList();
        Assert.False(items[0].TryGetProperty("exam", out _));
        Assert.Equal("likely", items[1].GetProperty("exam").GetString());
        Assert.Equal("the title says exam", items[1].GetProperty("exam_reason").GetString());
        Assert.Equal("possible", items[2].GetProperty("exam").GetString());
        Assert.Contains("hint, not a fact", document.RootElement.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ACalendarWithNoExamsSaysNothingOfThem()
    {
        var events = new CalendarEventList([new CalendarEvent { Title = "Dentist", Start = Now.AddDays(1), End = Now.AddDays(1).AddHours(1) }]);

        var json = CalendarToolResults.Events("Sample", isSample: false, Now, Now.AddDays(14), TimeZoneInfo.Utc, events);

        Assert.DoesNotContain("exam", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheExamNoteDoesNotHideTheNoteThatThereAreMoreEvents()
    {
        var many = Enumerable.Range(0, CalendarToolResults.MaxEvents + 1)
            .Select(index => new CalendarEvent { Title = index == 0 ? "Physics final exam" : "Event " + index, Start = Now.AddHours(index), End = Now.AddHours(index + 1) })
            .ToList();

        var json = CalendarToolResults.Events("Sample", isSample: false, Now, Now.AddDays(14), TimeZoneInfo.Utc, new CalendarEventList(many));

        using var document = JsonDocument.Parse(json);
        var note = document.RootElement.GetProperty("note").GetString()!;
        Assert.Contains("more events than these", note, StringComparison.Ordinal);
        Assert.Contains("hint, not a fact", note, StringComparison.Ordinal);
    }
}
