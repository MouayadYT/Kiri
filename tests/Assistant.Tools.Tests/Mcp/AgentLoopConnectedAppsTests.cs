using System.Runtime.CompilerServices;
using Assistant.Core.Agent;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Tools;
using Assistant.Tools.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Mcp;

/// <summary>
/// The agent loop with the real orchestrator, registry and executor, over built-in tools and an installed MCP integration together (PROJECT_SPEC §4.8,
/// step 114): one loop runs both kinds of tool under the same rules, only the integrations the request is about are loaded, and its bounds hold for a
/// tool of an integration as for a built-in one.
/// </summary>
public sealed class AgentLoopConnectedAppsTests
{
    private const string AddMilk = "Add 'buy milk' to my Todoist and tell me what 12*3 is";

    internal static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    internal sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal sealed class NoImages : IImagePreprocessor
    {
        public Task<Assistant.Core.Imaging.PreparedImage> PrepareAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    internal sealed class ScriptedModel(params IReadOnlyList<AssistantResponseChunk>[] rounds) : IModelService
    {
        private int _next;

        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelInfo?>(new ModelInfo("test-model", 8192) { SupportsToolCalling = true });

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            foreach (var chunk in _next < rounds.Length ? rounds[_next++] : [])
            {
                yield return chunk;
            }

            await Task.CompletedTask;
        }
    }

    internal static AssistantResponseChunk Call(string tool, string arguments, string id) =>
        AssistantResponseChunk.ForToolCall(new ToolCall(id, tool, arguments));

    internal static AssistantResponseChunk Words(string text) => AssistantResponseChunk.ForTextDelta(text);

    private static AssistantOrchestrator Orchestrator(ScriptedModel model, ConnectedAppsFixture apps, AgentTraceStore traces, AgentLimits? limits = null) =>
        new(
            model, new FixedSettings(), new PromptBuilder(), new NoImages(), new Clock(Start), NullLogger<AssistantOrchestrator>.Instance,
            toolRegistry: apps.Tools, toolExecutor: apps.Executor, traceSink: traces, agentLimits: limits);

    private static async Task<List<AssistantResponseChunk>> RunAsync(AssistantOrchestrator orchestrator, ConversationSession session, string prompt)
    {
        var chunks = new List<AssistantResponseChunk>();
        await foreach (var chunk in orchestrator.AskAsync(session, prompt))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static StubMcpClient TodoClient(InstalledIntegration _) => ConnectedAppsFixture.Todoist();

    [Fact]
    public async Task OneRunCallsABuiltInToolAndAToolOfAnIntegration_UnderTheSameRules()
    {
        var todo = ConnectedAppsFixture.Todoist();
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()], clients: _ => todo);
        var model = new ScriptedModel(
            [
                Call("calculate", """{"expression":"12*3"}""", "c1"),
                Call("mcp_todoist_create_task", """{"task_title":"buy milk"}""", "c2"),
            ],
            [Words("It is 36, and I added the task.")]);
        var traces = new AgentTraceStore();
        var session = ConversationSession.Start(new Clock(Start));

        var chunks = await RunAsync(Orchestrator(model, apps, traces), session, AddMilk);

        // Both kinds of tool were offered in the first request, and both ran.
        var offered = model.Requests[0].Tools.Select(tool => tool.Name).ToList();
        Assert.Contains("calculate", offered);
        Assert.Contains("mcp_todoist_create_task", offered);
        var results = chunks.Where(chunk => chunk.Type == AssistantResponseChunkType.ToolResult).Select(chunk => chunk.ToolResult!).ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Status == ToolResultStatus.Succeeded, result.OutputJson));
        Assert.Equal("createTask", Assert.Single(todo.Calls).Tool);
        Assert.Equal(1, apps.Confirmation.Asked);
        Assert.Equal("It is 36, and I added the task.", session.Conversation.Messages[^1].Text);

        var trace = Assert.Single(traces.Recent());
        Assert.Equal(["calculate", "mcp_todoist_create_task"], trace.Calls.Select(call => call.ToolName));
        Assert.All(trace.Calls, call => Assert.Equal(AgentCallDisposition.Ran, call.Disposition));
        Assert.Equal(AgentStopReason.Answered, trace.StopReason);
    }

    [Fact]
    public async Task AnIntegrationThatTheRequestIsNotAboutIsNeverLoaded_AndItsSchemasAreNeverOffered()
    {
        var notes = Sample.Remote("notes", "Jotter");
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp(), notes], clients: TodoClient);
        var model = new ScriptedModel([Call("calculate", """{"expression":"12*3"}""", "c1")], [Words("36.")]);

        await RunAsync(Orchestrator(model, apps, new AgentTraceStore()), ConversationSession.Start(new Clock(Start)), "what is 12*3");

        Assert.Equal(0, apps.Clients.CreateCalls);
        Assert.All(model.Requests, request => Assert.DoesNotContain(request.Tools, tool => ConnectedAppTools.IsConnectedAppTool(tool.Name)));
    }

    [Fact]
    public async Task OnlyTheIntegrationTheRequestIsAboutIsLoaded_AndAToolOfAnotherIsRefused()
    {
        var notes = Sample.Remote("notes", "Jotter");
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp(), notes], clients: TodoClient);
        var model = new ScriptedModel([Call("mcp_notes_create_task", """{"taskTitle":"x"}""", "c1")], [Words("I could not.")]);
        var traces = new AgentTraceStore();

        var chunks = await RunAsync(Orchestrator(model, apps, traces), ConversationSession.Start(new Clock(Start)), "Add 'buy milk' to my Todoist");

        Assert.Equal(1, apps.Clients.CreateCalls);
        var result = Assert.Single(chunks, chunk => chunk.Type == AssistantResponseChunkType.ToolResult).ToolResult!;
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.UnknownTool, code);
    }

    [Fact]
    public async Task AToolOfAnIntegrationThatNeverAnswers_IsGivenUpWhenTheRunsTimeIsUp_AndTheRunStillEndsInWords()
    {
        var todo = ConnectedAppsFixture.Todoist();
        todo.OnCall = async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Sample.Text("never");
        };
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()], clients: _ => todo);
        var model = new ScriptedModel(
            [Call("mcp_todoist_list_tasks", "{}", "c1")],
            [Words("The app did not answer in time.")]);
        var traces = new AgentTraceStore();
        var session = ConversationSession.Start(new Clock(Start));
        var limits = new AgentLimits { TotalTime = TimeSpan.FromMilliseconds(500) };

        var chunks = await RunAsync(Orchestrator(model, apps, traces, limits), session, "Show my tasks in Todoist");

        var result = Assert.Single(chunks, chunk => chunk.Type == AssistantResponseChunkType.ToolResult).ToolResult!;
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.TimedOut, code);
        Assert.Equal("The app did not answer in time.", session.Conversation.Messages[^1].Text);
        Assert.Empty(model.Requests[1].Tools);
        var trace = Assert.Single(traces.Recent());
        Assert.Equal(AgentStopReason.TimeLimit, trace.StopReason);
        Assert.Equal(AgentCallDisposition.Ran, trace.Calls.Single().Disposition);
    }

    [Fact]
    public async Task ABudgetKeepsTheToolsOfTheRequestAndLeavesTheRestOut()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()], clients: TodoClient);
        var model = new ScriptedModel([Words("Hi.")]);
        var orchestrator = new AssistantOrchestrator(
            model, new FixedSettings(), new PromptBuilder(), new NoImages(), new Clock(Start), NullLogger<AssistantOrchestrator>.Instance,
            toolRegistry: apps.Tools, toolExecutor: apps.Executor, toolSelector: new BudgetedToolSelector(new AgentToolBudget(MaxTools: 1)));

        await RunAsync(orchestrator, ConversationSession.Start(new Clock(Start)), "Add 'buy milk' to my Todoist");

        // calculate and the app's tools were all candidates; the one tool that fits the request is what the model is given.
        Assert.Equal(["mcp_todoist_create_task"], model.Requests[0].Tools.Select(tool => tool.Name));
    }
}
