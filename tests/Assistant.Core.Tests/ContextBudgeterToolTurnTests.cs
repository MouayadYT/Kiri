using Assistant.Core.Budgeting;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Xunit;
using static Assistant.Core.Tests.BudgetTestData;

namespace Assistant.Core.Tests;

/// <summary>
/// Fitting a turn that is in the middle of a tool call: the model has been asked, has called a tool and been given its result, and is
/// to answer. The user's message it answers is the one before the call, not the conversation's last.
/// </summary>
public sealed class ContextBudgeterToolTurnTests
{
    private static readonly string Instructions = Sized("rules", 4);
    private static readonly int Fixed = ContextBudgeter.RequestOverheadTokens + 4;
    private static readonly int Overhead = ContextBudgeter.MessageOverheadTokens;

    private readonly ContextBudgeter _budgeter = new(new HeuristicTokenEstimator());

    private static Message Calling(string argumentsJson) =>
        new(Guid.NewGuid(), MessageRole.Assistant, string.Empty, Now) { ToolCalls = [new ToolCall("c1", "t", argumentsJson)] };

    private static Message Result(string text) =>
        new(Guid.NewGuid(), MessageRole.Tool, text, Now) { ToolResult = new ToolResult("c1", "t", ToolResultStatus.Succeeded, text) };

    [Fact]
    public void TheMessageAnAnswerIsFor_IsTheUsersLast_OrTheOneBeforeTheToolResultsOfItsTurn()
    {
        Assert.Equal(0, ContextBudgeter.AnsweredIndex([User("a")]));
        Assert.Equal(0, ContextBudgeter.AnsweredIndex([User("a"), Calling("{}"), Result("r")]));
        Assert.Equal(2, ContextBudgeter.AnsweredIndex([User("a"), Answer("b"), User("c"), Calling("{}"), Result("r"), Calling("{}"), Result("s")]));
        Assert.Equal(-1, ContextBudgeter.AnsweredIndex([]));
        Assert.Equal(-1, ContextBudgeter.AnsweredIndex([User("a"), Answer("b")]));
        Assert.Equal(-1, ContextBudgeter.AnsweredIndex([Result("r")]));
    }

    [Fact]
    public void ATurnInTheMiddleOfAToolCall_KeepsItsOwnMessagesWhole_AndCostsThemBeforeTheEarlierTurns()
    {
        var earlier = new[] { User(Sized("first", 20)), Answer(Sized("reply", 20)) };
        var turn = new[] { User(Sized("question", 10)), Calling("{}"), Result(Sized("result", 100)) };
        var conversation = earlier.Concat(turn).ToArray();
        var tail = (Overhead + Estimate("t") + Estimate("{}") + 4) + (Overhead + 100);
        var question = Overhead + 10;
        var earlierCost = 2 * (Overhead + 20);

        // Room for the question and the tool's result, and the earlier turn.
        var all = _budgeter.Fit(Instructions, conversation, LargeModel, Room(Fixed + 4 + question + tail + earlierCost));
        Assert.Equal(Fixed + question + tail + earlierCost, all.Report.EstimatedPromptTokens);
        Assert.Equal(1, all.Report.TurnsKept);
        Assert.Same(conversation, all.Messages);

        // One token short of that: the earlier turn goes, the turn being answered stays, tool messages and all.
        var short_ = _budgeter.Fit(Instructions, conversation, LargeModel, Room(Fixed + question + tail + earlierCost - 1));
        Assert.Equal(1, short_.Report.TurnsLeftOut);
        Assert.Equal(turn.Select(message => message.Id), short_.Messages.Select(message => message.Id));
        Assert.Contains(ContextBudgetNotices.EarlierMessagesLeftOut, short_.Notices);
    }

    [Fact]
    public void TheContextOfTheUsersMessage_IsStillContextForTheQuestion_WhileATurnIsInTheMiddleOfAToolCall()
    {
        var file = File("notes.txt", Sized("text", 30));
        var asked = new[] { User("question", file) };
        var midTurn = new[] { User("question", file), Calling("{}"), Result("found") };

        var before = _budgeter.Fit(Instructions, asked, LargeModel, Room(1000));
        var during = _budgeter.Fit(Instructions, midTurn, LargeModel, Room(1000));

        // The file keeps the rank of what the user chose for this question, which an earlier message's would not have.
        Assert.Equal(before.Report.Items.Select(item => item.Priority), during.Report.Items.Select(item => item.Priority));
        Assert.Equal(ContextFate.Whole, Assert.Single(during.Report.Items).Fate);
    }

    [Fact]
    public void ThePromptBuilder_CarriesTheToolsAndTheirCost_AndEndsWithTheToolsResult()
    {
        var search = new ToolDefinition("search_files", "Find the user's files.", """{"type":"object"}""", RiskLevel.ReadOnly);
        var builder = new PromptBuilder();
        var conversation = new[] { User("find it"), Calling("""{"query":"it"}"""), Result("""{"found":1}""") };

        var without = builder.Build(null, conversation, LargeModel, Room(100_000));
        var with = builder.Build(null, conversation, LargeModel, Room(100_000), tools: [search]);

        Assert.Empty(without.Request.Tools);
        Assert.Equal([search], with.Request.Tools);
        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.Tool],
            with.Request.Messages.Select(message => message.Role));
        Assert.Contains(AssistantInstructions.ToolGuidance, with.Request.Instructions, StringComparison.Ordinal);
        Assert.Contains(AssistantInstructions.NoToolGuidance, without.Request.Instructions, StringComparison.Ordinal);

        // The tools are part of the prompt, so the same room has less left for the conversation.
        Assert.True(with.Budget!.EstimatedPromptTokens > without.Budget!.EstimatedPromptTokens);
    }

    [Fact]
    public void AConversationThatDoesNotEndWithAUsersMessageOrAToolsResult_IsNotOneToAnswer()
    {
        var builder = new PromptBuilder();

        Assert.Throws<ArgumentException>(() => builder.Build(null, [User("a"), Answer("b")], LargeModel));
        Assert.Throws<ArgumentException>(() => _budgeter.Fit(Instructions, [User("a"), Answer("b")], LargeModel, Room(100)));
    }
}
