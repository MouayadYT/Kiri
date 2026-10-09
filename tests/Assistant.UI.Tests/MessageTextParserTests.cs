using Assistant.UI.Messages;
using Xunit;

namespace Assistant.UI.Tests;

public sealed class MessageTextParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData("  \n\t\n ")]
    public void BlankTextHasNoBlocks(string text) => Assert.Empty(MessageTextParser.Parse(text));

    [Fact]
    public void BlankLinesSeparateParagraphsAndSingleLineBreaksStay()
    {
        var blocks = MessageTextParser.Parse("First line\r\n  second line  \n\n\nNext paragraph");
        Assert.Equal<MessageBlock>(
            [new ParagraphBlock("First line\nsecond line"), new ParagraphBlock("Next paragraph")], blocks);
    }

    [Theory]
    [InlineData("# Title", 1, "Title")]
    [InlineData("## Title ##", 2, "Title")]
    [InlineData("   ### Spaced   ", 3, "Spaced")]
    [InlineData("###### Six", 6, "Six")]
    [InlineData("## Learning C#", 2, "Learning C#")]
    public void HashesAndASpaceStartAHeading(string line, int level, string text) =>
        Assert.Equal(new HeadingBlock(level, text), Assert.Single(MessageTextParser.Parse(line)));

    [Theory]
    [InlineData("#hashtag")]
    [InlineData("####### Seven is too many")]
    [InlineData("    # Indented four")]
    public void OtherHashesAreText(string line) =>
        Assert.IsType<ParagraphBlock>(Assert.Single(MessageTextParser.Parse(line)));

    [Fact]
    public void HeadingsEndParagraphsAndEmptyHeadingsAreSkipped()
    {
        var blocks = MessageTextParser.Parse("Intro\n## Details\nBody\n#\n# ");
        Assert.Equal<MessageBlock>(
            [new ParagraphBlock("Intro"), new HeadingBlock(2, "Details"), new ParagraphBlock("Body")], blocks);
    }

    [Fact]
    public void BulletsOfAnyKindMakeOneList()
    {
        var list = SingleList("- one\n* two\n+ three\n• four");
        Assert.Equal(
            [Item("•", "one"), Item("•", "two"), Item("•", "three"), Item("•", "four")], list.Items);
    }

    [Fact]
    public void NumberingStartsFromTheFirstItemAsInMarkdown()
    {
        Assert.Equal([Item("1.", "a"), Item("2.", "b"), Item("3.", "c")], SingleList("1. a\n1. b\n1) c").Items);
        Assert.Equal([Item("7.", "a"), Item("8.", "b")], SingleList("7. a\n2. b").Items);
    }

    [Fact]
    public void IndentingNestsItemsWithTheirOwnNumbering()
    {
        var list = SingleList("1. Fruit\n   - Apple\n   - Pear\n     1. Ripe\n     2. Raw\n2. Vegetables\n\t1. Leek");
        Assert.Equal(
            [
                Item("1.", "Fruit"), Item("•", "Apple", 1), Item("•", "Pear", 1), Item("1.", "Ripe", 2),
                Item("2.", "Raw", 2), Item("2.", "Vegetables"), Item("1.", "Leek", 1),
            ],
            list.Items);
    }

    [Fact]
    public void SwitchingBetweenBulletsAndNumbersStartsANewList()
    {
        var blocks = MessageTextParser.Parse("- a\n- b\n1. c\n  - d\n  2. e");
        Assert.Equal(2, blocks.Count);
        Assert.Equal([Item("•", "a"), Item("•", "b")], Assert.IsType<ListBlock>(blocks[0]).Items);
        Assert.Equal([Item("1.", "c"), Item("•", "d", 1), Item("2.", "e", 1)], Assert.IsType<ListBlock>(blocks[1]).Items);
    }

    [Fact]
    public void IndentedLinesContinueAnItemAndBlankLinesKeepTheListOpen()
    {
        var list = SingleList("- First\n  more of it\n\n- Second\n\n  after a gap");
        Assert.Equal([Item("•", "First\nmore of it"), Item("•", "Second\nafter a gap")], list.Items);
    }

    [Fact]
    public void AnUnindentedLineEndsTheList()
    {
        var blocks = MessageTextParser.Parse("Steps:\n1. Open it\n2. Close it\nThat's all.\n\n- x\n\nDone");
        Assert.Equal(5, blocks.Count);
        Assert.Equal(new ParagraphBlock("Steps:"), blocks[0]);
        Assert.Equal([Item("1.", "Open it"), Item("2.", "Close it")], Assert.IsType<ListBlock>(blocks[1]).Items);
        Assert.Equal(new ParagraphBlock("That's all."), blocks[2]);
        Assert.Equal([Item("•", "x")], Assert.IsType<ListBlock>(blocks[3]).Items);
        Assert.Equal(new ParagraphBlock("Done"), blocks[4]);
    }

    [Fact]
    public void OnlyTheNumberOneInterruptsAParagraph()
    {
        Assert.Equal(new ParagraphBlock("It opened in\n1999. Then it grew."),
            Assert.Single(MessageTextParser.Parse("It opened in\n1999. Then it grew.")));
        var blocks = MessageTextParser.Parse("Do this:\n1. First");
        Assert.Equal([Item("1.", "First")], Assert.IsType<ListBlock>(blocks[1]).Items);
    }

    [Theory]
    [InlineData("-5 degrees")]
    [InlineData("1.5 million")]
    [InlineData("**Bold** start")]
    [InlineData("*Emphasis* start")]
    public void MarkersNeedASpace(string line) =>
        Assert.Equal(new ParagraphBlock(line), Assert.Single(MessageTextParser.Parse(line)));

    [Fact]
    public void EveryPrefixOfAStreamingAnswerParses()
    {
        const string answer = "# Plan\n\nHere is what to do:\n\n1. Pack\n   - Map\n2. Go\n\nEnjoy.";
        for (var length = 0; length <= answer.Length; length++)
        {
            _ = MessageTextParser.Parse(answer[..length]);
        }

        Assert.Equal<MessageBlock>(
            [
                new HeadingBlock(1, "Plan"), new ParagraphBlock("Here is what to do:"),
                new ListBlock([Item("1.", "Pack"), Item("•", "Map", 1), Item("2.", "Go")]), new ParagraphBlock("Enjoy."),
            ],
            MessageTextParser.Parse(answer));
    }

    [Fact]
    public void BlocksCompareByValueAndKeepTheirTextOutOfToString()
    {
        Assert.Equal(new ListBlock([Item("•", "a")]), new ListBlock([Item("•", "a")]));
        Assert.Equal(new ListBlock([Item("•", "a")]).GetHashCode(), new ListBlock([Item("•", "a")]).GetHashCode());
        Assert.NotEqual(new ListBlock([Item("•", "a")]), new ListBlock([Item("•", "a"), Item("•", "b")]));

        MessageBlock[] blocks = [new ParagraphBlock("private text"), new HeadingBlock(1, "private text"),
            new ListBlock([Item("•", "private text")]), Item("•", "private text")];
        Assert.All(blocks, block => Assert.DoesNotContain("private", block.ToString()));
    }

    private static ListBlock SingleList(string text) => Assert.IsType<ListBlock>(Assert.Single(MessageTextParser.Parse(text)));

    private static ListItemBlock Item(string marker, string text, int depth = 0) => new(marker, text, depth);
}
