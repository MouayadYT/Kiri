using System.Text.Json;
using Assistant.Core.Agent;
using Assistant.Core.Calendar;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Messaging;
using Assistant.Core.People;
using Assistant.Core.Tools;
using Assistant.Tools.Calendar;
using Assistant.Tools.Messaging;
using Assistant.Tools.Tests.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Workflow;

/// <summary>
/// The canonical request, "Check my calendar for exams in the next two weeks and message my brother to remind him", carried out by the real agent loop over connected apps and the Assistant's own
/// tools (PROJECT_SPEC §4.8, step 116): the calendar is read through the generic integration route, the exams are picked out, "my brother" is worked out from the Assistant's own list, the
/// message is drafted, the user is asked, and the message is sent only after a yes, to the chat the draft named, with the text the draft held. The model is scripted (what it decides is
/// not what is tested); everything it calls is real.
/// </summary>
public sealed class CalendarToBrotherWorkflowTests
{
    private static List<ToolResult> Results(IEnumerable<AssistantResponseChunk> chunks) => WorkflowFixture.ResultsOf(chunks);

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task TheCalendarIsReadTheExamsPickedOutBrotherFoundTheMessageDraftedConfirmedAndSent()
    {
        await using var fixture = new WorkflowFixture();
        var model = WorkflowFixture.WholeJob();

        var (chunks, session) = await fixture.RunAsync(model);

        // The model was given the calendar's own tool, the Assistant's messaging tools, and nothing of the messaging app's: the Assistant sends for it.
        var offered = model.Requests[0].Tools.Select(tool => tool.Name).ToList();
        Assert.Contains("mcp_samplecalendar_list_events", offered);
        Assert.Contains("draft_message", offered);
        Assert.Contains("send_message", offered);
        Assert.DoesNotContain(offered, name => name.StartsWith("mcp_samplemessages", StringComparison.Ordinal));
        Assert.DoesNotContain("get_calendar_events", offered);
        Assert.DoesNotContain("search_calendar_events", offered);

        // The model was told today, the days worked out, how the calendar is checked, and the order to do the job in.
        var instructions = model.Requests[0].Instructions!;
        Assert.Contains("two weeks from today is 2026-10-16", instructions, StringComparison.Ordinal);
        Assert.Contains("exam_check", instructions, StringComparison.Ordinal);
        Assert.Contains("check their calendar and then message someone", instructions, StringComparison.Ordinal);

        var results = Results(chunks);
        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.True(result.Status == ToolResultStatus.Succeeded, result.OutputJson));

        // The calendar was read once, for the window the model gave, and what came back carried the Assistant's reading of the events.
        var read = Assert.Single(fixture.Calendar.Calls);
        Assert.Equal("list_events", read.Tool);
        Assert.Contains("2026-10-16", read.Arguments, StringComparison.Ordinal);
        var check = Json(results[0].OutputJson).GetProperty("exam_check");
        var likely = check.GetProperty("likely").EnumerateArray().Select(item => item.GetProperty("event").GetString()).ToList();
        Assert.Equal(2, likely.Count);
        Assert.Contains(likely, text => text!.StartsWith("Physics final exam", StringComparison.Ordinal));
        Assert.Contains(likely, text => text!.StartsWith("Calculus midterm", StringComparison.Ordinal));
        var possible = check.GetProperty("possible").EnumerateArray().Select(item => item.GetProperty("event").GetString()).ToList();
        Assert.Single(possible);
        Assert.StartsWith("Study session for the chemistry midterm", possible[0], StringComparison.Ordinal);
        Assert.Equal(5, check.GetProperty("events_checked").GetInt32());

        // "My brother" was worked out from the Assistant's own list, and the draft says who, where and that nothing was sent yet; no number is in it.
        var draft = Json(results[1].OutputJson);
        Assert.Equal("drafted", draft.GetProperty("status").GetString());
        Assert.Equal("Omar", draft.GetProperty("to").GetString());
        Assert.False(draft.GetProperty("sent").GetBoolean());
        Assert.True(draft.GetProperty("sample").GetBoolean());
        Assert.DoesNotContain("555", results[1].OutputJson, StringComparison.Ordinal);

        // The user was asked about exactly this message, and only then was it sent, to Omar's own chat and not the group he is in.
        var shown = Assert.Single(fixture.Confirmation.Shown);
        Assert.Equal(ConfirmationKind.SendMessage, shown.Kind);
        Assert.Contains(shown.Details, line => line.Label == "To" && line.Value == "Omar");
        Assert.Contains(shown.Details, line => line.Label == "Message" && line.Value == WorkflowFixture.Reminder);
        Assert.Contains(shown.Details, line => line.Label == "Through" && line.Value.Contains("Omar", StringComparison.Ordinal) && line.Value.Contains("a sample", StringComparison.Ordinal));
        var sent = Assert.Single(fixture.Messages.Calls, call => call.Tool == "send_message");
        using (var arguments = JsonDocument.Parse(sent.Arguments))
        {
            Assert.Equal("chat-omar", arguments.RootElement.GetProperty("chat_id").GetString());
            Assert.Equal(WorkflowFixture.Reminder, arguments.RootElement.GetProperty("text").GetString());
        }

        Assert.All(fixture.Messages.Calls.Where(call => call.Tool == "search_chats"), call => Assert.Contains("555", call.Arguments, StringComparison.Ordinal));
        var final = Json(results[2].OutputJson);
        Assert.Equal("sent", final.GetProperty("status").GetString());
        Assert.True(final.GetProperty("sample").GetBoolean());

        var trace = Assert.Single(fixture.Traces.Recent());
        Assert.Equal(["mcp_samplecalendar_list_events", "draft_message", "send_message"], trace.Calls.Select(call => call.ToolName));
        Assert.All(trace.Calls, call => Assert.Equal(AgentCallDisposition.Ran, call.Disposition));
        Assert.Equal(AgentStopReason.Answered, trace.StopReason);
        Assert.Equal("I found two exams and sent the reminder to Omar.", session.Conversation.Messages[^1].Text);
    }

    [Fact]
    public async Task NothingIsSentWhenTheUserDoesNotAllowIt()
    {
        await using var fixture = new WorkflowFixture(approve: false);
        var model = WorkflowFixture.WholeJob(finalWords: "You did not allow it, so I did not send the message.");

        var (chunks, _) = await fixture.RunAsync(model);

        var results = Results(chunks);
        Assert.Equal(ToolResultStatus.Declined, results[2].Status);
        Assert.DoesNotContain(fixture.Messages.Calls, call => call.Tool == "send_message");
        Assert.Single(fixture.Confirmation.Shown);
    }

    [Theory]
    [InlineData(ConfirmationDecision.NoAnswer)]
    [InlineData(ConfirmationDecision.CouldNotAsk)]
    [InlineData(ConfirmationDecision.Declined)]
    public async Task AQuestionThatIsNotAnsweredWithAYesIsANo(ConfirmationDecision decision)
    {
        await using var fixture = new WorkflowFixture(decision: decision);

        var (chunks, _) = await fixture.RunAsync(WorkflowFixture.WholeJob());

        Assert.NotEqual(ToolResultStatus.Succeeded, Results(chunks)[2].Status);
        Assert.DoesNotContain(fixture.Messages.Calls, call => call.Tool == "send_message");
    }

    [Fact]
    public async Task ACalendarWithNoExamsSaysSoAndTheModelThenHasNothingToSend()
    {
        await using var fixture = new WorkflowFixture(eventsJson: WorkflowFixture.NoExamsJson);
        var model = new AgentLoopConnectedAppsTests.ScriptedModel(
            [AgentLoopConnectedAppsTests.Call("mcp_samplecalendar_list_events", """{"start":"2026-10-02","end":"2026-10-16"}""", "c1")],
            [AgentLoopConnectedAppsTests.Words("There are no exams in the next two weeks, so I did not message your brother.")]);

        var (chunks, session) = await fixture.RunAsync(model);

        var check = Json(Results(chunks)[0].OutputJson).GetProperty("exam_check");
        Assert.Equal(0, check.GetProperty("likely").GetArrayLength());
        Assert.Equal(0, check.GetProperty("possible").GetArrayLength());
        Assert.Equal(2, check.GetProperty("events_checked").GetInt32());
        Assert.Empty(fixture.Messages.Calls);
        Assert.Empty(fixture.Confirmation.Shown);
        Assert.Contains("did not message", session.Conversation.Messages[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTwoSavedPeopleAreBrothersNothingIsDraftedOrSentAndTheModelIsToldToAsk()
    {
        await using var fixture = new WorkflowFixture(people: [WorkflowFixture.Brother("Omar"), WorkflowFixture.Brother("Sami", "+1 555 0177")]);

        var (chunks, _) = await fixture.RunAsync(WorkflowFixture.WholeJob());

        var results = Results(chunks);
        Assert.Equal("needs_clarification", Json(results[1].OutputJson).GetProperty("status").GetString());
        Assert.Contains("Do not choose", results[1].OutputJson, StringComparison.Ordinal);
        Assert.Empty(fixture.Messages.Calls);
        Assert.Equal("needs_clarification", Json(results[2].OutputJson).GetProperty("status").GetString());
        Assert.Empty(fixture.Confirmation.Shown);
    }

    [Fact]
    public async Task WhenNoBrotherIsSavedNothingIsMessagedAndNoNumberIsMadeUp()
    {
        await using var fixture = new WorkflowFixture(people: [Person.Create("Anna", WorkflowFixture.Start)]);

        var (chunks, _) = await fixture.RunAsync(WorkflowFixture.WholeJob());

        var results = Results(chunks);
        Assert.Equal("person_not_found", Json(results[1].OutputJson).GetProperty("status").GetString());
        Assert.Empty(fixture.Messages.Calls);
        Assert.Empty(fixture.Confirmation.Shown);
    }

    [Fact]
    public async Task AGroupThatTheBrotherIsInIsNeverTheChatTheMessageGoesTo()
    {
        const string onlyTheGroup =
            """{"chats":[{"id":"chat-family","title":"Family","network":"Messages","type":"group","participants":[{"phone":"+1 555 0100"},{"phone":"+1 555 0123"},{"phone":"+1 555 0177"}]}]}""";
        await using var fixture = new WorkflowFixture(chatsJson: onlyTheGroup);

        var (chunks, _) = await fixture.RunAsync(WorkflowFixture.WholeJob());

        var results = Results(chunks);
        Assert.Equal(ToolResultStatus.Failed, results[1].Status);
        Assert.Contains("no chat with one person named like Omar", results[1].OutputJson, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Messages.Calls, call => call.Tool == "send_message");
        Assert.Empty(fixture.Confirmation.Shown);
    }

    [Fact]
    public async Task TheMessagingAppsOwnSendingToolIsNotTheModelsToCall_EvenIfItWritesItsName()
    {
        await using var fixture = new WorkflowFixture();
        var model = new AgentLoopConnectedAppsTests.ScriptedModel(
            [AgentLoopConnectedAppsTests.Call("mcp_samplemessages_send_message", """{"chat_id":"chat-family","text":"hello everyone"}""", "c1")],
            [AgentLoopConnectedAppsTests.Words("I could not.")]);

        var (chunks, _) = await fixture.RunAsync(model);

        var result = Assert.Single(Results(chunks));
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.UnknownTool, code);
        Assert.Empty(fixture.Messages.Calls);
        Assert.Empty(fixture.Confirmation.Shown);
    }

    [Fact]
    public async Task WithNoMessagingAppConnectedTheMessagingToolsAreNotOfferedAndNothingIsStarted()
    {
        await using var fixture = new WorkflowFixture(messagesApp: false);
        var model = new AgentLoopConnectedAppsTests.ScriptedModel(
            [AgentLoopConnectedAppsTests.Call("mcp_samplecalendar_list_events", """{"start":"2026-10-02","end":"2026-10-16"}""", "c1")],
            [AgentLoopConnectedAppsTests.Words("I read your calendar but have no messaging app to tell your brother with.")]);

        await fixture.RunAsync(model);

        var offered = model.Requests[0].Tools.Select(tool => tool.Name).ToList();
        Assert.Contains("mcp_samplecalendar_list_events", offered);
        Assert.DoesNotContain("draft_message", offered);
        Assert.DoesNotContain("send_message", offered);
        Assert.DoesNotContain("check their calendar and then message someone", model.Requests[0].Instructions!, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Messages.ConnectCalls);
    }

    [Fact]
    public async Task WhileMessagingIsNotAllowedTheMessagingAppIsTheGenericRoutesAndItsToolsAreOffered()
    {
        await using var fixture = new WorkflowFixture(messagingAllowed: false);
        var model = new AgentLoopConnectedAppsTests.ScriptedModel([AgentLoopConnectedAppsTests.Words("Hi.")]);

        await fixture.RunAsync(model);

        // The Assistant does not send messages itself, so the app's own tools are the model's to use (and each is asked about first).
        var offered = model.Requests[0].Tools.Select(tool => tool.Name).ToList();
        Assert.DoesNotContain("draft_message", offered);
        Assert.DoesNotContain("send_message", offered);
        Assert.Contains("mcp_samplemessages_send_message", offered);
    }

    [Fact]
    public async Task ASendOfADraftOfAnotherMessageIsNotPossible_TheSameDraftIsMadeAgainForEveryCall()
    {
        await using var fixture = new WorkflowFixture();
        var model = new AgentLoopConnectedAppsTests.ScriptedModel(
            [AgentLoopConnectedAppsTests.Call("draft_message", WorkflowFixture.Json(new { recipient = "my brother", text = "first text" }), "c1")],
            [AgentLoopConnectedAppsTests.Call("send_message", WorkflowFixture.Json(new { recipient = "my brother", text = "different text" }), "c2")],
            [AgentLoopConnectedAppsTests.Words("Sent.")]);

        var (_, _) = await fixture.RunAsync(model);

        // What was approved is the call that was made: the text of the send, shown to the user, is the text that went out.
        var shown = Assert.Single(fixture.Confirmation.Shown);
        Assert.Contains(shown.Details, line => line.Label == "Message" && line.Value == "different text");
        var sent = Assert.Single(fixture.Messages.Calls, call => call.Tool == "send_message");
        Assert.Contains("different text", sent.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("first text", sent.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWholeJobRunsAsWellOverTheMadeUpProvidersWithNoConnectedApps()
    {
        var calendar = new InMemoryCalendarProvider(
        [
            new CalendarEvent { Title = "Dentist", Start = WorkflowFixture.Start.AddDays(1), End = WorkflowFixture.Start.AddDays(1).AddHours(1) },
            new CalendarEvent { Title = "Physics final exam", Start = WorkflowFixture.Start.AddDays(5), End = WorkflowFixture.Start.AddDays(5).AddHours(2), Location = "Hall B" },
            new CalendarEvent { Title = "Exam review session", Start = WorkflowFixture.Start.AddDays(4), End = WorkflowFixture.Start.AddDays(4).AddHours(1) },
            new CalendarEvent { Title = "Calculus midterm", Start = WorkflowFixture.Start.AddDays(9), End = WorkflowFixture.Start.AddDays(9).AddHours(2) },
        ]);
        var mock = new MockMessagingProvider();
        await using var fixture = new WorkflowFixture(calendarApp: false, messagesApp: false, calendarProvider: calendar, messagingProvider: mock);
        var model = new AgentLoopConnectedAppsTests.ScriptedModel(
            [AgentLoopConnectedAppsTests.Call("get_calendar_events", """{"start":"2026-10-02","end":"2026-10-16"}""", "c1")],
            [AgentLoopConnectedAppsTests.Call("draft_message", WorkflowFixture.Json(new { recipient = "my brother", text = WorkflowFixture.Reminder }), "c2")],
            [AgentLoopConnectedAppsTests.Call("send_message", WorkflowFixture.Json(new { recipient = "my brother", text = WorkflowFixture.Reminder }), "c3")],
            [AgentLoopConnectedAppsTests.Words("Done.")]);

        var (chunks, _) = await fixture.RunAsync(model);

        var results = Results(chunks);
        Assert.All(results, result => Assert.True(result.Status == ToolResultStatus.Succeeded, result.OutputJson));
        var events = Json(results[0].OutputJson).GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(["possible", "likely", "likely"], events.Where(item => item.TryGetProperty("exam", out _)).Select(item => item.GetProperty("exam").GetString()));
        var message = Assert.Single(mock.Sent);
        Assert.Equal("Omar", message.Message.Recipient.DisplayName);
        Assert.Equal(WorkflowFixture.Reminder, message.Message.Text);

        // The route is looked up again for the send (a send never relies on a draft from an earlier call), and nothing is sent by a draft.
        Assert.Equal(2, mock.Drafted.Count);
        Assert.Single(fixture.Confirmation.Shown);
    }
}
