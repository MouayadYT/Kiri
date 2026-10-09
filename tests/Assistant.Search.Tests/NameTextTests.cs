using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.History;
using Assistant.Search.Index;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>Finding a name whichever apostrophe it spells a word with.</summary>
public sealed class NameTextTests
{
    [Theory]
    [InlineData("anna's", new[] { "%anna_s%", "%annas%" })]
    [InlineData("anna’s", new[] { "%anna_s%", "%annas%" })]
    [InlineData("anna`s", new[] { "%anna_s%", "%annas%" })]
    [InlineData("annas", new[] { "%annas%", "%anna_s%" })]
    [InlineData("Wood's prospect's", new[] { "%Wood_s prospect_s%", "%Woods prospects%" })]
    [InlineData("don't", new[] { "%don_t%", "%dont%" })]
    public void AWordWithAnApostropheOrAPossessiveEndingHasTwoPatterns(string word, string[] expected)
    {
        Assert.Equal(expected, NameText.LikePatterns(word));
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("2026")]
    [InlineData("pdf")]
    [InlineData("was")]
    [InlineData("100s")]
    public void AnyOtherWordHasTheOnePatternItAlwaysHad(string word)
    {
        Assert.Equal(["%" + word + "%"], NameText.LikePatterns(word));
    }

    [Fact]
    public void TheWildcardsOfLikeStayEscapedAroundAnApostrophe()
    {
        Assert.Equal(["%100[%] of[_]%"], NameText.LikePatterns("100% of_"));
        Assert.Equal(["%a[_]b_s%", "%a[_]bs%"], NameText.LikePatterns("a_b's"));
    }

    [Fact]
    public void AWordOfOnlyApostrophesKeepsItsOwnLiteralPattern()
    {
        Assert.Equal(["%'%"], NameText.LikePatterns("'"));
    }

    [Fact]
    public void TheQueryFindsTheWordWithAStraightACurlyAndNoApostropheAlike()
    {
        var sql = SearchSqlBuilder.Build(
            SearchResultItemType.File,
            FileSearchPlan.Create(new FileSearchQuery("Anna's Archi.pdf"), 10) ?? throw new InvalidOperationException(),
            10,
            ExcludedFolders.None);

        Assert.Contains(
            "(System.ItemNameDisplay LIKE '%Anna_s%' OR System.ItemNameDisplay LIKE '%Annas%')",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("''", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AnApostropheCannotStartAnInjectedQuery()
    {
        var sql = SearchSqlBuilder.Build(
            SearchResultItemType.File,
            FileSearchPlan.Create(new FileSearchQuery("x' OR '1'='1"), 10) ?? throw new InvalidOperationException(),
            10,
            ExcludedFolders.None);

        Assert.DoesNotContain("'1'='1", sql, StringComparison.Ordinal);
        Assert.All(SearchQuery.Parse("x' OR '1'='1").Terms, term => Assert.DoesNotContain('\'', string.Concat(NameText.LikePatterns(term.Text))));
    }
}
