using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Messaging;
using Assistant.Core.People;
using Assistant.Core.Tools;
using Assistant.Tools.Messaging;
using Assistant.Tools.Tests.Mcp;
using Xunit;
using OutgoingMessage = Assistant.Core.Messaging.OutgoingMessage;

namespace Assistant.Tools.Tests.Workflow;

/// <summary>
/// When the messaging tools are worth offering to the model (PROJECT_SPEC §4.8, step 116): the provider has a messaging app, Messaging is allowed, and the request is about messaging; and
/// it is found out once for a turn, by the registry, before the tools are listed.
/// </summary>
public sealed class MessagingOfferingTests
{
    private static readonly Guid Conversation = Guid.NewGuid();

    private static ToolContext Context(string request = "Message my brother", Guid? conversation = null) => new(conversation ?? Conversation, request);

    private sealed class Provider(bool available = true, bool throws = false) : IMessagingProvider
    {
        public string Name => "P";

        public bool IsSample => false;

        public int Asked { get; private set; }

        public bool Available { get; set; } = available;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Asked++;
            return throws ? throw new InvalidOperationException("nope") : Task.FromResult(Available);
        }

        public Task<MessageDraft> CreateDraftAsync(OutgoingMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessageSendResult> SendAsync(MessageDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static DraftMessageTool Draft(IMessagingProvider? provider, IPermissionPolicy? permissions) => new(provider, new PersonResolver(new InMemoryPersonStore()), permissions);

    [Fact]
    public void AToolMadeWithoutThePermissionsIsOfferedAsItAlwaysWasWithoutBeingPrepared()
    {
        Assert.True(Draft(new Provider(), null).IsOffered(Context()));
    }

    [Fact]
    public void AToolMadeWithThePermissionsWaitsToBeToldBeforeItIsOffered()
    {
        Assert.False(Draft(new Provider(), new FakePermissions(true)).IsOffered(Context()));
    }

    [Fact]
    public async Task AfterATurnFoundOutItIsOfferedForAMessagingRequestOnly()
    {
        var tool = Draft(new Provider(), new FakePermissions(true));

        await tool.PrepareAsync(Context(), CancellationToken.None);

        Assert.True(tool.IsOffered(Context("Message my brother")));
        Assert.False(tool.IsOffered(Context("What is 2+2?", Guid.NewGuid())));
    }

    [Fact]
    public async Task ThePlainAnswerToWhoIsHeStaysInAConversationThatBeganWithAMessagingRequest()
    {
        var tool = Draft(new Provider(), new FakePermissions(true));
        var conversation = Guid.NewGuid();

        // "send a message to my brother" asks who he is; the reply is only a name, which is not about messaging by any word, but the conversation is.
        await tool.PrepareAsync(Context("Send a message to my brother saying hello", conversation), CancellationToken.None);
        await tool.PrepareAsync(Context("Mohammed", conversation), CancellationToken.None);

        Assert.True(tool.IsOffered(Context("Mohammed", conversation)));
        Assert.False(tool.IsOffered(Context("Mohammed", Guid.NewGuid())));
    }

    [Fact]
    public async Task ItIsNotOfferedWhileTheProviderHasNoMessagingApp()
    {
        var provider = new Provider(available: false);
        var tool = Draft(provider, new FakePermissions(true));

        await tool.PrepareAsync(Context(), CancellationToken.None);

        Assert.False(tool.IsOffered(Context()));
        provider.Available = true;
        await tool.PrepareAsync(Context(), CancellationToken.None);
        Assert.True(tool.IsOffered(Context()));
    }

    [Fact]
    public async Task ItIsNotOfferedWhileMessagingIsNotAllowed()
    {
        var tool = Draft(new Provider(), new FakePermissions(false));

        await tool.PrepareAsync(Context(), CancellationToken.None);

        Assert.False(tool.IsOffered(Context()));
    }

    [Fact]
    public async Task WhatATurnFoundOutIsForItsConversationAlone()
    {
        var provider = new Provider(available: false);
        var tool = Draft(provider, new FakePermissions(true));
        var other = Guid.NewGuid();

        await tool.PrepareAsync(Context(conversation: other), CancellationToken.None);
        provider.Available = true;
        await tool.PrepareAsync(Context(), CancellationToken.None);

        Assert.True(tool.IsOffered(Context()));
        Assert.False(tool.IsOffered(Context(conversation: other)));
    }

    [Fact]
    public async Task ATurnWithNoProviderFindsNothingOutAndTheToolIsNeverOffered()
    {
        var tool = Draft(null, new FakePermissions(true));

        await tool.PrepareAsync(Context(), CancellationToken.None);

        Assert.False(tool.IsOffered(Context()));
    }

    [Fact]
    public async Task ManyConversationsAreRememberedWithoutGrowingForever()
    {
        var tool = Draft(new Provider(), new FakePermissions(true));

        for (var index = 0; index < 200; index++)
        {
            await tool.PrepareAsync(Context(conversation: Guid.NewGuid()), CancellationToken.None);
        }

        await tool.PrepareAsync(Context(), CancellationToken.None);
        Assert.True(tool.IsOffered(Context()));
    }

    [Fact]
    public async Task TheRegistryPreparesTheBuiltInToolsOncePerTurnBeforeListingThem()
    {
        var provider = new Provider();
        var send = new SendMessageTool(provider, new PersonResolver(new InMemoryPersonStore()), new FakePermissions(true));
        var draft = Draft(provider, new FakePermissions(true));
        var registry = new ToolRegistry([draft, send]);
        var context = Context();

        Assert.Empty(registry.ToolsFor(context));
        await registry.PrepareToolsAsync(context);

        Assert.Equal(["draft_message", "send_message"], registry.ToolsFor(context).Select(tool => tool.Name));
        Assert.Equal(2, provider.Asked);
    }

    [Fact]
    public async Task ABuiltInToolThatCannotFindOutIsNotATurnsFailure()
    {
        var provider = new Provider(throws: true);
        var registry = new ToolRegistry([Draft(provider, new FakePermissions(true)), new FakeTool("calculate")]);
        var context = Context("Message my brother, what is 2+2");

        await registry.PrepareToolsAsync(context);

        // The tool that could not find out is not offered (it waits to be told), and the one that has nothing to find out is.
        Assert.Equal(["calculate"], registry.ToolsFor(context).Select(tool => tool.Name));
    }

    [Fact]
    public async Task ACancelledPreparationIsCancelled()
    {
        var registry = new ToolRegistry([Draft(new Provider(), new FakePermissions(true))]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.PrepareToolsAsync(Context(), cancelled.Token));
    }

    [Fact]
    public async Task AToolThatHasNothingToFindOutIsLeftAlone()
    {
        var calculate = new FakeTool("calculate");
        var registry = new ToolRegistry([calculate]);

        await registry.PrepareToolsAsync(Context("anything"));

        Assert.Equal(["calculate"], registry.ToolsFor(Context("anything")).Select(tool => tool.Name));
        Assert.Equal(0, calculate.Runs);
    }

    [Fact]
    public async Task ADraftFailureOfNoMessagingAppIsToldToTheModelInPlainWords()
    {
        var provider = new FailingProvider();
        var store = new InMemoryPersonStore();
        await store.SaveAsync(Person.Create("Omar", DateTimeOffset.UtcNow) with { Relationships = ["Brother"], Identifiers = [new PersonIdentifier(PersonIdentifierKind.Phone, "+1 555 0100")] });
        var tool = new DraftMessageTool(provider, new PersonResolver(store));

        var result = await tool.RunAsync(new ToolCall("c1", "draft_message", "{}"), Arguments("""{"recipient":"my brother","text":"hi"}"""), Context(), CancellationToken.None);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("No messaging app is connected", result.OutputJson, StringComparison.Ordinal);
        Assert.Contains("Nothing was sent", result.OutputJson, StringComparison.Ordinal);
        Assert.DoesNotContain("555", result.OutputJson, StringComparison.Ordinal);
    }

    private static JsonElement Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FailingProvider : IMessagingProvider
    {
        public string Name => "P";

        public bool IsSample => false;

        public Task<MessageDraft> CreateDraftAsync(OutgoingMessage message, CancellationToken cancellationToken) =>
            throw new MessagingProviderException(MessagingFailure.NotConnected);

        public Task<MessageSendResult> SendAsync(MessageDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
