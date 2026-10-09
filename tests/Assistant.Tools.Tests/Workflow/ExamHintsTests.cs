using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core.Domain;
using Assistant.Tools.Calendar;
using Assistant.Tools.Mcp;
using Assistant.Tools.Tests.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Workflow;

/// <summary>The Assistant's own check of which events a connected calendar app returned are exams (PROJECT_SPEC §4.8, step 116): the common shapes, marked as a hint.</summary>
public sealed class ExamHintsTests
{
    private static JsonObject? Check(string text) => ExamHints.Check(Sample.Text(text));

    private static List<string> Events(JsonNode? list) => [.. list!.AsArray().Select(item => item!["event"]!.GetValue<string>())];

    [Fact]
    public void TheEventsOfAnAppsAnswerAreCheckedAndTheExamsListedWithWhy()
    {
        var check = Check(WorkflowFixture.EventsJson)!;

        Assert.Equal(5, check["events_checked"]!.GetValue<int>());
        Assert.Equal(["Physics final exam (sample) (2026-10-07T09:00:00)", "Calculus midterm (sample) (2026-10-11T13:00:00)"], Events(check["likely"]));
        Assert.Equal(["Study session for the chemistry midterm (sample) (2026-10-08T15:00:00)"], Events(check["possible"]));
        Assert.Equal("the title says exam", check["likely"]![0]!["why"]!.GetValue<string>());
        Assert.Contains("a hint, not a fact", check["by"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void GoogleStyleEventsAreRead()
    {
        var check = Check(
            """
            {"kind":"calendar#events","items":[
              {"id":"1","summary":"Chemistry final exam","start":{"dateTime":"2026-10-09T09:00:00+02:00"},"end":{"dateTime":"2026-10-09T11:00:00+02:00"},"location":"Hall 3"},
              {"id":"2","summary":"Holiday","start":{"date":"2026-10-12"},"end":{"date":"2026-10-13"}}]}
            """)!;

        Assert.Equal(2, check["events_checked"]!.GetValue<int>());
        Assert.Equal(["Chemistry final exam (2026-10-09T09:00:00+02:00)"], Events(check["likely"]));
        Assert.Empty(check["possible"]!.AsArray());
    }

    [Theory]
    [InlineData("""[{"title":"Biology midterm","start":"2026-10-09"}]""")]
    [InlineData("""{"events":[{"name":"Biology midterm","startTime":"2026-10-09"}]}""")]
    [InlineData("""{"data":{"results":[{"subject":"Biology midterm","date":"2026-10-09"}]}}""")]
    [InlineData("""{"events":[{"event_name":"Biology midterm","start_time":"2026-10-09"}]}""")]
    public void TheNamesAnAppGivesATitleAndAStartAreAllRead(string json)
    {
        var check = Check(json)!;

        Assert.Single(check["likely"]!.AsArray());
    }

    [Fact]
    public void AnEventsPlaceAndNotesAreCheckedToo()
    {
        var check = Check("""{"events":[{"title":"Physics","start":"2026-10-09","location":"Exam Hall B"},{"title":"Meeting","start":"2026-10-10","description":"Bring the exam notes"}]}""")!;

        Assert.Equal(["Physics (2026-10-09)"], Events(check["likely"]));
        Assert.Equal(["Meeting (2026-10-10)"], Events(check["possible"]));
    }

    [Fact]
    public void AnAppThatAnswersInPlainLinesHasEachLineCheckedAsAnEvent()
    {
        var check = Check("2026-10-07 09:00 Physics final exam (Hall B)\n2026-10-08 10:00 Dentist\n2026-10-11 13:00 Calculus midterm")!;

        Assert.Equal(3, check["events_checked"]!.GetValue<int>());
        Assert.Equal(2, check["likely"]!.AsArray().Count);
        Assert.Empty(check["possible"]!.AsArray());
    }

    [Fact]
    public void StructuredContentIsCheckedToo()
    {
        var result = new McpToolResult(false, [], Sample.Json("""{"events":[{"title":"Math exam","start":"2026-10-09"}]}"""));

        Assert.Single(ExamHints.Check(result)!["likely"]!.AsArray());
    }

    [Fact]
    public void AnAnswerWithNoEventsInItAddsNothing()
    {
        Assert.Null(Check("""{"events":[]}"""));
        Assert.Null(Check(""));
        Assert.Null(Check("""{"status":"ok"}"""));
    }

    [Fact]
    public void ACalendarWithNoExamsSaysSoWithEmptyListsSoTheModelKnowsTheCheckWasMade()
    {
        var check = Check("""{"events":[{"title":"Dentist","start":"2026-10-09"}]}""")!;

        Assert.Equal(1, check["events_checked"]!.GetValue<int>());
        Assert.Empty(check["likely"]!.AsArray());
        Assert.Empty(check["possible"]!.AsArray());
    }

    [Fact]
    public void AToolThatFailedIsNeverChecked()
    {
        Assert.Null(ExamHints.Check(new McpToolResult(true, [new McpContentBlock(McpContentKind.Text, """{"events":[{"title":"Exam","start":"x"}]}""", null, null, null)], null)));
    }

    [Fact]
    public void NoMoreThanTenOfEachAreListedAndTwoHundredEventsAreChecked()
    {
        var many = string.Join(',', Enumerable.Range(0, 300).Select(index => $$"""{"title":"Final exam {{index}}","start":"2026-10-09"}"""));

        var check = Check($$"""{"events":[{{many}}]}""")!;

        Assert.Equal(200, check["events_checked"]!.GetValue<int>());
        Assert.Equal(10, check["likely"]!.AsArray().Count);
    }

    [Fact]
    public void AnEventsTitleIsOneShortLineWithNoMarkupInTheList()
    {
        var check = Check("""{"events":[{"title":"<b>Final</b>\nexam  hall","start":"2026-10-09"}]}""")!;

        Assert.Equal("b Final /b exam hall (2026-10-09)", Events(check["likely"]).Single());
    }

    [Fact]
    public void TheCheckIsPutBesideWhatTheAppSaidAndNeverInPlaceOfIt()
    {
        var call = new ToolCall("c1", "mcp_cal_list_events", "{}");
        var app = Sample.Text(WorkflowFixture.EventsJson);

        var plain = McpToolResults.Map(call, "Sample Calendar", app);
        var checkedResult = McpToolResults.Map(call, "Sample Calendar", app, checkExams: true);

        using var plainJson = JsonDocument.Parse(plain.OutputJson);
        using var checkedJson = JsonDocument.Parse(checkedResult.OutputJson);
        Assert.False(plainJson.RootElement.TryGetProperty("exam_check", out _));
        Assert.True(checkedJson.RootElement.TryGetProperty("exam_check", out var check));
        Assert.Equal(2, check.GetProperty("likely").GetArrayLength());
        Assert.Equal(
            plainJson.RootElement.GetProperty("content")[0].GetProperty("text").GetString(),
            checkedJson.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void AnAnswerThatIsNotUnderstoodGetsNoCheckEvenWhenAskedFor()
    {
        var result = McpToolResults.Map(new ToolCall("c1", "t", "{}"), "App", Sample.Text("Nothing today."), checkExams: false);
        var asked = McpToolResults.Map(new ToolCall("c1", "t", "{}"), "App", Sample.Text("{\"status\":\"ok\"}"), checkExams: true);

        Assert.DoesNotContain("exam_check", result.OutputJson, StringComparison.Ordinal);
        Assert.DoesNotContain("exam_check", asked.OutputJson, StringComparison.Ordinal);
    }
}
