using System.Runtime.CompilerServices;
using System.Text.Json;
using Assistant.Core.Agent;
using Assistant.Core.Audit;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Assistant.Core.Tests.AssistantOrchestratorToolTests;

namespace Assistant.Core.Tests;

/// <summary>
/// The agent loop reports to the activity log (PROJECT_SPEC §4.8, step 117): each tool call of a run is a step with its tool, its risk, how it ended and what the user answered;
/// the user can stop the run and it ends as stopped; and nothing a tool was called with or returned is in the log.
/// </summary>
public sealed class AgentTaskAuditTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private readonly TestClock _clock = new(Start);
    private readonly FakeAuditStore _store = new();
    private readonly AuditLog _log;

    private static readonly ToolDefinition Search = new(
        "search_files", "Find the user's files.", """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""", RiskLevel.ReadOnly);

    private static readonly ToolDefinition Open = new(
        "open_application", "Open an app.", """{"type":"object","properties":{"application":{"type":"string"}},"required":["application"]}""", RiskLevel.SideEffect);

    private static ModelInfo ToolModel => new("test-model", 8192) { SupportsToolCalling = true };

    public AgentTaskAuditTests() => _log = new AuditLog(_clock, NullLogger<AuditLog>.Instance, _store, new FixedSettings());

    private static AssistantResponseChunk Call(string name, string arguments, string id) => AssistantResponseChunk.ForToolCall(new ToolCall(id, name, arguments));

    private static AssistantResponseChunk Words(string text) => AssistantResponseChunk.ForTextDelta(text);

    private static IReadOnlyList<AssistantResponseChunk> Searching(string query, string id = "c1") => [Call("search_files", $$"""{"query":"{{query}}"}""", id)];

    private static IReadOnlyList<AssistantResponseChunk> Opening(string app, string id = "c2") => [Call("open_application", $$"""{"application":"{{app}}"}""", id)];

    private AssistantOrchestrator Orchestrator(IModelService model, IToolExecutor executor, IToolRegistry? registry = null, AgentLimits? limits = null, bool withLog = true) =>
        new(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), _clock, NullLogger<AssistantOrchestrator>.Instance,
            toolRegistry: registry ?? new FakeToolRegistry(Search, Open), toolExecutor: executor, agentLimits: limits, taskLog: withLog ? _log : null);

    private static async Task<List<AssistantResponseChunk>> ReadAllAsync(IAsyncEnumerable<AssistantResponseChunk> chunks)
    {
        var all = new List<AssistantResponseChunk>();
        await foreach (var chunk in chunks)
        {
            all.Add(chunk);
        }

        return all;
    }

    private async Task<AgentTaskSnapshot> TheOnlyTaskAsync()
    {
        await _log.FlushAsync();
        return Assert.Single(await _log.ListAsync(10)).Task!;
    }

    // ---- what is recorded ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARunThatCallsTwoTools_IsOneTaskWithTwoSteps_EachWithItsToolAndRisk()
    {
        var model = new SequencedModel(ToolModel, Searching("report"), Opening("notepad"), [Words("Done.")]);
        var session = ConversationSession.Start(_clock);

        await ReadAllAsync(Orchestrator(model, new ScriptedExecutor()).AskAsync(session, "look"));

        var task = await TheOnlyTaskAsync();
        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Equal(session.Conversation.Id, task.ConversationId);
        Assert.Equal(["search_files", "open_application"], task.Steps.Select(step => step.Name));
        Assert.Equal([RiskLevel.ReadOnly, RiskLevel.SideEffect], task.Steps.Select(step => step.Risk!.Value));
        Assert.All(task.Steps, step => Assert.Equal(AuditStatus.Succeeded, step.Status));
        Assert.Null(task.FailurePoint);
        Assert.True(task.IsWorthShowing);
        Assert.NotNull(task.EndedAt);
    }

    [Fact]
    public async Task ARunThatOnlyAnswersInWords_LeavesNoTask()
    {
        var model = new SequencedModel(ToolModel, [Words("Hello.")]);
        var announced = 0;
        _log.TaskStarted += (_, _) => announced++;

        await ReadAllAsync(Orchestrator(model, new ScriptedExecutor()).AskAsync(ConversationSession.Start(_clock), "hi"));

        Assert.Equal(0, announced);
        Assert.Empty(await _log.ListAsync(10));
        Assert.Empty(_store.Tasks);
    }

    [Fact]
    public async Task WithoutALogARunIsWhatItWas_AndTheExecutorIsGivenNoObserver()
    {
        var model = new SequencedModel(ToolModel, Searching("a"), [Words("Done.")]);
        var executor = new ScriptedExecutor();

        await ReadAllAsync(Orchestrator(model, executor, withLog: false).AskAsync(ConversationSession.Start(_clock), "look"));

        Assert.Null(Assert.Single(executor.Contexts).Confirmations);
        Assert.Empty(await _log.ListAsync(10));
    }

    [Fact]
    public async Task EachCallIsGivenTheObserverOfItsOwnStep()
    {
        var model = new SequencedModel(ToolModel, Searching("a"), Opening("notepad"), [Words("Done.")]);
        var executor = new ScriptedExecutor();

        await ReadAllAsync(Orchestrator(model, executor).AskAsync(ConversationSession.Start(_clock), "look"));

        Assert.Equal(2, executor.Contexts.Count);
        Assert.All(executor.Contexts, context => Assert.IsAssignableFrom<IAgentStepScope>(context.Confirmations));
        Assert.Equal([1, 2], executor.Contexts.Select(context => ((IAgentStepScope)context.Confirmations!).Sequence));
    }

    [Fact]
    public async Task WhatATaskWasGivenAndWhatItReturnedIsNotInTheLog()
    {
        const string SecretArgument = "hunter2-the-secret-argument";
        const string SecretResult = "private-result-text-from-a-tool";
        var model = new SequencedModel(
            ToolModel, [Call("search_files", $$"""{"query":"{{SecretArgument}}"}""", "c1")], [Words("Here is what I found.")]);
        var executor = new ScriptedExecutor { Output = $$"""{"found":"{{SecretResult}}"}""" };

        await ReadAllAsync(Orchestrator(model, executor).AskAsync(ConversationSession.Start(_clock), "find my taxes please"));
        await _log.FlushAsync();

        var everything = JsonSerializer.Serialize(await _log.ListAsync(10)) + JsonSerializer.Serialize(_store.Tasks.Values);
        Assert.DoesNotContain(SecretArgument, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretResult, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("taxes", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Here is what I found", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTaskIsKept()
    {
        var model = new SequencedModel(ToolModel, Searching("a"), [Words("Done.")]);

        await ReadAllAsync(Orchestrator(model, new ScriptedExecutor()).AskAsync(ConversationSession.Start(_clock), "look"));
        await _log.FlushAsync();

        var saved = Assert.Single(_store.Tasks.Values);
        Assert.Equal(AgentTaskStatus.Completed, saved.Status);
        Assert.Single(saved.Steps);
    }

    // ---- the user's answer --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStepThatWaitsForTheUser_IsSeenWaiting_AndKeepsWhatTheyAnswered()
    {
        var model = new SequencedModel(ToolModel, Opening("notepad", "c1"), [Words("Opened.")]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new ScriptedExecutor
        {
            Handler = async (call, context, token) =>
            {
                context.Confirmations!.Asking();
                asked.SetResult();
                await release.Task.WaitAsync(token);
                context.Confirmations.Answered(ConfirmationDecision.Approved);
                return new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}");
            },
        };
        AgentTaskSnapshot? waiting = null;
        _log.TaskStarted += (_, e) => waiting = e.Task.Snapshot;

        var run = ReadAllAsync(Orchestrator(model, executor).AskAsync(ConversationSession.Start(_clock), "open it"));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var whileWaiting = Assert.Single(await _log.ListAsync(10)).Task!;
        release.SetResult();
        await run;

        Assert.NotNull(waiting);
        Assert.Equal(AuditStatus.WaitingForYou, Assert.Single(whileWaiting.Steps).Status);
        Assert.True(whileWaiting.IsRunning);
        var done = await TheOnlyTaskAsync();
        var step = Assert.Single(done.Steps);
        Assert.Equal((AuditStatus.Succeeded, ConfirmationDecision.Approved), (step.Status, step.Confirmation));
    }

    [Fact]
    public async Task AStepTheUserDidNotAllow_IsDeclined_WithTheirAnswer_AndIsNotAFailure()
    {
        var model = new SequencedModel(ToolModel, Opening("notepad", "c1"), [Words("Okay, I didn't open it.")]);
        var executor = new ScriptedExecutor
        {
            Handler = (call, context, _) =>
            {
                context.Confirmations!.Asking();
                context.Confirmations.Answered(ConfirmationDecision.Declined);
                return Task.FromResult(ToolErrors.Result(call, ToolResultStatus.Declined, ToolErrors.Declined, "The user did not allow that."));
            },
        };

        await ReadAllAsync(Orchestrator(model, executor).AskAsync(ConversationSession.Start(_clock), "open it"));

        var task = await TheOnlyTaskAsync();
        var step = Assert.Single(task.Steps);
        Assert.Equal((AuditStatus.Declined, ConfirmationDecision.Declined, ToolErrors.Declined), (step.Status, step.Confirmation, step.ErrorCode));
        Assert.Null(task.FailurePoint);
        Assert.Equal(AgentTaskStatus.Completed, task.Status);
    }

    [Fact]
    public async Task AQuestionNobodyAnswered_IsTheRunsFailurePoint()
    {
        var model = new SequencedModel(ToolModel, Opening("notepad", "c1"), [Words("You didn't answer.")]);
        var executor = new ScriptedExecutor
        {
            Handler = (call, context, _) =>
            {
                context.Confirmations!.Asking();
                context.Confirmations.Answered(ConfirmationDecision.NoAnswer);
                return Task.FromResult(ToolErrors.Result(call, ToolResultStatus.Declined, ToolErrors.NoAnswer, "No answer."));
            },
        };

        await ReadAllAsync(Orchestrator(model, executor).AskAsync(ConversationSession.Start(_clock), "open it"));

        var task = await TheOnlyTaskAsync();
        Assert.Equal("Step 1 (Open an application) wasn't done: there was no answer in time", task.FailurePoint);
    }

    // ---- how a step ended ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AToolThatFailedKeepsItsCode_AndOneThatTookTooLongIsTimedOut()
    {
        var model = new SequencedModel(ToolModel, Searching("a", "c1"), Searching("b", "c2"), [Words("Neither worked.")]);
        var limits = new AgentLimits { MaxRoundsWithoutProgress = 4 };
        var calls = 0;
        var executor = new ScriptedExecutor
        {
            Handler = (call, _, _) => Task.FromResult(
                ++calls == 1
                    ? ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, "The tool failed. (a path C:\\private\\x.txt)")
                    : ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.TimedOut, "Too long.")),
        };

        await ReadAllAsync(Orchestrator(model, executor, limits: limits).AskAsync(ConversationSession.Start(_clock), "look"));

        var task = await TheOnlyTaskAsync();
        Assert.Equal([AuditStatus.Failed, AuditStatus.TimedOut], task.Steps.Select(step => step.Status));
        Assert.Equal([ToolErrors.Failed, ToolErrors.TimedOut], task.Steps.Select(step => step.ErrorCode));
        Assert.Equal("Step 2 (Search your files) took too long", task.FailurePoint);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(task), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACodeAToolMadeUpIsKeptAsTheGeneralOne()
    {
        var model = new SequencedModel(ToolModel, Searching("a"), [Words("It failed.")]);
        var executor = new ScriptedExecutor
        {
            Handler = (call, _, _) => Task.FromResult(ToolErrors.Result(call, ToolResultStatus.Failed, "token_abc123_rejected_by_server", "x")),
        };

        await ReadAllAsync(Orchestrator(model, executor).AskAsync(ConversationSession.Start(_clock), "look"));

        var step = Assert.Single((await TheOnlyTaskAsync()).Steps);
        Assert.Equal(ToolErrors.Failed, step.ErrorCode);
    }

    [Fact]
    public async Task ACallThatWasNotMade_IsInTheTaskWithItsReason_AndIsNotCountedAsAStep()
    {
        var model = new SequencedModel(ToolModel, Searching("a", "c1"), Searching("a", "c2"), [Words("Done.")]);

        await ReadAllAsync(Orchestrator(model, new ScriptedExecutor()).AskAsync(ConversationSession.Start(_clock), "look"));

        var task = await TheOnlyTaskAsync();
        Assert.Equal([AuditStatus.Succeeded, AuditStatus.Skipped], task.Steps.Select(step => step.Status));
        Assert.Equal(ToolErrors.Repeated, task.Steps[1].ErrorCode);
        Assert.Equal(1, task.StepCount);
        Assert.False(task.IsWorthShowing);
    }

    [Fact]
    public async Task AToolTheModelWasNotOffered_IsInTheTaskAsNotRun()
    {
        var model = new SequencedModel(ToolModel, [Call("open_application", """{"application":"x"}""", "c1")], [Words("Could not.")]);
        var registry = new HiddenToolRegistry(offered: [Search], known: [Search, Open]);
        var executor = new ScriptedExecutor();

        await ReadAllAsync(Orchestrator(model, executor, registry).AskAsync(ConversationSession.Start(_clock), "open"));

        var step = Assert.Single((await TheOnlyTaskAsync()).Steps);
        Assert.Equal((AuditStatus.Skipped, ToolErrors.UnknownTool, "open_application"), (step.Status, step.ErrorCode, step.Name));
        Assert.Empty(executor.Contexts);
    }

    // ---- stopping ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheUserStopsARunFromThePanel_AndItEndsStopped_WhereItWas()
    {
        var model = new SequencedModel(ToolModel, Searching("a", "c1"), Searching("b", "c2"), [Words("Done.")]);
        var executor = new ScriptedExecutor { Handler = WaitsForever };
        var session = ConversationSession.Start(_clock);
        IAgentTaskView? view = null;
        _log.TaskStarted += (_, e) => view = e.Task;
        using var caller = new CancellationTokenSource();

        var run = ReadAllAsync(Orchestrator(model, executor).AskAsync(session, "look", cancellationToken: caller.Token));
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(view!.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.False(caller.Token.IsCancellationRequested);
        var task = await TheOnlyTaskAsync();
        Assert.Equal(AgentTaskStatus.Cancelled, task.Status);
        Assert.Equal(AuditStatus.Cancelled, Assert.Single(task.Steps).Status);
        Assert.Equal("Stopped during step 1 (Search your files)", task.FailurePoint);
        Assert.True(task.CancelRequested);
        Assert.True(task.IsWorthShowing);
    }

    [Fact]
    public async Task TheUserStopsARunFromTheActivityPage_ThroughTheLog()
    {
        var model = new SequencedModel(ToolModel, Searching("a", "c1"), [Words("Done.")]);
        var executor = new ScriptedExecutor { Handler = WaitsForever };

        var run = ReadAllAsync(Orchestrator(model, executor).AskAsync(ConversationSession.Start(_clock), "look"));
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var id = Assert.Single(await _log.ListAsync(10)).Id;
        Assert.True(_log.CancelTask(id));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(AgentTaskStatus.Cancelled, (await TheOnlyTaskAsync()).Status);
    }

    [Fact]
    public async Task TheCallersOwnStopEndsTheTaskAsStoppedToo()
    {
        var model = new SequencedModel(ToolModel, Searching("a", "c1"), [Words("Done.")]);
        var executor = new ScriptedExecutor { Handler = WaitsForever };
        using var caller = new CancellationTokenSource();

        var run = ReadAllAsync(Orchestrator(model, executor).AskAsync(ConversationSession.Start(_clock), "look", cancellationToken: caller.Token));
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var task = await TheOnlyTaskAsync();
        Assert.Equal((AgentTaskStatus.Cancelled, AuditStatus.Cancelled), (task.Status, task.Steps[0].Status));
    }

    [Fact]
    public async Task APanelStopBetweenStepsStopsBeforeTheNextOneIsStarted()
    {
        var model = new SequencedModel(ToolModel, Searching("a", "c1"), Searching("b", "c2"), [Words("Done.")]);
        IAgentTaskView? view = null;
        _log.TaskStarted += (_, e) => view = e.Task;
        var executor = new ScriptedExecutor
        {
            Handler = (call, _, _) =>
            {
                // The user presses Cancel while the first tool is finishing; the second is never started.
                view!.Cancel();
                return Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}"));
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadAllAsync(Orchestrator(model, executor).AskAsync(ConversationSession.Start(_clock), "look")));

        Assert.Single(executor.Contexts);
        var task = await TheOnlyTaskAsync();
        Assert.Equal(AgentTaskStatus.Cancelled, task.Status);
        Assert.Equal("Stopped after step 1 (Search your files)", task.FailurePoint);
    }

    // ---- when a run cannot go on ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ARunThatUsedAllItsSteps_IsIncomplete_AndSaysWhere()
    {
        var model = new SequencedModel(ToolModel, Searching("a", "c1"), [Words("Here is what I have.")]);

        await ReadAllAsync(Orchestrator(model, new ScriptedExecutor(), limits: new AgentLimits { MaxToolRounds = 1 }).AskAsync(ConversationSession.Start(_clock), "look"));

        var task = await TheOnlyTaskAsync();
        Assert.Equal(AgentTaskStatus.Incomplete, task.Status);
        Assert.Equal("Used all the steps it is allowed after step 1 (Search your files)", task.FailurePoint);
        Assert.True(task.IsWorthShowing);
    }

    [Fact]
    public async Task AModelThatFailsAfterATool_EndsTheTaskFailed()
    {
        var model = new FailingAfterToolModel();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAllAsync(Orchestrator(model, new ScriptedExecutor()).AskAsync(ConversationSession.Start(_clock), "look")));

        var task = await TheOnlyTaskAsync();
        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal("The model couldn't go on after step 1 (Search your files)", task.FailurePoint);
    }

    [Fact]
    public async Task RunsInDifferentConversationsAreAnnouncedWithTheirOwn()
    {
        var announced = new List<Guid>();
        _log.TaskStarted += (_, e) => announced.Add(e.ConversationId);
        var first = ConversationSession.Start(_clock);
        var second = ConversationSession.Start(_clock);

        await ReadAllAsync(Orchestrator(new SequencedModel(ToolModel, Searching("a"), [Words("Done.")]), new ScriptedExecutor()).AskAsync(first, "look"));
        await ReadAllAsync(Orchestrator(new SequencedModel(ToolModel, Searching("a"), [Words("Done.")]), new ScriptedExecutor()).AskAsync(second, "look"));

        Assert.Equal([first.Conversation.Id, second.Conversation.Id], announced);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> WaitsForever(ToolCall call, ToolContext context, CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        return new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}");
    }

    private sealed class ScriptedExecutor : IToolExecutor
    {
        public List<ToolContext> Contexts { get; } = [];

        public string Output { get; init; } = "{}";

        public Func<ToolCall, ToolContext, CancellationToken, Task<ToolResult>>? Handler { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default) =>
            ExecuteAsync(call, new ToolContext(Guid.Empty), cancellationToken);

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            Started.TrySetResult();
            return Handler is not null
                ? Handler(call, context, cancellationToken)
                : Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, Output));
        }
    }

    // A registry that knows more tools than it offers: the model may not call what it was not given.
    private sealed class HiddenToolRegistry(IReadOnlyList<ToolDefinition> offered, IReadOnlyList<ToolDefinition> known) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Tools { get; } = offered;

        public ToolDefinition? Find(string name) => known.FirstOrDefault(tool => tool.Name == name);
    }

    // The first answer calls a tool; the next one fails.
    private sealed class FailingAfterToolModel : IModelService
    {
        private int _calls;

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) => Task.FromResult<ModelInfo?>(ToolModel);

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_calls++ > 0)
            {
                throw new InvalidOperationException("the model failed");
            }

            yield return Call("search_files", """{"query":"a"}""", "c1");
            await Task.CompletedTask;
        }
    }
}
