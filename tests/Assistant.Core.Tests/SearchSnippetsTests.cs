using Assistant.Core.Domain;
using Assistant.Core.History;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>The piece of a message a search result shows.</summary>
public sealed class SearchSnippetsTests
{
    private static readonly Guid Message = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static SearchSnippet? Snippet(string text, params string[] words) =>
        SearchSnippets.Build(Message, text, [.. words.Select(word => new SearchTerm(word, word.Contains(' ')))]);

    private static string[] Marked(SearchSnippet snippet) =>
        [.. snippet.Matches.Select(match => snippet.Text.Substring(match.Start, match.Length))];

    [Fact]
    public void AShortMessageIsShownWhole_WithItsMatchMarked()
    {
        var snippet = Snippet("Saturday looks sunny.", "sunny");

        Assert.NotNull(snippet);
        Assert.Equal(Message, snippet.MessageId);
        Assert.Equal("Saturday looks sunny.", snippet.Text);
        Assert.Equal(["sunny"], Marked(snippet));
    }

    [Fact]
    public void MatchingIgnoresCaseAndAccents_AndMarksTheTextAsItIs()
    {
        var snippet = Snippet("Dinner at the Café Rouge", "cafe");

        Assert.NotNull(snippet);
        Assert.Equal(["Café"], Marked(snippet));
    }

    [Fact]
    public void ANonLatinWordIsFound()
    {
        var snippet = Snippet("مرحبا بالعالم كيف حالك", "بالعالم");

        Assert.NotNull(snippet);
        Assert.Equal(["بالعالم"], Marked(snippet));
    }

    [Fact]
    public void AMatchAtTheStartOfAWordIsPreferredToOneInsideAWord()
    {
        var snippet = Snippet("The broadcast begins at eight. Cast members arrive at six.", "cast");

        Assert.NotNull(snippet);
        Assert.Equal(["Cast"], Marked(snippet));
    }

    [Fact]
    public void AWordInsideAWordStillMatches_WhenNothingStartsWithIt()
    {
        var snippet = Snippet("The broadcast begins at eight.", "cast");

        Assert.NotNull(snippet);
        Assert.Equal(["cast"], Marked(snippet));
    }

    [Fact]
    public void ANoMatchGivesNoSnippet()
    {
        Assert.Null(Snippet("Saturday looks sunny.", "rain"));
        Assert.Null(Snippet("Saturday looks sunny.", []));
    }

    [Fact]
    public void ALongMessageIsCutAroundTheMatch_WithEllipsesWhereItWasCut()
    {
        var text = string.Join(' ', Enumerable.Repeat("filler", 60)) + " the treasure is buried here " +
            string.Join(' ', Enumerable.Repeat("padding", 60));

        var snippet = Snippet(text, "treasure");

        Assert.NotNull(snippet);
        Assert.True(snippet.Text.Length <= SearchSnippets.DefaultMaxLength + 2, snippet.Text.Length.ToString());
        Assert.StartsWith("…", snippet.Text, StringComparison.Ordinal);
        Assert.EndsWith("…", snippet.Text, StringComparison.Ordinal);
        Assert.Contains("treasure", snippet.Text, StringComparison.Ordinal);
        Assert.Equal(["treasure"], Marked(snippet));
    }

    [Fact]
    public void AMatchNearTheStart_IsCutOnlyAtTheEnd()
    {
        var text = "treasure " + string.Join(' ', Enumerable.Repeat("padding", 60));

        var snippet = Snippet(text, "treasure");

        Assert.NotNull(snippet);
        Assert.StartsWith("treasure", snippet.Text, StringComparison.Ordinal);
        Assert.EndsWith("…", snippet.Text, StringComparison.Ordinal);
        Assert.Equal(new TextMatch(0, 8), Assert.Single(snippet.Matches));
    }

    [Fact]
    public void ACutBeginsAndEndsAtWholeWords()
    {
        var text = string.Join(' ', Enumerable.Range(0, 80).Select(number => $"alpha{number}")) + " target " +
            string.Join(' ', Enumerable.Range(0, 80).Select(number => $"omega{number}"));

        var snippet = Snippet(text, "target");

        Assert.NotNull(snippet);
        var words = snippet.Text.Trim('…').Split(' ');
        Assert.All(words, word => Assert.True(
            word == "target" || word.StartsWith("alpha", StringComparison.Ordinal) || word.StartsWith("omega", StringComparison.Ordinal),
            $"'{word}' is not a whole word."));
    }

    [Fact]
    public void EveryMatchInTheSnippetIsMarked_ForEveryWord()
    {
        var snippet = Snippet("Pasta with spinach, then more pasta and spinach.", "pasta", "spinach");

        Assert.NotNull(snippet);
        Assert.Equal(["Pasta", "spinach", "pasta", "spinach"], Marked(snippet));
    }

    [Fact]
    public void APhraseIsMatchedTogether()
    {
        var snippet = Snippet("The weekend was fine, but the weather this weekend is better.", "weather this weekend");

        Assert.NotNull(snippet);
        Assert.Equal(["weather this weekend"], Marked(snippet));
    }

    [Fact]
    public void TheSnippetIsOneLine_WithoutMarkdownMarks()
    {
        var snippet = Snippet("# Weekend\n\n- **Saturday** looks `sunny`\n- see [the forecast](https://example.com/x) for more", "sunny");

        Assert.NotNull(snippet);
        Assert.Equal("Weekend Saturday looks sunny see the forecast for more", snippet.Text);
        Assert.Equal(["sunny"], Marked(snippet));
    }

    [Fact]
    public void CodeIsSearchedToo_ButItsFencesAreNotShown()
    {
        var snippet = Snippet("Try this:\n```csharp\nConsole.WriteLine(\"hi\");\n```\nDone", "writeline");

        Assert.NotNull(snippet);
        Assert.DoesNotContain("```", snippet.Text, StringComparison.Ordinal);
        Assert.Equal(["WriteLine"], Marked(snippet));
    }

    [Fact]
    public void ALongWordThatFillsTheTextDoesNotMarkMoreThanAFewMatches()
    {
        var snippet = Snippet(string.Concat(Enumerable.Repeat("aa ", 200)), "a");

        Assert.NotNull(snippet);
        Assert.True(snippet.Matches.Count <= 12);
    }

    [Fact]
    public void ToStringHoldsNoText()
    {
        var snippet = Snippet("Secret plans for the weekend", "secret");

        Assert.NotNull(snippet);
        Assert.DoesNotContain("Secret", snippet.ToString(), StringComparison.Ordinal);
    }
}
