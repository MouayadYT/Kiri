using Assistant.Core.Documents;
using Assistant.Documents.Extraction;
using Xunit;

namespace Assistant.Documents.Tests;

/// <summary>The one plain shape every reader's text is put in, and the limit that cuts what is too long.</summary>
public sealed class TextCleanerTests
{
    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\r\n\r\nb", "a\n\nb")]
    public void EveryKindOfLineBreakBecomesOne(string raw, string expected)
    {
        Assert.Equal(expected, TextCleaner.Clean(raw));
    }

    [Fact]
    public void MoreThanOneBlankLineInARowIsOne()
    {
        Assert.Equal("a\n\nb", TextCleaner.Clean("a\n\n\n\n\nb"));
        Assert.Equal("a\n\nb", TextCleaner.Clean("a\n  \n\t\n\nb"));
    }

    [Fact]
    public void SpaceAtTheEndOfALineAndAroundTheTextGoesAndIndentationStays()
    {
        Assert.Equal("a\n    indented\nb", TextCleaner.Clean("\n\n  \na   \n    indented  \t\nb  \n\n"));
    }

    [Fact]
    public void ControlCharactersAndInvisibleMarksAreRemovedAndNoBreakSpacesAreSpaces()
    {
        var zeroWidth = ((char)0x200B).ToString();
        var byteOrderMark = ((char)0xFEFF).ToString();
        var softHyphen = ((char)0x00AD).ToString();
        var noBreakSpace = ((char)0x00A0).ToString();

        Assert.Equal("ab c", TextCleaner.Clean($"a{zeroWidth}{byteOrderMark}{softHyphen}\0\u0001b{noBreakSpace}c"));
    }

    [Fact]
    public void PageAndParagraphSeparatorsAreLineBreaks()
    {
        var lineSeparator = ((char)0x2028).ToString();
        var paragraphSeparator = ((char)0x2029).ToString();

        Assert.Equal("a\nb\n\nc", TextCleaner.Clean($"a{lineSeparator}b{paragraphSeparator}{paragraphSeparator}c"));
        Assert.Equal("a\nb", TextCleaner.Clean("a\fb"));
        Assert.Equal("a\nb", TextCleaner.Clean("a\vb"));
    }

    [Fact]
    public void ALoneSurrogateBecomesTheReplacementCharacterAndAPairStays()
    {
        var replacement = ((char)0xFFFD).ToString();
        var rocket = char.ConvertFromUtf32(0x1F680);

        Assert.Equal($"a{replacement}b", TextCleaner.Clean("a" + (char)0xD800 + "b"));
        Assert.Equal($"a{replacement}b", TextCleaner.Clean("a" + (char)0xDC00 + "b"));
        Assert.Equal($"a{rocket}b", TextCleaner.Clean($"a{rocket}b"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void NothingCleansToNothing(string? raw)
    {
        Assert.Equal(string.Empty, TextCleaner.Clean(raw));
    }

    [Fact]
    public void TheCollectorCutsThePieceThatCrossesTheLimitAtAWordAndStops()
    {
        var collector = new SegmentCollector(20);

        var more = collector.Add(DocumentLocation.ForPage(1), "one two three four five six seven");

        Assert.False(more);
        Assert.True(collector.IsFull);
        Assert.True(collector.Truncated);
        var segment = Assert.Single(collector.Segments);
        Assert.Equal("one two three four", segment.Text);
        Assert.True(segment.Text.Length <= 20);
    }

    [Fact]
    public void TextThatFitsExactlyIsNotTruncatedUntilMoreTextComes()
    {
        var collector = new SegmentCollector(5);

        Assert.False(collector.Add(DocumentLocation.ForPage(1), "abcde"));
        Assert.False(collector.Truncated);

        Assert.False(collector.Add(DocumentLocation.ForPage(2), "\n  \n"));
        Assert.False(collector.Truncated);

        Assert.False(collector.Add(DocumentLocation.ForPage(3), "more"));
        Assert.True(collector.Truncated);
        Assert.Single(collector.Segments);
    }

    [Fact]
    public void APieceWithNoTextIsDroppedAndTheNextOneStillCounts()
    {
        var collector = new SegmentCollector(100);

        Assert.True(collector.Add(DocumentLocation.ForPage(1), "   "));
        Assert.True(collector.Add(DocumentLocation.ForPage(2), null));
        Assert.True(collector.Add(DocumentLocation.ForPage(3), "text"));

        var segment = Assert.Single(collector.Segments);
        Assert.Equal(3, segment.Location.Number);
    }

    [Fact]
    public void TheCollectorNeverSplitsASurrogatePair()
    {
        var rocket = char.ConvertFromUtf32(0x1F680);
        var collector = new SegmentCollector(3);

        collector.Add(DocumentLocation.ForPage(1), "ab" + rocket + rocket);

        var text = Assert.Single(collector.Segments).Text;
        Assert.Equal("ab", text);
    }
}
