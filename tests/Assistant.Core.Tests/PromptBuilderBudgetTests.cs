using Assistant.Core.Budgeting;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Settings;
using Xunit;
using static Assistant.Core.Tests.BudgetTestData;

namespace Assistant.Core.Tests;

/// <summary>The prompt builder fitting the conversation into the model's window when it is given the user's limits.</summary>
public sealed class PromptBuilderBudgetTests
{
    private static readonly ModelInfo TextModel = new("text-model", 1_000_000);
    private readonly PromptBuilder _builder = new();

    [Fact]
    public void WithLimits_TheRequestAsksForNoMoreThanTheTokensReservedForTheAnswer()
    {
        var built = _builder.Build(null, [User("Hi")], TextModel, Room(2000, reserved: 300));

        Assert.Equal(300, built.Request.MaxOutputTokens);
        Assert.Empty(built.Notices);
        Assert.NotNull(built.Budget);
        Assert.False(built.Budget.AnythingTrimmed);
    }

    [Fact]
    public void WithoutLimits_TheConversationIsSentAsItIs_WithNoCapOnTheAnswer()
    {
        var conversation = new[] { User(Tokens(5000)) };

        var built = _builder.Build(null, conversation, TextModel);

        Assert.Null(built.Request.MaxOutputTokens);
        Assert.Null(built.Budget);
        Assert.Equal(Tokens(5000), Assert.Single(built.Request.Messages).Text);
    }

    [Fact]
    public void WhatDoesNotFit_IsLeftOutOfTheMessagesThatAreLaidOut_AndTheUserIsTold()
    {
        var conversation = new[]
        {
            User("first question " + Tokens(200)),
            Answer("first answer " + Tokens(200)),
            User("second question", File("Big.txt", Tokens(4000))),
        };

        var built = _builder.Build(null, conversation, TextModel, Room(900));

        // The earlier turn is kept (it is cheaper than the file), and the file is cut to its start.
        Assert.Equal(3, built.Request.Messages.Count);
        var asked = built.Request.Messages[^1].Text;
        Assert.Contains("<untrusted_context id=\"1\" kind=\"file\" name=\"Big.txt\">", asked, StringComparison.Ordinal);
        Assert.Contains(ContextBudgeter.ContextCutMarker.Trim(), asked, StringComparison.Ordinal);
        Assert.EndsWith("second question", asked, StringComparison.Ordinal);
        Assert.Equal([ContextBudgetNotices.ContextShortened(["Big.txt"])], built.ContextWarnings);
        Assert.Empty(built.Notices);
    }

    [Fact]
    public void TheBudgetWarnings_AreKeptApartFromTheNoticesTheBuilderMakes()
    {
        var conversation = new[] { User("Read these", File("Empty.txt", null), File("Big.txt", Tokens(4000))) };

        var built = _builder.Build(null, conversation, TextModel, Room(700));

        Assert.Equal([PromptBuilder.EmptyContextNotice], built.Notices);
        Assert.Equal([ContextBudgetNotices.ContextShortened(["Big.txt"])], built.ContextWarnings);
    }

    [Fact]
    public void AnImageTheBudgetLeavesOut_IsNotSent_AndTheUserIsTold()
    {
        var vision = new ModelInfo("vision", 1_000_000) { SupportsVision = true };
        var conversation = new[] { User("What is this?", Screenshot("Shot", "ocr", [1, 2, 3])) };

        var sent = _builder.Build(null, conversation, vision, Room(2500));
        var dropped = _builder.Build(null, conversation, vision, Room(600));

        Assert.Single(sent.Request.Images);
        Assert.Empty(dropped.Request.Images);
        Assert.Equal([ContextBudgetNotices.ContextLeftOut(["Shot"])], dropped.ContextWarnings);
    }

    [Theory]
    [InlineData(ContextItemType.Selection, "selection")]
    [InlineData(ContextItemType.File, "file")]
    [InlineData(ContextItemType.Page, "page")]
    [InlineData(ContextItemType.Screenshot, "screenshot_text")]
    [InlineData(ContextItemType.SearchResults, "search_results")]
    public void TheWrapperAroundAPieceOfContext_CostsNoMoreThanTheBudgetAllowsFor(ContextItemType type, string kind)
    {
        var label = "Report";
        var item = new ContextItem(Guid.NewGuid(), type, label) { Text = "x" };

        // A message with ten pieces, so the ids have two digits, and the pieces are joined by blank lines.
        var conversation = new[] { User("Q", Enumerable.Repeat(item, 10).ToArray()) };
        var text = Assert.Single(_builder.Build(null, conversation, TextModel).Request.Messages).Text;

        var perPiece = (Estimate(text) - Estimate("Q") - 10 * (Estimate(label) + Estimate("x"))) / 10.0;
        Assert.True(
            perPiece <= ContextBudgeter.ContextBlockOverheadTokens,
            $"A {kind} piece costs {perPiece} beyond its label and text, the budget allows {ContextBudgeter.ContextBlockOverheadTokens}.");
        Assert.Contains($"kind=\"{kind}\"", text, StringComparison.Ordinal);
    }
}
