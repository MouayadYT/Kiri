using Assistant.Core.Contracts;
using Assistant.Core.Tools;
using Assistant.Tools.Messaging;
using Assistant.Tools.Tests.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Workflow;

/// <summary>
/// While a message is being sent for the user, none of the messaging app's own tools is offered to the model beside send_message. With Beeper connected, "send a
/// message to my brother on iMessage" was answered about every other time by the model calling Beeper's own search for "brother" (0.1.146): the app's tools that
/// do not send were offered next to send_message, and a small model took the search. Asking the app for something else offers its tools as before.
/// </summary>
public sealed class MessagingSendFocusTests
{
    [Theory]
    [InlineData("send a message to my brother on imessage")]
    [InlineData("Send the message using iMessage")]
    [InlineData("send it")]
    [InlineData("text mom I'm running late")]
    [InlineData("message my brother asking what time he arrives")]
    [InlineData("tell Omar what we said about the last invoice")]
    [InlineData("can you please send a message to my sister")]
    [InlineData("hey kiri, text my brother and ask him when he's coming")]
    [InlineData("ok now reply to mom saying yes")]
    [InlineData("dm sara hello")]
    [InlineData("I want to send a message to my brother")]
    [InlineData("I'd like to write to my brother that I'm late")]
    [InlineData("let my mom know I'm on my way")]
    [InlineData("draft a message to dad")]
    public void ARequestThatAsksForAMessageToBeSentIsOne(string request) => Assert.True(MessagingRequests.IsSending(request));

    [Theory]
    [InlineData("what did my brother send me")]
    [InlineData("search my messages for the dinner plans")]
    [InlineData("show me the last message from mom")]
    [InlineData("message from mom?")]
    [InlineData("tell me what my brother said")]
    [InlineData("send me the latest messages from my sister")]
    [InlineData("did my brother reply")]
    [InlineData("archive the chat with Omar")]
    [InlineData("imessage")]
    [InlineData("yes")]
    [InlineData("what's the weather")]
    [InlineData("")]
    [InlineData(null)]
    public void ARequestThatAsksToLookOrAsksTheAssistantIsNot(string? request) => Assert.False(MessagingRequests.IsSending(request));

    [Theory]
    [InlineData("search my messages for the dinner plans", true)]
    [InlineData("what did my brother say", true)]
    [InlineData("send a message to my brother asking what time he arrives", false)]
    [InlineData("imessage", false)]
    public void LookingIsToldFromSending(string request, bool looking) => Assert.Equal(looking, MessagingRequests.IsLooking(request));

    // The tools Beeper has beside the two the Assistant sends with: the ones a model reached for.
    private static WorkflowFixture WithAMessagingAppThatHasMoreTools()
    {
        var fixture = new WorkflowFixture(calendarApp: false);
        fixture.Messages.Tools.Add(Sample.Tool(
            "search", "Search for chats, participants and messages in one call.",
            """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""", readOnly: true));
        fixture.Messages.Tools.Add(Sample.Tool(
            "search_messages", "Search messages across chats.",
            """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""", readOnly: true));
        fixture.Messages.Tools.Add(Sample.Tool(
            "list_messages", "List the messages of a chat.",
            """{"type":"object","properties":{"chatID":{"type":"string"}},"required":["chatID"]}""", readOnly: true));
        return fixture;
    }

    private static async Task<List<string>> OfferedAsync(WorkflowFixture fixture, Guid conversation, string request)
    {
        var context = new ToolContext(conversation, request);
        await fixture.Tools.PrepareToolsAsync(context);
        return [.. fixture.Tools.ToolsFor(context).Select(tool => tool.Name)];
    }

    [Theory]
    [InlineData("send a message to my brother on imessage")]
    [InlineData("Send the message using iMessage")]
    [InlineData("text my brother that the messages search is broken")]
    public async Task ForAMessageToSend_NoneOfTheAppsToolsIsOffered_OnlyTheAssistantsOwn(string request)
    {
        await using var fixture = WithAMessagingAppThatHasMoreTools();

        var offered = await OfferedAsync(fixture, Guid.NewGuid(), request);

        Assert.Contains("send_message", offered);
        Assert.DoesNotContain(offered, name => ConnectedAppTools.IsConnectedAppTool(name));
    }

    [Fact]
    public async Task AskedToLookSomethingUpInTheApp_ItsToolsAreOfferedAsBefore_WithoutTheOnesThatSend()
    {
        await using var fixture = WithAMessagingAppThatHasMoreTools();

        var offered = await OfferedAsync(fixture, Guid.NewGuid(), "search my messages for the dinner plans");

        var apps = offered.Where(ConnectedAppTools.IsConnectedAppTool).ToList();
        Assert.Contains(apps, name => name.EndsWith("search_messages", StringComparison.Ordinal));
        Assert.DoesNotContain(apps, name => name.EndsWith("send_message", StringComparison.Ordinal));
        Assert.DoesNotContain(apps, name => name.EndsWith("search_chats", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheUsersAnswersOnTheWayStayInIt_AndAskingToLookEndsIt()
    {
        await using var fixture = WithAMessagingAppThatHasMoreTools();
        var conversation = Guid.NewGuid();

        // Words that name the app and ask for nothing: in a conversation of their own they bring the app's tools, as before.
        const string answer = "the sample messages one";
        Assert.Contains(await OfferedAsync(fixture, Guid.NewGuid(), answer), ConnectedAppTools.IsConnectedAppTool);

        // As the answer to "which chat?", while a message is being sent, they do not.
        await OfferedAsync(fixture, conversation, "send a message to my brother saying hi");
        Assert.DoesNotContain(await OfferedAsync(fixture, conversation, answer), ConnectedAppTools.IsConnectedAppTool);

        // The user asks to look at something instead: that is a request of its own, and what follows it is too.
        Assert.Contains(await OfferedAsync(fixture, conversation, "search my messages for the dinner plans"), ConnectedAppTools.IsConnectedAppTool);
        Assert.Contains(await OfferedAsync(fixture, conversation, answer), ConnectedAppTools.IsConnectedAppTool);
    }

    [Fact]
    public async Task OnceTheMessageHasGone_TheConversationIsNoLongerSendingOne()
    {
        var conversation = Guid.NewGuid();
        var asked = new ToolContext(conversation, "send a message to my brother saying hi");
        MessagingFlows.Note(asked);
        var after = new ToolContext(conversation, "thanks");

        Assert.True(MessagingFlows.IsSending(after));
        MessagingFlows.End(conversation);

        Assert.False(MessagingFlows.IsSending(after));
        Assert.True(MessagingFlows.IsSending(asked));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WhileMessagingIsNotAllowed_TheAppsToolsAreItsOwnToOffer()
    {
        // The Assistant does not send for the user then, so nothing of the app is kept from the model, whatever is asked.
        await using var fixture = new WorkflowFixture(calendarApp: false, messagingAllowed: false);

        var offered = await OfferedAsync(fixture, Guid.NewGuid(), "send a message to my brother on imessage");

        Assert.Contains(offered, name => name.EndsWith("send_message", StringComparison.Ordinal) && ConnectedAppTools.IsConnectedAppTool(name));
    }
}
