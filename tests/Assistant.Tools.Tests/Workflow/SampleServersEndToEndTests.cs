using System.Globalization;
using System.Text.Json;
using Assistant.Core.Agent;
using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Tests.Mcp;
using Xunit;
using Loop = Assistant.Tools.Tests.Mcp.AgentLoopConnectedAppsTests;

namespace Assistant.Tools.Tests.Workflow;

/// <summary>
/// The whole job over the two made-up apps that ship beside the app, run as the real programs they are (PROJECT_SPEC §4.8, step 116): a calendar whose events are made up around today,
/// and a messaging app with Omar's chat and a group Omar is in. Everything else is real as well: the stdio transport, the connection manager, the loop, the tools and the executor. Only
/// the model is scripted and the user's answer is given by the test.
/// </summary>
public sealed class SampleServersEndToEndTests : IDisposable
{
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "Assistant.SampleMcpServer.exe");

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private InstalledIntegration Program(string id, string name, string app, string[] readOnly, string[] toolNames) =>
        new()
        {
            Id = id,
            Name = name,
            Enabled = true,
            IsSample = true,
            Transport = new IntegrationTransport
            {
                Kind = McpTransportKind.Stdio,
                Command = ExePath,
                Arguments = ["--app", app],
                Environment = new Dictionary<string, string> { ["ASSISTANT_SAMPLE_LOG"] = _folder.File(app + ".log") },
            },
            Capabilities = new IntegrationCapabilities { Tools = true, ToolNames = toolNames, RefreshedAt = WorkflowFixture.Start },
            Health = new IntegrationHealth { Status = IntegrationHealthStatus.Healthy },
            Permissions = new IntegrationPermissions { ReadOnlyTools = readOnly },
        };

    private static List<JsonElement> Lines(string path) =>
        File.Exists(path) ? [.. File.ReadAllLines(path).Where(line => line.Length > 0).Select(line => JsonDocument.Parse(line).RootElement.Clone())] : [];

    [Fact]
    public async Task TheSampleCalendarAndMessagingAppDoTheWholeJobAsRealPrograms()
    {
        Assert.True(File.Exists(ExePath), "The sample program is not beside the tests: " + ExePath);
        var calendar = Program("samplecalendar", "Sample Calendar", "calendar", ["list_events", "search_events"], ["list_events", "search_events"]);
        var messages = Program("samplemessages", "Sample Messages", "messages", ["search_chats"], ["search_chats", "send_message"]);
        await using var fixture = new WorkflowFixture(
            realClients: new McpClientFactory(null, new McpClientOptions()), installed: [calendar, messages]);
        var today = DateTime.Now.Date;
        string Day(int days) => today.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // The calendar window is today to the day two weeks from today, as the model is told it.
        var scripted = new Loop.ScriptedModel(
            [Loop.Call("mcp_samplecalendar_list_events", WorkflowFixture.Json(new { start = Day(0), end = Day(14) }), "c1")],
            [Loop.Call("draft_message", WorkflowFixture.Json(new { recipient = "my brother", text = WorkflowFixture.Reminder }), "c2")],
            [Loop.Call("send_message", WorkflowFixture.Json(new { recipient = "my brother", text = WorkflowFixture.Reminder }), "c3")],
            [Loop.Words("Done.")]);

        var (chunks, _) = await fixture.RunAsync(scripted);

        var results = WorkflowFixture.ResultsOf(chunks);
        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.True(result.Status == ToolResultStatus.Succeeded, result.OutputJson));

        // The made-up events came back with the Assistant's reading of them: the exams in the window, the study session as only possible, the one three weeks off not there.
        var check = JsonDocument.Parse(results[0].OutputJson).RootElement.GetProperty("exam_check");
        var likely = check.GetProperty("likely").EnumerateArray().Select(item => item.GetProperty("event").GetString()!).ToList();
        Assert.Equal(3, likely.Count);
        Assert.Contains(likely, text => text.StartsWith("Physics final exam (sample)", StringComparison.Ordinal));
        Assert.Contains(likely, text => text.StartsWith("Calculus midterm (sample)", StringComparison.Ordinal));
        Assert.Contains(likely, text => text.StartsWith("Biology quiz (sample)", StringComparison.Ordinal));
        Assert.DoesNotContain(likely, text => text.Contains("History", StringComparison.Ordinal));
        Assert.Single(check.GetProperty("possible").EnumerateArray());
        var appsOwnWords = JsonDocument.Parse(results[0].OutputJson).RootElement.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("\"sample\":true", appsOwnWords, StringComparison.Ordinal);
        Assert.Contains("made up", appsOwnWords, StringComparison.Ordinal);

        // Omar's own chat was found by his saved number, the group was not taken, and the message went to Omar's chat with the text the user was shown.
        Assert.Equal("drafted", JsonDocument.Parse(results[1].OutputJson).RootElement.GetProperty("status").GetString());
        var shown = Assert.Single(fixture.Confirmation.Shown);
        Assert.Contains(shown.Details, line => line.Label == "To" && line.Value == "Omar");
        Assert.Contains(shown.Details, line => line.Label == "Through" && line.Value.Contains("Sample Messages", StringComparison.Ordinal) && line.Value.Contains("a sample", StringComparison.Ordinal));
        Assert.Contains(shown.Details, line => line.Label == "Message" && line.Value == WorkflowFixture.Reminder);

        var calls = Lines(_folder.File("messages.log"));
        Assert.Contains(calls, call => call.GetProperty("tool").GetString() == "search_chats");
        var sent = Assert.Single(calls, call => call.GetProperty("tool").GetString() == "send_message");
        Assert.Equal("sample-chat-omar", sent.GetProperty("arguments").GetProperty("chat_id").GetString());
        Assert.Equal(WorkflowFixture.Reminder, sent.GetProperty("arguments").GetProperty("text").GetString());
        Assert.Equal("sent", JsonDocument.Parse(results[2].OutputJson).RootElement.GetProperty("status").GetString());
        Assert.True(JsonDocument.Parse(results[2].OutputJson).RootElement.GetProperty("sample").GetBoolean());

        var calendarCalls = Lines(_folder.File("calendar.log"));
        Assert.Equal(["list_events"], calendarCalls.Select(call => call.GetProperty("tool").GetString()));

        var trace = Assert.Single(fixture.Traces.Recent());
        Assert.Equal(AgentStopReason.Answered, trace.StopReason);
    }

    [Fact]
    public async Task NothingReachesTheSampleMessagingAppWhenTheUserSaysNo()
    {
        var messages = Program("samplemessages", "Sample Messages", "messages", ["search_chats"], ["search_chats", "send_message"]);
        await using var fixture = new WorkflowFixture(
            calendarApp: false, approve: false, realClients: new McpClientFactory(null, new McpClientOptions()), installed: [messages]);
        var model = new Loop.ScriptedModel(
            [Loop.Call("draft_message", WorkflowFixture.Json(new { recipient = "my brother", text = "Exam on Friday" }), "c1")],
            [Loop.Call("send_message", WorkflowFixture.Json(new { recipient = "my brother", text = "Exam on Friday" }), "c2")],
            [Loop.Words("Not sent.")]);

        var (chunks, _) = await fixture.RunAsync(model);

        Assert.Equal(ToolResultStatus.Declined, WorkflowFixture.ResultsOf(chunks)[1].Status);
        var calls = Lines(_folder.File("messages.log"));
        Assert.DoesNotContain(calls, call => call.GetProperty("tool").GetString() == "send_message");
        Assert.Single(fixture.Confirmation.Shown);
    }
}
