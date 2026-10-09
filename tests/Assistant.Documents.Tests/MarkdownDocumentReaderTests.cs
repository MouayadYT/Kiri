using Assistant.Core.Documents;
using Assistant.Documents.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Documents.Tests;

/// <summary>Reading Markdown: sections by heading with their trails and lines, and what is and is not a heading.</summary>
public sealed class MarkdownDocumentReaderTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly MarkdownDocumentReader _reader = new(NullLogger<MarkdownDocumentReader>.Instance);

    public void Dispose() => _folder.Dispose();

    private async Task<DocumentReadResult> ReadAsync(string markdown, DocumentReadOptions? options = null) =>
        await _reader.ReadAsync(_folder.Write("doc.md", markdown), options);

    [Fact]
    public void TheReaderSaysWhatItHandles()
    {
        Assert.Equal("markdown", _reader.Id);
        Assert.Equal([".md", ".markdown"], _reader.SupportedExtensions);
        Assert.Equal(["text/markdown", "text/x-markdown"], _reader.SupportedMimeTypes);
    }

    [Fact]
    public async Task EachHeadingStartsASectionWithItsTrailAndLines()
    {
        var result = await ReadAsync(
            "Intro before any heading.\n\n# Install\nUse the installer.\n\n## Windows\nRun setup.\n\n## Linux\nUse apt.\n\n# Usage\nRun it.\n");

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Collection(
            result.Segments,
            s =>
            {
                Assert.Equal(DocumentLocation.ForSection(1, null, 1, 1), s.Location);
                Assert.Equal("Intro before any heading.", s.Text);
            },
            s =>
            {
                Assert.Equal(DocumentLocation.ForSection(2, "Install", 3, 4), s.Location);
                Assert.Equal("# Install\nUse the installer.", s.Text);
            },
            s =>
            {
                Assert.Equal(DocumentLocation.ForSection(3, "Install > Windows", 6, 7), s.Location);
                Assert.Equal("## Windows\nRun setup.", s.Text);
            },
            s =>
            {
                Assert.Equal(DocumentLocation.ForSection(4, "Install > Linux", 9, 10), s.Location);
                Assert.Equal("## Linux\nUse apt.", s.Text);
            },
            s =>
            {
                Assert.Equal(DocumentLocation.ForSection(5, "Usage", 12, 13), s.Location);
                Assert.Equal("# Usage\nRun it.", s.Text);
            });
    }

    [Fact]
    public async Task SectionsAreMarkedInTheTextForAPrompt()
    {
        var result = await ReadAsync("# One\nalpha\n\n## Two\nbeta");

        Assert.Equal("[Section 1: One]\n# One\nalpha\n\n[Section 2: One > Two]\n## Two\nbeta", result.ToText());
    }

    [Fact]
    public async Task ADocumentWithNoHeadingIsOnePieceWithItsLines()
    {
        var result = await ReadAsync("just some text\nand more\n\n- a list\n");

        var segment = Assert.Single(result.Segments);
        Assert.Equal(DocumentLocation.WholeDocument(1, 4), segment.Location);
    }

    [Fact]
    public async Task ASectionThatLeavesALevelStartsTheTrailAgainFromItsLevel()
    {
        var result = await ReadAsync("# A\n## B\n### C\n## D\n# E\n### F\n");

        Assert.Equal(
            ["A", "A > B", "A > B > C", "A > D", "E", "E > F"],
            result.Segments.Select(s => s.Location.Label!).ToArray());
    }

    [Fact]
    public async Task AHashLineInsideACodeFenceIsCodeAndNotAHeading()
    {
        var result = await ReadAsync(
            "# Real\n```bash\n# a comment in a script\necho hi\n```\n~~~\n# another comment\n~~~\n````\n```\n# still code\n````\n# Second\n");

        Assert.Equal(["Real", "Second"], result.Segments.Select(s => s.Location.Label!).ToArray());
        Assert.Contains("# a comment in a script", result.Segments[0].Text);
        Assert.Contains("# still code", result.Segments[0].Text);
    }

    [Fact]
    public async Task AnUnclosedFenceRunsToTheEndOfTheFile()
    {
        var result = await ReadAsync("# Real\n```\n# not a heading\n## neither\n");

        var segment = Assert.Single(result.Segments);
        Assert.Equal("Real", segment.Location.Label);
    }

    [Theory]
    [InlineData("#hashtag is not a heading")]
    [InlineData("####### seven hashes")]
    [InlineData("    # indented four spaces is code")]
    [InlineData("\\# escaped")]
    [InlineData("> # in a quote")]
    [InlineData("- # in a list")]
    public async Task ALineThatLooksLikeAHeadingButIsNotIsText(string line)
    {
        var result = await ReadAsync("Title text\n\n" + line + "\n");

        var segment = Assert.Single(result.Segments);
        Assert.Equal(DocumentLocationKind.Document, segment.Location.Kind);
    }

    [Theory]
    [InlineData("# Title", "Title")]
    [InlineData("   ### Indented three", "Indented three")]
    [InlineData("# Title #", "Title")]
    [InlineData("## Title ####   ", "Title")]
    [InlineData("# Title#", "Title#")]
    [InlineData("#\tTabbed", "Tabbed")]
    [InlineData("###### Six", "Six")]
    public async Task AnAtxHeadingHasItsTextWithoutItsMarkers(string heading, string expected)
    {
        var result = await ReadAsync(heading + "\nbody\n");

        Assert.Equal(expected, result.Segments[0].Location.Label);
    }

    [Fact]
    public async Task ALineOfEqualsOrDashesUnderTextMakesThatTextAHeading()
    {
        var result = await ReadAsync("Top\n===\nbody one\n\nSecond\n------\nbody two\n");

        Assert.Equal(["Top", "Top > Second"], result.Segments.Select(s => s.Location.Label!).ToArray());
        Assert.Equal("Top\n===\nbody one", result.Segments[0].Text);
        Assert.Equal(DocumentLocation.ForSection(2, "Top > Second", 5, 7), result.Segments[1].Location);
    }

    [Fact]
    public async Task ADashedLineAfterABlankLineIsARuleNotAHeading()
    {
        var result = await ReadAsync("# Only\ntext\n\n---\n\nmore text\n- item\n---\n\n***\n___\n");

        var segment = Assert.Single(result.Segments);
        Assert.Equal("Only", segment.Location.Label);
    }

    [Fact]
    public async Task YamlFrontMatterIsNotHeadingsAndStaysWithTheText()
    {
        var result = await ReadAsync("---\ntitle: My page\ntags: a, b\n---\n# Real heading\nbody\n");

        Assert.Collection(
            result.Segments,
            front =>
            {
                Assert.Null(front.Location.Label);
                Assert.Equal("---\ntitle: My page\ntags: a, b\n---", front.Text);
            },
            real => Assert.Equal("Real heading", real.Location.Label));
    }

    [Fact]
    public async Task ADashedLineAtTheTopWithNoClosingOneIsNotFrontMatter()
    {
        var result = await ReadAsync("---\ntext after a rule\n# Heading\n");

        Assert.Equal(2, result.Segments.Count);
        Assert.Equal("Heading", result.Segments[1].Location.Label);
    }

    [Fact]
    public async Task MarkdownIsKeptAsWrittenTablesLinksAndAll()
    {
        var markdown = "# Table\n\n| a | b |\n|---|---|\n| 1 | [link](http://example.com) |\n\n**bold** and `code`\n";

        var result = await ReadAsync(markdown);

        Assert.Equal(markdown.TrimEnd(), result.Segments[0].Text);
    }

    [Fact]
    public async Task WindowsLineBreaksGiveTheSameSectionsAndLineNumbers()
    {
        var unix = await ReadAsync("# A\nx\n# B\ny\n");
        var windows = await ReadAsync("# A\r\nx\r\n# B\r\ny\r\n");

        Assert.Equal(unix.Segments.Select(s => s.Location), windows.Segments.Select(s => s.Location));
        Assert.Equal(unix.Segments.Select(s => s.Text), windows.Segments.Select(s => s.Text));
    }

    [Fact]
    public async Task AnEmptyHeadingIsASectionWithNoLabelAndAnEmptyTrailLevelIsSkipped()
    {
        var result = await ReadAsync("# Top\n##\ntext\n### Deep\nmore\n");

        Assert.Equal(["Top", "Top", "Top > Deep"], result.Segments.Select(s => s.Location.Label!).ToArray());
    }

    [Fact]
    public async Task ALongTrailIsCutAtTheLimit()
    {
        var result = await ReadAsync("# " + new string('a', 100) + "\n## " + new string('b', 100) + "\ntext\n");

        var label = result.Segments[1].Location.Label!;
        Assert.Equal(HeadingTrailLimit, label.Length);
        Assert.StartsWith(new string('a', 100) + " > bbb", label);
        Assert.EndsWith("…", label);
    }

    private const int HeadingTrailLimit = Assistant.Documents.Extraction.HeadingTrail.MaxLabelLength;

    [Fact]
    public async Task MoreSectionsThanTheUnitLimitAreLeftOutAndSaySo()
    {
        var result = await ReadAsync("# 1\na\n# 2\nb\n# 3\nc\n# 4\nd\n", new DocumentReadOptions { MaxUnits = 3 });

        Assert.Equal(3, result.Segments.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task TheCharacterLimitCutsInsideASectionAndStopsThere()
    {
        var result = await ReadAsync("# One\n" + string.Join(" ", Enumerable.Repeat("word", 200)) + "\n# Two\nlater\n", new DocumentReadOptions { MaxCharacters = 100 });

        var segment = Assert.Single(result.Segments);
        Assert.True(result.Truncated);
        Assert.True(segment.Text.Length <= 100);
        Assert.Equal("One", segment.Location.Label);
    }

    [Fact]
    public async Task AnEmptyMarkdownFileHasNoText()
    {
        var result = await ReadAsync(string.Empty);

        Assert.Equal(DocumentReadStatus.NoText, result.Status);
    }

    [Fact]
    public async Task ADocumentOfOneHugeLineIsReadInLinearTime()
    {
        var line = "#" + new string(' ', 400_000) + "#";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await ReadAsync(line + "\n" + "a " + new string('-', 400_000) + "\n=====\n");

        Assert.NotEqual(DocumentReadStatus.Corrupt, result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "a long line took too long");
    }
}
