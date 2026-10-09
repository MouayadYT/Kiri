using Assistant.Core.Domain;
using Assistant.Core.History;
using Assistant.Search.Index;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>How well a name fits what was typed.</summary>
public sealed class NameRelevanceTests
{
    private static int Score(string name, string typed, SearchResultItemType type = SearchResultItemType.File) =>
        NameRelevance.Score(new SearchResultItem(type, name, @"C:\" + name), SearchQuery.Parse(typed).Terms);

    [Theory]
    [InlineData("budget.xlsx", "budget", NameRelevance.Exact)]
    [InlineData("Budget.XLSX", "budget.xlsx", NameRelevance.Exact)]
    [InlineData("budget 2026.xlsx", "\"budget 2026\"", NameRelevance.Exact)]
    [InlineData("budget 2026.xlsx", "budget", NameRelevance.Prefix)]
    [InlineData("2026 budget.xlsx", "budget", NameRelevance.WordStart)]
    [InlineData("annual_budget.xlsx", "budget", NameRelevance.WordStart)]
    [InlineData("AnnualBudget.xlsx", "budget", NameRelevance.WordStart)]
    [InlineData("rebudgeting.xlsx", "budget", NameRelevance.Contains)]
    [InlineData("notes.txt", "budget", NameRelevance.Elsewhere)]
    [InlineData("budget notes.txt", "budget 2026", NameRelevance.Elsewhere)]
    public void ANameIsScoredByHowCloseItIsToTheWordsTyped(string name, string typed, int expected)
    {
        Assert.Equal(expected, Score(name, typed));
    }

    [Fact]
    public void AFolderNameWithADotIsWholeAndNotAnExtension()
    {
        Assert.Equal(NameRelevance.Exact, Score("Finance.old", "finance.old", SearchResultItemType.Folder));
        Assert.Equal(NameRelevance.Prefix, Score("Finance.old", "finance", SearchResultItemType.Folder));
    }

    [Fact]
    public void WordsInAnyOrderCountWhenEveryOneIsInTheName()
    {
        Assert.Equal(NameRelevance.WordStart, Score("2026 budget final.xlsx", "final 2026"));
    }

    [Theory]
    [InlineData("Anna’s Archive.pdf", "annas")]
    [InlineData("Anna’s Archive.pdf", "anna's")]
    [InlineData("Anna's Archive.pdf", "anna’s")]
    [InlineData("Annas Archive.pdf", "anna’s")]
    public void AnApostropheInTheNameOrTheWordsDoesNotMakeAMatchScoreAsElsewhere(string name, string typed)
    {
        Assert.Equal(NameRelevance.Prefix, Score(name, typed));
    }

    [Fact]
    public void ANameThatIsTheWordsWithoutItsApostropheIsExact()
    {
        Assert.Equal(NameRelevance.Exact, Score("Anna’s.pdf", "annas"));
    }
}
