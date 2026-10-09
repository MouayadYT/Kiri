using Assistant.Core.Budgeting;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Messaging;
using Assistant.Core.Orchestration;
using Assistant.Core.People;
using Assistant.Core.Tools;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>What the model is told for a request that joins the calendar to a message (PROJECT_SPEC §4.8, step 116), and what the pieces that live in Core say.</summary>
public sealed class CalendarWorkflowGuidanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static ToolDefinition Tool(string name, string description = "A tool.") => new(name, description, """{"type":"object"}""", RiskLevel.ReadOnly);

    private static string SystemOf(params ToolDefinition[] tools)
    {
        var conversation = new List<Message> { new(Guid.NewGuid(), MessageRole.User, "Check my calendar for exams in the next two weeks and message my brother", Now) };
        return new PromptBuilder(new ContextService(new ContextBudgeter(new HeuristicTokenEstimator())), new FixedClock(Now))
            .Build(null, conversation, new ModelInfo("test-model", 8192) { SupportsToolCalling = true }, tools: tools).Request.Instructions;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static readonly ToolDefinition ConnectedEvents = Tool("mcp_samplecalendar_list_events", "[Sample Calendar] Lists the events between a start and an end.");

    [Fact]
    public void TheDaysAModelMiscountsAreWorkedOutForIt()
    {
        var text = AssistantInstructions.DateReferences(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));

        Assert.Contains("tomorrow is 2026-10-04", text, StringComparison.Ordinal);
        Assert.Contains("a week from today is 2026-10-10", text, StringComparison.Ordinal);
        Assert.Contains("two weeks from today is 2026-10-17", text, StringComparison.Ordinal);
        Assert.Contains("a month from today is 2026-11-03", text, StringComparison.Ordinal);
        Assert.Contains("The next two weeks", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFortnightIsCountedAcrossTheEndOfAMonthAndAYear()
    {
        Assert.Contains("two weeks from today is 2026-11-07", AssistantInstructions.DateReferences(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero)), StringComparison.Ordinal);
        Assert.Contains("two weeks from today is 2027-01-04", AssistantInstructions.DateReferences(new DateTimeOffset(2026, 12, 21, 9, 0, 0, TimeSpan.Zero)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuiltInCalendarGuidanceNowCarriesTheDaysWorkedOut()
    {
        var text = AssistantInstructions.CalendarToolsGuidance(Now);

        Assert.Contains("two weeks from today is 2026-10-17", text, StringComparison.Ordinal);
        Assert.Contains("Today is Saturday 2026-10-03", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mcp_samplecalendar_list_events", "[Sample Calendar] Lists things.", true)]
    [InlineData("mcp_gcal_search", "[Google Calendar] Finds events by words.", true)]
    [InlineData("mcp_agendaapp_today", "[Agenda] Reads the agenda.", true)]
    [InlineData("mcp_todoist_create_task", "[Todoist] Creates a task.", false)]
    [InlineData("mcp_notes_list_notes", "[Notes] Lists the notes.", false)]
    [InlineData("get_calendar_events", "Reads the calendar.", false)]
    [InlineData("calculate", "Calculates events of numbers.", false)]
    public void AConnectedAppsCalendarReaderIsKnownByTheWordsOfItsNameAndDescription(string name, string description, bool expected)
    {
        Assert.Equal(expected, CalendarToolResults.IsConnectedCalendarReader(Tool(name, description)));
    }

    [Fact]
    public void AConnectedCalendarIsToldTodaysDateTheDaysWorkedOutAndWhatTheExamCheckIs()
    {
        var text = SystemOf(ConnectedEvents);

        Assert.Contains("A connected calendar app can read the user's calendar", text, StringComparison.Ordinal);
        Assert.Contains("Today is Saturday 2026-10-03", text, StringComparison.Ordinal);
        Assert.Contains("two weeks from today is 2026-10-17", text, StringComparison.Ordinal);
        Assert.Contains("exam_check", text, StringComparison.Ordinal);
        Assert.Contains("It is a hint", text, StringComparison.Ordinal);
        Assert.Contains("never make up an event", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("get_calendar_events", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuiltInCalendarToolsHaveTheirOwnGuidanceAndNotTheConnectedOne()
    {
        var text = SystemOf(Tool("get_calendar_events"));

        Assert.Contains("get_calendar_events", text, StringComparison.Ordinal);
        Assert.DoesNotContain("A connected calendar app can read", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOrderToDoItInIsToldOnlyWhenTheModelCanReadACalendarAndMessage()
    {
        var both = SystemOf(ConnectedEvents, Tool("draft_message"), Tool("send_message"));
        var builtInBoth = SystemOf(Tool("get_calendar_events"), Tool("draft_message"), Tool("send_message"));
        var calendarOnly = SystemOf(ConnectedEvents);
        var messagingOnly = SystemOf(Tool("draft_message"), Tool("send_message"));
        var neither = SystemOf(Tool("calculate"));

        Assert.Contains("check their calendar and then message someone", both, StringComparison.Ordinal);
        Assert.Contains("check their calendar and then message someone", builtInBoth, StringComparison.Ordinal);
        Assert.DoesNotContain("check their calendar and then message someone", calendarOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("check their calendar and then message someone", messagingOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("check their calendar and then message someone", neither, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOrderSaysReadChooseDraftSendAndSayWhatHappenedAndNeverToMessageWhenThereIsNothingToSay()
    {
        var text = AssistantInstructions.CalendarMessageWorkflowGuidance;

        Assert.Contains("(1) Read the events", text, StringComparison.Ordinal);
        Assert.Contains("exams", text, StringComparison.Ordinal);
        Assert.Contains("likely", text, StringComparison.Ordinal);
        Assert.Contains("If there are none, tell the user and do not message anyone", text, StringComparison.Ordinal);
        Assert.Contains("draft_message", text, StringComparison.Ordinal);
        Assert.Contains("my brother", text, StringComparison.Ordinal);
        Assert.Contains("taken from the calendar and nothing else", text, StringComparison.Ordinal);
        Assert.Contains("send_message with the same person and exactly the same text", text, StringComparison.Ordinal);
        Assert.Contains("If they did not allow it, say it was not sent", text, StringComparison.Ordinal);
        Assert.Contains("ask the user which one", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AUserWhoAskedForTheMessageIsNotAskedInWordsAsWellAsByTheQuestion()
    {
        var text = AssistantInstructions.MessagingToolsGuidance;

        Assert.Contains("that is how they decide, so never ask them in words whether to send it", text, StringComparison.Ordinal);
        Assert.Contains("If they did not say what the message should say, ask them that", text, StringComparison.Ordinal);
        Assert.Contains("only when the user asks for a draft", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ADraftTellsTheModelToSendWhenTheUserAskedForItAndNotToAskInWords()
    {
        var recipient = new MessageRecipient(Guid.NewGuid(), "Omar", [new(PersonIdentifierKind.Phone, "+1 555 0100")]);
        var draft = new MessageDraft(new OutgoingMessage(recipient, "hi"), "Messages chat with Omar", "ref");

        var note = MessagingToolResults.Drafted("Your messaging app", isSample: false, draft);

        Assert.Contains("If the user asked you to send this message, call send_message now", note, StringComparison.Ordinal);
        Assert.Contains("do not ask them in words as well", note, StringComparison.Ordinal);
        Assert.Contains("ask whether to send it", note, StringComparison.Ordinal);
    }

    [Fact]
    public void ADraftThatCameFromASampleAppIsSaidToBeASampleWhateverTheProviderIs()
    {
        var recipient = new MessageRecipient(Guid.NewGuid(), "Omar", []);
        var draft = new MessageDraft(new OutgoingMessage(recipient, "hi"), "Messages chat with Omar", "ref", IsSample: true);

        Assert.True(draft.IsSample);
        Assert.False(new MessageDraft(new OutgoingMessage(recipient, "hi"), "route", "ref").IsSample);
    }

    [Fact]
    public async Task TheMessagingProviderIsThereByDefaultAndTheNewFailureIsOneMoreCase()
    {
        Assert.True(await ((IMessagingProvider)new DefaultProvider()).IsAvailableAsync());
        Assert.Equal(5, (int)MessagingFailure.NotConnected);
        Assert.Equal(MessagingFailure.NotConnected, new MessagingProviderException(MessagingFailure.NotConnected).Failure);
    }

    private sealed class DefaultProvider : IMessagingProvider
    {
        public string Name => "x";

        public bool IsSample => false;

        public Task<MessageDraft> CreateDraftAsync(OutgoingMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessageSendResult> SendAsync(MessageDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("mom", true)]
    [InlineData("brother", true)]
    [InlineData("bro", true)]
    [InlineData("wifey", true)]
    [InlineData("colleague", false)]
    [InlineData("", false)]
    public void ARelationshipWordIsKnownWhenItIsOnTheList(string word, bool expected)
    {
        Assert.Equal(expected, RelationshipTerms.IsKnown(word));
    }
}
