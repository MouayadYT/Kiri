using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Memory;
using Assistant.Core.Messaging;
using Assistant.Core.People;
using Assistant.Core.Tools;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Messaging;
using Assistant.Tools.Messaging.ConnectedApps;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using OutgoingMessage = Assistant.Core.Messaging.OutgoingMessage;

namespace Assistant.Tools.Tests.Workflow;

/// <summary>
/// A person with more than one chat (a Beeper user reached on iMessage and on Beeper itself): the user is asked which once, their answer chooses the chat, and the
/// chat a message was sent through is remembered, so that the next message to that person goes there without a question.
/// </summary>
public sealed class McpMessagingRouteTests
{
    private const string TwoChats =
        """
        {"chats":[
          {"id":"imsg-1","title":"Sami","network":"iMessage","type":"single","participants":[{"id":"+15550100"}]},
          {"id":"!room:beeper.local","title":"Sami","network":"Beeper","type":"single","participants":[{"id":"@sami:beeper.com"}]}]}
        """;

    private static readonly MessageRecipient Sami = new(Guid.NewGuid(), "Sami", [new PersonIdentifier(PersonIdentifierKind.ChatName, "Sami")]);

    private sealed class Setup : IAsyncDisposable
    {
        public Setup(Func<string, string> search)
        {
            Client = WorkflowFixture.MessagesClient("{}");
            Client.OnCall = (tool, arguments, _) => Task.FromResult(Sample.Text(
                tool.Name == "search_chats" ? search(arguments.GetProperty("query").GetString() ?? string.Empty) : """{"status":"sent"}"""));
            var registry = new InstalledIntegrationRegistry(
                new MemoryIntegrationStore([WorkflowFixture.MessagesIntegration(isSample: false)]), NullLogger<InstalledIntegrationRegistry>.Instance);
            Manager = new McpConnectionManager(
                registry, new StubClientFactory(_ => Client), TestSettings.LocalOnly(false), TimeProvider.System, new McpLoadingOptions(),
                NullLogger<McpConnectionManager>.Instance, cache: null);
            Provider = new McpMessagingProvider(registry, Manager, new FakePermissions(true), NullLogger<McpMessagingProvider>.Instance, Memory);
        }

        public StubMcpClient Client { get; }

        public InMemoryMemoryStore Memory { get; } = new();

        // What the user said they prefer, which is what these tests are about; which chat is a person's on each service is kept beside it (see the last tests).
        public List<MemoryEntry> Preferred => [.. Memory.Entries.Where(entry => entry.Kind == MemoryKind.MessageRoute)];

        public List<MemoryEntry> Chats => [.. Memory.Entries.Where(entry => entry.Kind == MemoryKind.MessageChat)];

        public McpConnectionManager Manager { get; }

        public McpMessagingProvider Provider { get; }

        public List<string> Queries => [.. Client.Calls.Where(call => call.Tool == "search_chats").Select(call => JsonDocument.Parse(call.Arguments).RootElement.GetProperty("query").GetString()!)];

        public List<string> SentTo => [.. Client.Calls.Where(call => call.Tool == "send_message").Select(call => JsonDocument.Parse(call.Arguments).RootElement.GetProperty("chat_id").GetString()!)];

        public ValueTask DisposeAsync() => Manager.DisposeAsync();
    }

    private static OutgoingMessage To(MessageRecipient person, string via = "", bool own = true) => new(person, "On my way") { Via = via, ViaIsUsersOwn = own };

    [Fact]
    public async Task TwoChatsAreAQuestionThatNamesTheServicesTheUserCanAnswerWith()
    {
        await using var setup = new Setup(_ => TwoChats);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(To(Sami), CancellationToken.None));

        Assert.Equal(MessagingFailure.Ambiguous, failure.Failure);
        Assert.Equal(["Beeper", "iMessage"], failure.Options.Order());
    }

    [Theory]
    [InlineData("Beeper", "!room:beeper.local", "Beeper")]
    [InlineData("through beeper", "!room:beeper.local", "Beeper")]
    [InlineData("iMessage", "imsg-1", "iMessage")]
    [InlineData("use imessage please", "imsg-1", "iMessage")]
    [InlineData("@sami:beeper.com", "!room:beeper.local", "Beeper")]
    public async Task TheUsersAnswerChoosesTheChat_ByItsServiceOrByAHandleInIt(string via, string chat, string service)
    {
        await using var setup = new Setup(_ => TwoChats);

        var draft = await setup.Provider.CreateDraftAsync(To(Sami, via), CancellationToken.None);

        Assert.Contains(chat, draft.Reference, StringComparison.Ordinal);
        Assert.Equal(service, draft.Service);
        Assert.Equal("Sample Messages", draft.App);
        Assert.Empty(setup.SentTo);
    }

    [Fact]
    public async Task AnAnswerThatFitsNoneOfTheChatsIsStillAQuestion()
    {
        await using var setup = new Setup(_ => TwoChats);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(To(Sami, "the blue one"), CancellationToken.None));

        Assert.Equal(MessagingFailure.Ambiguous, failure.Failure);
    }

    [Fact]
    public async Task AServiceThePersonHasNoChatOnIsSaid_WithTheServicesTheirChatsAreOn_AndNothingGoesThroughAnother()
    {
        await using var setup = new Setup(_ => TwoChats);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(To(Sami, "WhatsApp"), CancellationToken.None));

        Assert.Equal(MessagingFailure.ServiceNotFound, failure.Failure);
        Assert.Equal("WhatsApp", failure.Service);
        Assert.Equal(["Beeper", "iMessage"], failure.Options.Order());
        Assert.Empty(setup.SentTo);
    }

    // What the user's own Beeper looked like: the chat on Beeper is called by the person's first name, and the one on iMessage by their whole name, or it is
    // archived, or the handle saved for them is a Beeper one. The likeliest chat is the Beeper one, and "on iMessage" still finds the other.
    [Theory]
    [InlineData("""{"id":"imsg-1","title":"Sami Haddad","network":"iMessage","type":"single"}""", "")]
    [InlineData("""{"id":"imsg-1","title":"Sami","network":"iMessage","type":"single","isArchived":true}""", "")]
    [InlineData("""{"id":"imsg-1","title":"Sami","network":"iMessage","type":"single"}""", "@sami:beeper.com")]
    public async Task AServiceTheUserNamesIsLookedForAmongAllOfThePersonsChats_NotOnlyTheLikeliest(string onIMessage, string handle)
    {
        var chats = """{"chats":[""" + onIMessage + """,{"id":"!room:beeper.local","title":"Sami","network":"Beeper","type":"single","participants":[{"id":"@sami:beeper.com"}]}]}""";
        await using var setup = new Setup(_ => chats);
        var person = handle.Length == 0
            ? Sami
            : new MessageRecipient(Guid.NewGuid(), "Sami", [new PersonIdentifier(PersonIdentifierKind.Username, handle)]);

        var draft = await setup.Provider.CreateDraftAsync(To(person, "iMessage"), CancellationToken.None);

        Assert.Contains("imsg-1", draft.Reference, StringComparison.Ordinal);
        Assert.Equal("iMessage", draft.Service);
    }

    [Fact]
    public async Task TheServiceChosenInSettingsChoosesTheChat_AndAnotherNamedNowStillWins()
    {
        await using var setup = new Setup(_ => TwoChats);
        await setup.Memory.SaveAsync(new MemoryEntry(Guid.NewGuid(), MemoryKind.MessageRoute, "Messages to Sami go through iMessage.", DateTimeOffset.UtcNow)
        {
            Key = Sami.PersonId.ToString("N"),
            Value = Assistant.Tools.Messaging.MessageRoutes.ForService("iMessage"),
        });

        var chosen = await setup.Provider.CreateDraftAsync(To(Sami), CancellationToken.None);
        var named = await setup.Provider.CreateDraftAsync(To(Sami, "Beeper"), CancellationToken.None);

        Assert.Equal("iMessage", chosen.Service);
        Assert.Equal("Beeper", named.Service);
    }

    [Fact]
    public async Task SendingThroughAChatKeepsNothing_TheChatTheUserSaysTheyPreferIsKept_AndUsedNextTimeWithoutAQuestion()
    {
        await using var setup = new Setup(_ => TwoChats);
        var draft = await setup.Provider.CreateDraftAsync(To(Sami, "Beeper"), CancellationToken.None);

        var sent = await setup.Provider.SendAsync(draft, CancellationToken.None);

        // A chat named for one message is not the one preferred: the next message that names none is a question again.
        Assert.Equal(("Beeper", "Sample Messages"), (sent.Service, sent.App));
        Assert.Empty(setup.Preferred);
        var asked = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(To(Sami), CancellationToken.None));
        Assert.Equal(MessagingFailure.Ambiguous, asked.Failure);

        // The user says it is the one they prefer: it is kept, under the person, in words Settings shows.
        Assert.True(await setup.Provider.PreferAsync(draft));
        var kept = Assert.Single(setup.Preferred);
        Assert.Equal(MemoryKind.MessageRoute, kept.Kind);
        Assert.Equal(Sami.PersonId.ToString("N"), kept.Key);
        Assert.Equal("Messages to Sami go through Beeper in Sample Messages.", kept.Text);

        // The next message names no chat: the preferred one is used, though the person still has two.
        var next = await setup.Provider.CreateDraftAsync(To(Sami), CancellationToken.None);
        await setup.Provider.SendAsync(next, CancellationToken.None);

        Assert.Equal(["!room:beeper.local", "!room:beeper.local"], setup.SentTo);
        Assert.Single(setup.Preferred);
    }

    [Fact]
    public async Task AnotherServiceNamedForOneMessageIsUsedForThatMessage_AndThePreferredOneStays()
    {
        await using var setup = new Setup(_ => TwoChats);
        var preferred = await setup.Provider.CreateDraftAsync(To(Sami, "Beeper"), CancellationToken.None);
        Assert.True(await setup.Provider.PreferAsync(preferred));

        await setup.Provider.SendAsync(await setup.Provider.CreateDraftAsync(To(Sami, "iMessage"), CancellationToken.None), CancellationToken.None);
        await setup.Provider.SendAsync(await setup.Provider.CreateDraftAsync(To(Sami), CancellationToken.None), CancellationToken.None);

        // "On iMessage" went through the iMessage chat once; the message after it, which named none, went through Beeper as before.
        Assert.Equal(["imsg-1", "!room:beeper.local"], setup.SentTo);
        Assert.Equal("Messages to Sami go through Beeper in Sample Messages.", Assert.Single(setup.Preferred).Text);
    }

    // The user as they answer the questions put to them on screen: each is written down, and answered by what it asks.
    private sealed class Answers(Func<ToolConfirmation, bool> yes) : IPermissionService
    {
        public List<string> Asked { get; } = [];

        public Task<ConfirmationDecision> ConfirmToolCallAsync(
            ToolDefinition tool, ToolCall call, ToolContext context, ToolConfirmation confirmation, CancellationToken cancellationToken = default)
        {
            Asked.Add(confirmation.Title);
            return Task.FromResult(yes(confirmation) ? ConfirmationDecision.Approved : ConfirmationDecision.Declined);
        }
    }

    // A conversation with the send tool over the provider, for a brother called Sami who has a chat on iMessage and one on Beeper.
    private sealed class Chat
    {
        private readonly Guid _conversation = Guid.NewGuid();
        private readonly ToolExecutor _executor;
        private readonly ToolRegistry _registry;
        private int _calls;

        public Chat(Setup setup, Answers? answers, bool asksAboutPreference = true)
        {
            var people = new InMemoryPersonStore();
            people.SaveAsync(Person.Create("Sami", DateTimeOffset.UtcNow) with
            {
                Relationships = ["brother"],
                Identifiers = [new PersonIdentifier(PersonIdentifierKind.ChatName, "Sami")],
            }).GetAwaiter().GetResult();
            var resolver = new PersonResolver(people);
            Answers = answers ?? new Answers(_ => true);
            ITool[] tools = [new DraftMessageTool(setup.Provider, resolver), new SendMessageTool(setup.Provider, resolver, null, asksAboutPreference ? Answers : null)];
            _executor = new ToolExecutor(tools, Answers, new FakePermissions(true));
            _registry = new ToolRegistry(tools);
        }

        public Answers Answers { get; }

        // The user says something, and the model calls send_message for it with what it was given.
        public async Task<ToolResult> SayAsync(string request, string text, string? via = null)
        {
            var context = new ToolContext(_conversation, request);
            await _registry.PrepareToolsAsync(context);
            var arguments = new Dictionary<string, string> { ["recipient"] = "my brother", ["text"] = text };
            if (via is not null)
            {
                arguments["via"] = via;
            }

            return await _executor.ExecuteAsync(new ToolCall("c" + ++_calls, "send_message", JsonSerializer.Serialize(arguments)), context);
        }
    }

    [Fact]
    public async Task AskedWhichChat_TheUsersAnswerIsAskedAboutOnce_AndAYesMakesItWhereTheirMessagesGo()
    {
        await using var setup = new Setup(_ => TwoChats);
        var chat = new Chat(setup, null);

        // "Send a message to my brother": he has two chats, so nothing is sent and the model is told to ask which, and not to ask about preferring it.
        var which = await chat.SayAsync("send a message to my brother saying hi", "hi");
        Assert.Equal(ToolResultStatus.Failed, which.Status);
        Assert.Contains("more than one chat", which.OutputJson, StringComparison.Ordinal);
        Assert.Contains("do not ask that yourself", which.OutputJson, StringComparison.Ordinal);
        Assert.Empty(chat.Answers.Asked);

        // "Beeper": first whether that is the one they prefer, then the message itself.
        var sent = await chat.SayAsync("Beeper", "hi", via: "Beeper");
        Assert.Equal(ToolResultStatus.Succeeded, sent.Status);
        Assert.Equal(["Is Beeper your preferred way to message Sami?", "Send this message to Sami?"], chat.Answers.Asked);
        Assert.Equal("Messages to Sami go through Beeper in Sample Messages.", Assert.Single(setup.Preferred).Text);

        // From then on a message that names no chat goes through Beeper, with only the message's own question.
        chat.Answers.Asked.Clear();
        Assert.Equal(ToolResultStatus.Succeeded, (await chat.SayAsync("send a message to my brother saying on my way", "on my way")).Status);
        Assert.Equal(["Send this message to Sami?"], chat.Answers.Asked);

        // "Using iMessage" goes through the iMessage chat for that message, is not asked about, and changes nothing that is kept.
        chat.Answers.Asked.Clear();
        Assert.Equal(ToolResultStatus.Succeeded, (await chat.SayAsync("send a message to my brother using iMessage saying here", "here")).Status);
        Assert.Equal(["Send this message to Sami?"], chat.Answers.Asked);
        Assert.Equal(ToolResultStatus.Succeeded, (await chat.SayAsync("send a message to my brother saying back", "back")).Status);

        Assert.Equal(["!room:beeper.local", "!room:beeper.local", "imsg-1", "!room:beeper.local"], setup.SentTo);
        Assert.Equal("Messages to Sami go through Beeper in Sample Messages.", Assert.Single(setup.Preferred).Text);
    }

    [Fact]
    public async Task ANoToPreferringItStillSendsTheMessage_KeepsNothing_AndTheUserIsAskedWhichAgainNextTime()
    {
        await using var setup = new Setup(_ => TwoChats);
        var chat = new Chat(setup, new Answers(question => !question.Title.StartsWith("Is ", StringComparison.Ordinal)));

        await chat.SayAsync("send a message to my brother saying hi", "hi");
        var sent = await chat.SayAsync("the iMessage one", "hi", via: "iMessage");

        Assert.Equal(ToolResultStatus.Succeeded, sent.Status);
        Assert.Equal(["Is iMessage your preferred way to message Sami?", "Send this message to Sami?"], chat.Answers.Asked);
        Assert.Equal(["imsg-1"], setup.SentTo);
        Assert.Empty(setup.Preferred);

        // Nothing was kept, so the next message that names no chat is a question again, and its answer is asked about again.
        chat.Answers.Asked.Clear();
        var again = await chat.SayAsync("send a message to my brother saying hello", "hello");
        Assert.Contains("more than one chat", again.OutputJson, StringComparison.Ordinal);
        await chat.SayAsync("beeper", "hello", via: "Beeper");
        Assert.Equal("Is Beeper your preferred way to message Sami?", chat.Answers.Asked[0]);
    }

    [Fact]
    public async Task AServiceNamedInTheRequestItselfIsNotAskedAbout_AndIsNotKept()
    {
        await using var setup = new Setup(_ => TwoChats);
        var chat = new Chat(setup, null);

        // The model passes nothing along: the service is read from what the user wrote.
        var sent = await chat.SayAsync("send a message to my brother through WhatsApp or rather on imessage saying hi", "hi", via: "iMessage");
        var plain = await chat.SayAsync("send a message to my brother on beeper saying hi", "hi");

        Assert.Equal(ToolResultStatus.Succeeded, sent.Status);
        Assert.Equal(ToolResultStatus.Succeeded, plain.Status);
        Assert.Equal(["Send this message to Sami?", "Send this message to Sami?"], chat.Answers.Asked);
        Assert.Equal(["imsg-1", "!room:beeper.local"], setup.SentTo);
        Assert.Empty(setup.Preferred);
    }

    [Fact]
    public async Task WithNothingToAskTheUserThrough_TheAnswerIsUsedForTheMessageAndNothingIsKept()
    {
        await using var setup = new Setup(_ => TwoChats);
        var chat = new Chat(setup, null, asksAboutPreference: false);

        await chat.SayAsync("send a message to my brother saying hi", "hi");
        var sent = await chat.SayAsync("Beeper", "hi", via: "Beeper");

        Assert.Equal(ToolResultStatus.Succeeded, sent.Status);
        Assert.Equal(["Send this message to Sami?"], chat.Answers.Asked);
        Assert.Empty(setup.Preferred);
    }

    [Fact]
    public async Task ADraftThatWasNotMadeHereCannotBePreferred()
    {
        await using var setup = new Setup(_ => TwoChats);
        var foreign = new MessageDraft(To(Sami), "Somewhere", "not a reference this provider made");

        Assert.False(await setup.Provider.PreferAsync(foreign));
        Assert.Empty(setup.Memory.Entries);
    }

    [Fact]
    public async Task ARememberedChatTheAppNoLongerFindsIsLookedForAgain_AndNothingIsSentToItBlind()
    {
        var chats = TwoChats;
        await using var setup = new Setup(_ => chats);
        Assert.True(await setup.Provider.PreferAsync(await setup.Provider.CreateDraftAsync(To(Sami, "Beeper"), CancellationToken.None)));

        // The Beeper chat is gone: only the other is left, and it is the one found.
        chats = """{"chats":[{"id":"imsg-1","title":"Sami","network":"iMessage","type":"single"}]}""";
        var draft = await setup.Provider.CreateDraftAsync(To(Sami), CancellationToken.None);

        Assert.Contains("imsg-1", draft.Reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHandleTheUserGaveFindsAChatThePersonsNameDidNot_AndOnlyAChatThatHoldsIt()
    {
        await using var setup = new Setup(query => query == "@momo:beeper.com"
            ? """{"chats":[{"id":"!momo:beeper.local","title":"Momo","network":"Beeper","type":"single","participants":[{"id":"@momo:beeper.com"}]},{"id":"other","title":"Someone","type":"single"}]}"""
            : """{"chats":[]}""");

        var draft = await setup.Provider.CreateDraftAsync(To(Sami, "@momo:beeper.com"), CancellationToken.None);

        Assert.Contains("!momo:beeper.local", draft.Reference, StringComparison.Ordinal);
        Assert.Equal("Beeper chat with Sami \"Momo\" in Sample Messages", draft.Route);
        Assert.Equal(["Sami", "@momo:beeper.com"], setup.Queries);

        // Sent once, it is where Sami's messages go: found again by the handle, with nobody asked.
        await setup.Provider.SendAsync(draft, CancellationToken.None);
        var next = await setup.Provider.CreateDraftAsync(To(Sami), CancellationToken.None);
        Assert.Contains("!momo:beeper.local", next.Reference, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("@momo:beeper.com", false)]
    [InlineData("Beeper", true)]
    [InlineData("the other one", true)]
    public async Task WordsThatAreNotTheUsersOwn_OrThatAreNoHandle_AreNeverLookedUpInTheApp(string via, bool own)
    {
        // A page or a file may put words in the model's mouth, and a plain word finds any chat that mentions it: neither adds someone to message.
        await using var setup = new Setup(query => query == "Sami"
            ? """{"chats":[]}"""
            : """{"chats":[{"id":"stranger","title":"Beeper Help","network":"Beeper","type":"single","participants":[{"id":"@momo:beeper.com"}]}]}""");

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(To(Sami, via, own), CancellationToken.None));

        Assert.Equal(MessagingFailure.RecipientNotFound, failure.Failure);
        Assert.Equal(["Sami"], setup.Queries);
    }

    private const string MarcusChats =
        """
        {"chats":[
          {"id":"imsg-9","title":"Marcus","network":"iMessage","type":"single","participants":[{"id":"+15550199"}]},
          {"id":"!marcus:beeper.local","title":"Marcus","network":"Beeper","type":"single","participants":[{"id":"@marcus:beeper.com"}]}]}
        """;

    [Fact]
    public async Task SomeoneReachedByAHandleIsLookedForByTheNameInIt_AndTheHandlesServiceChoosesAmongTheirChats()
    {
        // The app's search looks at what chats are called: the handle as it is written finds nothing, and the name in it finds both of the person's chats.
        await using var setup = new Setup(query => query.Contains('@', StringComparison.Ordinal) ? """{"chats":[]}""" : MarcusChats);
        var marcus = new MessageRecipient(
            Guid.NewGuid(), "Marcus",
            [new PersonIdentifier(PersonIdentifierKind.ChatName, "Marcus"), new PersonIdentifier(PersonIdentifierKind.Username, "@marcus:beeper.com", "Beeper")]);

        var draft = await setup.Provider.CreateDraftAsync(To(marcus), CancellationToken.None);

        Assert.Contains("!marcus:beeper.local", draft.Reference, StringComparison.Ordinal);
        Assert.Equal("Beeper", draft.Service);
        Assert.Contains("Marcus", setup.Queries);
    }

    [Fact]
    public async Task SomeoneWhoWasSavedWithAHandleForTheirNameIsStillFound()
    {
        // What 0.1.141 could leave in People: the handle as the person's name. The name in it is what their chat is called.
        await using var setup = new Setup(query => query.Contains('@', StringComparison.Ordinal) ? """{"chats":[]}""" : MarcusChats);
        var saved = new MessageRecipient(Guid.NewGuid(), "@marcus:beeper.com", [new PersonIdentifier(PersonIdentifierKind.ChatName, "@marcus:beeper.com")]);

        var draft = await setup.Provider.CreateDraftAsync(To(saved), CancellationToken.None);

        Assert.Contains("!marcus:beeper.local", draft.Reference, StringComparison.Ordinal);
        Assert.Contains(setup.Queries, query => string.Equals(query, "marcus", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(setup.SentTo);
    }

    // ---- Which chat is a person's on each service, learned when a message is sent there ------------------------------------------

    [Fact]
    public async Task OnceAMessageWentToTheirIMessageChat_OnIMessageGoesStraightToItAgain_FoundTheWayItWasFound()
    {
        await using var setup = new Setup(_ => TwoChats);

        // The first time, the person's chats are looked for and the one on iMessage is chosen among them.
        await setup.Provider.SendAsync(await setup.Provider.CreateDraftAsync(To(Sami, "iMessage"), CancellationToken.None), CancellationToken.None);

        var kept = Assert.Single(setup.Chats);
        Assert.Equal("On iMessage in Sample Messages, messages to Sami go to their chat.", kept.Text);
        Assert.StartsWith(Sami.PersonId.ToString("N") + ":", kept.Key, StringComparison.Ordinal);
        Assert.Empty(setup.Preferred);

        // From then on "on iMessage" is one lookup, by what found the chat then, however the user writes it.
        var before = setup.Queries.Count;
        var again = await setup.Provider.CreateDraftAsync(To(Sami, "use imessage please"), CancellationToken.None);

        Assert.Contains("imsg-1", again.Reference, StringComparison.Ordinal);
        Assert.Equal("iMessage", again.Service);
        Assert.Equal(["Sami"], setup.Queries.Skip(before));

        // It says nothing about which chat they prefer: a message that names none is still a question.
        var asked = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(To(Sami), CancellationToken.None));
        Assert.Equal(MessagingFailure.Ambiguous, asked.Failure);
    }

    [Fact]
    public async Task TheRememberedChatIsUsedEvenWhenLookingForThePersonWouldNoLongerSettleOnIt()
    {
        // What made "on iMessage" work only some of the time: the app's search answers a little differently from one time to the next.
        var chats = TwoChats;
        await using var setup = new Setup(_ => chats);
        await setup.Provider.SendAsync(await setup.Provider.CreateDraftAsync(To(Sami, "iMessage"), CancellationToken.None), CancellationToken.None);

        // Now the app also has a second iMessage chat that bears the name: looked for afresh, that would be a question.
        chats = TwoChats.Replace("]}", """,{"id":"imsg-2","title":"Sami","network":"iMessage","type":"single"}]}""", StringComparison.Ordinal);
        var draft = await setup.Provider.CreateDraftAsync(To(Sami, "iMessage"), CancellationToken.None);

        Assert.Contains("imsg-1", draft.Reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachServiceKeepsItsOwnChat_AndARememberedOneThatIsGoneOrForgottenIsLookedForAgain()
    {
        var chats = TwoChats;
        await using var setup = new Setup(_ => chats);
        await setup.Provider.SendAsync(await setup.Provider.CreateDraftAsync(To(Sami, "iMessage"), CancellationToken.None), CancellationToken.None);
        await setup.Provider.SendAsync(await setup.Provider.CreateDraftAsync(To(Sami, "Beeper"), CancellationToken.None), CancellationToken.None);

        Assert.Equal(
            ["On Beeper in Sample Messages, messages to Sami go to their chat.", "On iMessage in Sample Messages, messages to Sami go to their chat."],
            setup.Chats.Select(entry => entry.Text).Order());
        Assert.Equal(["imsg-1", "!room:beeper.local"], setup.SentTo);

        // The iMessage chat is replaced by another in the app: the one remembered is not found any more, and nothing is sent to it blind.
        chats = """{"chats":[{"id":"imsg-7","title":"Sami","network":"iMessage","type":"single"},{"id":"!room:beeper.local","title":"Sami","network":"Beeper","type":"single"}]}""";
        var found = await setup.Provider.CreateDraftAsync(To(Sami, "iMessage"), CancellationToken.None);
        Assert.Contains("imsg-7", found.Reference, StringComparison.Ordinal);

        // Forgotten in Settings, under Memory, it is looked for as if it had never been kept.
        foreach (var entry in setup.Chats)
        {
            await setup.Memory.DeleteAsync(entry.Id);
        }

        Assert.Contains("!room:beeper.local", (await setup.Provider.CreateDraftAsync(To(Sami, "Beeper"), CancellationToken.None)).Reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatIsKeptForAServiceIsNotToldToTheModelAsANote()
    {
        await using var setup = new Setup(_ => TwoChats);
        await setup.Provider.SendAsync(await setup.Provider.CreateDraftAsync(To(Sami, "iMessage"), CancellationToken.None), CancellationToken.None);

        Assert.Single(setup.Chats);
        Assert.Null(setup.Memory.ForPrompt());
    }

    [Theory]
    [InlineData("local-imessage_ba_AbC", "iMessage")]
    [InlineData("iMessage", "iMessage")]
    [InlineData("Beeper (Matrix)", "Beeper")]
    [InlineData("hungryserv", "Beeper")]
    [InlineData("local-whatsapp_ba_x1", "WhatsApp")]
    [InlineData("Threema", "Threema")]
    [InlineData("", "")]
    public void AServiceIsCalledWhatPeopleCallIt_HoweverTheAppWritesIt(string network, string name) =>
        Assert.Equal(name, MessagingServices.Name(network));

    [Theory]
    [InlineData("@sami:beeper.com", true)]
    [InlineData("sami.k", true)]
    [InlineData("+1 (555) 010-0199", true)]
    [InlineData("Beeper", false)]
    [InlineData("the second one", false)]
    [InlineData("a.b", false)]
    public void AHandleIsToldFromAPlainWord(string text, bool handle) =>
        Assert.Equal(handle, McpMessagingProvider.LooksLikeHandle(text));
}
