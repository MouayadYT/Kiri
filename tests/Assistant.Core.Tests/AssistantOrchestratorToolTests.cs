using System.Runtime.CompilerServices;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>A chat turn in which the model calls tools: they are run, their results join the conversation, and it answers again.</summary>
public sealed class AssistantOrchestratorToolTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly TestClock _clock = new(Start);

    private static readonly ToolDefinition Search = new(
        "search_files", "Find the user's files.", """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""", RiskLevel.ReadOnly);

    private static ModelInfo ToolModel => new("test-model", 8192) { SupportsToolCalling = true };

    private static AssistantResponseChunk Call(string name, string arguments, string id = "call_0") =>
        AssistantResponseChunk.ForToolCall(new ToolCall(id, name, arguments));

    private AssistantOrchestrator Orchestrator(SequencedModel model, FakeToolExecutor? executor = null, bool offerTools = true) =>
        new(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), _clock, NullLogger<AssistantOrchestrator>.Instance,
            toolRegistry: offerTools ? new FakeToolRegistry(Search) : null, toolExecutor: executor);

    [Fact]
    public async Task AModelThatCallsATool_IsGivenItsResult_AndAnswersWithIt()
    {
        var model = new SequencedModel(
            ToolModel,
            [Call("search_files", """{"query":"milestone"}""")],
            [AssistantResponseChunk.ForTextDelta("I found it.")]);
        var executor = new FakeToolExecutor("""{"found":1}""");
        var session = ConversationSession.Start(_clock);

        var chunks = await ReadAllAsync(Orchestrator(model, executor).AskAsync(session, "find milestone"));

        Assert.Equal(
            [AssistantResponseChunkType.ToolCall, AssistantResponseChunkType.ToolResult, AssistantResponseChunkType.TextDelta],
            chunks.Select(chunk => chunk.Type));
        var call = Assert.Single(executor.Calls);
        Assert.Equal(("search_files", """{"query":"milestone"}"""), (call.Call.ToolName, call.Call.ArgumentsJson));
        Assert.Equal(session.Conversation.Id, call.Context.ConversationId);

        // Both requests offered the tool; the second carried the model's call and what came of it after the user's message.
        Assert.All(model.Requests, request => Assert.Equal([Search], request.Tools));
        Assert.All(model.Requests, request => Assert.Equal(AssistantOrchestrator.ToolTemperature, request.Temperature));
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.Tool],
            model.Requests[1].Messages.Select(message => message.Role));
        Assert.Equal("""{"found":1}""", model.Requests[1].Messages[2].Text);
        Assert.Equal("search_files", Assert.Single(model.Requests[1].Messages[1].ToolCalls).ToolName);

        // The conversation holds the whole turn, and the tool's message says which call it answers.
        var messages = session.Conversation.Messages;
        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant],
            messages.Select(message => message.Role));
        Assert.Equal("I found it.", messages[3].Text);
        Assert.Equal(messages[1].ToolCalls[0].Id, messages[2].ToolResult!.ToolCallId);
    }

    [Fact]
    public async Task ANoticeAndTheUsersMessage_AreNotRepeatedByTheRoundsAfterTheFirst()
    {
        var model = new SequencedModel(
            ToolModel,
            [Call("search_files", """{"query":"a"}""")],
            [AssistantResponseChunk.ForTextDelta("Done.")]);
        var session = ConversationSession.Start(_clock);
        var image = new ContextItem(Guid.NewGuid(), ContextItemType.Screenshot, "Shot") { ImageData = new byte[] { 1, 2, 3 } };

        var chunks = await ReadAllAsync(Orchestrator(model, new FakeToolExecutor("{}")).AskAsync(session, "what is this", [image]));

        // The model cannot read images here, so the screenshot notice is told once, not again for the second round.
        Assert.Single(chunks, chunk => chunk.Type == AssistantResponseChunkType.Notice);
        Assert.Single(session.Conversation.Messages, message => message.Role == MessageRole.User);
    }

    [Fact]
    public async Task NoToolsAreOffered_WhenTheModelCannotCallThem_OrThereIsNothingToRunThemWith_AndACallIsNotRun()
    {
        var cannot = new SequencedModel(new ModelInfo("test-model", 8192), [Call("search_files", "{}")]);
        var executor = new FakeToolExecutor("{}");

        var chunks = await ReadAllAsync(Orchestrator(cannot, executor).AskAsync(ConversationSession.Start(_clock), "hi"));

        Assert.Empty(Assert.Single(cannot.Requests).Tools);
        Assert.Null(cannot.Requests[0].Temperature);
        Assert.Empty(executor.Calls);
        Assert.Equal([AssistantResponseChunkType.ToolCall], chunks.Select(chunk => chunk.Type));

        var nothingToRun = new SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Hi.")]);
        await ReadAllAsync(Orchestrator(nothingToRun, executor: null).AskAsync(ConversationSession.Start(_clock), "hi"));
        Assert.Empty(Assert.Single(nothingToRun.Requests).Tools);
    }

    [Fact]
    public async Task ATurnTakesAtMostFourRoundsOfTools_ThenTheModelMustAnswerWithoutThem()
    {
        var rounds = Enumerable.Range(0, 4)
            .Select(i => (IReadOnlyList<AssistantResponseChunk>)[Call("search_files", $$"""{"query":"look {{i}}"}""", "call_0")]).ToList();
        rounds.Add([AssistantResponseChunk.ForTextDelta("Enough.")]);
        var model = new SequencedModel(ToolModel, [.. rounds]);
        var executor = new FakeToolExecutor("{}");
        var session = ConversationSession.Start(_clock);

        await ReadAllAsync(Orchestrator(model, executor).AskAsync(session, "keep looking"));

        Assert.Equal(AssistantOrchestrator.MaxToolRounds, executor.Calls.Count);
        Assert.Equal(5, model.Requests.Count);
        Assert.All(model.Requests.Take(4), request => Assert.NotEmpty(request.Tools));
        Assert.Empty(model.Requests[4].Tools);
        Assert.Equal("Enough.", session.Conversation.Messages[^1].Text);

        // The engine called every one "call_0"; the conversation tells them apart.
        var ids = session.Conversation.Messages.SelectMany(message => message.ToolCalls).Select(call => call.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task TheSameCallAgainInOneAnswer_IsNotRunAgain_TheModelIsToldToUseItsResult()
    {
        var model = new SequencedModel(
            ToolModel,
            [Call("search_files", """{"query":"a"}""")],
            [Call("search_files", """{ "query": "a" }""", "call_1"), Call("search_files", """{"query":"b"}""", "call_2")],
            [AssistantResponseChunk.ForTextDelta("Done.")]);
        var executor = new FakeToolExecutor("{}");
        var session = ConversationSession.Start(_clock);

        var chunks = await ReadAllAsync(Orchestrator(model, executor).AskAsync(session, "find a"));

        // The first call and the new one ran; the repeat did not, and its result says why.
        Assert.Equal(["""{"query":"a"}""", """{"query":"b"}"""], executor.Calls.Select(call => call.Call.ArgumentsJson));
        var results = chunks.Where(chunk => chunk.Type == AssistantResponseChunkType.ToolResult).Select(chunk => chunk.ToolResult!).ToList();
        Assert.Equal(3, results.Count);
        Assert.Equal(ToolResultStatus.Failed, results[1].Status);
        Assert.Contains("already made", results[1].OutputJson, StringComparison.Ordinal);
        Assert.Equal("Done.", session.Conversation.Messages[^1].Text);
    }

    [Fact]
    public async Task AToolThatFails_IsAResultTheModelReads_NotAFailureOfTheAnswer()
    {
        var model = new SequencedModel(
            ToolModel,
            [Call("search_files", "{}")],
            [AssistantResponseChunk.ForTextDelta("I could not.")]);
        var executor = new FakeToolExecutor("{}") { Throws = new InvalidOperationException("boom") };
        var session = ConversationSession.Start(_clock);

        var chunks = await ReadAllAsync(Orchestrator(model, executor).AskAsync(session, "find it"));

        var result = Assert.Single(chunks, chunk => chunk.Type == AssistantResponseChunkType.ToolResult).ToolResult!;
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal("I could not.", session.Conversation.Messages[^1].Text);
    }

    [Fact]
    public async Task StoppingWhileAToolRuns_StopsTheTurn_AndKeepsWhatWasDone()
    {
        var model = new SequencedModel(ToolModel, [Call("search_files", "{}")], [AssistantResponseChunk.ForTextDelta("never")]);
        var executor = new FakeToolExecutor("{}") { WaitsForCancellation = true };
        var session = ConversationSession.Start(_clock);
        using var stop = new CancellationTokenSource();

        var turn = ReadAllAsync(Orchestrator(model, executor).AskAsync(session, "find it", cancellationToken: stop.Token));
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
        Assert.Single(model.Requests);
        Assert.Equal([MessageRole.User, MessageRole.Assistant], session.Conversation.Messages.Select(message => message.Role));
        await session.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheInstructionsTellTheModelHowToUseTheTools_OnlyWhenItHasThem()
    {
        var with = new SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("Hi.")]);
        var without = new SequencedModel(new ModelInfo("test-model", 8192), [AssistantResponseChunk.ForTextDelta("Hi.")]);

        await ReadAllAsync(Orchestrator(with, new FakeToolExecutor("{}")).AskAsync(ConversationSession.Start(_clock), "hi"));
        await ReadAllAsync(Orchestrator(without, new FakeToolExecutor("{}")).AskAsync(ConversationSession.Start(_clock), "hi"));

        Assert.Contains(AssistantInstructions.ToolGuidance, with.Requests[0].Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain(AssistantInstructions.NoToolGuidance, with.Requests[0].Instructions, StringComparison.Ordinal);
        Assert.Contains(AssistantInstructions.NoToolGuidance, without.Requests[0].Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain(AssistantInstructions.ToolGuidance, without.Requests[0].Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AToolExchangeTheAppMadeItself_IsInTheConversationTheModelIsAskedNext()
    {
        var session = ConversationSession.Start(_clock);
        var call = new ToolCall("call_a", "search_files", """{"query":"milestone"}""");
        var result = new ToolResult("call_a", "search_files", ToolResultStatus.Succeeded, """{"found":2}""");
        Assert.True(session.AddToolExchange("find milestone", call, result, "I found 2 files.", _clock.GetUtcNow()));
        var model = new SequencedModel(ToolModel, [AssistantResponseChunk.ForTextDelta("The first.")]);

        await ReadAllAsync(Orchestrator(model, new FakeToolExecutor("{}")).AskAsync(session, "the first one"));

        var sent = Assert.Single(model.Requests).Messages;
        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant, MessageRole.User],
            sent.Select(message => message.Role));
        Assert.Equal(("search_files", """{"found":2}"""), (sent[1].ToolCalls[0].ToolName, sent[2].Text));
        Assert.Equal("call_a", sent[2].ToolResult!.ToolCallId);
    }

    private static async Task<List<AssistantResponseChunk>> ReadAllAsync(IAsyncEnumerable<AssistantResponseChunk> chunks)
    {
        var all = new List<AssistantResponseChunk>();
        await foreach (var chunk in chunks)
        {
            all.Add(chunk);
        }

        return all;
    }

    /// <summary>A model that answers each request with the next list of chunks the test gave it, and records the requests.</summary>
    internal sealed class SequencedModel(ModelInfo active, params IReadOnlyList<AssistantResponseChunk>[] answers) : IModelService
    {
        private int _next;

        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) => Task.FromResult<ModelInfo?>(active);

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var answer = _next < answers.Length ? answers[_next++] : [];
            foreach (var chunk in answer)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
            }

            await Task.CompletedTask;
        }
    }

    internal sealed class FakeToolRegistry(params ToolDefinition[] tools) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Tools { get; } = tools;

        public ToolDefinition? Find(string name) => tools.FirstOrDefault(tool => tool.Name == name);
    }

    internal sealed class FakeToolExecutor(string output) : IToolExecutor
    {
        public List<(ToolCall Call, ToolContext Context)> Calls { get; } = [];

        public Exception? Throws { get; init; }

        public bool WaitsForCancellation { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default) =>
            ExecuteAsync(call, new ToolContext(Guid.Empty), cancellationToken);

        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add((call, context));
            Started.TrySetResult();
            if (Throws is not null)
            {
                throw Throws;
            }

            if (WaitsForCancellation)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, output);
        }
    }
}
