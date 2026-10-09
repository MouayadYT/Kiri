using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// A request that needs an integration is set aside while the user decides, and gone back to (PROJECT_SPEC §4.8, step 110): installed, it is answered as if the integration had been there
/// from the start without the user saying it again; not installed, the Assistant says so and nothing is done. The model's memory of the conversation never holds the offer.
/// </summary>
public sealed class AssistantOrchestratorContinueTests
{
    private const string Request = "Add 'buy my secret milk' to Microsoft To Do";
    private const string OfferText = "I can't create a task in Microsoft To Do yet. I put an integration below for you to look over.";
    private const string NotInstalledText = "I didn't install the Microsoft To Do integration, so I didn't create a task in Microsoft To Do.";

    private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static ModelInfo ToolModel => new("test-model", 8192) { SupportsToolCalling = true };

    private static readonly IntegrationOffer Offer = new()
    {
        OfferId = "offer-1", AppName = "Microsoft To Do", IntegrationName = "x", MakerText = "Community", Provides = "Create a task", Source = "npm",
    };

    // Offers the integration on the first request, and, once it is "installed", leaves the request to the model; or says something else.
    private sealed class Handler(Func<int, ToolContext, ConnectedAppReply?> answer) : IConnectedAppRequestHandler
    {
        public List<ToolContext> Asked { get; } = [];

        public Task<ConnectedAppReply?> TryAnswerAsync(ToolContext context, CancellationToken cancellationToken = default)
        {
            Asked.Add(context);
            return Task.FromResult(answer(Asked.Count, context));
        }

        public static Handler OfferingThenLeaving() => new((count, _) => count == 1
            ? new ConnectedAppReply(OfferText, ConnectedAppReplyKind.InstallOffered, Offer, NotInstalledText)
            : null);
    }

    private sealed class Registry : IToolRegistry
    {
        public List<ToolContext> Prepared { get; } = [];

        public IReadOnlyList<ToolDefinition> Tools { get; } = [new ToolDefinition("mcp_mstodo_create_task", "Creates a task.", """{"type":"object"}""", RiskLevel.SideEffect)];

        public Task PrepareToolsAsync(ToolContext context, CancellationToken cancellationToken = default)
        {
            Prepared.Add(context);
            return Task.CompletedTask;
        }

        public ToolDefinition? Find(string name) => Tools.FirstOrDefault(tool => tool.Name == name);
    }

    private static AssistantOrchestrator Orchestrator(
        AssistantOrchestratorToolTests.SequencedModel model, IConnectedAppRequestHandler handler, Registry? registry = null, Microsoft.Extensions.Logging.ILogger<AssistantOrchestrator>? logger = null) =>
        new(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), new TestClock(Start), logger ?? NullLogger<AssistantOrchestrator>.Instance,
            toolRegistry: registry ?? new Registry(), toolExecutor: new AssistantOrchestratorToolTests.FakeToolExecutor("{}"), connectedApps: handler);

    private static async Task<List<AssistantResponseChunk>> ReadAsync(IAsyncEnumerable<AssistantResponseChunk> chunks)
    {
        var all = new List<AssistantResponseChunk>();
        await foreach (var chunk in chunks)
        {
            all.Add(chunk);
        }

        return all;
    }

    // The request is asked, and the offer is the answer; what the Assistant handed out to go back with is returned with the session.
    private static async Task<(ConversationSession Session, PendingRequest Pending)> OfferedAsync(AssistantOrchestrator orchestrator)
    {
        var session = ConversationSession.Start(new TestClock(Start));
        var chunks = await ReadAsync(orchestrator.AskAsync(session, Request));
        return (session, Assert.IsType<PendingRequest>(chunks.Single(chunk => chunk.Type == AssistantResponseChunkType.IntegrationOffer).Pending));
    }

    [Fact]
    public async Task AnOfferComesWithTheRequestThatWasSetAsideNamedByTheIdsOfItsMessagesAndTheWordsForWhenItIsNotInstalled()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel);

        var (session, pending) = await OfferedAsync(Orchestrator(model, Handler.OfferingThenLeaving()));

        Assert.Equal(session.Conversation.Messages[0].Id, pending.RequestMessageId);
        Assert.Equal(session.Conversation.Messages[1].Id, pending.ReplyMessageId);
        Assert.Equal(NotInstalledText, pending.NotInstalledText);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task AnOfferWithoutWordsForWhenItIsNotInstalledSetsNothingAside()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel);
        var handler = new Handler((_, _) => new ConnectedAppReply(OfferText, ConnectedAppReplyKind.InstallOffered, Offer));

        var chunks = await ReadAsync(Orchestrator(model, handler).AskAsync(ConversationSession.Start(new TestClock(Start)), Request));

        Assert.Null(chunks.Single(chunk => chunk.Type == AssistantResponseChunkType.IntegrationOffer).Pending);
    }

    [Fact]
    public async Task InstalledTheRequestIsAnsweredByTheModelWithTheOfferTakenOutOfWhatItRemembersAndNoMessageOfTheUsersAdded()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Done! I added it.")]);
        var registry = new Registry();
        var handler = Handler.OfferingThenLeaving();
        var orchestrator = Orchestrator(model, handler, registry);
        var (session, pending) = await OfferedAsync(orchestrator);

        var chunks = await ReadAsync(orchestrator.ContinueAsync(session, pending, PendingOutcome.Installed));

        // The Assistant checks the request again, as it checks every request, and the installed integration is now there for the model.
        Assert.Equal(2, handler.Asked.Count);
        Assert.Equal(Request, handler.Asked[1].Request);
        Assert.Equal(Request, registry.Prepared.Last().Request);
        var asked = Assert.Single(model.Requests);
        var told = Assert.Single(asked.Messages);
        Assert.Equal((MessageRole.User, Request), (told.Role, told.Text));
        Assert.DoesNotContain(OfferText, asked.Instructions + string.Concat(asked.Messages.Select(message => message.Text)), StringComparison.Ordinal);
        Assert.Contains(asked.Tools, tool => tool.Name == "mcp_mstodo_create_task");
        Assert.Equal("Done! I added it.", Assert.Single(chunks).Text);

        // The conversation is the request and its answer, once each.
        Assert.Equal([MessageRole.User, MessageRole.Assistant], session.Conversation.Messages.Select(message => message.Role));
        Assert.Equal(pending.RequestMessageId, session.Conversation.Messages[0].Id);
        Assert.Equal("Done! I added it.", session.Conversation.Messages[1].Text);
    }

    [Fact]
    public async Task InstalledButStillNotUsableTheAssistantSaysWhyAndTheModelIsNotAsked()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Never.")]);
        var handler = new Handler((count, _) => count == 1
            ? new ConnectedAppReply(OfferText, ConnectedAppReplyKind.InstallOffered, Offer, NotInstalledText)
            : new ConnectedAppReply("I can't create a task in Microsoft To Do right now: it needs you to sign in.", ConnectedAppReplyKind.InstalledNotUsable));
        var orchestrator = Orchestrator(model, handler);
        var (session, pending) = await OfferedAsync(orchestrator);

        var chunks = await ReadAsync(orchestrator.ContinueAsync(session, pending, PendingOutcome.Installed));

        Assert.Empty(model.Requests);
        Assert.Equal("I can't create a task in Microsoft To Do right now: it needs you to sign in.", Assert.Single(chunks).Text);
        Assert.Equal([MessageRole.User, MessageRole.Assistant], session.Conversation.Messages.Select(message => message.Role));
    }

    [Fact]
    public async Task NotInstalledTheAssistantSaysSoTheModelIsNeverAskedAndThatIsWhatTheModelRemembers()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Fine.")]);
        var handler = Handler.OfferingThenLeaving();
        var orchestrator = Orchestrator(model, handler);
        var (session, pending) = await OfferedAsync(orchestrator);

        var chunks = await ReadAsync(orchestrator.ContinueAsync(session, pending, PendingOutcome.NotInstalled));

        Assert.Equal(NotInstalledText, Assert.Single(chunks).Text);
        Assert.Empty(model.Requests);
        Assert.Single(handler.Asked);
        Assert.Equal([MessageRole.User, MessageRole.Assistant], session.Conversation.Messages.Select(message => message.Role));
        Assert.Equal(NotInstalledText, session.Conversation.Messages[1].Text);

        // The next thing the user says is answered knowing the request was not carried out.
        await ReadAsync(orchestrator.AskAsync(session, "thanks"));
        Assert.Contains(Assert.Single(model.Requests).Messages, message => message.Role == MessageRole.Assistant && message.Text == NotInstalledText);
        Assert.DoesNotContain(model.Requests[0].Messages, message => message.Text == OfferText);
    }

    [Fact]
    public async Task WhenTheConversationHasGoneOnTheRequestIsAskedAgainAtTheEndAsItWasAskedAndAnsweredLast()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(
            ToolModel, [AssistantResponseChunk.ForTextDelta("Hello.")], [AssistantResponseChunk.ForTextDelta("Done! I added it.")]);
        var handler = Handler.OfferingThenLeaving();
        var orchestrator = Orchestrator(model, handler);
        var (session, pending) = await OfferedAsync(orchestrator);
        await ReadAsync(orchestrator.AskAsync(session, "hello"));

        var chunks = await ReadAsync(orchestrator.ContinueAsync(session, pending, PendingOutcome.Installed));

        Assert.Equal("Done! I added it.", Assert.Single(chunks).Text);
        var texts = session.Conversation.Messages.Select(message => message.Text).ToArray();
        Assert.Equal([Request, "hello", "Hello.", Request, "Done! I added it."], texts);
        Assert.DoesNotContain(OfferText, texts);
        Assert.Equal(Request, model.Requests[1].Messages[^1].Text);
        Assert.Equal(MessageRole.User, model.Requests[1].Messages[^1].Role);
    }

    [Fact]
    public async Task WhenTheConversationHasGoneOnNotInstalledIsSaidWhereTheOfferStood()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Hello.")]);
        var orchestrator = Orchestrator(model, Handler.OfferingThenLeaving());
        var (session, pending) = await OfferedAsync(orchestrator);
        await ReadAsync(orchestrator.AskAsync(session, "hello"));

        await ReadAsync(orchestrator.ContinueAsync(session, pending, PendingOutcome.NotInstalled));

        Assert.Equal([Request, NotInstalledText, "hello", "Hello."], session.Conversation.Messages.Select(message => message.Text).ToArray());
    }

    [Fact]
    public async Task ARequestThatIsNoLongerInTheConversationIsSaidSoAndNothingIsAsked()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Never.")]);
        var orchestrator = Orchestrator(model, Handler.OfferingThenLeaving());
        var (_, pending) = await OfferedAsync(orchestrator);
        var another = ConversationSession.Start(new TestClock(Start));

        var chunks = await ReadAsync(orchestrator.ContinueAsync(another, pending, PendingOutcome.Installed));

        Assert.Equal(AssistantOrchestrator.RequestForgottenText, Assert.Single(chunks).Text);
        Assert.Empty(model.Requests);
        Assert.Empty(another.Conversation.Messages);
    }

    [Fact]
    public async Task WithoutAModelTheConversationIsLeftAsItWasAndTheOfferStays()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel);
        var orchestrator = Orchestrator(model, Handler.OfferingThenLeaving());
        var (session, pending) = await OfferedAsync(orchestrator);
        var without = new AssistantOrchestrator(
            new NoModel(), new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), new TestClock(Start), NullLogger<AssistantOrchestrator>.Instance,
            connectedApps: Handler.OfferingThenLeaving());

        await Assert.ThrowsAsync<ModelNotSetUpException>(() => ReadAsync(without.ContinueAsync(session, pending, PendingOutcome.Installed)));

        Assert.Equal([Request, OfferText], session.Conversation.Messages.Select(message => message.Text).ToArray());
        await session.WhenIdleAsync();
    }

    private sealed class NoModel : IModelService
    {
        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) => Task.FromResult<ModelInfo?>(null);

        public IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No model.");
    }

    [Fact]
    public async Task AConversationThatIsStillAnsweringIsNotInterruptedAndAStoppedResumeKeepsWhatItSaid()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Part one. "), AssistantResponseChunk.ForTextDelta("Part two.")]);
        var orchestrator = Orchestrator(model, Handler.OfferingThenLeaving());
        var (session, pending) = await OfferedAsync(orchestrator);

        // A turn that is running holds the session: a second goes no further.
        var running = orchestrator.ContinueAsync(session, pending, PendingOutcome.Installed).GetAsyncEnumerator();
        Assert.True(await running.MoveNextAsync());
        Assert.Throws<InvalidOperationException>(() => orchestrator.ContinueAsync(session, pending, PendingOutcome.NotInstalled).GetAsyncEnumerator().MoveNextAsync().AsTask().GetAwaiter().GetResult());
        await running.DisposeAsync();

        Assert.Equal("Part one. ", session.Conversation.Messages[^1].Text);
        await session.WhenIdleAsync();
    }

    [Fact]
    public async Task StoppingTheResumeStopsTheTurnAndLeavesTheOfferOutOfWhatTheModelRemembers()
    {
        using var stop = new CancellationTokenSource();
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Part one. ")]);
        var orchestrator = Orchestrator(model, Handler.OfferingThenLeaving());
        var (session, pending) = await OfferedAsync(orchestrator);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(orchestrator.ContinueAsync(session, pending, PendingOutcome.Installed, stop.Token)));

        await session.WhenIdleAsync();
        Assert.DoesNotContain(OfferText, session.Conversation.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task TheLogSaysOnlyWhichWayItWentAndNeverWhatWasAsked()
    {
        var logger = new CapturingLogger<AssistantOrchestrator>();
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Done.")]);
        var orchestrator = Orchestrator(model, Handler.OfferingThenLeaving(), logger: logger);
        var (session, pending) = await OfferedAsync(orchestrator);

        await ReadAsync(orchestrator.ContinueAsync(session, pending, PendingOutcome.Installed));

        Assert.Contains(logger.Lines, line => line.Contains("Installed", StringComparison.Ordinal) && line.Contains("set aside", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, line => line.Contains("secret", StringComparison.OrdinalIgnoreCase) || line.Contains(NotInstalledText, StringComparison.Ordinal));
    }

    [Fact]
    public void ThePendingRequestAndTheChunkThatCarriesItNeverPrintTheirWords()
    {
        var pending = new PendingRequest(Guid.NewGuid(), Guid.NewGuid(), "SECRET words about the app");

        var chunk = AssistantResponseChunk.ForIntegrationOffer(Offer, pending);

        Assert.DoesNotContain("SECRET", pending.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", chunk.ToString(), StringComparison.Ordinal);
        Assert.Same(pending, chunk.Pending);
        Assert.Null(AssistantResponseChunk.ForIntegrationOffer(Offer).Pending);
    }

    [Fact]
    public async Task AnOrchestratorThatDoesNotGoBackToRequestsSaysSoAndDoesNotPretend()
    {
        IAssistantOrchestrator plain = new PlainOrchestrator();

        await Assert.ThrowsAsync<NotSupportedException>(async () => await ReadAsync(plain.ContinueAsync(
            ConversationSession.Start(new TestClock(Start)), new PendingRequest(Guid.NewGuid(), Guid.NewGuid(), "x"), PendingOutcome.Installed)));
    }

    private sealed class PlainOrchestrator : IAssistantOrchestrator
    {
        public IAsyncEnumerable<AssistantResponseChunk> AskAsync(
            ConversationSession session, string prompt, IReadOnlyList<ContextItem>? contextItems = null, string? instructions = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
