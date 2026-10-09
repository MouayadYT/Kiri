using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>What a session says about the turn it is answering.</summary>
public sealed class ConversationSessionTests
{
    [Fact]
    public async Task ASessionWithNoTurnRunning_IsIdleAtOnce()
    {
        var session = ConversationSession.Start(TimeProvider.System);

        await session.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ASessionWithNoMessagesTakesTheMessagesOfTheConversationItContinues_AndOneWithSomeDoesNot()
    {
        var session = ConversationSession.Start(TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        var earlier = new[]
        {
            new Message(Guid.NewGuid(), MessageRole.User, "hello", now),
            new Message(Guid.NewGuid(), MessageRole.Assistant, "hi", now),
        };

        Assert.True(session.Resume(earlier, now));
        Assert.Equal(["hello", "hi"], session.Conversation.Messages.Select(message => message.Text));

        Assert.False(session.Resume([new Message(Guid.NewGuid(), MessageRole.User, "other", now)], now));
        Assert.Equal(2, session.Conversation.Messages.Count);
    }

    [Fact]
    public void AToolExchangeTheAppMadeIsTheUsersQuestion_TheCall_ItsResult_AndWhatWasSaid()
    {
        var session = ConversationSession.Start(TimeProvider.System);
        var call = new ToolCall("call_a", "search_files", """{"query":"x"}""");
        var result = new ToolResult("call_a", "search_files", ToolResultStatus.Succeeded, """{"found":0}""");

        Assert.True(session.AddToolExchange("find x", call, result, "Nothing.", DateTimeOffset.UtcNow));
        Assert.True(session.AddToolExchange("find y", call, result, string.Empty, DateTimeOffset.UtcNow));

        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant, MessageRole.User, MessageRole.Assistant, MessageRole.Tool],
            session.Conversation.Messages.Select(message => message.Role));
        Assert.Equal(call, session.Conversation.Messages[1].ToolCalls[0]);
        Assert.Equal(result, session.Conversation.Messages[2].ToolResult);
        Assert.Throws<ArgumentException>(() => session.AddToolExchange(" ", call, result, "x", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task NoExchangeIsAddedWhileATurnIsRunning()
    {
        var model = new ScriptedModel();
        var orchestrator = new AssistantOrchestrator(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), TimeProvider.System, NullLogger<AssistantOrchestrator>.Instance);
        var session = ConversationSession.Start(TimeProvider.System);
        await using var turn = orchestrator.AskAsync(session, "Hi").GetAsyncEnumerator();
        model.Write("Hello");
        Assert.True(await turn.MoveNextAsync());

        var call = new ToolCall("c", "search_files", "{}");
        var result = new ToolResult("c", "search_files", ToolResultStatus.Succeeded, "{}");
        Assert.False(session.AddToolExchange("a", call, result, "b", DateTimeOffset.UtcNow));
        Assert.False(session.Resume([new Message(Guid.NewGuid(), MessageRole.User, "x", DateTimeOffset.UtcNow)], DateTimeOffset.UtcNow));

        model.End();
        Assert.False(await turn.MoveNextAsync());
        Assert.True(session.AddToolExchange("a", call, result, "b", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task WhenIdle_CompletesOnlyOnceTheTurnRunningHasEnded_AndTheSessionIsThenFreeAgain()
    {
        var model = new ScriptedModel();
        var orchestrator = new AssistantOrchestrator(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), TimeProvider.System, NullLogger<AssistantOrchestrator>.Instance);
        var session = ConversationSession.Start(TimeProvider.System);
        await using var turn = orchestrator.AskAsync(session, "Hi").GetAsyncEnumerator();
        model.Write("Hello");
        Assert.True(await turn.MoveNextAsync());

        var idle = session.WhenIdleAsync();
        await Task.Delay(50);
        Assert.False(idle.IsCompleted);

        model.End();
        Assert.False(await turn.MoveNextAsync());
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
        await session.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AStoppedTurn_IsIdleOnceItHasWoundUp()
    {
        var model = new ScriptedModel();
        var orchestrator = new AssistantOrchestrator(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), TimeProvider.System, NullLogger<AssistantOrchestrator>.Instance);
        var session = ConversationSession.Start(TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var turn = Task.Run(async () =>
        {
            await foreach (var _ in orchestrator.AskAsync(session, "Hi", cancellationToken: stop.Token))
            {
            }
        });
        model.Write("Hello");
        await Task.Delay(100);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);

        await session.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
