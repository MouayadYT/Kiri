using Assistant.Core.QuickSearch.Routing;
using Xunit;

namespace Assistant.Core.Tests;

public sealed class QueryRouterTests
{
    private static QueryRouter Router(bool calculator = false, Func<string, bool>? files = null) =>
        new(files, () => calculator);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingTypedBrowsesTheCategories(string typed)
    {
        var route = Router().Route(typed);

        Assert.Equal(QueryRouteKind.InstantSearch, route.Kind);
        Assert.Equal(QueryRouteReason.Empty, route.Reason);
        Assert.True(route.UsesInstantProviders);
    }

    [Fact]
    public void ANullQueryIsNothingTyped() => Assert.Equal(QueryRouteReason.Empty, Router().Route(null).Reason);

    [Theory]
    [InlineData("brave")]
    [InlineData("Google Chrome")]
    [InlineData("visual studio code")]
    [InlineData("open downloads")]
    [InlineData("mute")]
    [InlineData("volume 30")]
    [InlineData("lock")]
    [InlineData("report.pdf")]
    [InlineData("settings")]
    [InlineData("new conversation")]
    [InlineData("create folder")]
    [InlineData("list files")]
    [InlineData("what is")]
    [InlineData("how")]
    [InlineData("budget 2026")]
    public void NamesAndShortCommandsAreInstantSearch(string typed)
    {
        var route = Router().Route(typed);

        Assert.Equal(QueryRouteKind.InstantSearch, route.Kind);
        Assert.Equal(QueryRouteReason.LooksLikeAName, route.Reason);
        Assert.Equal("Search", route.Label);
        Assert.False(route.GoesToTheModel);
        Assert.False(route.WasForced);
        Assert.Null(route.Expression);
    }

    [Theory]
    [InlineData("what is the capital of France")]
    [InlineData("How do I center a div?")]
    [InlineData("explain quantum computing")]
    [InlineData("why is the sky blue")]
    [InlineData("is 7 prime")]
    [InlineData("translate good morning to Spanish")]
    [InlineData("pizza near me open?")]
    [InlineData("create a new folder structure for taxes")]
    [InlineData("what is a pdf")]
    [InlineData("what is the pdf format")]
    public void QuestionsAndInstructionsToAnAssistantGoToTheModel(string typed)
    {
        var route = Router().Route(typed);

        Assert.Equal(QueryRouteKind.DirectAnswer, route.Kind);
        Assert.Equal(QueryRouteReason.Question, route.Reason);
        Assert.Equal("Ask", route.Label);
        Assert.True(route.GoesToTheModel);
        Assert.False(route.UsesInstantProviders);
    }

    [Theory]
    [InlineData("summarize the pdf I downloaded yesterday")]
    [InlineData("what's on my screen")]
    [InlineData("explain this screenshot")]
    [InlineData("what is in my latest document")]
    [InlineData("compare these two files for me please")]
    public void AQuestionAboutTheUsersOwnFilesOrScreenGetsTheModelsToolsToo(string typed)
    {
        var route = Router().Route(typed);

        Assert.Equal(QueryRouteKind.AgentRequest, route.Kind);
        Assert.Equal(QueryRouteReason.NeedsFilesOrScreen, route.Reason);
        Assert.Equal("Ask with tools", route.Label);
        Assert.True(route.GoesToTheModel);
    }

    [Fact]
    public void LongTextIsAMessageAndNotAName()
    {
        var route = Router().Route("remind me to call the dentist tomorrow morning before work please");

        Assert.Equal(QueryRouteKind.DirectAnswer, route.Kind);
        Assert.Equal(QueryRouteReason.LongText, route.Reason);

        // Eight words are still a short phrase.
        Assert.Equal(QueryRouteKind.InstantSearch, Router().Route("one two three four five six seven eight").Kind);
    }

    [Fact]
    public void LongTextAboutTheUsersFilesGetsTools()
    {
        var route = Router().Route("pull out the action items from my meeting notes of last week please");

        Assert.Equal(QueryRouteKind.AgentRequest, route.Kind);
    }

    [Fact]
    public void TheUserCanForceAskModeWhateverTheWordsLookLike()
    {
        var plain = Router().Route("brave", new QueryRouteOptions { ForceAsk = true });
        var files = Router().Route("my files from yesterday", new QueryRouteOptions { ForceAsk = true });
        var sum = Router(calculator: true).Route("9+10", new QueryRouteOptions { ForceAsk = true });

        Assert.Equal((QueryRouteKind.DirectAnswer, QueryRouteReason.ForcedAsk), (plain.Kind, plain.Reason));
        Assert.True(plain.WasForced);
        Assert.Equal(QueryRouteKind.AgentRequest, files.Kind);
        Assert.True(files.WasForced);
        Assert.Equal(QueryRouteKind.DirectAnswer, sum.Kind);
        Assert.Null(sum.Expression);

        // Forcing nothing is still nothing typed.
        Assert.Equal(QueryRouteReason.Empty, Router().Route("", new QueryRouteOptions { ForceAsk = true }).Reason);
    }

    [Fact]
    public void WordsThatClearlyAskForFilesAreAStructuredFileSearchBeforeAnythingIsAsked()
    {
        var router = Router(files: text => text.Contains("pdf", StringComparison.OrdinalIgnoreCase));

        var route = router.Route("find the pdf about biology I edited last Tuesday");

        Assert.Equal((QueryRouteKind.FileSearch, QueryRouteReason.FileRequest), (route.Kind, route.Reason));
        Assert.Equal("File search", route.Label);
        Assert.True(route.UsesInstantProviders);
        Assert.False(route.GoesToTheModel);

        // It comes before the question rule: this is a question in form and a file request in fact.
        Assert.Equal(QueryRouteKind.FileSearch, router.Route("what pdf did I edit yesterday").Kind);

        // Without the planner's word for it, nothing is a file search.
        Assert.Equal(QueryRouteKind.AgentRequest, Router().Route("find the pdf about biology I edited last Tuesday please").Kind);
    }

    [Theory]
    [InlineData("9+10", "9 + 10")]
    [InlineData("9 + 10", "9 + 10")]
    [InlineData("what is 9+10", "9 + 10")]
    [InlineData("What's 12 * (3 + 4)", "12 * (3 + 4)")]
    [InlineData("whats 2^10", "2 ^ 10")]
    [InlineData("100/8", "100 / 8")]
    [InlineData("3 x 4", "3 * 4")]
    [InlineData("3 × 4", "3 * 4")]
    [InlineData("84 ÷ 7", "84 / 7")]
    [InlineData("5 * -3", "5 * -3")]
    [InlineData("-3 + 4", "-3 + 4")]
    [InlineData("1,000 + 5", "1000 + 5")]
    [InlineData("= 5+5", "5 + 5")]
    [InlineData("calc 3*4", "3 * 4")]
    [InlineData("calculate 7-2", "7 - 2")]
    [InlineData("9+10 =", "9 + 10")]
    [InlineData("9+10?", "9 + 10")]
    [InlineData("10-5", "10 - 5")]
    [InlineData("2.5*4", "2.5 * 4")]
    [InlineData(".5 + .25", ".5 + .25")]
    [InlineData("((1+2)*3)", "((1 + 2) * 3)")]
    [InlineData("17 % 5", "17 % 5")]
    public void StraightforwardArithmeticIsADeterministicCalculationWhileACalculatorIsAvailable(string typed, string expression)
    {
        var route = Router(calculator: true).Route(typed);

        Assert.Equal((QueryRouteKind.Calculation, QueryRouteReason.Arithmetic), (route.Kind, route.Reason));
        Assert.Equal(expression, route.Expression);
        Assert.Equal("Calculate", route.Label);
        Assert.False(route.UsesInstantProviders);
        Assert.False(route.GoesToTheModel);
    }

    [Theory]
    [InlineData("2024-10-02")]
    [InlineData("10/2/2026")]
    [InlineData("2.10.2026")]
    [InlineData("555-123-4567")]
    [InlineData("1.2.3")]
    [InlineData("3 apples + 4 pears")]
    [InlineData("x + 1")]
    [InlineData("5")]
    [InlineData("5+")]
    [InlineData("+")]
    [InlineData("(5+3")]
    [InlineData("5+3)")]
    [InlineData("5 5")]
    [InlineData("5 x")]
    [InlineData("1,5 + 2")]
    [InlineData("50%")]
    [InlineData("5++")]
    [InlineData("5 * / 3")]
    [InlineData("1..2 + 3")]
    [InlineData("2(3)")]
    public void NothingThatIsNotASumIsACalculation(string typed)
    {
        Assert.False(ArithmeticExpression.TryRead(typed, out var expression));
        Assert.Equal("", expression);
        Assert.NotEqual(QueryRouteKind.Calculation, Router(calculator: true).Route(typed).Kind);
    }

    [Fact]
    public void WithoutACalculatorASumIsAnOrdinaryQueryAndTheModelStillAnswersAQuestion()
    {
        Assert.Equal(QueryRouteKind.InstantSearch, Router(calculator: false).Route("9+10").Kind);
        Assert.Equal(QueryRouteKind.DirectAnswer, Router(calculator: false).Route("what is 9+10").Kind);
        Assert.Equal(QueryRouteKind.InstantSearch, new QueryRouter().Route("9+10").Kind);
    }

    [Fact]
    public void ALongSumIsNotRead()
    {
        Assert.False(ArithmeticExpression.TryRead(string.Join("+", Enumerable.Repeat("12345", 40)), out _));
        Assert.False(ArithmeticExpression.TryRead(null, out _));
        Assert.False(ArithmeticExpression.TryRead("   ", out _));
    }

    [Fact]
    public void WhatWasTypedIsNeverPrinted()
    {
        var route = Router(calculator: true).Route("what is 123456+654321");

        Assert.Equal("123456 + 654321", route.Expression);
        Assert.DoesNotContain("123456", route.ToString());
    }
}
