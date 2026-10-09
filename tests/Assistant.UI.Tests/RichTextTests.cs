using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using Assistant.Core.Domain;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>Emphasis, inline code, links and code blocks in an answer's prose (PROJECT_SPEC §4.2).</summary>
public sealed class InlineMarkdownTests
{
    [Fact]
    public void EmphasisIsReadIntoRunsOfOneStyleEach()
    {
        Assert.Equal(
            [
                new InlineSpan("A "),
                new InlineSpan("cat", InlineStyle.Bold),
                new InlineSpan(" is a small mammal ("),
                new InlineSpan("Felis catus", InlineStyle.Italic),
                new InlineSpan(")."),
            ],
            InlineMarkdown.Parse("A **cat** is a small mammal (*Felis catus*)."));

        Assert.Equal([new InlineSpan("Biology:", InlineStyle.Bold)], InlineMarkdown.Parse("**Biology:**"));
        Assert.Equal([new InlineSpan("both", InlineStyle.Bold | InlineStyle.Italic)], InlineMarkdown.Parse("***both***"));
        Assert.Equal([new InlineSpan("strong", InlineStyle.Bold)], InlineMarkdown.Parse("__strong__"));
        Assert.Equal([new InlineSpan("slanted", InlineStyle.Italic)], InlineMarkdown.Parse("_slanted_"));
        Assert.Equal([new InlineSpan("gone", InlineStyle.Strikethrough)], InlineMarkdown.Parse("~~gone~~"));
    }

    [Fact]
    public void EmphasisNestsInsideEmphasis()
    {
        Assert.Equal(
            [
                new InlineSpan("bold ", InlineStyle.Bold),
                new InlineSpan("and italic", InlineStyle.Bold | InlineStyle.Italic),
                new InlineSpan(" bold", InlineStyle.Bold),
            ],
            InlineMarkdown.Parse("**bold *and italic* bold**"));
        Assert.Equal(
            [
                new InlineSpan("slanted ", InlineStyle.Italic),
                new InlineSpan("strong", InlineStyle.Bold | InlineStyle.Italic),
                new InlineSpan(" slanted", InlineStyle.Italic),
            ],
            InlineMarkdown.Parse("*slanted **strong** slanted*"));
    }

    [Fact]
    public void AMarkThatNeverClosesIsText_SoAStreamingAnswerShowsItAsTypedUntilItCloses()
    {
        Assert.Equal([new InlineSpan("**Bio")], InlineMarkdown.Parse("**Bio"));
        Assert.Equal([new InlineSpan("a **b")], InlineMarkdown.Parse("a **b"));
        Assert.Equal([new InlineSpan("a *b")], InlineMarkdown.Parse("a *b"));
        Assert.Equal([new InlineSpan("`code")], InlineMarkdown.Parse("`code"));
        Assert.Equal([new InlineSpan("Bio", InlineStyle.Bold), new InlineSpan(" and")], InlineMarkdown.Parse("**Bio** and"));
    }

    [Theory]
    [InlineData("2 * 3 * 4")]
    [InlineData("snake_case_name and other_name")]
    [InlineData("a * b")]
    [InlineData("**")]
    [InlineData("****")]
    [InlineData("50% off ~ today")]
    public void TextThatOnlyLooksLikeMarksIsLeftAlone(string text) =>
        Assert.Equal([new InlineSpan(text)], InlineMarkdown.Parse(text));

    [Fact]
    public void InlineCodeIsShownAsWritten_AndItsMarksAreNotRead()
    {
        Assert.Equal(
            [new InlineSpan("Use "), new InlineSpan("a **b** _c_", InlineStyle.Code), new InlineSpan(" here")],
            InlineMarkdown.Parse("Use `a **b** _c_` here"));
        Assert.Equal([new InlineSpan("a`b", InlineStyle.Code)], InlineMarkdown.Parse("``a`b``"));
        Assert.Equal([new InlineSpan(" x ", InlineStyle.Code)], InlineMarkdown.Parse("`  x  `"));
        Assert.Equal(
            [new InlineSpan("bold ", InlineStyle.Bold), new InlineSpan("code", InlineStyle.Bold | InlineStyle.Code)],
            InlineMarkdown.Parse("**bold `code`**"));

        // A mark inside code cannot close emphasis outside it.
        Assert.Equal(
            [new InlineSpan("*a "), new InlineSpan("b*", InlineStyle.Code), new InlineSpan(" c")],
            InlineMarkdown.Parse("*a `b*` c"));
    }

    [Fact]
    public void ABackslashShowsTheMarkAfterItAsWritten()
    {
        Assert.Equal([new InlineSpan("*not italic*")], InlineMarkdown.Parse("\\*not italic\\*"));
        Assert.Equal([new InlineSpan("a \\b")], InlineMarkdown.Parse("a \\b"));
    }

    [Fact]
    public void LinksAreReadWithTheirWords_AndOnlyWebAndMailAddressesAreLinks()
    {
        Assert.Equal(
            [
                new InlineSpan("See "),
                new InlineSpan("the docs", InlineStyle.Link, "https://example.com/a"),
                new InlineSpan(" now"),
            ],
            InlineMarkdown.Parse("See [the docs](https://example.com/a) now"));
        Assert.Equal(
            [new InlineSpan("bold link", InlineStyle.Bold | InlineStyle.Link, "http://example.com")],
            InlineMarkdown.Parse("[**bold link**](http://example.com)"));
        Assert.Equal(
            [new InlineSpan("mail me", InlineStyle.Link, "mailto:me@example.com")],
            InlineMarkdown.Parse("[mail me](mailto:me@example.com)"));
        Assert.Equal(
            [new InlineSpan("a", InlineStyle.Link, "https://en.wikipedia.org/wiki/Cat_(disambiguation)")],
            InlineMarkdown.Parse("[a](https://en.wikipedia.org/wiki/Cat_(disambiguation))"));
        Assert.Equal(
            [new InlineSpan("x", InlineStyle.Link, "https://example.com")],
            InlineMarkdown.Parse("[x](https://example.com \"A title\")"));

        // Anything else stays as written, so nothing is run or opened that the user did not see.
        Assert.Equal([new InlineSpan("[click](javascript:alert(1))")], InlineMarkdown.Parse("[click](javascript:alert(1))"));
        Assert.Equal([new InlineSpan("[c](file:///C:/x.txt)")], InlineMarkdown.Parse("[c](file:///C:/x.txt)"));
        Assert.Equal(
            [
                new InlineSpan("[words] ("),
                new InlineSpan("https://example.com", InlineStyle.Link, "https://example.com"),
                new InlineSpan(")"),
            ],
            InlineMarkdown.Parse("[words] (https://example.com)"));
    }

    [Fact]
    public void WebAddressesTheModelDidNotWriteAsLinksAreLinksToo_WithoutTheirClosingPunctuation()
    {
        Assert.Equal(
            [
                new InlineSpan("Go to "),
                new InlineSpan("https://example.com/page", InlineStyle.Link, "https://example.com/page"),
                new InlineSpan(", then read on."),
            ],
            InlineMarkdown.Parse("Go to https://example.com/page, then read on."));
        Assert.Equal(
            [
                new InlineSpan("(see "),
                new InlineSpan("https://example.com", InlineStyle.Link, "https://example.com"),
                new InlineSpan(")"),
            ],
            InlineMarkdown.Parse("(see https://example.com)"));
        Assert.Equal(
            [new InlineSpan("https://example.com/a_b_c", InlineStyle.Link, "https://example.com/a_b_c")],
            InlineMarkdown.Parse("https://example.com/a_b_c"));

        // In code, an address is code.
        Assert.Equal([new InlineSpan("https://example.com", InlineStyle.Code)], InlineMarkdown.Parse("`https://example.com`"));
    }

    [Fact]
    public void PlainTextIsTheWordsWithoutTheMarks()
    {
        Assert.Equal(
            "A cat is a mammal, see docs and run x.",
            InlineMarkdown.ToPlainText("A **cat** is a *mammal*, see [docs](https://example.com) and run `x`."));
        Assert.Equal("", InlineMarkdown.ToPlainText(""));
    }

    [Fact]
    public void AnEnormousParagraphIsShownAsItIs()
    {
        var text = "**a** " + new string('*', 30_000);

        var span = Assert.Single(InlineMarkdown.Parse(text));

        Assert.Equal(text, span.Text);
        Assert.Equal(InlineStyle.None, span.Style);
    }

    [Fact]
    public void SpansNeverPutTheirWordsInToString() =>
        Assert.DoesNotContain("secret", new InlineSpan("secret", InlineStyle.Bold).ToString(), StringComparison.Ordinal);

    [Fact]
    public void ABlockOfCodeIsReadAsABlock_WithItsLanguage_AndNothingInsideItIsMarkdown()
    {
        var blocks = MessageTextParser.Parse("Here it is:\n\n```csharp\nvar x = **1**;\n\n- not a list\n```\n\nDone *now*.");

        Assert.Equal(
            [
                new ParagraphBlock("Here it is:"),
                new CodeBlock("var x = **1**;\n\n- not a list", "csharp"),
                new ParagraphBlock("Done *now*."),
            ],
            blocks);
    }

    [Fact]
    public void ACodeBlockWithoutAnEndRunsToTheEnd_SoAStreamingBlockGrows_AndAnEmptyOneIsNotShown()
    {
        Assert.Equal([new CodeBlock("a\nb", null)], MessageTextParser.Parse("```\na\nb"));
        Assert.Empty(MessageTextParser.Parse("```csharp"));
        Assert.Empty(MessageTextParser.Parse("```\n```"));
        Assert.Equal([new CodeBlock("x", "py")], MessageTextParser.Parse("```py\nx\n"));
    }

    [Fact]
    public void AFenceEndsWithAtLeastAsManyBackticks_AndInterruptsAParagraphAndAList()
    {
        Assert.Equal(
            [new CodeBlock("a\n```\nb", null)],
            MessageTextParser.Parse("````\na\n```\nb\n````"));
        var blocks = MessageTextParser.Parse("Text\n```\ncode\n```\n- item\n```\nmore\n```");
        Assert.Equal(
            [
                new ParagraphBlock("Text"),
                new CodeBlock("code", null),
                new ListBlock([new ListItemBlock("•", "item", 0)]),
                new CodeBlock("more", null),
            ],
            blocks);
    }
}

public sealed partial class PromptInputControlTests
{
    [Fact]
    public void ABlocksTextShowsItsEmphasis_AsRunsOfTheBlocksOwnText() => RunSta(() =>
    {
        var block = new TextBlock();

        InlineText.SetMarkdown(block, "A **cat** is *small*, ~~big~~, and `codes`.");

        Assert.Equal("A cat is small, big, and codes.", block.Text);
        var runs = block.Inlines.OfType<Run>().ToArray();
        Assert.Equal(FontWeights.Bold, runs.Single(run => run.Text == "cat").FontWeight);
        Assert.Equal(FontStyles.Italic, runs.Single(run => run.Text == "small").FontStyle);
        Assert.Equal(TextDecorations.Strikethrough, runs.Single(run => run.Text == "big").TextDecorations);
        var code = runs.Single(run => run.Text == "codes");
        Assert.Contains("Consolas", code.FontFamily.Source, StringComparison.Ordinal);
        Assert.NotNull(code.Background);
        Assert.Equal(FontWeights.Normal, runs[0].FontWeight);

        // Setting the text again replaces the runs, as a streaming answer does with every batch.
        InlineText.SetMarkdown(block, "plain");
        Assert.Equal("plain", block.Text);
        Assert.Single(block.Inlines);
    });

    [Fact]
    public void ALinkIsAHyperlinkThatOpensItsAddressWhenClicked() => RunSta(() =>
    {
        var block = new TextBlock();
        InlineText.SetMarkdown(block, "See [the docs](https://example.com/a) or https://example.org.");

        var links = block.Inlines.OfType<Hyperlink>().ToArray();

        Assert.Equal(["the docs", "https://example.org"], links.Select(link => ((Run)link.Inlines.First()).Text));
        Assert.Equal(new Uri("https://example.com/a"), links[0].NavigateUri);
        var opened = new List<Uri>();
        using (InlineText.OverrideLinkOpener(opened.Add))
        {
            links[0].RaiseEvent(new RequestNavigateEventArgs(links[0].NavigateUri, null) { Source = links[0], RoutedEvent = Hyperlink.RequestNavigateEvent });
        }

        Assert.Equal([new Uri("https://example.com/a")], opened);
    });

    [Fact]
    public void AnAnswersEmphasisCodeAndLinksAreDrawnInTheConversation() => RunSta(() => WithTheme(() =>
    {
        var answer = new MessageViewModel(MessageRole.Assistant);
        answer.Content.Add(new TextContent(
            """
            A **cat** is a small, domesticated feline mammal (*Felis catus*).

            **Biology:**

            - **Family:** Felidae
            - Lifespan: `12-18` years, see [Wikipedia](https://example.com/cat)

            ```csharp
            var cat = new Cat();
            ```
            """));
        var (window, vm, _) = CreatePanel();
        try
        {
            vm.Messages.Add(new MessageViewModel(MessageRole.User, "Tell me about cats"));
            vm.Messages.Add(answer);
            window.ShowConversation();
            WaitUntil(() => Named<Grid>(window, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();

            var texts = Descendants<TextBlock>(window).Where(text => text.IsVisible).ToArray();
            // No mark is left in what is drawn.
            Assert.DoesNotContain(texts, text => text.Text.Contains("**", StringComparison.Ordinal));
            Assert.DoesNotContain(texts, text => text.Text.Contains('`', StringComparison.Ordinal));
            var paragraph = Assert.Single(texts, text => text.Text.StartsWith("A cat is a small", StringComparison.Ordinal));
            Assert.Equal(FontWeights.Bold, paragraph.Inlines.OfType<Run>().Single(run => run.Text == "cat").FontWeight);
            Assert.Contains(texts, text => text.Text == "Biology:");
            Assert.Contains(texts, text => text.Text.Contains("years, see Wikipedia", StringComparison.Ordinal)
                && text.Inlines.OfType<Hyperlink>().Any());
            Assert.Contains(texts, text => text.Text == "var cat = new Cat();");
            Assert.Contains(texts, text => text.Text == "csharp");
            RenderGlass(window, "rich-text-2x.png", 2);
        }
        finally { window.Close(); }
    }));
}
