using Assistant.Core.Agent;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Events;
using Assistant.Core.Orchestration;
using Assistant.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Assistant.Core.Tests.AssistantOrchestratorToolTests;

namespace Assistant.Core.Tests;

/// <summary>
/// The question the Assistant asks before it changes anything (PROJECT_SPEC §4.8, step 115): text that is safe to show, a question that is always one a person can read,
/// a request that is answered once, a broker that treats every way of not being answered as a no, and a run whose clock stands still while the user reads.
/// </summary>
public sealed class ConfirmationTests
{
    private static readonly ToolDefinition Definition = new("send_message", "Sends.", """{"type":"object"}""", RiskLevel.SideEffect);
    private static readonly ToolCall Call = new("call-1", "send_message", """{"text":"hi"}""");

    private static ToolConfirmation Question(string title = "Send this message?") =>
        new(ConfirmationKind.SendMessage, title, [new ConfirmationDetail("Message", "hi")], "Send", "It cannot be taken back.");

    // ---- ConfirmationText -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("مرحبا بالعالم", "مرحبا بالعالم")]
    [InlineData("a 👨\u200D👩\u200D👧 b", "a 👨\u200D👩\u200D👧 b")]
    [InlineData("tab\there", "tab\there")]
    public void OrdinaryTextIsShownExactlyAsItIs_InAnyScript_EmojiIncluded(string text, string shown) =>
        Assert.Equal(shown, ConfirmationText.Visible(text, multiline: true));

    [Fact]
    public void LineBreaksAreMadeOneKind_OrSpacesInASingleLine()
    {
        Assert.Equal("a\nb\nc\nd", ConfirmationText.Visible("a\r\nb\rc\nd", multiline: true));
        Assert.Equal("a b c d", ConfirmationText.Visible("a\r\nb\rc\nd", multiline: false));
        Assert.Equal("a b c", ConfirmationText.Visible("a\u2028b\u2029c", multiline: false));
    }

    [Fact]
    public void AControlCharacterIsItsPicture_AndNeverDoesWhatItDoes()
    {
        Assert.Equal("a␀b", ConfirmationText.Visible("a\0b", multiline: true));
        Assert.Equal("a␛b", ConfirmationText.Visible("a\u001Bb", multiline: true));
        Assert.Equal("a␡b", ConfirmationText.Visible("a\u007Fb", multiline: true));
        Assert.Equal("a⟨U+0085⟩b".Replace("⟨U+0085⟩", "\n"), ConfirmationText.Visible("a\u0085b", multiline: true));
        Assert.Equal("a⟨U+0090⟩b", ConfirmationText.Visible("a\u0090b", multiline: true));
    }

    [Theory]
    [InlineData('\u202E')]
    [InlineData('\u202A')]
    [InlineData('\u2066')]
    [InlineData('\u2069')]
    [InlineData('\u200B')]
    [InlineData('\u200E')]
    [InlineData('\u061C')]
    [InlineData('\u2060')]
    [InlineData('\uFEFF')]
    [InlineData('\u00AD')]
    public void ACharacterThatTurnsTextAroundOrHasNoWidthIsAMark(char hidden)
    {
        var shown = ConfirmationText.Visible($"safe{hidden}.exe", multiline: false);

        Assert.DoesNotContain(hidden, shown);
        Assert.Equal($"safe⟨U+{(int)hidden:X4}⟩.exe", shown);
    }

    [Fact]
    public void ATagCharacterThatHidesTextInPlainSightIsAMarkToo()
    {
        var tag = char.ConvertFromUtf32(0xE0041);

        Assert.Equal("a⟨U+E0041⟩b", ConfirmationText.Visible("a" + tag + "b", multiline: false));
    }

    [Fact]
    public void ALoneSurrogateIsTheReplacementCharacter() =>
        Assert.Equal("a�b", ConfirmationText.Visible("a\uD800b", multiline: false));

    [Fact]
    public void ANameInATitleIsCutShort_NotInTheMiddleOfAnEmoji()
    {
        Assert.Equal("short", ConfirmationText.Short("short", 10));
        Assert.Equal("abcd…", ConfirmationText.Short("abcdefghij", 5));
        Assert.Equal("ab…", ConfirmationText.Short("ab😀😀😀", 4));
    }

    // ---- ToolConfirmation ----------------------------------------------------------------------------------------------------

    [Fact]
    public void AQuestionKeepsItsLinesInOrder_AndMakesTheirTextSafe()
    {
        var question = new ToolConfirmation(
            ConfirmationKind.ConnectedApp,
            "  Allow it?  ",
            [new ConfirmationDetail("To", "Omar\u202E"), new ConfirmationDetail("Message", "line one\r\nline two")],
            "  ",
            "  Careful.  ");

        Assert.Equal("Allow it?", question.Title);
        Assert.Equal("Allow", question.ApproveLabel);
        Assert.Equal("Careful.", question.Warning);
        Assert.Equal([("To", "Omar⟨U+202E⟩"), ("Message", "line one\nline two")], question.Details.Select(line => (line.Label, line.Value)));
        Assert.Equal(ConfirmationKind.ConnectedApp, question.Kind);
    }

    [Fact]
    public void AQuestionWithNothingToShowIsRefused_AndSoIsATitleThatSaysNothing()
    {
        Assert.Throws<ArgumentException>(() => new ToolConfirmation(ConfirmationKind.Other, "Allow?", []));
        Assert.Throws<ArgumentException>(() => new ToolConfirmation(ConfirmationKind.Other, " ", [new ConfirmationDetail("A", "b")]));
    }

    [Fact]
    public void AQuestionLargerThanAPersonCanBeAskedToReadIsRefused_NeverCut()
    {
        var big = new string('x', ToolConfirmation.MaxValueLength);

        Assert.Throws<ArgumentException>(() => new ToolConfirmation(ConfirmationKind.Other, "Allow?", [new ConfirmationDetail("A", big + "x")]));
        Assert.Throws<ArgumentException>(() => new ToolConfirmation(
            ConfirmationKind.Other, "Allow?", [.. Enumerable.Range(0, 4).Select(index => new ConfirmationDetail("A" + index, big))]));
        Assert.Throws<ArgumentException>(() => new ToolConfirmation(
            ConfirmationKind.Other, "Allow?", [.. Enumerable.Range(0, ToolConfirmation.MaxDetails + 1).Select(index => new ConfirmationDetail("A" + index, "b"))]));
        Assert.Throws<ArgumentException>(() => new ToolConfirmation(ConfirmationKind.Other, new string('t', ToolConfirmation.MaxTitleLength + 1), [new ConfirmationDetail("A", "b")]));

        // The most it can be is shown whole.
        var largest = new ToolConfirmation(ConfirmationKind.Other, "Allow?", [new ConfirmationDetail("A", big)]);
        Assert.Equal(big, largest.Details[0].Value);
    }

    [Fact]
    public void WhatTheUserIsAskedIsNeverInItsTextForALog()
    {
        var question = new ToolConfirmation(ConfirmationKind.SendMessage, "Send?", [new ConfirmationDetail("Message", "my secret words")]);

        Assert.DoesNotContain("secret", question.ToString(), StringComparison.Ordinal);
        Assert.Contains("SendMessage", question.ToString(), StringComparison.Ordinal);
        Assert.Contains("my secret words", question.AsText(), StringComparison.Ordinal);
    }

    // ---- ToolConfirmationRequested --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARequestIsAnsweredOnce_TheFirstAnswerWins_AndAnApprovalCannotBeUsedAfterwards()
    {
        var request = new ToolConfirmationRequested(Definition, Call, Guid.NewGuid(), Question());
        var changes = 0;
        request.StateChanged += (_, _) => changes++;

        Assert.Equal(ToolConfirmationState.Pending, request.State);
        Assert.True(request.Decline());
        Assert.False(request.Approve());
        Assert.False(request.Expire());
        Assert.False(request.Withdraw());

        Assert.Equal(ToolConfirmationState.Declined, request.State);
        Assert.Equal(ToolConfirmationState.Declined, await request.Settled);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ARequestIsNotShownUntilASurfaceSaysItIs_AndItsTextIsNotInTheLog()
    {
        var request = new ToolConfirmationRequested(Definition, Call, Guid.NewGuid(), Question());

        Assert.False(request.IsShown);
        request.MarkShown();
        Assert.True(request.IsShown);
        Assert.DoesNotContain("hi", request.ToString(), StringComparison.Ordinal);
    }

    // ---- ConfirmationBroker ------------------------------------------------------------------------------------------------------

    private static ConfirmationBroker Broker(IAppEventBus bus, TimeSpan? lifetime = null) =>
        new(bus, TimeProvider.System, NullLogger<ConfirmationBroker>.Instance, lifetime);

    private static AppEventBus NewBus() => new(NullLogger<AppEventBus>.Instance);

    // Something that shows a question, then does what the test says with it.
    private static Holder Surface(AppEventBus bus, Guid conversation, Action<ToolConfirmationRequested> then, bool show = true)
    {
        var owner = new object();
        var subscription = bus.Subscribe<object, ToolConfirmationRequested>(
            owner,
            (_, request, _) =>
            {
                if (request.ConversationId == conversation)
                {
                    if (show)
                    {
                        request.MarkShown();
                    }

                    then(request);
                }

                return Task.CompletedTask;
            });
        return new Holder(owner, subscription);
    }

    // The bus holds its subscribers weakly: this keeps the one a test makes alive until the test is over.
    private sealed class Holder(object owner, IDisposable subscription) : IDisposable
    {
        public object Owner { get; } = owner;

        public void Dispose() => subscription.Dispose();
    }

    [Fact]
    public async Task AQuestionThatNothingShowsIsNotAsked_AndIsAsGoodAsANo()
    {
        var bus = NewBus();
        var conversation = Guid.NewGuid();
        ToolConfirmationRequested? seen = null;

        // A surface for another conversation does not count.
        using var other = Surface(bus, Guid.NewGuid(), _ => { });
        using var ours = Surface(bus, conversation, request => seen = request, show: false);

        var decision = await Broker(bus, TimeSpan.FromSeconds(5)).ConfirmToolCallAsync(Definition, Call, new ToolContext(conversation), Question());

        Assert.Equal(ConfirmationDecision.CouldNotAsk, decision);
        Assert.Equal(ToolConfirmationState.Withdrawn, seen!.State);
    }

    [Fact]
    public async Task AQuestionWithNoSurfaceAtAllIsNotAsked()
    {
        var decision = await Broker(NewBus(), TimeSpan.FromSeconds(5)).ConfirmToolCallAsync(Definition, Call, new ToolContext(Guid.NewGuid()), Question());

        Assert.Equal(ConfirmationDecision.CouldNotAsk, decision);
    }

    [Theory]
    [InlineData(true, ConfirmationDecision.Approved)]
    [InlineData(false, ConfirmationDecision.Declined)]
    public async Task TheUsersAnswerIsTheDecision(bool yes, ConfirmationDecision expected)
    {
        var bus = NewBus();
        var conversation = Guid.NewGuid();
        using var surface = Surface(bus, conversation, request => _ = Task.Run(() => _ = yes ? request.Approve() : request.Decline()));

        var decision = await Broker(bus).ConfirmToolCallAsync(Definition, Call, new ToolContext(conversation), Question());

        Assert.Equal(expected, decision);
    }

    [Fact]
    public async Task AnAnswerThatCameBeforeTheBrokerWaitedStillCounts()
    {
        var bus = NewBus();
        var conversation = Guid.NewGuid();
        using var surface = Surface(bus, conversation, request => request.Approve());

        Assert.Equal(ConfirmationDecision.Approved, await Broker(bus).ConfirmToolCallAsync(Definition, Call, new ToolContext(conversation), Question()));
    }

    [Fact]
    public async Task AQuestionThatIsNotAnsweredInTimeIsWithdrawn_AndTheCallIsNotMade()
    {
        var bus = NewBus();
        var conversation = Guid.NewGuid();
        ToolConfirmationRequested? seen = null;
        using var surface = Surface(bus, conversation, request => seen = request);

        var decision = await Broker(bus, TimeSpan.FromMilliseconds(150)).ConfirmToolCallAsync(Definition, Call, new ToolContext(conversation), Question());

        Assert.Equal(ConfirmationDecision.NoAnswer, decision);
        Assert.Equal(ToolConfirmationState.Expired, seen!.State);

        // A click that comes late is ignored: the question is over.
        Assert.False(seen.Approve());
    }

    [Fact]
    public async Task StoppingTheAnswerTakesTheQuestionBack_AndAnApprovalAfterThatDoesNothing()
    {
        var bus = NewBus();
        var conversation = Guid.NewGuid();
        ToolConfirmationRequested? seen = null;
        using var surface = Surface(bus, conversation, request => seen = request);
        using var stop = new CancellationTokenSource();

        var asking = Broker(bus).ConfirmToolCallAsync(Definition, Call, new ToolContext(conversation), Question(), stop.Token);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        Assert.Equal(ToolConfirmationState.Withdrawn, seen!.State);
        Assert.False(seen.Approve());
    }

    [Fact]
    public async Task AQuestionIsNotEvenPublishedForAnAnswerThatWasAlreadyStopped()
    {
        var bus = NewBus();
        var published = 0;
        using var surface = Surface(bus, Guid.Empty, _ => published++);
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Broker(bus).ConfirmToolCallAsync(Definition, Call, new ToolContext(Guid.Empty), Question(), stop.Token));

        Assert.Equal(0, published);
    }

    [Fact]
    public async Task ASurfaceThatFails_IsANoAndNeverAYes()
    {
        var bus = new ThrowingBus();

        var decision = await Broker(bus).ConfirmToolCallAsync(Definition, Call, new ToolContext(Guid.NewGuid()), Question());

        Assert.Equal(ConfirmationDecision.CouldNotAsk, decision);
    }

    private sealed class ThrowingBus : IAppEventBus
    {
        public Task PublishAsync<TEvent>(TEvent appEvent, CancellationToken cancellationToken = default)
            where TEvent : class => throw new InvalidOperationException("no window");

        public IDisposable Subscribe<TSubscriber, TEvent>(TSubscriber subscriber, Func<TSubscriber, TEvent, CancellationToken, Task> handler)
            where TSubscriber : class
            where TEvent : class => throw new NotSupportedException();
    }

    [Fact]
    public void ALifetimeOfNothingIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Broker(NewBus(), TimeSpan.Zero));

    // ---- The run's clock -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheClockCancelsItsSourceWhenTheTimeIsUsedUp()
    {
        using var source = new CancellationTokenSource();
        _ = new RunClock(source, TimeSpan.FromMilliseconds(100));

        await Task.Delay(TimeSpan.FromSeconds(5), source.Token).ContinueWith(_ => { }, TaskScheduler.Default);

        Assert.True(source.IsCancellationRequested);
    }

    [Fact]
    public async Task TheClockStandsStillWhilePaused_AndGoesOnWithWhatWasLeft()
    {
        using var source = new CancellationTokenSource();
        var clock = new RunClock(source, TimeSpan.FromMilliseconds(400));

        using (clock.Pause())
        {
            await Task.Delay(700);
            Assert.False(source.IsCancellationRequested);
        }

        Assert.False(source.IsCancellationRequested);
        await Task.Delay(TimeSpan.FromSeconds(5), source.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        Assert.True(source.IsCancellationRequested);
    }

    [Fact]
    public async Task PausesNest_AndTheClockRunsAgainOnlyWhenTheLastEnds()
    {
        using var source = new CancellationTokenSource();
        var clock = new RunClock(source, TimeSpan.FromMilliseconds(250));

        var first = clock.Pause();
        var second = clock.Pause();
        first.Dispose();
        first.Dispose();
        await Task.Delay(500);
        Assert.False(source.IsCancellationRequested);

        second.Dispose();
        await Task.Delay(TimeSpan.FromSeconds(5), source.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        Assert.True(source.IsCancellationRequested);
    }

    [Fact]
    public void LettingGoAfterTheRunIsOverIsHarmless()
    {
        var source = new CancellationTokenSource();
        var clock = new RunClock(source, TimeSpan.FromSeconds(5));
        var hold = clock.Pause();
        source.Dispose();

        hold.Dispose();
    }

    // ---- The agent run waits for the user without spending its time ---------------------------------------------------

    private static readonly ToolDefinition Search = new(
        "search_files", "Find the user's files.", """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""", RiskLevel.ReadOnly);

    private sealed class AskingExecutor(TimeSpan userTakes, bool pause) : IToolExecutor
    {
        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default) =>
            ExecuteAsync(call, new ToolContext(Guid.Empty), cancellationToken);

        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken cancellationToken = default)
        {
            // What the executor does while the user reads a question, held by its clock when it can.
            using (pause ? context.RunPause?.Pause() : null)
            {
                await Task.Delay(userTakes, cancellationToken);
            }

            return new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, """{"done":true}""");
        }
    }

    private static async Task<(List<AssistantResponseChunk> Chunks, AgentTraceStore Traces)> RunAsync(bool pause)
    {
        var traces = new AgentTraceStore();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var model = new SequencedModel(
            new ModelInfo("test-model", 8192) { SupportsToolCalling = true },
            [AssistantResponseChunk.ForToolCall(new ToolCall("c1", "search_files", """{"query":"a"}"""))],
            [AssistantResponseChunk.ForTextDelta("All done.")]);
        var orchestrator = new AssistantOrchestrator(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), clock, NullLogger<AssistantOrchestrator>.Instance,
            toolRegistry: new FakeToolRegistry(Search), toolExecutor: new AskingExecutor(TimeSpan.FromMilliseconds(900), pause), traceSink: traces,
            agentLimits: new AgentLimits { TotalTime = TimeSpan.FromMilliseconds(400), FinalAnswerTime = TimeSpan.FromSeconds(5) });

        var chunks = new List<AssistantResponseChunk>();
        await foreach (var chunk in orchestrator.AskAsync(ConversationSession.Start(clock), "ask the user"))
        {
            chunks.Add(chunk);
        }

        return (chunks, traces);
    }

    [Fact]
    public async Task AToolThatHoldsTheRunsClockWhileTheUserReadsIsNotGivenUp_AndTheRunIsNotOutOfTime()
    {
        var (chunks, traces) = await RunAsync(pause: true);

        var result = Assert.Single(chunks, chunk => chunk.Type == AssistantResponseChunkType.ToolResult).ToolResult!;
        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(AgentStopReason.Answered, Assert.Single(traces.Recent()).StopReason);
        Assert.Equal("All done.", chunks[^1].Text);
    }

    [Fact]
    public async Task WithoutHoldingTheClock_TheSameWaitRunsTheRunOutOfTime()
    {
        var (chunks, traces) = await RunAsync(pause: false);

        var result = Assert.Single(chunks, chunk => chunk.Type == AssistantResponseChunkType.ToolResult).ToolResult!;
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.TimedOut, code);
        Assert.Equal(AgentStopReason.TimeLimit, Assert.Single(traces.Recent()).StopReason);
    }
}
