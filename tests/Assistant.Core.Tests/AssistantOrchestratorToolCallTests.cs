using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Assistant.Core.Tests.AssistantOrchestratorToolTests;

namespace Assistant.Core.Tests;

/// <summary>
/// What the orchestrator does with the tool calls the model streams (PROJECT_SPEC §4.8, §5.5): they are read and made tidy before
/// anything runs, a call that cannot be read is answered with why without asking the executor, only so many are run at once, and the
/// tools offered are decided again for each round.
/// </summary>
public sealed class AssistantOrchestratorToolCallTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly TestClock _clock = new(Start);

    private static readonly ToolDefinition Search = new(
        "search_files", "Find the user's files.", """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""", RiskLevel.ReadOnly);

    private static readonly ToolDefinition Volume = ToolDefinition.Create(
        "set_volume", "Sets the volume.", [new ToolParameter("percent", ToolParameterType.Integer, "The volume.", Minimum: 0, Maximum: 100)],
        RiskLevel.SideEffect);

    private static ModelInfo ToolModel => new("test-model", 8192) { SupportsToolCalling = true };

    private static AssistantResponseChunk Call(string name, string arguments, string id = "call_0") =>
        AssistantResponseChunk.ForToolCall(new ToolCall(id, name, arguments));

    private AssistantOrchestrator Orchestrator(SequencedModel model, IToolExecutor executor, IToolRegistry registry) =>
        new(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), _clock, NullLogger<AssistantOrchestrator>.Instance,
            toolRegistry: registry, toolExecutor: executor);

    private static async Task<List<AssistantResponseChunk>> ReadAllAsync(IAsyncEnumerable<AssistantResponseChunk> chunks)
    {
        var all = new List<AssistantResponseChunk>();
        await foreach (var chunk in chunks)
        {
            all.Add(chunk);
        }

        return all;
    }

    [Fact]
    public async Task TheCallIsMadeTidyBeforeItIsRun_AndTheConversationHoldsTheTidyCall()
    {
        var model = new SequencedModel(
            ToolModel,
            [Call(" functions.search_files ", "```json\n{ \"query\" : \"milestone\" }\n```")],
            [AssistantResponseChunk.ForTextDelta("Found it.")]);
        var executor = new FakeToolExecutor("""{"found":1}""");
        var session = ConversationSession.Start(_clock);

        var chunks = await ReadAllAsync(Orchestrator(model, executor, new FakeToolRegistry(Search)).AskAsync(session, "find milestone"));

        var ran = Assert.Single(executor.Calls).Call;
        Assert.Equal(("search_files", """{"query":"milestone"}"""), (ran.ToolName, ran.ArgumentsJson));
        var shown = Assert.Single(chunks, chunk => chunk.Type == AssistantResponseChunkType.ToolCall).ToolCall!;
        Assert.Equal(("search_files", """{"query":"milestone"}"""), (shown.ToolName, shown.ArgumentsJson));
        var kept = Assert.Single(session.Conversation.Messages.SelectMany(message => message.ToolCalls));
        Assert.Equal("""{"query":"milestone"}""", kept.ArgumentsJson);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"query":"a","query":"b"}""")]
    [InlineData("[1]")]
    public async Task ACallThatCannotBeRead_IsAnsweredWithWhy_AndTheExecutorIsNeverAsked(string arguments)
    {
        var model = new SequencedModel(
            ToolModel,
            [Call("search_files", arguments)],
            [AssistantResponseChunk.ForTextDelta("Sorry.")]);
        var executor = new FakeToolExecutor("{}");
        var session = ConversationSession.Start(_clock);

        var chunks = await ReadAllAsync(Orchestrator(model, executor, new FakeToolRegistry(Search)).AskAsync(session, "find it"));

        Assert.Empty(executor.Calls);
        var result = Assert.Single(chunks, chunk => chunk.Type == AssistantResponseChunkType.ToolResult).ToolResult!;
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.InvalidArguments, code);
        Assert.Contains("search_files(query: text)", result.OutputJson, StringComparison.Ordinal);

        // The model reads it in the next round and answers.
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(MessageRole.Tool, model.Requests[1].Messages[^1].Role);
        Assert.Equal("Sorry.", session.Conversation.Messages[^1].Text);
    }

    [Fact]
    public async Task ACallThatNamesNoTool_IsLeftToTheExecutor_WhichKnowsWhatIsRegistered()
    {
        var model = new SequencedModel(
            ToolModel,
            [Call("run_powershell", """{"script":"dir"}""")],
            [AssistantResponseChunk.ForTextDelta("Done.")]);
        var executor = new FakeToolExecutor("{}");

        await ReadAllAsync(Orchestrator(model, executor, new FakeToolRegistry(Search)).AskAsync(ConversationSession.Start(_clock), "run it"));

        Assert.Equal("run_powershell", Assert.Single(executor.Calls).Call.ToolName);
    }

    [Fact]
    public async Task OnlySoManyCallsOfOneAnswerAreRun_TheRestAreToldSo()
    {
        var many = Enumerable.Range(0, AssistantOrchestrator.MaxCallsPerRound + 3)
            .Select(i => Call("search_files", $$"""{"query":"q{{i}}"}""", $"call_{i}")).ToList();
        var model = new SequencedModel(ToolModel, [.. many], [AssistantResponseChunk.ForTextDelta("Enough.")]);
        var executor = new FakeToolExecutor("{}");
        var session = ConversationSession.Start(_clock);

        var chunks = await ReadAllAsync(Orchestrator(model, executor, new FakeToolRegistry(Search)).AskAsync(session, "look for all"));

        Assert.Equal(AssistantOrchestrator.MaxCallsPerRound, executor.Calls.Count);
        var results = chunks.Where(chunk => chunk.Type == AssistantResponseChunkType.ToolResult).Select(chunk => chunk.ToolResult!).ToList();
        Assert.Equal(many.Count, results.Count);
        Assert.All(results.Take(AssistantOrchestrator.MaxCallsPerRound), result => Assert.Equal(ToolResultStatus.Succeeded, result.Status));
        Assert.All(results.Skip(AssistantOrchestrator.MaxCallsPerRound), result =>
        {
            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
            Assert.Equal(ToolErrors.TooManyCalls, code);
        });

        // Every call has its result in the conversation, so the model's next prompt is whole.
        var calls = session.Conversation.Messages.SelectMany(message => message.ToolCalls).Select(call => call.Id).ToList();
        var answered = session.Conversation.Messages.Where(message => message.ToolResult is not null).Select(message => message.ToolResult!.ToolCallId).ToList();
        Assert.Equal(calls, answered);
    }

    [Fact]
    public async Task TheSameCallTwice_IsAFailureWithItsOwnCode()
    {
        var model = new SequencedModel(
            ToolModel,
            [Call("search_files", """{"query":"a"}""", "c1"), Call("search_files", """{ "query": "a" }""", "c2")],
            [AssistantResponseChunk.ForTextDelta("Done.")]);
        var executor = new FakeToolExecutor("{}");

        var chunks = await ReadAllAsync(Orchestrator(model, executor, new FakeToolRegistry(Search)).AskAsync(ConversationSession.Start(_clock), "find a"));

        Assert.Single(executor.Calls);
        var repeat = chunks.Where(chunk => chunk.Type == AssistantResponseChunkType.ToolResult).Select(chunk => chunk.ToolResult!).Last();
        Assert.True(ToolErrors.TryRead(repeat.OutputJson, out var code, out var message));
        Assert.Equal(ToolErrors.Repeated, code);
        Assert.Contains("already made", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AToolThatThrows_IsAStructuredFailure()
    {
        var model = new SequencedModel(ToolModel, [Call("search_files", """{"query":"a"}""")], [AssistantResponseChunk.ForTextDelta("No.")]);
        var executor = new FakeToolExecutor("{}") { Throws = new InvalidOperationException("secret C:\\private") };

        var chunks = await ReadAllAsync(Orchestrator(model, executor, new FakeToolRegistry(Search)).AskAsync(ConversationSession.Start(_clock), "find a"));

        var result = Assert.Single(chunks, chunk => chunk.Type == AssistantResponseChunkType.ToolResult).ToolResult!;
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.Failed, code);
        Assert.DoesNotContain("private", result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheToolsOfferedAreDecidedAgainForEachRound()
    {
        // A tool that is only worth offering once another has run (a screenshot that was taken can be read).
        var registry = new GrowingRegistry(Search, Volume);
        var model = new SequencedModel(
            ToolModel,
            [Call("search_files", """{"query":"a"}""")],
            [AssistantResponseChunk.ForTextDelta("Done.")]);
        var executor = new FakeToolExecutor("{}");
        registry.OnExecuted(executor);

        await ReadAllAsync(Orchestrator(model, executor, registry).AskAsync(ConversationSession.Start(_clock), "go"));

        Assert.Equal([Search.Name], model.Requests[0].Tools.Select(tool => tool.Name));
        Assert.Equal([Search.Name, Volume.Name], model.Requests[1].Tools.Select(tool => tool.Name));
    }

    // A registry that offers its second tool only after the executor has run a call.
    private sealed class GrowingRegistry(ToolDefinition first, ToolDefinition second) : IToolRegistry
    {
        private FakeToolExecutor? _executor;

        public IReadOnlyList<ToolDefinition> Tools { get; } = [first, second];

        public void OnExecuted(FakeToolExecutor executor) => _executor = executor;

        public IReadOnlyList<ToolDefinition> ToolsFor(ToolContext context) =>
            _executor is { Calls.Count: > 0 } ? [first, second] : [first];

        public ToolDefinition? Find(string name) => Tools.FirstOrDefault(tool => tool.Name == name);
    }
}
