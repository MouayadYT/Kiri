using Assistant.Core.Voice;
using Xunit;

namespace Assistant.Core.Tests;

public sealed class SpeechSegmenterTests
{
    // Feeds the text in pieces of <size> characters, as a model streams it, and collects every piece that was handed over.
    private static List<string> Stream(string text, int size = 3, SpeechSegmenterOptions? options = null)
    {
        var segmenter = new SpeechSegmenter(options);
        var pieces = new List<string>();
        for (var i = 0; i < text.Length; i += size)
        {
            pieces.AddRange(segmenter.Append(text.Substring(i, Math.Min(size, text.Length - i))));
        }

        pieces.AddRange(segmenter.Complete());
        return pieces;
    }

    [Fact]
    public void SentencesAreSeparatePieces()
    {
        var pieces = Stream("You have two meetings tomorrow. The first is the design review at ten. The second is a call with your brother.");

        Assert.Equal(
            [
                "You have two meetings tomorrow.",
                "The first is the design review at ten.",
                "The second is a call with your brother.",
            ],
            pieces);
    }

    [Fact]
    public void AClauseBreakCutsTheFirstPieceWhenItIsLongEnough()
    {
        var segmenter = new SpeechSegmenter();

        var first = segmenter.Append("Your next meeting is at three, ");

        Assert.Equal(["Your next meeting is at three"], first);
    }

    [Fact]
    public void AShortFirstClauseIsNotCutOffAlone()
    {
        var segmenter = new SpeechSegmenter();

        Assert.Empty(segmenter.Append("Yes, "));
    }

    [Fact]
    public void ASentenceEndCutsTheFirstPieceWhateverItsLength()
    {
        var segmenter = new SpeechSegmenter();

        Assert.Empty(segmenter.Append("Sure."));
        var pieces = segmenter.Append(" I can ");

        Assert.Equal(["Sure."], pieces);
    }

    [Fact]
    public void LaterShortSentencesJoinTheNextSoThatSpeechIsNotChoppy()
    {
        var pieces = Stream("Sure. Here is the answer you asked for. Yes. Then we are done with the whole thing for today.", size: 5);

        Assert.Equal("Sure.", pieces[0]);
        Assert.Equal("Here is the answer you asked for.", pieces[1]);
        Assert.Equal("Yes. Then we are done with the whole thing for today.", pieces[2]);
    }

    [Theory]
    [InlineData("The price is 3.5 dollars and that is cheap for what you get.", 1)]
    [InlineData("Dr. Smith will see you at 3 p.m. on Monday afternoon in the office.", 1)]
    [InlineData("Visit example.com for more information about the subject of this talk.", 1)]
    [InlineData("It was written by J. K. Rowling and published in the nineties by a small press.", 1)]
    [InlineData("See the note e.g. the first one for more detail about how to do it right.", 1)]
    public void DotsInsideASentenceDoNotEndIt(string text, int expected) => Assert.Equal(expected, Stream(text).Count);

    [Fact]
    public void ALineBreakEndsAPiece()
    {
        var pieces = Stream("Here are your tasks:\n- Buy milk\n- Call the dentist\n- Finish the report");

        Assert.Equal(["Here are your tasks:", "Buy milk", "Call the dentist", "Finish the report"], pieces);
    }

    [Fact]
    public void ANumberedListMarkerIsNotASentence()
    {
        var pieces = Stream("Steps:\n1. Open the settings\n2. Choose the voice\n3. Press test");

        Assert.Equal(["Steps:", "Open the settings", "Choose the voice", "Press test"], pieces);
    }

    [Fact]
    public void CodeBlocksAreNotSpoken()
    {
        var pieces = Stream("Here is the code.\n```csharp\nvar x = 1;\nConsole.WriteLine(x);\n```\nThat prints the number one.");

        Assert.Equal(["Here is the code.", "That prints the number one."], pieces);
        Assert.DoesNotContain(pieces, piece => piece.Contains("Console", StringComparison.Ordinal));
    }

    [Fact]
    public void MarkdownIsMadePlainInThePieces()
    {
        var pieces = Stream("**Important:** see [the guide](https://example.com/guide) for `details`.");

        Assert.Equal(["Important: see the guide for details."], pieces);
    }

    [Fact]
    public void ALongRunWithoutPunctuationIsCutAtAWord()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 120));

        var pieces = Stream(text, size: 7);

        Assert.True(pieces.Count >= 2);
        Assert.All(pieces, piece => Assert.True(piece.Length <= 250));
        Assert.Equal(text, string.Join(' ', pieces));
    }

    [Fact]
    public void WithoutPunctuationTheFirstPieceStillStartsTheVoice()
    {
        var segmenter = new SpeechSegmenter();
        var words = string.Join(' ', Enumerable.Repeat("alpha", 30));

        var first = segmenter.Append(words + " ");

        Assert.NotEmpty(first);
    }

    [Fact]
    public void WhatIsLeftWhenTheAnswerEndsIsSpoken()
    {
        var segmenter = new SpeechSegmenter();

        Assert.Empty(segmenter.Append("And that is all"));
        Assert.Equal(["And that is all"], segmenter.Complete());
    }

    [Fact]
    public void ADecisionThatNeedsTheNextCharacterWaitsForIt()
    {
        var segmenter = new SpeechSegmenter();

        // "3." might be the start of 3.5.
        Assert.Empty(segmenter.Append("The total is 3."));
        Assert.Empty(segmenter.Append("5 dollars"));
        Assert.Equal(["The total is 3.5 dollars"], segmenter.Complete());
    }

    [Fact]
    public void ThePiecesAreTheTextInOrderAndNothingIsLost()
    {
        const string text = "First, we open the box. Then we read the card carefully; it says: do not shake. After that, we wait! Is that clear? Good.";

        var spoken = string.Join(' ', Stream(text, size: 2));

        Assert.Equal(
            "First, we open the box. Then we read the card carefully; it says: do not shake. After that, we wait! Is that clear? Good.",
            spoken);
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        var segmenter = new SpeechSegmenter();
        segmenter.Append("Half of a senten");
        segmenter.Reset();

        Assert.Empty(segmenter.Complete());
        Assert.Equal(0, segmenter.Emitted);
    }

    [Fact]
    public void ARuleOrABulletAloneSaysNothing()
    {
        Assert.Empty(Stream("---\n* \n"));
    }
}

public sealed class SpeechTextCleanerTests
{
    [Theory]
    [InlineData("**bold** and *italic* and __under__", "bold and italic and under")]
    [InlineData("Use `dotnet build` now", "Use dotnet build now")]
    [InlineData("# A heading", "A heading")]
    [InlineData("- a bullet", "a bullet")]
    [InlineData("3. third item", "third item")]
    [InlineData("> quoted words", "quoted words")]
    [InlineData("[the docs](https://example.com/x) say so", "the docs say so")]
    [InlineData("See https://example.com/page for more", "See for more")]
    [InlineData("snake_case_name is fine", "snake case name is fine")]
    [InlineData("Fish & chips", "Fish and chips")]
    [InlineData("It works — mostly", "It works, mostly")]
    [InlineData("Great job \U0001F389", "Great job")]
    [InlineData("2*3 is six", "2 times 3 is six")]
    public void MarkdownAndSymbolsAreMadePlain(string raw, string expected) => Assert.Equal(expected, SpeechTextCleaner.Clean(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("* ")]
    [InlineData("| --- | --- |")]
    [InlineData("\U0001F389\U0001F389")]
    public void NothingSpeakableIsEmpty(string raw) => Assert.Equal("", SpeechTextCleaner.Clean(raw));

    [Fact]
    public void TableRowsAreReadCellByCell() => Assert.Equal("Name, Age", SpeechTextCleaner.Clean("| Name | Age |"));

    [Fact]
    public void LinesAreSeparatedByAPause() => Assert.Equal("First line. Second line", SpeechTextCleaner.Clean("First line\nSecond line"));
}

public sealed class SpokenRequestTextTests
{
    [Theory]
    [InlineData("what's on my calendar tomorrow", "What's on my calendar tomorrow?")]
    [InlineData("how do i reset my password", "How do I reset my password?")]
    [InlineData("open the settings", "Open the settings")]
    [InlineData("i'm tired of this", "I'm tired of this")]
    [InlineData("what time is it?", "What time is it?")]
    [InlineData("  many   spaces  here ", "Many spaces here")]
    [InlineData("what", "What")]
    [InlineData("what's on my calendar to morrow", "What's on my calendar tomorrow?")]
    [InlineData("remind me to night and to day", "Remind me tonight and today")]
    [InlineData("go to morrowind", "Go to morrowind")]
    public void ARequestIsWrittenAsOne(string heard, string expected) => Assert.Equal(expected, SpokenRequestText.Normalize(heard));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("...")]
    [InlineData(null)]
    public void NothingHeardIsEmpty(string? heard) => Assert.Equal("", SpokenRequestText.Normalize(heard));

    [Theory]
    [InlineData("kiri what's on my calendar", "what's on my calendar")]
    [InlineData("Kiri, what's on my calendar tomorrow?", "what's on my calendar tomorrow?")]
    [InlineData("hey kiri set a timer", "set a timer")]
    [InlineData("kerry open the settings", "open the settings")]
    [InlineData("e what's on my calendar", "what's on my calendar")]
    [InlineData("y carie what's the weather", "what's the weather")]
    [InlineData("ri what time is it", "what time is it")]
    [InlineData("curry what time is it", "what time is it")]
    [InlineData("kiri", "")]
    [InlineData("kiri kiri what's up", "what's up")]
    [InlineData("what's on my calendar", "what's on my calendar")]
    [InlineData("hello there", "hello there")]
    [InlineData("carry the box over here", "carry the box over here")]
    public void TheWakeWordIsTakenOffTheFrontOfARequest(string heard, string expected) => Assert.Equal(expected, SpokenRequestText.StripWakeWord(heard));

    [Theory]
    [InlineData("kiri", true)]
    [InlineData("Kerry,", true)]
    [InlineData("curry", true)]
    [InlineData("siri", false)]
    [InlineData("carry", false)]
    [InlineData("", false)]
    public void WhichSpellingsAreTheWakeWord(string word, bool expected) => Assert.Equal(expected, SpokenRequestText.IsWakeWord(word));
}

public sealed class AudioRingBufferTests
{
    [Fact]
    public void HoldsTheLastSamplesInOrder()
    {
        var ring = new AudioRingBuffer(5);
        ring.Write([1, 2, 3]);
        ring.Write([4, 5, 6, 7]);

        Assert.Equal([3, 4, 5, 6, 7], ring.Last(5));
        Assert.Equal([6, 7], ring.Last(2));
        Assert.Equal(5, ring.Count);
        Assert.Equal(7, ring.TotalWritten);
    }

    [Fact]
    public void AWriteLargerThanTheBufferKeepsItsEnd()
    {
        var ring = new AudioRingBuffer(3);
        ring.Write([1, 2, 3, 4, 5, 6, 7]);

        Assert.Equal([5, 6, 7], ring.Last(3));
    }

    [Fact]
    public void AskingForMoreThanIsHeldReturnsWhatThereIs()
    {
        var ring = new AudioRingBuffer(10);
        ring.Write([9, 8]);

        Assert.Equal([9, 8], ring.Last(10));
        Assert.Empty(new AudioRingBuffer(4).Last(3));
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var ring = new AudioRingBuffer(4);
        ring.Write([1, 2, 3]);
        ring.Clear();

        Assert.Equal(0, ring.Count);
        Assert.Empty(ring.Last(4));
    }
}
