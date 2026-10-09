using Assistant.Core.Contracts;
using Assistant.Core.Diagnostics;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Storage;
using Assistant.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>What Core gives the tools of connected apps (PROJECT_SPEC section 4.8, step 104): the request in the tool context, a hook to load tools by it, and the words the model is told.</summary>
public sealed class ConnectedAppToolsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static ModelInfo ToolModel => new("test-model", 8192) { SupportsToolCalling = true };

    private static ToolDefinition Definition(string name) =>
        new(name, "Does a thing.", """{"type":"object","properties":{}}""", RiskLevel.ReadOnly);

    [Fact]
    public void TheToolContextCarriesTheRequestButNeverPrintsIt()
    {
        var context = new ToolContext(Guid.NewGuid(), "my private request about the tax return");

        Assert.Equal("my private request about the tax return", context.Request);
        Assert.DoesNotContain("tax return", context.ToString(), StringComparison.Ordinal);
        Assert.Contains(context.ConversationId.ToString(), context.ToString(), StringComparison.Ordinal);
        Assert.Null(new ToolContext(Guid.NewGuid()).Request);
    }

    [Fact]
    public void ToolContextsOfTheSameConversationAndRequestAreEqual()
    {
        var conversation = Guid.NewGuid();
        Assert.Equal(new ToolContext(conversation, "a"), new ToolContext(conversation, "a"));
        Assert.NotEqual(new ToolContext(conversation, "a"), new ToolContext(conversation, "b"));
        Assert.Equal(new ToolContext(conversation), new ToolContext(conversation, null));
    }

    [Fact]
    public async Task ARegistryThatLoadsNothingLazilyHasNothingToPrepare()
    {
        IToolRegistry registry = new AssistantOrchestratorToolTests.FakeToolRegistry(Definition("a_tool"));
        await registry.PrepareToolsAsync(new ToolContext(Guid.NewGuid(), "anything"));
        Assert.Equal(["a_tool"], registry.ToolsFor(new ToolContext(Guid.NewGuid())).Select(tool => tool.Name));
    }

    [Theory]
    [InlineData("mcp_todoist_create_task", true)]
    [InlineData("mcp_", true)]
    [InlineData("search_files", false)]
    [InlineData("MCP_todoist", false)]
    [InlineData("my_mcp_tool", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AConnectedAppsToolIsToldByItsName(string? name, bool connectedApp) =>
        Assert.Equal(connectedApp, ConnectedAppTools.IsConnectedAppTool(name));

    [Fact]
    public void TheModelIsToldWhatAConnectedAppsToolIsOnlyWhenItIsOfferedOne()
    {
        var builder = new PromptBuilder();
        var conversation = new[] { new Message(Guid.NewGuid(), MessageRole.User, "Add milk to my Todoist", Start) };

        var withApp = builder.Build(null, conversation, ToolModel, null, Guid.NewGuid(), [Definition("search_files"), Definition("mcp_todoist_create_task")]).Request.Instructions;
        var builtInOnly = builder.Build(null, conversation, ToolModel, null, Guid.NewGuid(), [Definition("search_files")]).Request.Instructions;
        var noTools = builder.Build(null, conversation, ToolModel).Request.Instructions;

        Assert.Contains(AssistantInstructions.ConnectedAppToolsGuidance, withApp, StringComparison.Ordinal);
        Assert.DoesNotContain(AssistantInstructions.ConnectedAppToolsGuidance, builtInOnly, StringComparison.Ordinal);
        Assert.DoesNotContain(AssistantInstructions.ConnectedAppToolsGuidance, noTools, StringComparison.Ordinal);

        // A request that is offered no connected app's tool is told exactly what it was told before.
        Assert.Equal(
            AssistantInstructions.Default + "\n\n" + AssistantInstructions.ToolGuidance + "\n\n" + AssistantInstructions.UntrustedContextGuidance,
            builtInOnly);
    }

    [Fact]
    public void TheGuidanceSaysWhatTheToolsAreAndThatWhatTheyReturnIsDataNotInstructions()
    {
        var guidance = AssistantInstructions.ConnectedAppToolsGuidance;
        Assert.Contains("mcp_", guidance, StringComparison.Ordinal);
        Assert.Contains("data, not instructions", guidance, StringComparison.Ordinal);
        Assert.Contains("confirm", guidance, StringComparison.Ordinal);
        Assert.Contains("never make a value up", guidance, StringComparison.Ordinal);
        Assert.True(guidance.Length < 800, "It takes room in a small model's window.");
    }

    [Fact]
    public void TheIntegrationsFileIsBesideTheSettingsAndIsNotADirectory()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "assistant-paths-test"));

        Assert.Equal(Path.Combine(paths.RootDirectory, "integrations.json"), paths.IntegrationsFilePath);
        Assert.Equal(AppPaths.IntegrationsFileName, Path.GetFileName(paths.IntegrationsFilePath));
        Assert.DoesNotContain(paths.IntegrationsFilePath, paths.Directories);
    }

    [Fact]
    public void AnIntegrationsIdMayBeLoggedAndNothingElseAboutItMay()
    {
        Assert.Equal("todoist", LogPrivacy.Sanitize("IntegrationId", "todoist"));
        Assert.Equal(LogPrivacy.Redacted, LogPrivacy.Sanitize("IntegrationName", "Todoist"));
        Assert.Equal(LogPrivacy.Redacted, LogPrivacy.Sanitize("Endpoint", "https://mcp.example.com/mcp"));
        Assert.Equal(LogPrivacy.Redacted, LogPrivacy.Sanitize("Command", @"C:\Tools\server.exe"));
        Assert.Equal(LogPrivacy.Redacted, LogPrivacy.Sanitize("Request", "add milk"));
    }

    // ---- The orchestrator's part ----

    private sealed class LoadingRegistry(params ToolDefinition[] always) : IToolRegistry
    {
        private ToolDefinition[] _loaded = [];

        public IReadOnlyList<ToolDefinition> Tools { get; } = always;

        public List<ToolContext> Prepared { get; } = [];

        public List<ToolContext> Asked { get; } = [];

        public Func<ToolContext, CancellationToken, Task>? OnPrepare { get; set; }

        public ToolDefinition[] ToLoad { get; set; } = [];

        public ToolDefinition? Find(string name) => always.Concat(_loaded).FirstOrDefault(tool => tool.Name == name);

        public IReadOnlyList<ToolDefinition> ToolsFor(ToolContext context)
        {
            Asked.Add(context);
            return [.. always, .. _loaded];
        }

        public async Task PrepareToolsAsync(ToolContext context, CancellationToken cancellationToken = default)
        {
            Prepared.Add(context);
            if (OnPrepare is not null)
            {
                await OnPrepare(context, cancellationToken);
            }

            _loaded = ToLoad;
        }
    }

    private static AssistantOrchestrator Orchestrator(AssistantOrchestratorToolTests.SequencedModel model, IToolRegistry? registry, IToolExecutor? executor) =>
        new(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), new TestClock(Start), NullLogger<AssistantOrchestrator>.Instance,
            toolRegistry: registry, toolExecutor: executor);

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
    public async Task ThePrepareHookIsGivenTheConversationAndTheRequestBeforeTheModelIsAsked()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Done.")]);
        var registry = new LoadingRegistry(Definition("search_files")) { ToLoad = [Definition("mcp_todoist_create_task")] };
        var order = new List<string>();
        registry.OnPrepare = (_, _) =>
        {
            order.Add("prepare");
            return Task.CompletedTask;
        };
        var session = ConversationSession.Start(new TestClock(Start));

        await ReadAsync(Orchestrator(model, registry, new AssistantOrchestratorToolTests.FakeToolExecutor("{}")).AskAsync(session, "add milk to my todoist"));

        var prepared = Assert.Single(registry.Prepared);
        Assert.Equal(session.Conversation.Id, prepared.ConversationId);
        Assert.Equal("add milk to my todoist", prepared.Request);
        Assert.Equal(["prepare"], order);

        // What it loaded is what the model was offered.
        var offered = Assert.Single(model.Requests).Tools;
        Assert.Equal(["search_files", "mcp_todoist_create_task"], offered.Select(tool => tool.Name));
    }

    [Fact]
    public async Task EachRoundOfTheTurnAsksForTheToolsAgainWithTheSameRequestAndTheExecutorGetsItToo()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(
            ToolModel,
            [AssistantResponseChunk.ForToolCall(new ToolCall("c1", "mcp_todoist_create_task", """{"task_title":"milk"}"""))],
            [AssistantResponseChunk.ForTextDelta("Done.")]);
        var registry = new LoadingRegistry(Definition("search_files")) { ToLoad = [Definition("mcp_todoist_create_task")] };
        var executor = new AssistantOrchestratorToolTests.FakeToolExecutor("{}");

        await ReadAsync(Orchestrator(model, registry, executor).AskAsync(ConversationSession.Start(new TestClock(Start)), "add milk to my todoist"));

        // It is prepared once for the turn, not for each round.
        Assert.Single(registry.Prepared);
        Assert.True(registry.Asked.Count >= 2);
        Assert.All(registry.Asked, context => Assert.Equal("add milk to my todoist", context.Request));
        var call = Assert.Single(executor.Calls);
        Assert.Equal("add milk to my todoist", call.Context.Request);
        Assert.Equal(registry.Prepared[0].ConversationId, call.Context.ConversationId);
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains(model.Requests[1].Tools, tool => tool.Name == "mcp_todoist_create_task");
    }

    [Fact]
    public async Task ARegistryThatFailsToLoadCannotFailTheTurn()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Still here.")]);
        var registry = new LoadingRegistry(Definition("search_files")) { OnPrepare = (_, _) => throw new InvalidOperationException("the app exploded") };
        var session = ConversationSession.Start(new TestClock(Start));

        var chunks = await ReadAsync(Orchestrator(model, registry, new AssistantOrchestratorToolTests.FakeToolExecutor("{}")).AskAsync(session, "hello"));

        Assert.Contains(chunks, chunk => chunk.Type == AssistantResponseChunkType.TextDelta);
        Assert.Equal(["search_files"], Assert.Single(model.Requests).Tools.Select(tool => tool.Name));
    }

    [Fact]
    public async Task ATurnThatIsCancelledWhileToolsLoadStopsAndNeverAsksTheModel()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Never.")]);
        using var cancel = new CancellationTokenSource();
        var registry = new LoadingRegistry(Definition("search_files"))
        {
            OnPrepare = async (_, token) =>
            {
                await cancel.CancelAsync();
                token.ThrowIfCancellationRequested();
            },
        };
        var session = ConversationSession.Start(new TestClock(Start));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadAsync(Orchestrator(model, registry, new AssistantOrchestratorToolTests.FakeToolExecutor("{}")).AskAsync(session, "hello", cancellationToken: cancel.Token)));

        Assert.Empty(model.Requests);
        Assert.Empty(session.Conversation.Messages);
    }

    [Fact]
    public async Task NothingIsPreparedForAModelThatCannotCallTools()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(new ModelInfo("plain", 4096), [AssistantResponseChunk.ForTextDelta("Hi.")]);
        var registry = new LoadingRegistry(Definition("search_files"));

        await ReadAsync(Orchestrator(model, registry, new AssistantOrchestratorToolTests.FakeToolExecutor("{}")).AskAsync(ConversationSession.Start(new TestClock(Start)), "hello"));

        Assert.Empty(registry.Prepared);
        Assert.Empty(Assert.Single(model.Requests).Tools);
    }

    [Fact]
    public async Task NothingIsPreparedWhenThereIsNothingToRunToolsWith()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Hi.")]);
        var registry = new LoadingRegistry(Definition("search_files"));

        await ReadAsync(Orchestrator(model, registry, executor: null).AskAsync(ConversationSession.Start(new TestClock(Start)), "hello"));

        Assert.Empty(registry.Prepared);
    }

    [Fact]
    public async Task ARequestIsNotKeptOrLoggedByTheHook()
    {
        var model = new AssistantOrchestratorToolTests.SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Done.")]);
        var registry = new LoadingRegistry(Definition("search_files"));

        await ReadAsync(Orchestrator(model, registry, new AssistantOrchestratorToolTests.FakeToolExecutor("{}")).AskAsync(ConversationSession.Start(new TestClock(Start)), "a private request"));

        Assert.DoesNotContain("private request", registry.Prepared[0].ToString(), StringComparison.Ordinal);
    }
}
