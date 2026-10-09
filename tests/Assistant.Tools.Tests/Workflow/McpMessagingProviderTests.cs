using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Messaging;
using Assistant.Core.People;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Messaging.ConnectedApps;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using OutgoingMessage = Assistant.Core.Messaging.OutgoingMessage;

namespace Assistant.Tools.Tests.Workflow;

/// <summary>
/// The Assistant's messaging through a connected messaging app (PROJECT_SPEC §4.8, step 116): it finds the person's one-to-one chat with the app's own search and never guesses, a draft
/// sends nothing, and a send is only of a draft this provider made, through the tool and to the chat the draft names, with the text it holds.
/// </summary>
public sealed class McpMessagingProviderTests
{
    private static readonly MessageRecipient Omar = new(
        Guid.NewGuid(), "Omar", [new PersonIdentifier(PersonIdentifierKind.Phone, "+1 555 0100", "Messages")]);

    private static OutgoingMessage Message(string text = "Hello Omar", MessageRecipient? to = null) => new(to ?? Omar, text);

    private sealed class Setup : IAsyncDisposable
    {
        public Setup(StubMcpClient client, InstalledIntegration? integration = null, bool permission = true, ILogger<McpMessagingProvider>? logger = null, params InstalledIntegration[] others)
        {
            Client = client;
            Integration = integration ?? WorkflowFixture.MessagesIntegration();
            Store = new MemoryIntegrationStore([Integration, .. others]);
            Registry = new InstalledIntegrationRegistry(Store, NullLogger<InstalledIntegrationRegistry>.Instance);
            Clients = new StubClientFactory(_ => Client);
            Manager = new McpConnectionManager(
                Registry, Clients, TestSettings.LocalOnly(false), TimeProvider.System, new McpLoadingOptions(), NullLogger<McpConnectionManager>.Instance, cache: null);
            Provider = new McpMessagingProvider(Registry, Manager, new FakePermissions(permission), logger ?? NullLogger<McpMessagingProvider>.Instance);
        }

        public StubMcpClient Client { get; }

        public InstalledIntegration Integration { get; }

        public MemoryIntegrationStore Store { get; }

        public InstalledIntegrationRegistry Registry { get; }

        public StubClientFactory Clients { get; }

        public McpConnectionManager Manager { get; }

        public McpMessagingProvider Provider { get; }

        public IEnumerable<(string Tool, string Arguments)> Sent => Client.Calls.Where(call => call.Tool == "send_message");

        public ValueTask DisposeAsync() => Manager.DisposeAsync();
    }

    private static Setup With(string chats = WorkflowFixture.ChatsJson, InstalledIntegration? integration = null, bool permission = true, ILogger<McpMessagingProvider>? logger = null) =>
        new(WorkflowFixture.MessagesClient(chats), integration, permission, logger);

    // ---- The Messaging permission, held by the service itself (step 119) --------------------------------------------------------

    [Fact]
    public async Task WhileMessagingIsNotAllowedNothingIsLookedUpInTheAppAndNothingIsSent()
    {
        await using var setup = With(permission: false);
        var draft = new MessageDraft(Message(), "route", "ref", true);

        var drafting = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(), CancellationToken.None));
        var sending = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.SendAsync(draft, CancellationToken.None));

        Assert.Equal(MessagingFailure.NotAllowed, drafting.Failure);
        Assert.Equal(MessagingFailure.NotAllowed, sending.Failure);
        Assert.Empty(setup.Client.Calls);
        Assert.Equal(0, setup.Clients.CreateCalls);
    }

    // ---- Drafting ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADraftFindsThePersonsOneToOneChatAndSendsNothing()
    {
        await using var setup = With();

        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);

        Assert.Equal("Messages chat with Omar in Sample Messages", draft.Route);
        Assert.True(draft.IsSample);
        Assert.Equal("Hello Omar", draft.Message.Text);
        Assert.Empty(setup.Sent);
        var search = Assert.Single(setup.Client.Calls, call => call.Tool == "search_chats");
        Assert.Contains("+1 555 0100", search.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReferenceOfADraftHoldsNeitherTheTextNorTheNumberAndIsNeverShown()
    {
        await using var setup = With();

        var draft = await setup.Provider.CreateDraftAsync(Message("a private text"), CancellationToken.None);

        Assert.DoesNotContain("a private text", draft.Reference, StringComparison.Ordinal);
        Assert.DoesNotContain("555", draft.Reference, StringComparison.Ordinal);
        Assert.DoesNotContain("a private text", draft.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Omar", draft.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADraftIsNotASampleWhenTheAppIsNot()
    {
        await using var setup = With(integration: WorkflowFixture.MessagesIntegration(isSample: false));

        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);

        Assert.False(draft.IsSample);
    }

    [Fact]
    public async Task AGroupThatThePersonIsInIsNeverTheChat()
    {
        await using var setup = With();

        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);

        Assert.DoesNotContain("family", draft.Route, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chat-omar", draft.Reference, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-family", draft.Reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyAGroupMeansNoChatWithThePerson()
    {
        await using var setup = With("""{"chats":[{"id":"g","title":"Family","type":"group","participants":[{"phone":"+1 555 0100"},{"phone":"2"},{"phone":"3"}]}]}""");

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(), CancellationToken.None));

        Assert.Equal(MessagingFailure.RecipientNotFound, failure.Failure);
    }

    [Fact]
    public async Task NoChatAtAllIsNotFound()
    {
        await using var setup = With("""{"chats":[]}""");

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(), CancellationToken.None));

        Assert.Equal(MessagingFailure.RecipientNotFound, failure.Failure);
    }

    [Fact]
    public async Task SeveralChatsAreAQuestionForTheUserAndNeverAChoice()
    {
        await using var setup = With(
            """{"chats":[{"id":"a","title":"Omar","network":"Messages","type":"single"},{"id":"b","title":"Omar K","network":"Signal","type":"single"}]}""");
        var either = new MessageRecipient(Guid.NewGuid(), "Omar", [new PersonIdentifier(PersonIdentifierKind.Phone, "+1 555 0100")]);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(to: either), CancellationToken.None));

        Assert.Equal(MessagingFailure.Ambiguous, failure.Failure);
        Assert.Equal(["Messages", "Signal"], failure.Options.Order());
    }

    [Fact]
    public async Task WhenEveryAddressIsSavedForAServiceOnlyChatsOnThatServiceCount()
    {
        await using var setup = With(
            """{"chats":[{"id":"a","title":"Omar","network":"Messages","type":"single"},{"id":"b","title":"Omar","network":"Signal","type":"single"}]}""");
        var onSignal = new MessageRecipient(Guid.NewGuid(), "Omar", [new PersonIdentifier(PersonIdentifierKind.Username, "omar.k", "Signal")]);

        var draft = await setup.Provider.CreateDraftAsync(Message(to: onSignal), CancellationToken.None);

        Assert.Contains("Signal chat", draft.Route, StringComparison.Ordinal);
        Assert.Contains("\"v\":\"b\"", draft.Reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChatOnAServiceTheUserDidNotSaveTheAddressForIsNoChat()
    {
        await using var setup = With("""{"chats":[{"id":"a","title":"Omar","network":"Telegram","type":"single"}]}""");
        var onSignal = new MessageRecipient(Guid.NewGuid(), "Omar", [new PersonIdentifier(PersonIdentifierKind.Username, "omar.k", "Signal")]);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(to: onSignal), CancellationToken.None));

        Assert.Equal(MessagingFailure.RecipientNotFound, failure.Failure);
    }

    [Fact]
    public async Task AChatThatHoldsTheSavedNumberIsPreferredToOneThatDoesNot()
    {
        await using var setup = With(
            """
            {"chats":[
              {"id":"someone-else","title":"Omar Hassan","type":"single","participants":[{"phone":"+44 20 7946 0000"}]},
              {"id":"his","title":"Omar","type":"single","participants":[{"phone":"+1 (555) 0100"}]}]}
            """);

        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);

        Assert.Contains("\"v\":\"his\"", draft.Reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePersonsNameInTheChatTitleIsShownWhenItIsNotTheOneTheUserKnowsThemBy()
    {
        await using var setup = With("""{"chats":[{"id":"a","title":"Omar K.","network":"Messages","type":"single"}]}""");

        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);

        Assert.Equal("Messages chat with Omar \"Omar K.\" in Sample Messages", draft.Route);
    }

    [Fact]
    public async Task ANumberIsAlsoLookedForByItsDigitsAlone()
    {
        await using var setup = With(
            """{"chats":[]}""");

        await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(), CancellationToken.None));

        var queries = setup.Client.Calls.Where(call => call.Tool == "search_chats").Select(call => JsonDocument.Parse(call.Arguments).RootElement.GetProperty("query").GetString()).ToList();
        Assert.Equal(["+1 555 0100", "15550100"], queries);
    }

    [Fact]
    public async Task APersonWithNoNumberIsLookedForByNameAndOnlyAOneToOneChatNamedLikeThemCounts()
    {
        await using var setup = With();
        var nameOnly = new MessageRecipient(Guid.NewGuid(), "Omar", []);

        var draft = await setup.Provider.CreateDraftAsync(Message(to: nameOnly), CancellationToken.None);

        Assert.Contains("\"v\":\"chat-omar\"", draft.Reference, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-family", draft.Reference, StringComparison.Ordinal);
        var queries = setup.Client.Calls.Where(call => call.Tool == "search_chats").Select(call => JsonDocument.Parse(call.Arguments).RootElement.GetProperty("query").GetString()).ToList();
        Assert.Equal(["Omar"], queries);
        Assert.Empty(setup.Sent);
    }

    [Fact]
    public async Task ANameThatOnlyAGroupBearsIsNotFound()
    {
        await using var setup = With("""{"chats":[{"id":"g","title":"Omar","type":"group","participants":[{"name":"Omar"},{"name":"A"},{"name":"B"}]}]}""");
        var nameOnly = new MessageRecipient(Guid.NewGuid(), "Omar", []);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(to: nameOnly), CancellationToken.None));

        Assert.Equal(MessagingFailure.RecipientNotFound, failure.Failure);
    }

    [Fact]
    public async Task AChatTheAppDoesNotSayIsAGroupIsNotTakenForOne()
    {
        // An app whose chat objects carry no group flag, type or participants: only a chat known to be a group is left out.
        await using var setup = With("""{"chats":[{"id":"chat-savannah","title":"Savannah"}]}""");
        var nameOnly = new MessageRecipient(Guid.NewGuid(), "Savannah", [new PersonIdentifier(PersonIdentifierKind.ChatName, "Savannah")]);

        var draft = await setup.Provider.CreateDraftAsync(Message(to: nameOnly), CancellationToken.None);

        Assert.Contains("\"v\":\"chat-savannah\"", draft.Reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASlipOfTheKeyboardInTheNameStillFindsTheChat()
    {
        await using var setup = With("""{"chats":[{"id":"chat-savannah","title":"Savanah Ali","type":"single"}]}""");
        var nameOnly = new MessageRecipient(Guid.NewGuid(), "Savannah", [new PersonIdentifier(PersonIdentifierKind.ChatName, "Savannah")]);

        var draft = await setup.Provider.CreateDraftAsync(Message(to: nameOnly), CancellationToken.None);

        Assert.Contains("chat-savannah", draft.Reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenNoChatIsNamedLikeThemTheChatsTheAppDidFindAreToldSoTheUserCanSayWhichIsMeant()
    {
        await using var setup = With("""{"chats":[{"id":"a","title":"Mo","type":"single"},{"id":"g","title":"Family","type":"group"},{"id":"b","title":"Hamza K","type":"single"}]}""");
        var nameOnly = new MessageRecipient(Guid.NewGuid(), "Savannah", [new PersonIdentifier(PersonIdentifierKind.ChatName, "Savannah")]);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(to: nameOnly), CancellationToken.None));

        Assert.Equal(MessagingFailure.RecipientNotFound, failure.Failure);
        Assert.Equal(["Hamza K", "Mo"], failure.Options.Order());
        Assert.Empty(setup.Sent);
    }

    [Fact]
    public async Task AnAnswerInWordsAroundJsonStillFindsTheChat()
    {
        // An app that tells the model how to use what it found writes prose around the data.
        await using var setup = With("Found 1 chat. Use the chatID with send_message.\n\n{\"chats\":[{\"id\":\"chat-savannah\",\"title\":\"Savannah\",\"type\":\"single\"}]}");
        var nameOnly = new MessageRecipient(Guid.NewGuid(), "Savannah", [new PersonIdentifier(PersonIdentifierKind.ChatName, "Savannah")]);

        var draft = await setup.Provider.CreateDraftAsync(Message(to: nameOnly), CancellationToken.None);

        Assert.Contains("chat-savannah", draft.Reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerListedInWordsFindsTheChat()
    {
        await using var setup = With("Found 2 chats:\n\n## Savannah\n- chatID: !abc:beeper.local\n- type: single\n\n## Family\n- chatID: !def:beeper.local\n- type: group");
        var nameOnly = new MessageRecipient(Guid.NewGuid(), "Savannah", [new PersonIdentifier(PersonIdentifierKind.ChatName, "Savannah")]);

        var draft = await setup.Provider.CreateDraftAsync(Message(to: nameOnly), CancellationToken.None);

        Assert.Contains("!abc:beeper.local", draft.Reference, StringComparison.Ordinal);
        Assert.DoesNotContain("!def:beeper.local", draft.Reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerThatCannotBeReadIsDrawnInTheLogWithoutWhatItSays()
    {
        var logger = new CapturingLogger();
        await using var setup = With("Sorry, nothing about Savannah Secretson was found anywhere.", logger: logger);
        var nameOnly = new MessageRecipient(Guid.NewGuid(), "Savannah", [new PersonIdentifier(PersonIdentifierKind.ChatName, "Savannah")]);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(to: nameOnly), CancellationToken.None));

        Assert.Equal(MessagingFailure.RecipientNotFound, failure.Failure);
        var drawn = logger.Messages.Where(message => message.Contains("looks like", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(drawn);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain("Savannah", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Secretson", message, StringComparison.Ordinal);
        });
        Assert.Contains(logger.Messages, message => message.Contains("no_chat_found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AChatNamedExactlyAndNotArchivedBeatsTheOthersThatHoldTheName()
    {
        // Several chats hold the name (as Beeper really answered): the live chat named exactly as the person is the one; the old, archived ones and the longer names are not.
        await using var setup = With(
            "# Chats\n\nFound 4 chats\n\n" +
            "## Omar (chatID: 131051)\nChat on Beeper (Matrix) (matrix) with Omar, me.\n**Type**: single\n\n" +
            "## Omar (chatID: 130658)\nChat on Telegram (telegram) with Omar, me.\n**Type**: single\nThis chat is archived.\n\n" +
            "## Omar Hassan (chatID: 45845)\nChat on LinkedIn (linkedin) with Omar Hassan, me.\n**Type**: single\n\n" +
            "## Family & Omar (chatID: 123807)\nChat on WhatsApp (local-whatsapp_ba_AbC) with Ali, Omar, Me.\n**Type**: group\n");
        var nameOnly = new MessageRecipient(Guid.NewGuid(), "Omar", [new PersonIdentifier(PersonIdentifierKind.ChatName, "Omar")]);

        var draft = await setup.Provider.CreateDraftAsync(Message(to: nameOnly), CancellationToken.None);

        Assert.Contains("\"v\":\"131051\"", draft.Reference, StringComparison.Ordinal);
        Assert.Contains("Beeper (Matrix) chat with Omar", draft.Route, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoLiveChatsNamedExactlyAreStillAQuestionForTheUser()
    {
        await using var setup = With(
            "## Omar (chatID: 1)\nChat on Beeper (Matrix) (matrix) with Omar, me.\n**Type**: single\n\n" +
            "## Omar (chatID: 2)\nChat on Telegram (telegram) with Omar, me.\n**Type**: single\n");
        var nameOnly = new MessageRecipient(Guid.NewGuid(), "Omar", [new PersonIdentifier(PersonIdentifierKind.ChatName, "Omar")]);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(to: nameOnly), CancellationToken.None));

        Assert.Equal(MessagingFailure.Ambiguous, failure.Failure);
        Assert.Empty(setup.Sent);
    }

    [Fact]
    public async Task AChatNamedExactlyIsTheOneEvenWhenItIsArchivedAndTheOthersOnlyHoldTheName()
    {
        await using var setup = With(
            "## Omar (chatID: 7)\nChat on Telegram (telegram) with Omar, me.\n**Type**: single\nThis chat is archived.\n\n" +
            "## Omar Hassan (chatID: 8)\nChat on LinkedIn (linkedin) with Omar Hassan, me.\n**Type**: single\n");
        var nameOnly = new MessageRecipient(Guid.NewGuid(), "Omar", [new PersonIdentifier(PersonIdentifierKind.ChatName, "Omar")]);

        var draft = await setup.Provider.CreateDraftAsync(Message(to: nameOnly), CancellationToken.None);

        Assert.Contains("\"v\":\"7\"", draft.Reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANameThatSeveralChatsBearAreAQuestionForTheUserAndNeverAChoice()
    {
        await using var setup = With(
            """{"chats":[{"id":"a","title":"Omar","network":"WhatsApp","type":"single"},{"id":"b","title":"Omar","network":"Telegram","type":"single"}]}""");
        var nameOnly = new MessageRecipient(Guid.NewGuid(), "Omar", []);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(to: nameOnly), CancellationToken.None));

        Assert.Equal(MessagingFailure.Ambiguous, failure.Failure);
        Assert.Equal(["Telegram", "WhatsApp"], failure.Options.Order());
    }

    [Fact]
    public async Task AChatNameAndAnOtherNameAreBothLookedForAndASavedAppNameKeepsNoChatOut()
    {
        await using var setup = With("""{"chats":[{"id":"bro-chat","title":"Bro","network":"WhatsApp","type":"single"}]}""");
        var byChatName = new MessageRecipient(Guid.NewGuid(), "Omar Hassan", [new PersonIdentifier(PersonIdentifierKind.ChatName, "Bro", "Sample Messages")])
        {
            Aliases = ["Omi"],
        };

        var draft = await setup.Provider.CreateDraftAsync(Message(to: byChatName), CancellationToken.None);

        Assert.Contains("\"v\":\"bro-chat\"", draft.Reference, StringComparison.Ordinal);
        var queries = setup.Client.Calls.Where(call => call.Tool == "search_chats").Select(call => JsonDocument.Parse(call.Arguments).RootElement.GetProperty("query").GetString()).ToList();
        Assert.Equal(["Bro", "Omar Hassan", "Omi"], queries);
    }

    [Fact]
    public async Task APersonWhoseSavedNumberFindsNoChatIsNotLookedForByNameInstead()
    {
        await using var setup = With("""{"chats":[]}""");

        await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(), CancellationToken.None));

        Assert.DoesNotContain("Omar", setup.Client.Calls.Where(call => call.Tool == "search_chats").Select(call => call.Arguments), StringComparer.Ordinal);
    }

    [Fact]
    public async Task WithNoMessagingAppTheDraftIsNotConnectedAndNothingIsStarted()
    {
        var notes = Sample.Remote("notes", "Notes") with { Capabilities = new IntegrationCapabilities { Tools = true, ToolNames = ["list_notes"] } };
        await using var setup = new Setup(new StubMcpClient(), notes);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(), CancellationToken.None));

        Assert.Equal(MessagingFailure.NotConnected, failure.Failure);
        Assert.Equal(0, setup.Client.ConnectCalls);
    }

    [Fact]
    public async Task ADisabledAppIsNotUsed()
    {
        await using var setup = With(integration: WorkflowFixture.MessagesIntegration() with { Enabled = false });

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(), CancellationToken.None));

        Assert.Equal(MessagingFailure.NotConnected, failure.Failure);
        Assert.False(await setup.Provider.IsAvailableAsync());
    }

    [Fact]
    public async Task AnAppThatCannotBeReachedIsUnavailable()
    {
        var client = WorkflowFixture.MessagesClient(WorkflowFixture.ChatsJson);
        client.ConnectFailure = new McpException(McpFailure.ConnectFailed);
        await using var setup = new Setup(client);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(), CancellationToken.None));

        Assert.Equal(MessagingFailure.Unavailable, failure.Failure);
    }

    [Theory]
    [InlineData(McpFailure.AuthRequired, MessagingFailure.SignInNeeded)]
    [InlineData(McpFailure.Forbidden, MessagingFailure.SignInNeeded)]
    [InlineData(McpFailure.Server, MessagingFailure.Rejected)]
    [InlineData(McpFailure.TimedOut, MessagingFailure.Unavailable)]
    [InlineData(McpFailure.Protocol, MessagingFailure.Unavailable)]
    public async Task ASearchThatFailsIsToldInTheAssistantsWordsAndSendsNothing(McpFailure failure, MessagingFailure expected)
    {
        var client = WorkflowFixture.MessagesClient(WorkflowFixture.ChatsJson);
        client.OnCall = (_, _, _) => throw new McpException(failure);
        await using var setup = new Setup(client);

        var thrown = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(), CancellationToken.None));

        Assert.Equal(expected, thrown.Failure);
        Assert.Empty(setup.Sent);
    }

    [Fact]
    public async Task AToolAddressedByANumberNeedsNoSearchAndTheDraftNamesTheNumberOnlyInItsReference()
    {
        var client = new StubMcpClient();
        client.Tools.Add(Sample.Tool("send_message", "Sends a text.", """{"type":"object","properties":{"to":{"type":"string"},"body":{"type":"string"}},"required":["to","body"]}"""));
        var integration = WorkflowFixture.MessagesIntegration() with { Capabilities = new IntegrationCapabilities { Tools = true, ToolNames = ["send_message"], RefreshedAt = DateTimeOffset.UtcNow } };
        await using var setup = new Setup(client, integration);

        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);

        Assert.Empty(client.Calls);
        Assert.Equal("Messages message to Omar in Sample Messages", draft.Route);
        Assert.DoesNotContain("555", draft.Route, StringComparison.Ordinal);
        var result = await setup.Provider.SendAsync(draft, CancellationToken.None);
        Assert.Equal(MessageDeliveryStatus.Sent, result.Status);
        var call = Assert.Single(client.Calls);
        using var arguments = JsonDocument.Parse(call.Arguments);
        Assert.Equal("+1 555 0100", arguments.RootElement.GetProperty("to").GetString());
        Assert.Equal("Hello Omar", arguments.RootElement.GetProperty("body").GetString());
    }

    // ---- Sending -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASendCallsTheAppsToolWithTheChatOfTheDraftAndTheTextOfTheDraftAndNothingElse()
    {
        await using var setup = With();
        var draft = await setup.Provider.CreateDraftAsync(Message("Exam on Friday"), CancellationToken.None);

        var result = await setup.Provider.SendAsync(draft, CancellationToken.None);

        Assert.Equal(MessageDeliveryStatus.Sent, result.Status);
        Assert.Equal(draft.Route, result.Route);
        var call = Assert.Single(setup.Sent);
        using var arguments = JsonDocument.Parse(call.Arguments);
        Assert.Equal(["chat_id", "text"], arguments.RootElement.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("chat-omar", arguments.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal("Exam on Friday", arguments.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task AMessageWithQuotesLineBreaksAndUnicodeIsSentAsItIs()
    {
        await using var setup = With();
        const string text = "He said \"hi\"\nsecond line \\ ☃ 😀";
        var draft = await setup.Provider.CreateDraftAsync(Message(text), CancellationToken.None);

        await setup.Provider.SendAsync(draft, CancellationToken.None);

        using var arguments = JsonDocument.Parse(Assert.Single(setup.Sent).Arguments);
        Assert.Equal(text, arguments.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ASendThatTheAppSaysIsPendingIsPendingAndNotSent()
    {
        var client = WorkflowFixture.MessagesClient(WorkflowFixture.ChatsJson);
        client.OnCall = (tool, _, _) => Task.FromResult(Sample.Text(tool.Name == "search_chats" ? WorkflowFixture.ChatsJson : """{"status":"queued"}"""));
        await using var setup = new Setup(client);
        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);

        var result = await setup.Provider.SendAsync(draft, CancellationToken.None);

        Assert.Equal(MessageDeliveryStatus.Pending, result.Status);
    }

    [Theory]
    [InlineData(McpFailure.TimedOut)]
    [InlineData(McpFailure.Closed)]
    public async Task ASendThatWentQuietIsPendingNeitherSentNorFailedAndIsNeverMadeTwice(McpFailure failure)
    {
        var client = WorkflowFixture.MessagesClient(WorkflowFixture.ChatsJson);
        client.OnCall = (tool, _, _) => tool.Name == "search_chats" ? Task.FromResult(Sample.Text(WorkflowFixture.ChatsJson)) : throw new McpException(failure);
        await using var setup = new Setup(client);
        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);

        var result = await setup.Provider.SendAsync(draft, CancellationToken.None);

        Assert.Equal(MessageDeliveryStatus.Pending, result.Status);
        Assert.Single(setup.Sent);
    }

    [Fact]
    public async Task AToolThatSaysItFailedIsARejection()
    {
        var client = WorkflowFixture.MessagesClient(WorkflowFixture.ChatsJson);
        client.OnCall = (tool, _, _) => Task.FromResult(
            tool.Name == "search_chats" ? Sample.Text(WorkflowFixture.ChatsJson) : new McpToolResult(true, [new McpContentBlock(McpContentKind.Text, "no", null, null, null)], null));
        await using var setup = new Setup(client);
        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.SendAsync(draft, CancellationToken.None));

        Assert.Equal(MessagingFailure.Rejected, failure.Failure);
    }

    [Fact]
    public async Task AServerErrorOnTheSendIsARejectionAndAnAuthErrorIsASignIn()
    {
        var client = WorkflowFixture.MessagesClient(WorkflowFixture.ChatsJson);
        var failWith = McpFailure.Server;
        client.OnCall = (tool, _, _) => tool.Name == "search_chats" ? Task.FromResult(Sample.Text(WorkflowFixture.ChatsJson)) : throw new McpException(failWith);
        await using var setup = new Setup(client);
        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);

        Assert.Equal(MessagingFailure.Rejected, (await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.SendAsync(draft, CancellationToken.None))).Failure);
        failWith = McpFailure.AuthRequired;
        Assert.Equal(MessagingFailure.SignInNeeded, (await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.SendAsync(draft, CancellationToken.None))).Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"i":"samplemessages"}""")]
    [InlineData("""{"i":"samplemessages","t":"send_message","a":"chat_id","x":"text","v":""}""")]
    [InlineData("""{"i":"samplemessages","t":"send_message","a":"chat_id","x":"text","v":5}""")]
    public async Task AReferenceThatIsNotOneThisProviderMadeIsNeverSent(string reference)
    {
        await using var setup = With();
        var forged = new MessageDraft(Message(), "Messages chat with Omar", reference);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.SendAsync(forged, CancellationToken.None));

        Assert.Equal(MessagingFailure.Rejected, failure.Failure);
        Assert.Empty(setup.Client.Calls);
    }

    [Fact]
    public async Task ADraftWhoseToolIsNotTheOneBoundNowIsNeverSent()
    {
        await using var setup = With();
        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);
        var tampered = draft with { Reference = draft.Reference.Replace("\"t\":\"send_message\"", "\"t\":\"search_chats\"", StringComparison.Ordinal) };

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.SendAsync(tampered, CancellationToken.None));

        Assert.Equal(MessagingFailure.Rejected, failure.Failure);
        Assert.Empty(setup.Sent);
    }

    [Fact]
    public async Task ADraftForAnAppThatIsNotThereIsUnavailable()
    {
        await using var setup = With();
        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);
        await setup.Registry.RemoveAsync("samplemessages");

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.SendAsync(draft, CancellationToken.None));

        Assert.Equal(MessagingFailure.Unavailable, failure.Failure);
        Assert.Empty(setup.Sent);
    }

    [Fact]
    public async Task ADraftForAnAppThatWasTurnedOffIsUnavailable()
    {
        await using var setup = With();
        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);
        await setup.Registry.UpdateAsync("samplemessages", integration => integration with { Enabled = false });

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.SendAsync(draft, CancellationToken.None));

        Assert.Equal(MessagingFailure.Unavailable, failure.Failure);
        Assert.Empty(setup.Sent);
    }

    [Fact]
    public async Task AMessageWithNoTextIsNeverSent()
    {
        await using var setup = With();
        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);
        var empty = draft with { Message = draft.Message with { Text = "  " } };

        await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.SendAsync(empty, CancellationToken.None));

        Assert.Empty(setup.Sent);
    }

    [Fact]
    public async Task ACancelledSendIsCancelledAndNotAFailure()
    {
        await using var setup = With();
        var draft = await setup.Provider.CreateDraftAsync(Message(), CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Provider.SendAsync(draft, cancelled.Token));
    }

    // ---- Availability and the app's own tools ------------------------------------------------------------------------

    [Fact]
    public async Task TheProviderIsAvailableOnlyWhileAnEnabledAppHasAToolThatCouldSendAText()
    {
        await using var with = With();
        await using var without = new Setup(
            new StubMcpClient(), Sample.Remote("notes", "Notes") with { Capabilities = new IntegrationCapabilities { Tools = true, ToolNames = ["list_notes"] } });
        await using var noNamesYet = new Setup(new StubMcpClient(), Sample.Remote("m", "M"));

        Assert.True(await with.Provider.IsAvailableAsync());
        Assert.False(await without.Provider.IsAvailableAsync());
        Assert.False(await noNamesYet.Provider.IsAvailableAsync());
        Assert.Equal(0, with.Client.ConnectCalls);
    }

    [Fact]
    public async Task TheAppsOwnSendingAndSearchingToolsAreKeptFromTheModelWhileTheAssistantSendsForIt()
    {
        await using var setup = With();
        var catalog = await setup.Manager.GetCatalogAsync("samplemessages", TimeSpan.FromSeconds(5), CancellationToken.None);

        var reserved = await setup.Provider.ReservedToolsAsync(setup.Integration, catalog!.Tools, CancellationToken.None);

        Assert.Equal(["search_chats", "send_message"], reserved.Order());
    }

    [Fact]
    public async Task AToolThatWritesIntoAChatsDraftIsKeptFromTheModelToo()
    {
        // Beeper's "focus_app" opens a chat with words already in its draft: a small model reached for it in place of the Assistant's own draft and send.
        var client = WorkflowFixture.MessagesClient(WorkflowFixture.ChatsJson);
        client.Tools.Add(Sample.Tool(
            "focus_app", "Focuses the app, optionally on a chat, with a draft.",
            """{"type":"object","properties":{"chatID":{"type":"string"},"draftText":{"type":"string"},"messageID":{"type":"string"}}}"""));
        client.Tools.Add(Sample.Tool("list_accounts", "Lists the accounts.", """{"type":"object","properties":{}}""", readOnly: true));
        await using var setup = new Setup(client);
        var catalog = await setup.Manager.GetCatalogAsync("samplemessages", TimeSpan.FromSeconds(5), CancellationToken.None);

        var reserved = await setup.Provider.ReservedToolsAsync(setup.Integration, catalog!.Tools, CancellationToken.None);

        Assert.Equal(["focus_app", "search_chats", "send_message"], reserved.Order());
    }

    [Fact]
    public async Task NothingIsKeptFromTheModelWhileMessagingIsNotAllowed()
    {
        await using var setup = With(permission: false);
        var catalog = await setup.Manager.GetCatalogAsync("samplemessages", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Empty(await setup.Provider.ReservedToolsAsync(setup.Integration, catalog!.Tools, CancellationToken.None));
    }

    [Fact]
    public async Task NothingIsKeptFromTheModelOfAnAppThatIsNotAMessagingApp()
    {
        await using var setup = With();
        var calendar = WorkflowFixture.CalendarClient(WorkflowFixture.EventsJson);
        await using var calendarSetup = new Setup(calendar, WorkflowFixture.CalendarIntegration());
        var catalog = await calendarSetup.Manager.GetCatalogAsync("samplecalendar", TimeSpan.FromSeconds(5), CancellationToken.None);

        // The provider asked is the messaging one, which has an app of its own; the calendar's tools are none of its business.
        Assert.Empty(await setup.Provider.ReservedToolsAsync(WorkflowFixture.CalendarIntegration(), catalog!.Tools, CancellationToken.None));
    }

    [Fact]
    public async Task ABlockedSendToolIsNotUsedAndTheAppIsNotMessagingThen()
    {
        var blocked = WorkflowFixture.MessagesIntegration() with
        {
            Permissions = new IntegrationPermissions { ReadOnlyTools = ["search_chats"], BlockedTools = ["send_message"] },
        };
        await using var setup = With(integration: blocked);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(), CancellationToken.None));

        Assert.Equal(MessagingFailure.NotConnected, failure.Failure);
        Assert.Empty(setup.Sent);
    }

    // ---- Privacy -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoNameNumberChatOrMessageIsEverLogged()
    {
        var logger = new CapturingLogger();
        await using var setup = With(logger: logger);
        var draft = await setup.Provider.CreateDraftAsync(Message("a very private text"), CancellationToken.None);
        await setup.Provider.SendAsync(draft, CancellationToken.None);
        await Assert.ThrowsAsync<MessagingProviderException>(() => setup.Provider.CreateDraftAsync(Message(to: new MessageRecipient(Guid.NewGuid(), "Zed", [new PersonIdentifier(PersonIdentifierKind.Phone, "000999", "Signal")])), CancellationToken.None));

        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain("Omar", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Zed", message, StringComparison.Ordinal);
            Assert.DoesNotContain("555", message, StringComparison.Ordinal);
            Assert.DoesNotContain("000999", message, StringComparison.Ordinal);
            Assert.DoesNotContain("private", message, StringComparison.Ordinal);
            Assert.DoesNotContain("chat-omar", message, StringComparison.Ordinal);
        });
    }

    private sealed class CapturingLogger : ILogger<McpMessagingProvider>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
