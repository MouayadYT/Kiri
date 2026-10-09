using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Assistant.Core.Domain;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    [Fact]
    public void ProseStreamsIntoBlocksAndRedrawsOnlyWhatChanged()
    {
        var prose = new TextContent();
        Assert.Empty(prose.Blocks);
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)prose.Blocks).CollectionChanged += (_, e) => changes.Add(e.Action);

        prose.Text = "# Plan";
        prose.Text = "# Plan\n\nPack";
        var heading = prose.Blocks[0];
        prose.Text = "# Plan\n\nPack the map";
        Assert.Same(heading, prose.Blocks[0]);
        Assert.Equal(new ParagraphBlock("Pack the map"), prose.Blocks[1]);
        prose.Text = "# Plan";
        Assert.Equal(new HeadingBlock(1, "Plan"), Assert.Single(prose.Blocks));
        Assert.Equal(
            [NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Replace,
             NotifyCollectionChangedAction.Remove],
            changes);

        prose.Text = null!;
        Assert.Equal("", prose.Text);
        Assert.Empty(prose.Blocks);
        Assert.Equal(nameof(TextContent), new TextContent("private text").ToString());
    }

    [Fact]
    public void AnAssistantMessageIsTypedContentInOrder()
    {
        var message = new MessageViewModel(MessageRole.Assistant, "First part.");
        var first = Assert.IsType<TextContent>(Assert.Single(message.Content));
        Assert.Equal("First part.", first.Text);

        message.Content.Add(new SampleResultCard("caption", "1"));
        message.Content.Add(new TextContent("Second part."));
        Assert.Equal([typeof(TextContent), typeof(SampleResultCard), typeof(TextContent)], message.Content.Select(part => part.GetType()));
        Assert.Equal("First part.\n\nSecond part.", message.Text);
        Assert.Empty(new MessageViewModel(MessageRole.Assistant).Content);
    }

    [Theory]
    [InlineData(MessageRole.User)]
    [InlineData(MessageRole.Tool)]
    public void OnlyTheAssistantsMessagesHaveContent(MessageRole role)
    {
        var message = new MessageViewModel(role, "# Not a heading\n- nor a list");
        Assert.Empty(message.Content);
        Assert.Equal("# Not a heading\n- nor a list", message.Text);
    }

    [Fact]
    public void AnswersShowParagraphsHeadingsAndListsInAnOpenColumn() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        model.StartNew("Synthetic question");
        model.Messages.Add(new MessageViewModel(MessageRole.Assistant,
            "Synthetic intro.\n\n## Synthetic heading\nSynthetic paragraph.\n\n- First\n  - Nested\n- Second\n\n9. Ninth\n10. Tenth"));
        try
        {
            panel.ShowConversation();
            Pump();
            var area = Named<Grid>(panel, "ConversationLayer");
            Rect Bounds(string text) => BoundsIn(area, TextNamed(area, text));

            // Open text across the conversation's width on the left, in white.
            var intro = TextNamed(area, "Synthetic intro.");
            Assert.Equal((30, 358), (Bounds("Synthetic intro.").Left, Bounds("Synthetic intro.").Width));
            Assert.Equal(15, intro.FontSize);
            Assert.Equal(FontWeights.Normal, intro.FontWeight);
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(intro.Foreground).Color);

            // A heading is larger and semibold, with more room above it than below.
            var heading = TextNamed(area, "Synthetic heading");
            Assert.Equal(17, heading.FontSize);
            Assert.Equal(FontWeights.SemiBold, heading.FontWeight);
            Assert.Equal(AutomationHeadingLevel.Level2, AutomationProperties.GetHeadingLevel(heading));
            Assert.Equal(Bounds("Synthetic intro.").Bottom + 22, Bounds("Synthetic heading").Top, 3);
            Assert.Equal(Bounds("Synthetic heading").Bottom + 12, Bounds("Synthetic paragraph.").Top, 3);

            // List items hang from their markers, and nesting moves an item under the text of the one above.
            Assert.Equal(Bounds("Synthetic paragraph.").Bottom + 12, Bounds("First").Top, 3);
            Assert.Equal(Bounds("First").Bottom + 6, Bounds("Nested").Top, 3);
            Assert.Equal(Bounds("Nested").Bottom + 6, Bounds("Second").Top, 3);
            Assert.Equal((48, 66, 48), (Bounds("First").Left, Bounds("Nested").Left, Bounds("Second").Left));
            var bullets = Descendants<TextBlock>(area).Where(text => text.Text == "•").Select(text => BoundsIn(area, text)).ToArray();
            Assert.Equal([42, 60, 42], bullets.Select(bullet => Math.Round(bullet.Right, 3)));
            Assert.Equal(Bounds("First").Top, bullets[0].Top, 3);

            // Numbers are right-aligned in a column as wide as the widest, so their items' text lines up.
            Assert.Equal(Bounds("Second").Bottom + 12, Bounds("Ninth").Top, 3);
            var textLeft = Bounds("Ninth").Left;
            Assert.True(textLeft > 48, $"The numbers' column is too narrow ({textLeft - 30:F1}).");
            Assert.Equal(textLeft, Bounds("Tenth").Left, 3);
            Assert.Equal(textLeft - 6, Bounds("9.").Right, 3);
            Assert.Equal(textLeft - 6, Bounds("10.").Right, 3);
            Assert.Equal(Bounds("Ninth").Top, Bounds("9.").Top, 3);

            // A heading that opens an answer starts where the answer does.
            model.Messages.Add(new MessageViewModel(MessageRole.User, "Synthetic follow-up"));
            model.Messages.Add(new MessageViewModel(MessageRole.Assistant, "# Opening heading\nText"));
            Pump();
            var followUp = BoundsIn(area, Descendants<SpeechBubble>(area).Last());
            Assert.Equal(followUp.Bottom + 40, Bounds("Opening heading").Top, 3);
            Assert.Equal(20, TextNamed(area, "Opening heading").FontSize);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void CardsSitBelowTheAnswerAndReachPastItsText() => RunSta(() => WithTheme(() =>
    {
        UseSampleCardTemplate();
        var (panel, model, _) = CreatePanel();
        model.StartNew("Synthetic question");
        var answer = new MessageViewModel(MessageRole.Assistant, "Synthetic answer.");
        answer.Content.Add(new SampleResultCard("Synthetic caption", "42"));
        var cardsOnly = new MessageViewModel(MessageRole.Assistant);
        cardsOnly.Content.Add(new TextContent()); // Prose that has not streamed in yet takes no space.
        cardsOnly.Content.Add(new SampleResultCard("Another caption", "7"));
        cardsOnly.Content.Add(new UntemplatedCard());
        var textOnly = new MessageViewModel(MessageRole.Assistant, "Text only.");
        model.Messages.Add(answer);
        model.Messages.Add(cardsOnly);
        model.Messages.Add(textOnly);
        model.Messages.Add(new MessageViewModel(MessageRole.User, "Synthetic follow-up"));
        try
        {
            panel.ShowConversation();
            Pump();
            var area = Named<Grid>(panel, "ConversationLayer");
            Rect Bounds(string text) => BoundsIn(area, TextNamed(area, text));
            Rect[] Cards() => Descendants<ContentControl>(area).Where(frame => frame.Content is MessageCard)
                .Select(frame => BoundsIn(area, frame)).ToArray();

            // A card is 19.5 below the answer's text, 16 in from the panel's edges, and black with the panel's corners.
            var cards = Cards();
            Assert.Equal(3, cards.Length);
            Assert.Equal((16, 386), (cards[0].Left, cards[0].Width));
            Assert.Equal(Bounds("Synthetic answer.").Bottom + 19.5, cards[0].Top, 3);
            var frame = Descendants<ContentControl>(area).First(control => control.Content is MessageCard);
            var surface = Assert.Single(Descendants<PanelShape>(frame));
            Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(surface.Fill).Color);
            Assert.Equal(40.5, surface.CornerSize);
            Assert.False(frame.Focusable);

            // Its kind of card draws inside the frame's padding.
            Assert.Equal(new Point(34, cards[0].Top + 26.5), BoundsIn(area, TextNamed(area, "Synthetic caption")).TopLeft);

            // An answer with words has the row with its copy button under it, which takes 24 (6 under the answer, 28 of button, 10 of that in the gap
            // to the next message). Without text, cards start where the message does, 12 apart; a kind without a template draws nothing.
            const double row = 24;
            Assert.Equal(cards[0].Bottom + row + 40, cards[1].Top, 3);
            Assert.Equal(cards[1].Bottom + 12, cards[2].Top, 3);
            Assert.Equal(26.5 + 30, cards[2].Height, 3);
            Assert.DoesNotContain(Descendants<TextBlock>(area), text => text.Text.Contains(nameof(UntemplatedCard)));

            // Without cards, the next message follows the text; a card that arrives later appears below it.
            Assert.Equal(cards[2].Bottom + 40, Bounds("Text only.").Top, 3);
            var followUp = Descendants<SpeechBubble>(area).Last();
            Assert.Equal(Bounds("Text only.").Bottom + row + 40, BoundsIn(area, followUp).Top, 3);
            textOnly.Content.Add(new SampleResultCard("Late caption", "1"));
            Pump();
            Assert.Equal(Bounds("Text only.").Bottom + 19.5, Cards()[3].Top, 3);
            Assert.Equal(Cards()[3].Bottom + row + 40, BoundsIn(area, followUp).Top, 3);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void ContentIsDrawnByWhatProducedItAndProseIsNeverBoxed() => RunSta(() => WithTheme(() =>
    {
        UseSampleCardTemplate();
        UseSampleNoticeTemplate();
        var (panel, model, _) = CreatePanel();
        model.StartNew("Synthetic question");
        model.Messages.Add(new MessageViewModel(MessageRole.Assistant, "Plain prose answer.\n\nWith a second paragraph."));
        var mixed = new MessageViewModel(MessageRole.Assistant, "Before the card.");
        mixed.Content.Add(new SampleResultCard("Synthetic caption", "42"));
        mixed.Content.Add(new TextContent("After the card."));
        mixed.Content.Add(new SampleNotice("Synthetic notice"));
        mixed.Content.Add(new UntemplatedContent());
        model.Messages.Add(mixed);
        try
        {
            panel.ShowConversation();
            Pump();
            var area = Named<Grid>(panel, "ConversationLayer");
            var transcript = Named<FadingScrollViewer>(panel, "Transcript");
            Rect Bounds(string text) => BoundsIn(area, TextNamed(area, text));
            var frames = Descendants<ContentControl>(transcript).Where(control => control.Content is MessageCard).ToArray();

            // Prose is open text in the column, never inside a frame or a card-coloured surface.
            foreach (var prose in new[] { "Plain prose answer.", "With a second paragraph.", "Before the card.", "After the card." })
            {
                Assert.Equal(30, Bounds(prose).Left, 3);
                Assert.False(IsInside(TextNamed(area, prose), transcript, element => element is ContentControl { Content: MessageCard } or PanelShape),
                    $"Prose '{prose}' was boxed.");
            }

            // Only the card is framed, between the prose parts, in the order they were added.
            var card = BoundsIn(area, Assert.Single(frames));
            Assert.Single(Descendants<PanelShape>(transcript));
            Assert.Equal(Bounds("Before the card.").Bottom + 19.5, card.Top, 3);
            Assert.Equal(card.Bottom + 19.5, Bounds("After the card.").Top, 3);

            // Another kind of content is drawn by its own template, in the text column and unframed; a kind without a
            // template draws nothing.
            var notice = TextNamed(area, "Synthetic notice");
            Assert.Equal(FontStyles.Italic, notice.FontStyle);
            Assert.Equal(Bounds("After the card.").Bottom + 12, Bounds("Synthetic notice").Top, 3);
            Assert.Equal(30, Bounds("Synthetic notice").Left, 3);
            Assert.False(IsInside(notice, transcript, element => element is ContentControl { Content: MessageCard } or PanelShape));
            Assert.DoesNotContain(Descendants<TextBlock>(area), text => text.Text.Contains(nameof(UntemplatedContent)));
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void ConversationMatchesItsReference() => RunSta(() => WithTheme(() =>
    {
        UseSampleCardTemplate();
        var (panel, model, _) = CreatePanel();
        model.StartNew("What is 9+10");
        var answer = new MessageViewModel(MessageRole.Assistant, "9 + 10 is 19.");
        answer.Content.Add(new SampleResultCard("9 + 10 =", "19"));
        model.Messages.Add(answer);
        try
        {
            panel.ShowConversation();
            var area = Named<Grid>(panel, "ConversationLayer");
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            RenderGlass(panel, "conversation-reference.png", 2);

            model.Messages.Add(new MessageViewModel(MessageRole.User, "Plan a short synthetic trip for me, with a packing list"));
            model.Messages.Add(new MessageViewModel(MessageRole.Assistant,
                "# Weekend plan\nA synthetic answer that wraps across more than one line of the conversation.\n\n" +
                "## Pack\n- Map\n- Water\n  - Two bottles\n\n## Steps\n1. Leave early\n2. Take the long road home\n\nEnjoy."));
            Pump();
            Named<FadingScrollViewer>(panel, "Transcript").ScrollToEnd();
            Pump();
            RenderGlass(panel, "conversation-blocks.png", 2);
        }
        finally { panel.Close(); }
    }));

    private static TextBlock TextNamed(DependencyObject root, string text) =>
        Descendants<TextBlock>(root).Single(block => block.Text == text);

    // Whether any visual between element and root matches, such as a card frame around prose.
    private static bool IsInside(DependencyObject element, DependencyObject root, Func<DependencyObject, bool> match)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null && current != root;
             current = VisualTreeHelper.GetParent(current))
        {
            if (match(current)) return true;
        }

        return false;
    }

    // A stand-in for a future kind of content that is not a card, such as a notice.
    private static void UseSampleNoticeTemplate()
    {
        var template = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <TextBlock Text="{Binding Notice}" FontStyle="Italic" Foreground="White" />
            </DataTemplate>
            """);
        template.DataType = typeof(SampleNotice);
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            [new DataTemplateKey(typeof(SampleNotice))] = template,
        });
    }

    // A stand-in for a future kind of card, laid out like the reference's result card.
    private static void UseSampleCardTemplate()
    {
        var template = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <StackPanel VerticalAlignment="Center">
                        <TextBlock Text="{Binding Caption}" FontFamily="Segoe UI Variable Text" FontSize="13" Foreground="#4D4D4D" />
                        <TextBlock Text="{Binding Value}" FontFamily="Segoe UI Variable Display" FontSize="26" Foreground="#F2F2F2" />
                    </StackPanel>
                    <Ellipse Grid.Column="1" Width="24" Height="24" Fill="#232323" VerticalAlignment="Center" />
                </Grid>
            </DataTemplate>
            """);
        template.DataType = typeof(SampleResultCard);
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            [new DataTemplateKey(typeof(SampleResultCard))] = template,
        });
    }
}

public sealed class SampleResultCard(string caption, string value) : MessageCard
{
    public string Caption { get; } = caption;
    public string Value { get; } = value;
}

public sealed class UntemplatedCard : MessageCard;

public sealed class SampleNotice(string notice) : MessageContent
{
    public string Notice { get; } = notice;
}

public sealed class UntemplatedContent : MessageContent;
