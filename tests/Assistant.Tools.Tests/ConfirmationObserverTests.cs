using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// What the executor tells the activity log about the question it asks (PROJECT_SPEC §4.8, steps 115 and 117): that the user was asked, and what they answered, never what was
/// asked. A call that is refused before anyone is asked, or only reads, tells nothing; a log that fails never fails the call.
/// </summary>
public sealed class ConfirmationObserverTests
{
    private static ToolCall Call() => new("call-1", "change_thing", """{"name":"report"}""");

    private sealed class Observer(List<string> events, Exception? failure = null) : IConfirmationObserver
    {
        public void Asking()
        {
            events.Add("asking");
            if (failure is not null)
            {
                throw failure;
            }
        }

        public void Answered(ConfirmationDecision decision)
        {
            events.Add("answered:" + decision);
            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    private sealed class Thing(List<string> events, RiskLevel risk = RiskLevel.SideEffect, ToolResult? refusal = null) : ITool
    {
        public ToolDefinition Definition { get; } = ToolDefinition.Create(
            "change_thing", "Changes a thing.", [new ToolParameter("name", ToolParameterType.String, "Which thing.")], risk);

        public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

        public Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(
                refusal is not null
                    ? ToolPlan.Refuse(refusal)
                    : ToolPlan.Do(token => RunAsync(call, arguments, context, token), new ToolConfirmation(ConfirmationKind.Other, "Change it?", [new ConfirmationDetail("Thing", "report")], "Change")));

        public Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
        {
            events.Add("run");
            return Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}"));
        }
    }

    private sealed class Pending : IPermissionService
    {
        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ConfirmationDecision> ConfirmToolCallAsync(
            ToolDefinition tool, ToolCall call, ToolContext context, ToolConfirmation confirmation, CancellationToken cancellationToken = default)
        {
            Asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return ConfirmationDecision.Approved;
        }
    }

    private sealed class Throwing : IPermissionService
    {
        public Task<ConfirmationDecision> ConfirmToolCallAsync(
            ToolDefinition tool, ToolCall call, ToolContext context, ToolConfirmation confirmation, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("nothing could ask");
    }

    private static ToolContext Context(Observer observer) => new(Guid.NewGuid(), "change the thing", Confirmations: observer);

    [Fact]
    public async Task AYesIsToldBeforeTheToolRuns_AfterTheQuestionIsAsked()
    {
        var events = new List<string>();
        var executor = new ToolExecutor([new Thing(events)], new FakeConfirmation(approve: true));

        var result = await executor.ExecuteAsync(Call(), Context(new Observer(events)));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(["asking", "answered:Approved", "run"], events);
    }

    [Theory]
    [InlineData(ConfirmationDecision.Declined)]
    [InlineData(ConfirmationDecision.NoAnswer)]
    [InlineData(ConfirmationDecision.CouldNotAsk)]
    public async Task EveryOtherAnswerIsToldToo_AndTheToolDoesNotRun(ConfirmationDecision decision)
    {
        var events = new List<string>();
        var executor = new ToolExecutor([new Thing(events)], new FakeConfirmation(approve: false) { Decision = decision });

        var result = await executor.ExecuteAsync(Call(), Context(new Observer(events)));

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Equal(["asking", "answered:" + decision], events);
    }

    [Fact]
    public async Task WithNoOneToAskItIsToldThatTheUserCouldNotBeAsked_WithoutAskingAnyone()
    {
        var events = new List<string>();
        var executor = new ToolExecutor([new Thing(events)]);

        var result = await executor.ExecuteAsync(Call(), Context(new Observer(events)));

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Equal(["answered:CouldNotAsk"], events);
    }

    [Fact]
    public async Task AQuestionThatCouldNotBeAskedBecauseItFailedIsToldAsSuch()
    {
        var events = new List<string>();
        var executor = new ToolExecutor([new Thing(events)], new Throwing());

        var result = await executor.ExecuteAsync(Call(), Context(new Observer(events)));

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Equal(["asking", "answered:CouldNotAsk"], events);
    }

    [Fact]
    public async Task ACallThatIsRefusedBeforeAnyoneIsAsked_TellsNothing()
    {
        var events = new List<string>();
        var refusal = ToolErrors.Result(Call(), ToolResultStatus.Failed, ToolErrors.Failed, "No such thing.");
        var executor = new ToolExecutor([new Thing(events, refusal: refusal)], new FakeConfirmation(approve: true));

        await executor.ExecuteAsync(Call(), Context(new Observer(events)));

        Assert.Empty(events);
    }

    [Fact]
    public async Task AToolThatOnlyReadsTellsNothing_ItIsNotAsked()
    {
        var events = new List<string>();
        var executor = new ToolExecutor([new Thing(events, RiskLevel.ReadOnly)], new FakeConfirmation(approve: true));

        await executor.ExecuteAsync(Call(), Context(new Observer(events)));

        Assert.Equal(["run"], events);
    }

    [Fact]
    public async Task AQuestionThatIsStoppedWhileItWaits_IsAskedAndNeverAnswered()
    {
        var events = new List<string>();
        var question = new Pending();
        var executor = new ToolExecutor([new Thing(events)], question);
        using var stop = new CancellationTokenSource();

        var running = executor.ExecuteAsync(Call(), Context(new Observer(events)), stop.Token);
        await question.Asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(["asking"], events);
    }

    [Fact]
    public async Task ALogThatFailsNeverFailsTheCall()
    {
        var events = new List<string>();
        var executor = new ToolExecutor([new Thing(events)], new FakeConfirmation(approve: true));

        var result = await executor.ExecuteAsync(Call(), Context(new Observer(events, new InvalidOperationException("log"))));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(["asking", "answered:Approved", "run"], events);
    }

    [Fact]
    public async Task WithoutAnObserverTheCallIsWhatItWas()
    {
        var events = new List<string>();
        var executor = new ToolExecutor([new Thing(events)], new FakeConfirmation(approve: true));

        var result = await executor.ExecuteAsync(Call(), new ToolContext(Guid.NewGuid(), "change"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(["run"], events);
    }
}
