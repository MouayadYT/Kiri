using Assistant.Core.History;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>The provisional title a conversation gets from the first thing the user asked.</summary>
public sealed class ConversationTitleTests
{
    [Fact]
    public void AShortRequestIsTheTitleAsAsked()
    {
        Assert.Equal("What’s the weather this weekend?", ConversationTitle.Provisional("What’s the weather this weekend?"));
    }

    [Fact]
    public void TheTitleIsOneLine_WhateverSeparatedTheWords()
    {
        Assert.Equal("Plan my weekend in Lisbon", ConversationTitle.Provisional("  Plan my\n\tweekend\r\n  in   Lisbon \n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void ARequestWithNoWordsGivesNoTitle(string? request)
    {
        Assert.Equal(string.Empty, ConversationTitle.Provisional(request));
    }

    [Fact]
    public void ALongRequestIsCutAtAWord_WithAnEllipsis()
    {
        var request = "Please summarize the notes from Tuesday's meeting with the design team and list every follow-up we agreed on";

        var title = ConversationTitle.Provisional(request);

        Assert.True(title.Length <= ConversationTitle.MaxLength, title);
        Assert.EndsWith("…", title, StringComparison.Ordinal);
        Assert.StartsWith(title[..^1], request, StringComparison.Ordinal);

        // The cut is between words: what is kept ends where a word ends in the request.
        Assert.Equal(' ', request[title.Length - 1]);
    }

    [Fact]
    public void ALongWordWithNoSpaceIsCutWhereItIs()
    {
        var request = new string('x', 300);

        var title = ConversationTitle.Provisional(request);

        Assert.Equal(ConversationTitle.MaxLength, title.Length);
        Assert.Equal(new string('x', ConversationTitle.MaxLength - 1) + "…", title);
    }

    [Fact]
    public void TheCutNeverSplitsASurrogatePair()
    {
        // Emoji are two UTF-16 characters each; the cut falls inside one unless it backs off.
        var request = string.Concat(Enumerable.Repeat("😀", 80));

        var title = ConversationTitle.Provisional(request);

        Assert.True(title.Length <= ConversationTitle.MaxLength);
        Assert.False(char.IsHighSurrogate(title[^2]), "The title ends with half of a character.");
        Assert.EndsWith("…", title, StringComparison.Ordinal);
    }

    [Fact]
    public void PunctuationBeforeTheCutIsNotLeftBehindTheEllipsis()
    {
        var request = new string('a', 60) + " - " + new string('b', 60);

        var title = ConversationTitle.Provisional(request);

        Assert.Equal(new string('a', 60) + "…", title);
    }

    [Fact]
    public void ARequestOfExactlyTheMostCharactersIsNotCut()
    {
        var request = new string('a', ConversationTitle.MaxLength);

        Assert.Equal(request, ConversationTitle.Provisional(request));
    }
}
