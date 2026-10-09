using Assistant.Core.History;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>How what the user typed into the history search is read into words.</summary>
public sealed class SearchQueryTests
{
    private static string[] Words(string? text) => [.. SearchQuery.Parse(text).Terms.Select(term => term.Text)];

    [Fact]
    public void WordsAreSeparatedBySpaces()
    {
        Assert.Equal(["pasta", "spinach"], Words("  pasta \t spinach\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("? ! - …")]
    public void TextWithNoWordsIsAnEmptyQuery(string? text)
    {
        var query = SearchQuery.Parse(text);

        Assert.True(query.IsEmpty);
        Assert.Empty(query.Terms);
    }

    [Fact]
    public void APunctuationMarkAloneIsNotAWord_ButOneInsideAWordStays()
    {
        Assert.Equal(["7-eleven", "pizza"], Words("7-eleven - pizza ?"));
    }

    [Fact]
    public void WordsInQuotesAreOnePhrase()
    {
        var query = SearchQuery.Parse("\"weekend weather\" sunny");

        Assert.Equal(["weekend weather", "sunny"], query.Terms.Select(term => term.Text));
        Assert.Equal([true, false], query.Terms.Select(term => term.IsPhrase));
    }

    [Fact]
    public void AQuoteThatIsNeverClosedEndsWithTheText()
    {
        var query = SearchQuery.Parse("sunny \"weekend weather");

        Assert.Equal(["sunny", "weekend weather"], query.Terms.Select(term => term.Text));
        Assert.True(query.Terms[1].IsPhrase);
    }

    [Fact]
    public void ASingleWordInQuotesIsAWord_NotAPhrase()
    {
        var query = SearchQuery.Parse("\"pasta\"");

        Assert.Equal(["pasta"], query.Terms.Select(term => term.Text));
        Assert.False(query.Terms[0].IsPhrase);
    }

    [Fact]
    public void EmptyQuotesAreNothing()
    {
        Assert.Empty(SearchQuery.Parse("\"\" \" \"").Terms);
    }

    [Fact]
    public void SpacingInsideAPhraseIsSingleSpaced()
    {
        Assert.Equal(["weekend weather"], Words("\"weekend \t  weather\""));
    }

    [Fact]
    public void OnlyTheFirstFewWordsAreUsed()
    {
        var words = Enumerable.Range(1, SearchQuery.MaxTerms + 4).Select(index => $"word{index}");

        var terms = Words(string.Join(' ', words));

        Assert.Equal(SearchQuery.MaxTerms, terms.Length);
        Assert.Equal("word1", terms[0]);
        Assert.Equal($"word{SearchQuery.MaxTerms}", terms[^1]);
    }

    [Fact]
    public void AVeryLongWordIsCutShort()
    {
        var terms = Words(new string('a', 500));

        Assert.Equal(SearchQuery.MaxTermLength, Assert.Single(terms).Length);
    }

    [Fact]
    public void ADigitOrAnyLetterMakesAWord()
    {
        Assert.Equal(["5", "é", "ش"], Words("5 é ش"));
    }
}
