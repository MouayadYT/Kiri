using System.Runtime.CompilerServices;
using Assistant.Core.Agent;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Assistant.Core.Tests.AssistantOrchestratorToolTests;

namespace Assistant.Core.Tests;

/// <summary>
/// The bounded agent loop (PROJECT_SPEC §4.8, step 114): rounds, calls and time are bounded, a run that goes in circles is stopped, a run always
/// ends in words, and how it went is kept in a trace that is not the conversation.
/// </summary>
public sealed class AgentRunnerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly TestClock _clock = new(Start);
    private readonly AgentTraceStore _traces = new();

    private static readonly ToolDefinition Search = new(
        "search_files", "Find the user's files.", """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""", RiskLevel.ReadOnly);

    private static readonly ToolDefinition Volume = new(
        "get_volume", "Read the speakers' volume.", """{"type":"object","properties":{}}""", RiskLevel.ReadOnly);

    private static ModelInfo ToolModel => new("test-model", 8192) { SupportsToolCalling = true };

    private static AssistantResponseChunk Call(string name, string arguments, string id = "call_0") =>
        AssistantResponseChunk.ForToolCall(new ToolCall(id, name, arguments));

    private static AssistantResponseChunk Words(string text) => AssistantResponseChunk.ForTextDelta(text);

    private static IReadOnlyList<AssistantResponseChunk> Search_(string query) => [Call("search_files", $$"""{"query":"{{query}}"}""")];

    private AssistantOrchestrator Orchestrator(
        IModelService model,
        IToolExecutor executor,
        IToolRegistry? registry = null,
        AgentLimits? limits = null,
        IAgentToolSelector? selector = null,
        IAgentTraceSink? sink = null) =>
        new(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), _clock, NullLogger<AssistantOrchestrator>.Instance,
            toolRegistry: registry ?? new FakeToolRegistry(Search, Volume), toolExecutor: executor,
            toolSelector: selector, traceSink: sink ?? _traces, agentLimits: limits);

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
    public void TheDefaultBounds_AreTheSpecsFourRounds_AndTheOthersAreFinite()
    {
        var limits = AgentLimits.Default;

        Assert.Equal(4, limits.MaxToolRounds);
        Assert.Equal(AssistantOrchestrator.MaxToolRounds, limits.MaxToolRounds);
        Assert.Equal(AssistantOrchestrator.MaxCallsPerRound, limits.MaxCallsPerRound);
        Assert.InRange(limits.MaxToolCalls, 1, 64);
        Assert.True(limits.TotalTime > TimeSpan.Zero && limits.FinalAnswerTime > TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void ABoundOutsideItsRange_IsRefused(int rounds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentLimits { MaxToolRounds = rounds });

    [Fact]
    public void TimesOutsideTheirRange_AreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentLimits { TotalTime = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentLimits { TotalTime = TimeSpan.FromHours(1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentLimits { FinalAnswerTime = TimeSpan.FromMinutes(10) });
    }

    [Fact]
    public async Task ARunThatReachesTheRoundLimit_AsksForTheFinalAnswerWithoutTools_AndSaysSo()
    {
        var model = new SequencedModel(ToolModel, Search_("a"), Search_("b"), [Words("Here is what I found.")]);
        var session = ConversationSession.Start(_clock);
        var limits = new AgentLimits { MaxToolRounds = 2 };

        await ReadAllAsync(Orchestrator(model, new FakeToolExecutor("""{"found":1}"""), limits: limits).AskAsync(session, "look"));

        Assert.Equal(3, model.Requests.Count);
        Assert.All(model.Requests.Take(2), request => Assert.NotEmpty(request.Tools));
        Assert.Empty(model.Requests[2].Tools);
        Assert.Contains(AssistantInstructions.FinalAnswerGuidance, model.Requests[2].Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain(AssistantInstructions.FinalAnswerGuidance, model.Requests[1].Instructions, StringComparison.Ordinal);
        Assert.Equal("Here is what I found.", session.Conversation.Messages[^1].Text);

        var trace = Assert.Single(_traces.Recent());
        Assert.Equal((AgentOutcome.Completed, AgentStopReason.RoundLimit), (trace.Outcome, trace.StopReason));
        Assert.Equal(3, trace.Steps.Count);
        Assert.True(trace.Steps[2].IsFinalAnswer);
        Assert.Empty(trace.Steps[2].OfferedTools);
    }

    [Fact]
    public async Task ARunThatAnswersAtOnce_HasNoBoundOnItsTrace()
    {
        var model = new SequencedModel(ToolModel, [Words("Hello.")]);

        await ReadAllAsync(Orchestrator(model, new FakeToolExecutor("{}")).AskAsync(ConversationSession.Start(_clock), "hi"));

        var trace = Assert.Single(_traces.Recent());
        Assert.Equal((AgentOutcome.Completed, AgentStopReason.Answered), (trace.Outcome, trace.StopReason));
        Assert.Equal(0, trace.ToolsRun);
        Assert.Equal(["search_files", "get_volume"], Assert.Single(trace.Steps).OfferedTools);
        Assert.False(trace.AssistantWrotePart);
    }

    [Fact]
    public async Task OnlySoManyCallsAreRunInAWholeRun_TheRestAreToldSo_AndTheModelMustAnswer()
    {
        var model = new SequencedModel(
            ToolModel,
            [Call("search_files", """{"query":"a"}""", "c1"), Call("search_files", """{"query":"b"}""", "c2")],
            [Call("search_files", """{"query":"c"}""", "c3"), Call("search_files", """{"query":"d"}""", "c4")],
            [Words("Enough.")]);
        var executor = new FakeToolExecutor("{}");
        var session = ConversationSession.Start(_clock);
        var limits = new AgentLimits { MaxToolCalls = 3, MaxToolRounds = 8 };

        var chunks = await ReadAllAsync(Orchestrator(model, executor, limits: limits).AskAsync(session, "look"));

        Assert.Equal(3, executor.Calls.Count);
        var results = chunks.Where(chunk => chunk.Type == AssistantResponseChunkType.ToolResult).Select(chunk => chunk.ToolResult!).ToList();
        Assert.Equal(4, results.Count);
        Assert.True(ToolErrors.TryRead(results[3].OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.TooManyCalls, code);

        // Three calls were run, which is the bound: the next request has no tools.
        Assert.Empty(model.Requests[2].Tools);
        var trace = Assert.Single(_traces.Recent());
        Assert.Equal(AgentStopReason.ToolCallLimit, trace.StopReason);
        Assert.Equal(3, trace.ToolsRun);
        Assert.Equal(AgentCallDisposition.OverLimit, trace.Calls.Last().Disposition);
    }

    [Fact]
    public async Task TimeThatRunsOutWhileTheModelIsAnswering_EndsTheRunWithAFinalAnswer()
    {
        var model = new ScriptedModel(
            ToolModel,
            ScriptedModel.Chunks(Search_("a")),
            ScriptedModel.Hangs("Let me think"),
            ScriptedModel.Chunks([Words("I ran short of time, but here is what I know.")]));
        var session = ConversationSession.Start(_clock);
        var limits = new AgentLimits { TotalTime = TimeSpan.FromMilliseconds(400), FinalAnswerTime = TimeSpan.FromSeconds(5) };

        var chunks = await ReadAllAsync(Orchestrator(model, new FakeToolExecutor("{}"), limits: limits).AskAsync(session, "look"));

        Assert.Equal("I ran short of time, but here is what I know.", session.Conversation.Messages[^1].Text);
        Assert.Equal(3, model.Requests.Count);
        Assert.Empty(model.Requests[2].Tools);
        Assert.Contains(AssistantInstructions.FinalAnswerGuidance, model.Requests[2].Instructions, StringComparison.Ordinal);
        Assert.Equal(AssistantResponseChunkType.TextDelta, chunks[^1].Type);
        var trace = Assert.Single(_traces.Recent());
        Assert.Equal((AgentOutcome.Completed, AgentStopReason.TimeLimit), (trace.Outcome, trace.StopReason));
        Assert.True(trace.Steps[1].TimedOut);
    }

    [Fact]
    public async Task TimeThatRunsOutWhileAToolRuns_GivesThatCallUp_AndEveryCallStillHasItsResult()
    {
        var model = new ScriptedModel(
            ToolModel,
            ScriptedModel.Chunks([Call("search_files", """{"query":"a"}""", "c1"), Call("get_volume", "{}", "c2")]),
            ScriptedModel.Chunks([Words("It took too long.")]));
        var executor = new FakeToolExecutor("{}") { WaitsForCancellation = true };
        var session = ConversationSession.Start(_clock);
        var limits = new AgentLimits { TotalTime = TimeSpan.FromMilliseconds(300) };

        var chunks = await ReadAllAsync(Orchestrator(model, executor, limits: limits).AskAsync(session, "look"));

        var results = chunks.Where(chunk => chunk.Type == AssistantResponseChunkType.ToolResult).Select(chunk => chunk.ToolResult!).ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
            Assert.Equal(ToolErrors.TimedOut, code);
        });

        // The call that was running was given up; the one after it was never started.
        Assert.Single(executor.Calls);
        var asked = session.Conversation.Messages.SelectMany(message => message.ToolCalls).Select(call => call.Id).ToList();
        var answered = session.Conversation.Messages.Where(message => message.ToolResult is not null).Select(message => message.ToolResult!.ToolCallId).ToList();
        Assert.Equal(asked, answered);
        Assert.Equal("It took too long.", session.Conversation.Messages[^1].Text);
        Assert.Empty(model.Requests[1].Tools);
        Assert.Equal(AgentStopReason.TimeLimit, Assert.Single(_traces.Recent()).StopReason);
    }

    [Fact]
    public async Task WhenTheTimeRunsOutAndTheModelSaysNothing_TheAssistantSaysSoInItsOwnWords()
    {
        var model = new ScriptedModel(
            ToolModel,
            ScriptedModel.Chunks(Search_("a")),
            ScriptedModel.Hangs(null),
            ScriptedModel.Hangs(null));
        var session = ConversationSession.Start(_clock);
        var limits = new AgentLimits { TotalTime = TimeSpan.FromMilliseconds(250), FinalAnswerTime = TimeSpan.FromMilliseconds(250) };

        var chunks = await ReadAllAsync(Orchestrator(model, new FakeToolExecutor("{}"), limits: limits).AskAsync(session, "look"));

        var last = chunks[^1];
        Assert.Equal((AssistantResponseChunkType.TextDelta, AgentRunner.TimeLimitText), (last.Type, last.Text));
        Assert.Equal(AgentRunner.TimeLimitText, session.Conversation.Messages[^1].Text);
        Assert.Equal(MessageRole.Assistant, session.Conversation.Messages[^1].Role);
        Assert.True(Assert.Single(_traces.Recent()).AssistantWrotePart);
    }

    [Fact]
    public async Task AModelThatSaysNothingAfterItsTools_IsNotLeftWithoutWords()
    {
        var model = new SequencedModel(ToolModel, Search_("a"), []);
        var session = ConversationSession.Start(_clock);

        var chunks = await ReadAllAsync(Orchestrator(model, new FakeToolExecutor("{}")).AskAsync(session, "look"));

        Assert.Equal(AgentRunner.NoAnswerText, chunks[^1].Text);
        Assert.Equal(AgentRunner.NoAnswerText, session.Conversation.Messages[^1].Text);
    }

    [Fact]
    public async Task ARunWithNoToolsThatSaysNothing_IsLeftAsItWas()
    {
        var model = new SequencedModel(ToolModel, []);
        var session = ConversationSession.Start(_clock);

        var chunks = await ReadAllAsync(Orchestrator(model, new FakeToolExecutor("{}")).AskAsync(session, "hi"));

        Assert.Empty(chunks);
        Assert.False(Assert.Single(_traces.Recent()).AssistantWrotePart);
    }

    [Fact]
    public async Task ARunThatKeepsRepeatingACall_IsStopped_BeforeTheRoundLimit()
    {
        var model = new SequencedModel(
            ToolModel,
            Search_("a"), Search_("a"), Search_("a"), Search_("a"),
            [Words("I could not do more.")]);
        var executor = new FakeToolExecutor("{}");
        var session = ConversationSession.Start(_clock);
        var limits = new AgentLimits { MaxToolRounds = 8 };

        await ReadAllAsync(Orchestrator(model, executor, limits: limits).AskAsync(session, "look"));

        // It ran once; the repeats were not run, and two rounds in a row that did nothing ended the run (not eight rounds).
        Assert.Single(executor.Calls);
        Assert.Equal(4, model.Requests.Count);
        Assert.Empty(model.Requests[3].Tools);
        var trace = Assert.Single(_traces.Recent());
        Assert.Equal(AgentStopReason.Looping, trace.StopReason);
        Assert.Equal(
            [AgentCallDisposition.Ran, AgentCallDisposition.Repeated, AgentCallDisposition.Repeated],
            trace.Calls.Select(call => call.Disposition));
    }

    [Fact]
    public async Task ARunThatRepeatsOldCallsRoundAfterRound_IsStopped_EvenWhileItAlsoMakesNewOnes()
    {
        var model = new SequencedModel(
            ToolModel,
            Search_("a"),
            [Call("search_files", """{"query":"b"}""", "b1"), Call("search_files", """{"query":"a"}""", "b2")],
            [Call("search_files", """{"query":"c"}""", "c1"), Call("search_files", """{"query":"a"}""", "c2")],
            [Call("search_files", """{"query":"d"}""", "d1"), Call("search_files", """{"query":"a"}""", "d2")],
            [Words("Enough.")]);
        var executor = new FakeToolExecutor("{}");
        var limits = new AgentLimits { MaxToolRounds = 8 };

        await ReadAllAsync(Orchestrator(model, executor, limits: limits).AskAsync(ConversationSession.Start(_clock), "look"));

        // Every round made something new, so no round was idle; the third repeat of the old call is what ended the run.
        Assert.Equal(4, executor.Calls.Count);
        Assert.Empty(model.Requests[4].Tools);
        Assert.Equal(AgentStopReason.Looping, Assert.Single(_traces.Recent()).StopReason);
    }

    [Fact]
    public async Task RoundsThatAllFail_AreStopped_Too()
    {
        var model = new SequencedModel(
            ToolModel,
            Search_("a"), Search_("b"),
            [Words("None of it worked.")]);
        var executor = new FailingExecutor();
        var session = ConversationSession.Start(_clock);
        var limits = new AgentLimits { MaxToolRounds = 8 };

        await ReadAllAsync(Orchestrator(model, executor, limits: limits).AskAsync(session, "look"));

        Assert.Equal(2, executor.Calls);
        var trace = Assert.Single(_traces.Recent());
        Assert.Equal(AgentStopReason.Looping, trace.StopReason);
        Assert.All(trace.Calls, call => Assert.Equal(ToolErrors.Failed, call.ErrorCode));
        Assert.Equal("None of it worked.", session.Conversation.Messages[^1].Text);
    }

    [Fact]
    public async Task AFailureFollowedByASuccess_IsNotALoop()
    {
        var model = new SequencedModel(ToolModel, Search_("a"), Search_("b"), Search_("c"), [Words("Done.")]);
        var executor = new FailingExecutor(failFirst: 1);
        var limits = new AgentLimits { MaxToolRounds = 8 };

        await ReadAllAsync(Orchestrator(model, executor, limits: limits).AskAsync(ConversationSession.Start(_clock), "look"));

        Assert.Equal(3, executor.Calls);
        Assert.Equal(AgentStopReason.Answered, Assert.Single(_traces.Recent()).StopReason);
    }

    [Fact]
    public async Task AToolTheModelWasNotOffered_IsNotRun_EvenWhenTheAppHasIt()
    {
        var model = new SequencedModel(ToolModel, [Call("get_volume", "{}")], [Words("I cannot.")]);
        var executor = new FakeToolExecutor("{}");
        var registry = new FakeToolRegistry(Search, Volume);
        var narrowed = new OnlyTheseTools(Search.Name);

        var chunks = await ReadAllAsync(Orchestrator(model, executor, registry, selector: narrowed).AskAsync(ConversationSession.Start(_clock), "volume"));

        Assert.Empty(executor.Calls);
        var result = Assert.Single(chunks, chunk => chunk.Type == AssistantResponseChunkType.ToolResult).ToolResult!;
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.UnknownTool, code);
        Assert.Equal(AgentCallDisposition.NotOffered, Assert.Single(_traces.Recent()).Calls.Single().Disposition);
    }

    [Fact]
    public async Task TheToolsAreChosenForTheRequestOnce_NotAgainForEveryRound()
    {
        var registry = new CountingRegistry(Search, Volume);
        var model = new SequencedModel(ToolModel, Search_("a"), Search_("b"), [Words("Done.")]);

        await ReadAllAsync(Orchestrator(model, new FakeToolExecutor("{}"), registry).AskAsync(ConversationSession.Start(_clock), "look for it"));

        Assert.Equal(1, registry.Prepared);
        Assert.Equal("look for it", registry.LastRequest);
        Assert.True(registry.Asked >= 3);
    }

    [Fact]
    public async Task TheSelectorDecidesWhatEveryRoundOffers()
    {
        var model = new SequencedModel(ToolModel, Search_("a"), [Words("Done.")]);
        var selector = new OnlyTheseTools(Search.Name);

        await ReadAllAsync(Orchestrator(model, new FakeToolExecutor("{}"), selector: selector).AskAsync(ConversationSession.Start(_clock), "find a"));

        Assert.All(model.Requests, request => Assert.Equal([Search.Name], request.Tools.Select(tool => tool.Name)));
        Assert.Equal([Search.Name], _traces.Recent()[0].Steps[0].OfferedTools);
    }

    [Fact]
    public async Task TheTrace_IsKeptApartFromTheConversation_AndHoldsNoPrivateContent()
    {
        const string Secret = "my private tax return 4417";
        var model = new SequencedModel(ToolModel, Search_(Secret), [Words("Found it, the answer is 4417.")]);
        var session = ConversationSession.Start(_clock);

        await ReadAllAsync(Orchestrator(model, new FakeToolExecutor($$"""{"name":"{{Secret}}"}""")).AskAsync(session, "find " + Secret));

        var trace = Assert.Single(_traces.Recent());
        Assert.Equal(session.Conversation.Id, trace.ConversationId);

        // Names, counts, durations and a fingerprint: nothing a prompt, an argument, a result or an answer said.
        var everything = trace.ToString() + string.Join(' ', trace.Steps.Select(step => step.ToString())) + string.Join(' ', trace.Calls.Select(call => call.ToString()));
        Assert.DoesNotContain("tax", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("4417", everything, StringComparison.Ordinal);
        var call = Assert.Single(trace.Calls);
        Assert.Equal(("search_files", AgentCallDisposition.Ran), (call.ToolName, call.Disposition));
        Assert.Equal(12, call.ArgumentsFingerprint.Length);
        Assert.True(call.ResultCharacters > 0);

        // And the conversation does not hold the trace: it holds the words and the tool exchange.
        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant],
            session.Conversation.Messages.Select(message => message.Role));
    }

    [Fact]
    public async Task ASinkThatFails_DoesNotFailTheRun()
    {
        var model = new SequencedModel(ToolModel, [Words("Hi.")]);
        var session = ConversationSession.Start(_clock);

        var chunks = await ReadAllAsync(Orchestrator(model, new FakeToolExecutor("{}"), sink: new ThrowingSink()).AskAsync(session, "hi"));

        Assert.Equal("Hi.", Assert.Single(chunks).Text);
    }

    [Fact]
    public async Task StoppingARun_IsATraceOfAStoppedRun_AndKeepsWhatWasDone()
    {
        var model = new SequencedModel(ToolModel, Search_("a"), [Words("never")]);
        var executor = new FakeToolExecutor("{}") { WaitsForCancellation = true };
        var session = ConversationSession.Start(_clock);
        using var stop = new CancellationTokenSource();

        var turn = ReadAllAsync(Orchestrator(model, executor).AskAsync(session, "look", cancellationToken: stop.Token));
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
        var trace = Assert.Single(_traces.Recent());
        Assert.Equal((AgentOutcome.Stopped, AgentStopReason.Cancelled), (trace.Outcome, trace.StopReason));
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task StoppingBetweenTwoCallsOfARound_DoesNotRunTheSecond()
    {
        var model = new SequencedModel(ToolModel, [Call("search_files", """{"query":"a"}""", "c1"), Call("get_volume", "{}", "c2")], [Words("never")]);
        using var stop = new CancellationTokenSource();
        var executor = new StoppingExecutor(stop);
        var session = ConversationSession.Start(_clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadAllAsync(Orchestrator(model, executor).AskAsync(session, "look", cancellationToken: stop.Token)));

        // The user's Stop is not the time running out: the second call is neither run nor answered as if it had timed out.
        Assert.Equal(1, executor.Calls);
        Assert.Single(session.Conversation.Messages, message => message.ToolResult is not null);
    }

    [Fact]
    public async Task AModelThatFails_IsAFailedTrace_AndTheErrorIsNotHidden()
    {
        var model = new ScriptedModel(ToolModel, ScriptedModel.Throws(new InvalidOperationException("engine gone")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAllAsync(Orchestrator(model, new FakeToolExecutor("{}")).AskAsync(ConversationSession.Start(_clock), "hi")));

        var trace = Assert.Single(_traces.Recent());
        Assert.Equal((AgentOutcome.Failed, AgentStopReason.Failed), (trace.Outcome, trace.StopReason));
    }

    [Fact]
    public void TheTraceStore_KeepsOnlyTheLastRuns_AndCanBeAskedByConversation()
    {
        var store = new AgentTraceStore();
        var mine = Guid.NewGuid();
        for (var i = 0; i < AgentTraceStore.Capacity + 5; i++)
        {
            store.Record(new AgentTrace(Guid.NewGuid(), i % 2 == 0 ? mine : Guid.NewGuid(), Start, TimeSpan.Zero, AgentOutcome.Completed, AgentStopReason.Answered, [], false));
        }

        Assert.Equal(AgentTraceStore.Capacity, store.Recent().Count);
        Assert.All(store.Recent(mine), trace => Assert.Equal(mine, trace.ConversationId));
    }

    // ---- the selector ----

    private static ToolDefinition Tool(string name, string description) =>
        new(name, description, """{"type":"object","properties":{}}""", RiskLevel.ReadOnly);

    [Fact]
    public void WhatFitsTheBudget_IsOfferedAsItIs()
    {
        IReadOnlyList<ToolDefinition> tools = [Tool("a_tool", "One."), Tool("b_tool", "Two.")];

        var chosen = new BudgetedToolSelector().Select(new ToolContext(Guid.NewGuid(), "anything"), tools);

        Assert.Same(tools, chosen);
    }

    [Fact]
    public void WhenThereAreTooMany_TheOnesTheRequestIsAboutAreKept_InTheirOrder()
    {
        var tools = new List<ToolDefinition>
        {
            Tool("search_files", "Find the user's files by name."),
            Tool("set_volume", "Set the speakers' volume."),
            Tool("get_calendar_events", "Read the events of the user's calendar."),
            Tool("mcp_todo_add_task", "Add a task to the todo list."),
            Tool("open_folder", "Open a folder."),
        };
        var selector = new BudgetedToolSelector(new AgentToolBudget(MaxTools: 2));

        var chosen = selector.Select(new ToolContext(Guid.NewGuid(), "add a task to my todo list"), tools);

        Assert.Contains(chosen, tool => tool.Name == "mcp_todo_add_task");
        Assert.Equal(2, chosen.Count);
        Assert.Equal(chosen.Select(tool => tool.Name), tools.Where(tool => chosen.Contains(tool)).Select(tool => tool.Name));
    }

    [Fact]
    public void WhenNothingMatches_TheEarlierRegisteredAreKept()
    {
        var tools = new List<ToolDefinition> { Tool("first_tool", "x"), Tool("second_tool", "y"), Tool("third_tool", "z") };
        var selector = new BudgetedToolSelector(new AgentToolBudget(MaxTools: 2));

        var chosen = selector.Select(new ToolContext(Guid.NewGuid(), "zzzzz qqqqq"), tools);

        Assert.Equal(["first_tool", "second_tool"], chosen.Select(tool => tool.Name));
    }

    [Fact]
    public void TheCharacterBudgetCountsToo_AndABigToolIsSkippedSoASmallOneCanFit()
    {
        var big = Tool("big_tool", new string('x', 500));
        var small = Tool("small_tool", "tiny");
        var selector = new BudgetedToolSelector(new AgentToolBudget(MaxTools: 5, MaxSchemaCharacters: 200));

        var chosen = selector.Select(new ToolContext(Guid.NewGuid(), null), [big, small]);

        Assert.Equal(["small_tool"], chosen.Select(tool => tool.Name));
    }

    [Fact]
    public void ARequestWordMatchesAToolWordWithAnEnding()
    {
        var tools = new List<ToolDefinition> { Tool("aaa_tool", "nothing here"), Tool("get_calendar_events", "events"), Tool("zzz_tool", "nothing here") };
        var selector = new BudgetedToolSelector(new AgentToolBudget(MaxTools: 1));

        var chosen = selector.Select(new ToolContext(Guid.NewGuid(), "what is in my calendars"), tools);

        Assert.Equal(["get_calendar_events"], chosen.Select(tool => tool.Name));
    }

    // ---- doubles ----

    private sealed class OnlyTheseTools(params string[] names) : IAgentToolSelector
    {
        public IReadOnlyList<ToolDefinition> Select(ToolContext context, IReadOnlyList<ToolDefinition> candidates) =>
            [.. candidates.Where(tool => names.Contains(tool.Name))];
    }

    private sealed class ThrowingSink : IAgentTraceSink
    {
        public void Record(AgentTrace trace) => throw new InvalidOperationException("disk full");
    }

    private sealed class CountingRegistry(params ToolDefinition[] tools) : IToolRegistry
    {
        public int Prepared { get; private set; }

        public int Asked { get; private set; }

        public string? LastRequest { get; private set; }

        public IReadOnlyList<ToolDefinition> Tools { get; } = tools;

        public Task PrepareToolsAsync(ToolContext context, CancellationToken cancellationToken = default)
        {
            Prepared++;
            LastRequest = context.Request;
            return Task.CompletedTask;
        }

        public IReadOnlyList<ToolDefinition> ToolsFor(ToolContext context)
        {
            Asked++;
            return Tools;
        }

        public ToolDefinition? Find(string name) => Tools.FirstOrDefault(tool => tool.Name == name);
    }

    // Answers its first call, and stops the run while doing so.
    private sealed class StoppingExecutor(CancellationTokenSource stop) : IToolExecutor
    {
        public int Calls { get; private set; }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            Calls++;
            await stop.CancelAsync();
            return new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}");
        }
    }

    // Fails every call, or only the first few.
    private sealed class FailingExecutor(int failFirst = int.MaxValue) : IToolExecutor
    {
        public int Calls { get; private set; }

        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(
                Calls <= failFirst
                    ? ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, "It failed.")
                    : new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}"));
        }
    }

    // A model whose answers are the next of a script: chunks, a wait that only a cancellation ends, or an exception.
    private sealed class ScriptedModel(ModelInfo active, params Func<CancellationToken, IAsyncEnumerable<AssistantResponseChunk>>[] answers) : IModelService
    {
        private int _next;

        public List<ModelRequest> Requests { get; } = [];

        public static Func<CancellationToken, IAsyncEnumerable<AssistantResponseChunk>> Chunks(IReadOnlyList<AssistantResponseChunk> chunks) =>
            token => Yield(chunks, null, null, token);

        public static Func<CancellationToken, IAsyncEnumerable<AssistantResponseChunk>> Hangs(string? textFirst) =>
            token => Yield([], textFirst, null, token);

        public static Func<CancellationToken, IAsyncEnumerable<AssistantResponseChunk>> Throws(Exception exception) =>
            token => Yield([], null, exception, token);

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) => Task.FromResult<ModelInfo?>(active);

        public IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return answers[_next++](cancellationToken);
        }

        private static async IAsyncEnumerable<AssistantResponseChunk> Yield(
            IReadOnlyList<AssistantResponseChunk> chunks, string? textFirst, Exception? failure, [EnumeratorCancellation] CancellationToken token)
        {
            if (failure is not null)
            {
                throw failure;
            }

            foreach (var chunk in chunks)
            {
                token.ThrowIfCancellationRequested();
                yield return chunk;
            }

            if (chunks.Count == 0)
            {
                if (textFirst is not null)
                {
                    yield return AssistantResponseChunk.ForTextDelta(textFirst);
                }

                await Task.Delay(Timeout.Infinite, token);
            }
        }
    }
}
