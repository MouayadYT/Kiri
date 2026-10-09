using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;
using Assistant.Tools.Mcp;
using Assistant.Tools.Tests.Mcp;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// The confirmation gate (PROJECT_SPEC §4.8, step 115): a tool that changes something is planned, the user is shown exactly what it would do and asked, and it runs only on a yes,
/// in code and whatever the model said; every way the question can fail to be answered is a no, and nothing the tool does comes before the answer.
/// </summary>
public sealed class ConfirmationGateTests
{
    private static ToolCall Call(string arguments = """{"name":"report"}""") => new("call-1", "change_thing", arguments);

    private static ToolContext Context(IRunPause? pause = null) => new(Guid.NewGuid(), "change the thing", pause);

    private static ToolConfirmation Question(string title = "Change the thing?") =>
        new(ConfirmationKind.Other, title, [new ConfirmationDetail("Thing", "report")], "Change");

    private static (string Code, string Message) Failure(ToolResult result)
    {
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out var message), result.OutputJson);
        return (code, message);
    }

    // A tool that changes something, and says what it works out before it does it.
    private sealed class PlannedTool(RiskLevel risk = RiskLevel.SideEffect) : ITool
    {
        public ToolDefinition Definition { get; } = ToolDefinition.Create(
            "change_thing", "Changes a thing.", [new ToolParameter("name", ToolParameterType.String, "Which thing.")], risk);

        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

        public ToolConfirmation? Confirmation { get; init; }

        public ToolResult? Refusal { get; init; }

        public Func<CancellationToken, Task>? Planning { get; init; }

        public int Plans { get; private set; }

        public int Runs { get; private set; }

        public List<string> RunArguments { get; } = [];

        public async Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
        {
            Plans++;
            if (Planning is { } planning)
            {
                await planning(cancellationToken);
            }

            return Refusal is { } refusal ? ToolPlan.Refuse(refusal) : ToolPlan.Do(token => RunAsync(call, arguments, context, token), Confirmation);
        }

        public Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
        {
            Runs++;
            RunArguments.Add(arguments.GetRawText());
            return Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, """{"done":true}"""));
        }
    }

    // A question that waits for the test to answer it.
    private sealed class Pending : IPermissionService
    {
        private readonly TaskCompletionSource<ConfirmationDecision> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WhenAsked => _asked.Task;

        public ToolConfirmation? Shown { get; private set; }

        public ToolCall? Call { get; private set; }

        public void Answer(ConfirmationDecision decision) => _answer.TrySetResult(decision);

        public async Task<ConfirmationDecision> ConfirmToolCallAsync(
            ToolDefinition tool, ToolCall call, ToolContext context, ToolConfirmation confirmation, CancellationToken cancellationToken = default)
        {
            Shown = confirmation;
            Call = call;
            _asked.TrySetResult();
            return await _answer.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class Throwing(Exception failure) : IPermissionService
    {
        public Task<ConfirmationDecision> ConfirmToolCallAsync(
            ToolDefinition tool, ToolCall call, ToolContext context, ToolConfirmation confirmation, CancellationToken cancellationToken = default) =>
            throw failure;
    }

    private sealed class CountingPause : IRunPause
    {
        public int Paused { get; private set; }

        public int Resumed { get; private set; }

        public IDisposable Pause()
        {
            Paused++;
            return new Release(this);
        }

        private sealed class Release(CountingPause owner) : IDisposable
        {
            public void Dispose() => owner.Resumed++;
        }
    }

    // ---- The gate ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NothingIsDoneUntilTheUserHasSaidYes_NotEvenAStep()
    {
        var tool = new PlannedTool { Confirmation = Question() };
        var question = new Pending();
        var executor = new ToolExecutor([tool], question);

        var running = executor.ExecuteAsync(Call(), Context());
        await question.WhenAsked.WaitAsync(TimeSpan.FromSeconds(5));

        // The tool has worked out what it would do, and has done none of it.
        Assert.Equal(1, tool.Plans);
        Assert.Equal(0, tool.Runs);
        Assert.False(running.IsCompleted);

        question.Answer(ConfirmationDecision.Approved);
        var result = await running;

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(1, tool.Runs);
    }

    [Theory]
    [InlineData(ConfirmationDecision.Declined, ToolErrors.Declined)]
    [InlineData(ConfirmationDecision.NoAnswer, ToolErrors.NoAnswer)]
    [InlineData(ConfirmationDecision.CouldNotAsk, ToolErrors.CouldNotAsk)]
    public async Task EveryAnswerButYesIsANo_TheToolIsNotRun_AndTheModelIsToldInWords(ConfirmationDecision decision, string code)
    {
        var tool = new PlannedTool { Confirmation = Question() };
        var question = new Pending();
        question.Answer(decision);

        var result = await new ToolExecutor([tool], question).ExecuteAsync(Call(), Context());

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Equal(code, Failure(result).Code);
        Assert.Contains("nothing was done", Failure(result).Message, StringComparison.Ordinal);
        Assert.Equal(0, tool.Runs);
    }

    [Fact]
    public async Task ANoTellsTheModelNotToAskAgainOnItsOwn()
    {
        var tool = new PlannedTool { Confirmation = Question() };
        var question = new Pending();
        question.Answer(ConfirmationDecision.Declined);

        var result = await new ToolExecutor([tool], question).ExecuteAsync(Call(), Context());

        Assert.Contains("Do not try it again unless the user asks you to", Failure(result).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoOneToAsk_NothingIsPlannedOrDone()
    {
        var tool = new PlannedTool { Confirmation = Question() };

        var result = await new ToolExecutor([tool]).ExecuteAsync(Call(), Context());

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Equal(ToolErrors.CouldNotAsk, Failure(result).Code);
        Assert.Equal(0, tool.Runs);
    }

    [Fact]
    public async Task AQuestionThatCouldNotBeAskedIsNotAYes()
    {
        var tool = new PlannedTool { Confirmation = Question() };

        var result = await new ToolExecutor([tool], new Throwing(new InvalidOperationException("no window"))).ExecuteAsync(Call(), Context());

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Equal(ToolErrors.CouldNotAsk, Failure(result).Code);
        Assert.Equal(0, tool.Runs);
    }

    [Fact]
    public async Task StoppingTheAnswerWhileTheQuestionWaitsDoesNothing_AndIsNotAFailure()
    {
        var tool = new PlannedTool { Confirmation = Question() };
        var question = new Pending();
        using var stop = new CancellationTokenSource();
        var running = new ToolExecutor([tool], question).ExecuteAsync(Call(), Context(), stop.Token);
        await question.WhenAsked.WaitAsync(TimeSpan.FromSeconds(5));

        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(0, tool.Runs);
    }

    [Fact]
    public async Task AStopThatComesWithTheYesWins_NothingIsDone()
    {
        var tool = new PlannedTool { Confirmation = Question() };
        using var stop = new CancellationTokenSource();
        var question = new StopsWhileApproving(stop);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ToolExecutor([tool], question).ExecuteAsync(Call(), Context(), stop.Token));

        Assert.Equal(0, tool.Runs);
    }

    private sealed class StopsWhileApproving(CancellationTokenSource stop) : IPermissionService
    {
        public async Task<ConfirmationDecision> ConfirmToolCallAsync(
            ToolDefinition tool, ToolCall call, ToolContext context, ToolConfirmation confirmation, CancellationToken cancellationToken = default)
        {
            await stop.CancelAsync();
            return ConfirmationDecision.Approved;
        }
    }

    [Fact]
    public async Task ACallThatCannotBeDoneIsRefusedBeforeAnyoneIsAsked()
    {
        var refusal = ToolErrors.Result(new ToolCall("other-id", "other", "{}"), ToolResultStatus.Failed, ToolErrors.Failed, "No such thing. Ask the user which.");
        var tool = new PlannedTool { Refusal = refusal };
        var question = new FakeConfirmation(false);

        var result = await new ToolExecutor([tool], question).ExecuteAsync(Call(), Context());

        Assert.Equal(0, question.Asked);
        Assert.Equal(0, tool.Runs);
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("No such thing", Failure(result).Message, StringComparison.Ordinal);

        // It is the answer to the call that was made, whatever the tool wrote on it.
        Assert.Equal("call-1", result.ToolCallId);
        Assert.Equal("change_thing", result.ToolName);
    }

    [Fact]
    public async Task AToolThatFailsWhileWorkingOutWhatItWouldDo_IsAFailure_AndNobodyIsAsked()
    {
        var tool = new PlannedTool { Planning = _ => throw new InvalidOperationException("boom") };
        var question = new FakeConfirmation(false);

        var result = await new ToolExecutor([tool], question).ExecuteAsync(Call(), Context());

        Assert.Equal(ToolErrors.Failed, Failure(result).Code);
        Assert.Equal(0, question.Asked);
        Assert.Equal(0, tool.Runs);
    }

    [Fact]
    public async Task AToolThatNeverFinishesWorkingOutWhatItWouldDo_IsGivenUp_EvenIfItIgnoresItsToken()
    {
        var tool = new PlannedTool { Timeout = TimeSpan.FromMilliseconds(200), Planning = _ => new TaskCompletionSource().Task };
        var question = new FakeConfirmation(false);

        var result = await new ToolExecutor([tool], question).ExecuteAsync(Call(), Context());

        Assert.Equal(ToolErrors.TimedOut, Failure(result).Code);
        Assert.Equal(0, question.Asked);
        Assert.Equal(0, tool.Runs);
    }

    [Fact]
    public async Task TheTimeAToolMayTakeStartsWhenTheUserHasSaidYes_NotWhileTheyRead()
    {
        // The tool may take a fifth of a second; the user takes longer than that to read the question.
        var tool = new PlannedTool { Timeout = TimeSpan.FromMilliseconds(300), Confirmation = Question() };
        var question = new Pending();
        var running = new ToolExecutor([tool], question).ExecuteAsync(Call(), Context());
        await question.WhenAsked.WaitAsync(TimeSpan.FromSeconds(5));

        await Task.Delay(500);
        question.Answer(ConfirmationDecision.Approved);

        Assert.Equal(ToolResultStatus.Succeeded, (await running).Status);
    }

    [Fact]
    public async Task AReadOnlyToolIsNeverPlannedOrAskedAbout_AndADestructiveOneNeverRuns()
    {
        var reading = new PlannedTool(RiskLevel.ReadOnly) { Confirmation = Question() };
        var destroying = new PlannedTool(RiskLevel.Destructive) { Confirmation = Question() };
        var question = new FakeConfirmation(false);

        var read = await new ToolExecutor([reading], question).ExecuteAsync(Call(), Context());
        var destroyed = await new ToolExecutor([destroying], question).ExecuteAsync(Call(), Context());

        Assert.Equal(ToolResultStatus.Succeeded, read.Status);
        Assert.Equal(0, reading.Plans);
        Assert.Equal(ToolErrors.NotAllowed, Failure(destroyed).Code);
        Assert.Equal(0, destroying.Plans + destroying.Runs);
        Assert.Equal(0, question.Asked);
    }

    // ---- What the user is shown ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhatTheToolSaidItWouldDoIsWhatTheUserIsShown()
    {
        var tool = new PlannedTool { Confirmation = Question("Change this thing?") };
        var question = new Pending();
        question.Answer(ConfirmationDecision.Approved);

        await new ToolExecutor([tool], question).ExecuteAsync(Call(), Context());

        Assert.Equal("Change this thing?", question.Shown!.Title);
        Assert.Equal("Change", question.Shown.ApproveLabel);
    }

    [Fact]
    public async Task AToolThatSaysNothingIsAskedAboutByItsOwnArguments_EveryOneOfThem_AsTheyWereGiven()
    {
        var tool = new PlannedTool();
        var question = new Pending();
        question.Answer(ConfirmationDecision.Approved);

        await new ToolExecutor([tool], question).ExecuteAsync(Call("""{"name":"first line\nsecond line"}"""), Context());

        Assert.Equal(ConfirmationKind.Other, question.Shown!.Kind);
        Assert.Equal("Allow “change_thing”?", question.Shown.Title);
        var line = Assert.Single(question.Shown.Details);
        Assert.Equal(("Name", "first line\nsecond line"), (line.Label, line.Value));
    }

    [Fact]
    public async Task WhatIsAskedAboutIsTheCallThatIsMade_AndNoOther()
    {
        var tool = new PlannedTool();
        var question = new Pending();
        question.Answer(ConfirmationDecision.Approved);

        // The model writes its call untidily; the call that is asked about and run is the tidy one, and the same.
        await new ToolExecutor([tool], question).ExecuteAsync(new ToolCall("c", "change_thing", """ { "name" : "report" } """), Context());

        Assert.Equal("""{"name":"report"}""", question.Call!.ArgumentsJson);
        Assert.Equal(["""{"name":"report"}"""], tool.RunArguments);
    }

    [Fact]
    public async Task TextThatWouldMakeTheQuestionLookLikeSomethingElseIsShownAsMarks()
    {
        var tool = new PlannedTool();
        var question = new Pending();
        question.Answer(ConfirmationDecision.Approved);

        // A character that turns what follows round, and one with no width.
        await new ToolExecutor([tool], question).ExecuteAsync(Call("{\"name\":\"safe\\u202Egnp.exe\\u200B\"}"), Context());

        var value = question.Shown!.Details[0].Value;
        Assert.DoesNotContain('\u202E', value);
        Assert.DoesNotContain('\u200B', value);
        Assert.Contains("⟨U+202E⟩", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallWithMoreInItThanAPersonCanReadIsNotMade_AndNobodyIsAskedToApproveWhatTheyCannotSee()
    {
        var definition = ToolDefinition.Create(
            "change_thing",
            "Changes a thing.",
            [.. Enumerable.Range(1, 4).Select(index => new ToolParameter("part" + index, ToolParameterType.String, "A part.", MaxLength: 4000))],
            RiskLevel.SideEffect);
        var tool = new LargeTool(definition);
        var question = new FakeConfirmation(false);
        var big = new string('x', 3_900);

        var result = await new ToolExecutor([tool], question).ExecuteAsync(
            Call(JsonSerializer.Serialize(new { part1 = big, part2 = big, part3 = big, part4 = big })), Context());

        Assert.Equal(ToolErrors.InvalidArguments, Failure(result).Code);
        Assert.Equal(0, question.Asked);
        Assert.Equal(0, tool.Runs);
    }

    private sealed class LargeTool(ToolDefinition definition) : ITool
    {
        public ToolDefinition Definition { get; } = definition;

        public int Runs { get; private set; }

        public Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}"));
        }
    }

    // ---- The run's clock -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRunsClockStandsStillWhileTheUserIsAsked()
    {
        var pause = new CountingPause();
        var tool = new PlannedTool { Confirmation = Question() };
        var question = new Pending();
        var running = new ToolExecutor([tool], question).ExecuteAsync(Call(), Context(pause));
        await question.WhenAsked.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((1, 0), (pause.Paused, pause.Resumed));

        question.Answer(ConfirmationDecision.Approved);
        await running;

        Assert.Equal((1, 1), (pause.Paused, pause.Resumed));
    }

    [Fact]
    public async Task TheClockRunsAgainWhenTheQuestionEndsInAStop()
    {
        var pause = new CountingPause();
        var question = new Pending();
        using var stop = new CancellationTokenSource();
        var running = new ToolExecutor([new PlannedTool { Confirmation = Question() }], question).ExecuteAsync(Call(), Context(pause), stop.Token);
        await question.WhenAsked.WaitAsync(TimeSpan.FromSeconds(5));

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Equal((1, 1), (pause.Paused, pause.Resumed));
    }

    // ---- A connected app's tool is no exception -----------------------------------------------------------------------------

    private static async Task<(ConnectedAppsFixture Apps, ToolContext Context)> PreparedAsync(bool confirm)
    {
        var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()], confirm: confirm);
        var context = ConnectedAppsFixture.Context("add a task to todoist and list my tasks");
        await apps.OfferedAsync(context);
        return (apps, context);
    }

    [Fact]
    public async Task ACreatedTaskInAConnectedAppIsAskedAboutWithTheAppAndEveryArgument_AndTheAppIsToldNothingBeforeTheYes()
    {
        var (apps, context) = await PreparedAsync(confirm: true);
        await using var cleanup = apps;

        var result = await apps.CallAsync(context, "mcp_todoist_create_task", """{"task_title":"buy milk","due_date":"tomorrow"}""");

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        var question = Assert.Single(apps.Confirmation.Shown);
        Assert.Equal(ConfirmationKind.ConnectedApp, question.Kind);
        Assert.Contains("Todoist", question.Title, StringComparison.Ordinal);
        Assert.Contains("createTask", question.Title, StringComparison.Ordinal);
        Assert.Equal(
            [("Task title", "buy milk"), ("Due date", "tomorrow")],
            question.Details.Select(line => (line.Label, line.Value)));
    }

    [Fact]
    public async Task ASideEffectOfAConnectedAppThatTheUserDeclinesNeverReachesTheApp()
    {
        var (apps, context) = await PreparedAsync(confirm: false);
        await using var cleanup = apps;

        var result = await apps.CallAsync(context, "mcp_todoist_create_task", """{"task_title":"buy milk"}""");

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Empty(apps.Clients.Created[0].Calls);
    }

    [Fact]
    public async Task AServerThatClaimsItsWriteOnlyReadsIsNotBelieved_EvenWhenTheUserTrustsServersHints()
    {
        var apps = new ConnectedAppsFixture(
            [ConnectedAppsFixture.TodoistApp(permissions => permissions with { TrustToolAnnotations = true })],
            clients: _ =>
            {
                var client = new StubMcpClient();
                client.Tools.Add(Sample.Tool("create_note", "Creates a note.", readOnly: true));
                client.Tools.Add(Sample.Tool("list_notes", "Lists the notes.", readOnly: true));
                return client;
            },
            confirm: false);
        await using var cleanup = apps;
        var context = ConnectedAppsFixture.Context("create a note and list my notes in todoist");
        await apps.OfferedAsync(context);

        var writes = await apps.CallAsync(context, "mcp_todoist_create_note", """{"text":"x"}""");
        var reads = await apps.CallAsync(context, "mcp_todoist_list_notes", """{"text":"x"}""");

        // The one whose name says it changes something is asked about (and declined); the one that only reads runs on the server's word.
        Assert.Equal(ToolResultStatus.Declined, writes.Status);
        Assert.Equal(1, apps.Confirmation.Asked);
        Assert.Equal(ToolResultStatus.Succeeded, reads.Status);
    }

    [Theory]
    [InlineData("create_task", true)]
    [InlineData("createTask", true)]
    [InlineData("tasks.delete", true)]
    [InlineData("send-message", true)]
    [InlineData("updateEvent", true)]
    [InlineData("list_tasks", false)]
    [InlineData("getSettings", false)]
    [InlineData("search_issues", false)]
    [InlineData("read_file", false)]
    [InlineData("do_it", false)]
    public void ANameThatSaysItChangesSomethingIsRecognized(string name, bool changes) =>
        Assert.Equal(changes, McpToolPolicy.NameSaysItChanges(name));
}
