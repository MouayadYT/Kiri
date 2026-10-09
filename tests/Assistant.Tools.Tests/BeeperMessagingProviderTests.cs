using System.Reflection;
using Assistant.Core.Messaging;
using Assistant.Core.People;
using Assistant.Tools.Messaging;
using Assistant.Tools.Messaging.Beeper;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// The Beeper side of messaging (PROJECT_SPEC §4.8, step 113): a boundary of two questions and a provider that only translates, so it never guesses between chats, never sends to a
/// group or without a draft, and drafting sends nothing. The gateway that really talks to Beeper is not part of this step, so these use a double of it.
/// </summary>
public sealed class BeeperMessagingProviderTests
{
    private readonly FakeGateway _gateway = new();

    private BeeperMessagingProvider Provider => new(_gateway);

    private static MessageRecipient Omar(params PersonIdentifier[] identifiers) =>
        new(Guid.NewGuid(), "Omar Hassan", identifiers.Length > 0 ? identifiers : [new(PersonIdentifierKind.Phone, "+44 7700 900123")]);

    private static OutgoingMessage Message(MessageRecipient recipient, string text = "hi") => new(recipient, text);

    [Fact]
    public void ItIsBeeperAndNotASample()
    {
        Assert.Equal("Beeper", Provider.Name);
        Assert.False(Provider.IsSample);
    }

    [Fact]
    public async Task TheOneChatThatFitsIsWhereTheMessageWillGo_AndDraftingSendsNothing()
    {
        _gateway.Chats.Add(new BeeperChat("!chat1", "Omar Hassan", "WhatsApp"));
        var recipient = Omar();

        var draft = await Provider.CreateDraftAsync(Message(recipient, "Running late"), CancellationToken.None);

        Assert.Equal("WhatsApp chat with Omar Hassan (in Beeper)", draft.Route);
        Assert.Equal("!chat1", draft.Reference);
        Assert.Equal("Running late", draft.Message.Text);
        Assert.Equal(0, _gateway.Sends);
        Assert.Equal("Omar Hassan", _gateway.Queries.Single().DisplayName);
        Assert.Equal(recipient.Identifiers, _gateway.Queries.Single().Identifiers);
    }

    [Fact]
    public async Task NoChatMeansTheRecipientIsNotFound()
    {
        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => Provider.CreateDraftAsync(Message(Omar()), CancellationToken.None));

        Assert.Equal(MessagingFailure.RecipientNotFound, failure.Failure);
    }

    [Fact]
    public async Task APersonWithNothingSavedToMatchAChatByIsNotLookedForByNameAlone()
    {
        _gateway.Chats.Add(new BeeperChat("!chat1", "Omar Hassan", "WhatsApp"));
        var nobody = new MessageRecipient(Guid.NewGuid(), "Omar Hassan", []);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => Provider.CreateDraftAsync(Message(nobody), CancellationToken.None));

        Assert.Equal(MessagingFailure.RecipientNotFound, failure.Failure);
        Assert.Empty(_gateway.Queries);
    }

    [Fact]
    public async Task SeveralChatsAreAQuestionAndNeverAChoice_WithTheServicesTheyAreOn()
    {
        _gateway.Chats.Add(new BeeperChat("!a", "Omar", "WhatsApp"));
        _gateway.Chats.Add(new BeeperChat("!b", "Omar", "Signal"));
        _gateway.Chats.Add(new BeeperChat("!c", "Omar H", "signal"));

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => Provider.CreateDraftAsync(Message(Omar()), CancellationToken.None));

        Assert.Equal(MessagingFailure.Ambiguous, failure.Failure);
        Assert.Equal(["WhatsApp", "Signal"], failure.Options);
    }

    [Fact]
    public async Task TheSameChatTwiceIsOneChat()
    {
        _gateway.Chats.Add(new BeeperChat("!a", "Omar", "WhatsApp"));
        _gateway.Chats.Add(new BeeperChat("!a", "Omar", "WhatsApp"));

        var draft = await Provider.CreateDraftAsync(Message(Omar()), CancellationToken.None);

        Assert.Equal("!a", draft.Reference);
    }

    [Fact]
    public async Task WhenEveryAddressIsForAServiceOnlyChatsOnThatServiceCount()
    {
        _gateway.Chats.Add(new BeeperChat("!a", "Omar", "WhatsApp"));
        _gateway.Chats.Add(new BeeperChat("!b", "Omar", "Signal"));
        var recipient = Omar(new PersonIdentifier(PersonIdentifierKind.Phone, "+44 7700 900123", "Signal"), new PersonIdentifier(PersonIdentifierKind.Username, "omar_h", "Signal"));

        var draft = await Provider.CreateDraftAsync(Message(recipient), CancellationToken.None);

        Assert.Equal("!b", draft.Reference);
    }

    [Fact]
    public async Task AChatOnAServiceTheUserDidNotSaveAnAddressForIsNotUsed()
    {
        _gateway.Chats.Add(new BeeperChat("!a", "Omar", "Telegram"));
        var recipient = Omar(new PersonIdentifier(PersonIdentifierKind.Phone, "+44 7700 900123", "WhatsApp"));

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => Provider.CreateDraftAsync(Message(recipient), CancellationToken.None));

        Assert.Equal(MessagingFailure.RecipientNotFound, failure.Failure);
    }

    [Fact]
    public async Task OneAddressForNoServiceInParticularMeansAnyServiceWillDo()
    {
        _gateway.Chats.Add(new BeeperChat("!a", "Omar", "Telegram"));
        var recipient = Omar(new PersonIdentifier(PersonIdentifierKind.Phone, "+44 7700 900123", "WhatsApp"), new PersonIdentifier(PersonIdentifierKind.Email, "omar@example.com"));

        var draft = await Provider.CreateDraftAsync(Message(recipient), CancellationToken.None);

        Assert.Equal("!a", draft.Reference);
    }

    [Fact]
    public async Task ADraftIsSentToItsChatWithItsText_AndASendBeeperOnlyQueuedIsPending()
    {
        _gateway.Chats.Add(new BeeperChat("!chat1", "Omar", "WhatsApp"));
        var draft = await Provider.CreateDraftAsync(Message(Omar(), "See you at 7"), CancellationToken.None);

        var confirmed = await Provider.SendAsync(draft, CancellationToken.None);
        _gateway.Confirms = false;
        var queued = await Provider.SendAsync(draft, CancellationToken.None);

        Assert.Equal(MessageDeliveryStatus.Sent, confirmed.Status);
        Assert.Equal(MessageDeliveryStatus.Pending, queued.Status);
        Assert.Equal(draft.Route, confirmed.Route);
        Assert.Equal([("!chat1", "See you at 7"), ("!chat1", "See you at 7")], _gateway.Sent);
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("  ", "text")]
    [InlineData("!chat", "")]
    [InlineData("!chat", "   ")]
    public async Task ADraftWithNoChatOrNoTextIsNeverSent(string reference, string text)
    {
        var draft = new MessageDraft(Message(Omar(), text), "route", reference);

        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => Provider.SendAsync(draft, CancellationToken.None));

        Assert.Equal(MessagingFailure.Rejected, failure.Failure);
        Assert.Equal(0, _gateway.Sends);
    }

    [Theory]
    [InlineData(BeeperGatewayFailure.NotAvailable, MessagingFailure.Unavailable)]
    [InlineData(BeeperGatewayFailure.NotAuthorized, MessagingFailure.SignInNeeded)]
    [InlineData(BeeperGatewayFailure.Rejected, MessagingFailure.Rejected)]
    public async Task WhatBeeperCannotDoIsToldInTheWordsOfTheAssistant(BeeperGatewayFailure beeper, MessagingFailure expected)
    {
        _gateway.FailWith = beeper;

        var draft = await Assert.ThrowsAsync<MessagingProviderException>(() => Provider.CreateDraftAsync(Message(Omar()), CancellationToken.None));
        var send = await Assert.ThrowsAsync<MessagingProviderException>(
            () => Provider.SendAsync(new MessageDraft(Message(Omar()), "route", "!chat"), CancellationToken.None));

        Assert.Equal(expected, draft.Failure);
        Assert.Equal(expected, send.Failure);
    }

    [Fact]
    public async Task ACancelledCallIsCancelledAndNotAFailureOfBeeper()
    {
        _gateway.Chats.Add(new BeeperChat("!a", "Omar", "WhatsApp"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider.CreateDraftAsync(Message(Omar()), cancelled.Token));
    }

    [Fact]
    public void TheBoundaryHasNoWayToDriveAnotherProgramsWindow()
    {
        // The gateway is two questions (find chats, send text). Nothing in the Beeper namespace clicks, types or reads another program's window, so the model cannot be handed
        // control of Beeper's interface through it.
        var methods = typeof(IBeeperGateway).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal);
        Assert.Equal(["FindDirectChatsAsync", "SendTextAsync"], methods);

        var forbidden = new[] { "Click", "SendKeys", "Automation", "FocusApp", "Window", "Mouse", "Keyboard" };
        var members = typeof(BeeperMessagingProvider).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(IBeeperGateway).Namespace)
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(member => member.Name);
        Assert.DoesNotContain(members, name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void TheChatAndTheQueryPrintWithoutNamesOrAddresses()
    {
        var text = new BeeperChat("!secret-id", "Secret Title", "WhatsApp") + " "
            + new BeeperChatQuery("Secret Name", [new(PersonIdentifierKind.Phone, "+44 7700 900123")]);

        foreach (var secret in new[] { "!secret-id", "Secret Title", "Secret Name", "7700" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ThroughTheToolsAPersonWhoseBrotherIsOnWhatsAppIsDraftedAndSent()
    {
        var store = new InMemoryPersonStore();
        await store.SaveAsync(Person.Create("Omar Hassan", DateTimeOffset.UnixEpoch) with
        {
            Relationships = ["Brother"],
            Identifiers = [new(PersonIdentifierKind.Phone, "+44 7700 900123", "WhatsApp")],
        });
        _gateway.Chats.Add(new BeeperChat("!chat1", "Omar", "WhatsApp"));
        var resolver = new PersonResolver(store);
        var context = new Assistant.Core.Contracts.ToolContext(Guid.NewGuid(), "text my brother");

        var draft = await new DraftMessageTool(Provider, resolver).RunAsync(
            new Assistant.Core.Domain.ToolCall("1", "draft_message", """{"recipient":"my brother","text":"hi"}"""),
            System.Text.Json.JsonDocument.Parse("""{"recipient":"my brother","text":"hi"}""").RootElement.Clone(), context, CancellationToken.None);
        Assert.Contains("\"sample\":false", draft.OutputJson, StringComparison.Ordinal);
        Assert.Contains("(in Beeper)", draft.OutputJson, StringComparison.Ordinal);
        Assert.Equal(0, _gateway.Sends);

        var sent = await new SendMessageTool(Provider, resolver).RunAsync(
            new Assistant.Core.Domain.ToolCall("2", "send_message", """{"recipient":"Omar Hassan","text":"hi"}"""),
            System.Text.Json.JsonDocument.Parse("""{"recipient":"Omar Hassan","text":"hi"}""").RootElement.Clone(), context, CancellationToken.None);
        Assert.Contains("\"status\":\"sent\"", sent.OutputJson, StringComparison.Ordinal);
        Assert.DoesNotContain("only a sample", sent.OutputJson, StringComparison.Ordinal);
        Assert.Equal([("!chat1", "hi")], _gateway.Sent);
    }

    private sealed class FakeGateway : IBeeperGateway
    {
        public List<BeeperChat> Chats { get; } = [];

        public List<BeeperChatQuery> Queries { get; } = [];

        public List<(string Chat, string Text)> Sent { get; } = [];

        public int Sends => Sent.Count;

        public bool Confirms { get; set; } = true;

        public BeeperGatewayFailure? FailWith { get; set; }

        public Task<IReadOnlyList<BeeperChat>> FindDirectChatsAsync(BeeperChatQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWith is { } failure)
            {
                throw new BeeperGatewayException(failure);
            }

            Queries.Add(query);
            return Task.FromResult<IReadOnlyList<BeeperChat>>([.. Chats]);
        }

        public Task<bool> SendTextAsync(string chatId, string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWith is { } failure)
            {
                throw new BeeperGatewayException(failure);
            }

            Sent.Add((chatId, text));
            return Task.FromResult(Confirms);
        }
    }
}
