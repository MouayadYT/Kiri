using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>A request for an external app that cannot be served is answered by the Assistant itself and never by the model (PROJECT_SPEC section 4.8, steps 105-106).</summary>
public sealed class AssistantOrchestratorConnectedAppReplyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static ModelInfo ToolModel => new("test-model", 8192) { SupportsToolCalling = true };

    private sealed class FakeHandler(Func<ToolContext, CancellationToken, Task<ConnectedAppReply?>> answer) : IConnectedAppRequestHandler
    {
        public List<ToolContext> Asked { get; } = [];

        public Task<ConnectedAppReply?> TryAnswerAsync(ToolContext context, CancellationToken cancellationToken = default)
        {
            Asked.Add(context);
            return answer(context, cancellationToken);
        }

        public static FakeHandler Replying(string text, ConnectedAppReplyKind kind = ConnectedAppReplyKind.DiscoveryBlocked) =>
            new((_, _) => Task.FromResult<ConnectedAppReply?>(new ConnectedAppReply(text, kind)));

        public static FakeHandler Leaving() => new((_, _) => Task.FromResult<ConnectedAppReply?>(null));
    }

    private static AssistantOrchestrator Orchestrator(
        AssistantOrchestratorToolTests.SequencedModel model, IConnectedAppRequestHandler? handler, Microsoft.Extensions.Logging.ILogger<AssistantOrchestrator>? logger = null) =>
        new(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), new TestClock(Start), logger ?? NullLogger<AssistantOrchestrator>.Instance,
            toolRegistry: new AssistantOrchestratorToolTests.FakeToolRegistry(), toolExecutor: new AssistantOrchestratorToolTests.FakeToolExecutor("{}"), connectedApps: handler);

    private static async Task<List<AssistantResponseChunk>> ReadAsync(IAsyncEnumerable<AssistantResponseChunk> chunks)
    {
        var all = new List<AssistantResponseChunk>();
        await foreach (var chunk in chunks)
        {
            all.Add(chunk);
        }

        return all;
    }

    [Fact]
    public async Task AnOfferInTheRepliesComesAfterItsTextAsAChunkOfItsOwnAndStillTheModelIsNeverAsked()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Done! I installed it.")]);
        var session = ConversationSession.Start(new TestClock(Start));
        var offer = new IntegrationOffer { OfferId = "abc", AppName = "Todoist", IntegrationName = "x", MakerText = "Official", Provides = "Create a task", Source = "npm" };
        var handler = new FakeHandler((_, _) => Task.FromResult<ConnectedAppReply?>(new ConnectedAppReply("I found one.", ConnectedAppReplyKind.InstallOffered, offer)));

        var chunks = await ReadAsync(Orchestrator(model, handler).AskAsync(session, "Add 'buy milk' to Todoist"));

        Assert.Empty(model.Requests);
        Assert.Equal([AssistantResponseChunkType.TextDelta, AssistantResponseChunkType.IntegrationOffer], chunks.Select(chunk => chunk.Type).ToArray());
        Assert.Equal("I found one.", chunks[0].Text);
        Assert.Same(offer, chunks[1].Offer);
        Assert.Equal("I found one.", session.Conversation.Messages.Last().Text);
    }

    [Fact]
    public void AnOfferChunkAndItsOfferNeverPrintTheirWordsInToString()
    {
        var offer = new IntegrationOffer { OfferId = "abc", AppName = "SECRET-APP", IntegrationName = "SECRET-NAME", MakerText = "m", Provides = "p", Source = "s" };

        var chunk = AssistantResponseChunk.ForIntegrationOffer(offer);

        Assert.DoesNotContain("SECRET", chunk.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", offer.ToString(), StringComparison.Ordinal);
        Assert.Same(offer, chunk.Offer);
        Assert.Throws<ArgumentNullException>(() => AssistantResponseChunk.ForIntegrationOffer(null!));
    }

    [Fact]
    public async Task TheAssistantsOwnReplyIsTheAnswerAndTheModelIsNeverAsked()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Done! I added it.")]);
        var session = ConversationSession.Start(new TestClock(Start));
        var handler = FakeHandler.Replying("I can't create a task in Microsoft To Do yet: no integration is installed.");

        var chunks = await ReadAsync(Orchestrator(model, handler).AskAsync(session, "Add 'buy milk' to Microsoft To Do"));

        Assert.Empty(model.Requests);
        var chunk = Assert.Single(chunks);
        Assert.Equal(AssistantResponseChunkType.TextDelta, chunk.Type);
        Assert.Equal("I can't create a task in Microsoft To Do yet: no integration is installed.", chunk.Text);
        Assert.Equal([MessageRole.User, MessageRole.Assistant], session.Conversation.Messages.Select(message => message.Role));
        Assert.Equal("Add 'buy milk' to Microsoft To Do", session.Conversation.Messages[0].Text);
        Assert.Equal("I can't create a task in Microsoft To Do yet: no integration is installed.", session.Conversation.Messages[1].Text);
    }

    [Fact]
    public async Task TheHandlerIsGivenTheConversationAndTheRequest()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel);
        var session = ConversationSession.Start(new TestClock(Start));
        var handler = FakeHandler.Replying("No.");

        await ReadAsync(Orchestrator(model, handler).AskAsync(session, "Add milk to Todoist"));

        var context = Assert.Single(handler.Asked);
        Assert.Equal(session.Conversation.Id, context.ConversationId);
        Assert.Equal("Add milk to Todoist", context.Request);
    }

    [Fact]
    public async Task WhenTheHandlerLeavesTheRequestTheModelAnswersAsAlways()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Hello.")]);
        var session = ConversationSession.Start(new TestClock(Start));

        var chunks = await ReadAsync(Orchestrator(model, FakeHandler.Leaving()).AskAsync(session, "hello"));

        Assert.Single(model.Requests);
        Assert.Equal("Hello.", Assert.Single(chunks).Text);
    }

    [Fact]
    public async Task WithNoHandlerNothingChanges()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Hello.")]);

        var chunks = await ReadAsync(Orchestrator(model, null).AskAsync(ConversationSession.Start(new TestClock(Start)), "Add milk to Todoist"));

        Assert.Single(model.Requests);
        Assert.Equal("Hello.", Assert.Single(chunks).Text);
    }

    [Fact]
    public async Task AHandlerThatFailsIsARequestForTheModel()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Hello.")]);
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("The handler failed."));

        var chunks = await ReadAsync(Orchestrator(model, handler).AskAsync(ConversationSession.Start(new TestClock(Start)), "Add milk to Todoist"));

        Assert.Equal("Hello.", Assert.Single(chunks).Text);
    }

    [Fact]
    public async Task StoppingWhileTheHandlerChecksStopsTheTurn()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Never.")]);
        using var cancel = new CancellationTokenSource();
        var handler = new FakeHandler(async (_, token) =>
        {
            await cancel.CancelAsync();
            token.ThrowIfCancellationRequested();
            return null;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadAsync(Orchestrator(model, handler).AskAsync(ConversationSession.Start(new TestClock(Start)), "Add milk to Todoist", cancellationToken: cancel.Token)));

        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task AModelThatCannotCallToolsStillGetsTheAssistantsReplyInsteadOfPretending()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(new ModelInfo("plain", 4096), [AssistantResponseChunk.ForTextDelta("Added!")]);

        var chunks = await ReadAsync(Orchestrator(model, FakeHandler.Replying("I can't do that yet.")).AskAsync(ConversationSession.Start(new TestClock(Start)), "Add milk to Todoist"));

        Assert.Empty(model.Requests);
        Assert.Equal("I can't do that yet.", Assert.Single(chunks).Text);
    }

    [Fact]
    public async Task TheConversationGoesOnAfterTheAssistantsReply()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Fine.")]);
        var session = ConversationSession.Start(new TestClock(Start));
        var replied = false;
        var handler = new FakeHandler((_, _) =>
        {
            var first = !replied;
            replied = true;
            return Task.FromResult<ConnectedAppReply?>(first ? new ConnectedAppReply("No.", ConnectedAppReplyKind.DiscoveryBlocked) : null);
        });
        var orchestrator = Orchestrator(model, handler);

        await ReadAsync(orchestrator.AskAsync(session, "Add milk to Todoist"));
        var second = await ReadAsync(orchestrator.AskAsync(session, "thanks"));

        Assert.Equal("Fine.", Assert.Single(second).Text);
        Assert.Equal(4, session.Conversation.Messages.Count);
        // The reply is part of the conversation the model reads next.
        Assert.Contains(Assert.Single(model.Requests).Messages, message => message.Role == MessageRole.Assistant && message.Text == "No.");
    }

    [Fact]
    public async Task TheLogSaysHowTheRequestWasHandledAndNeverWhatWasSaid()
    {
        var logger = new CapturingLogger<AssistantOrchestrator>();
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel);
        var handler = FakeHandler.Replying("a private reply about Microsoft To Do", ConnectedAppReplyKind.DiscoveryFound);

        await ReadAsync(Orchestrator(model, handler, logger).AskAsync(ConversationSession.Start(new TestClock(Start)), "Add 'my secret milk' to Microsoft To Do"));

        Assert.Contains(logger.Lines, line => line.Contains("DiscoveryFound", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, line => line.Contains("secret", StringComparison.OrdinalIgnoreCase) || line.Contains("private reply", StringComparison.Ordinal));
    }
}
