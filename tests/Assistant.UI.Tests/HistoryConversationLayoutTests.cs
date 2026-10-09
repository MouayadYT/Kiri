using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core.Domain;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // The conversation of the full-window reference, as synthetic content: an answer with a calculation card, a question
    // and an answer with a gallery of four photos.
    private static MessageViewModel[] ReferenceConversation()
    {
        var calculation = new MessageViewModel(MessageRole.Assistant, "9 + 10 is 19.");
        calculation.Content.Add(new CalculationResult("9 + 10", "19"));
        var photos = new MessageViewModel(MessageRole.Assistant, "I found 4 photos from yesterday.");
        photos.Content.Add(new ImageCollection(Enumerable.Range(0, 4).Select(i =>
            new ImageItem($"Synthetic photo {i}", new WriteableBitmap(8, 8, 96, 96, PixelFormats.Pbgra32, null)))));
        return [calculation, new MessageViewModel(MessageRole.User, "Find the image I took yesterday"), photos];
    }

    [Fact]
    public void FullWindowConversationSitsCenteredInTheWorkspaceWithTheInputBarBelow() => RunSta(() => WithTheme(() =>
    {
        var (window, history, _) = CreateHistoryWindow();
        try
        {
            window.Show();
            Pump();
            var root = Named<Grid>(window, "Root");
            var view = Named<ConversationView>(window, "Conversation");
            var composer = Named<Grid>(window, "Composer");

            // Nothing open: no conversation and no input bar, just the empty workspace's words.
            Assert.Equal(Visibility.Collapsed, view.Visibility);
            Assert.Equal(Visibility.Collapsed, composer.Visibility);

            history.Open(Guid.NewGuid(), ReferenceConversation(), ReferenceNow);
            Pump();
            view.Viewer.ScrollToTop();
            Pump();

            // The conversation fills the workspace, drawn by the view the floating conversation uses, in a column
            // centered on it: 605.4 wide, cards and gallery as wide as the column and the text 17 in from their sides.
            Assert.Equal(new Rect(320, 0, 1120, 824), BoundsIn(root, view));
            Assert.Same(history.Selected!.Messages, view.Messages);
            var column = BoundsIn(root, view.List);
            Assert.Equal(320 + ((1120 - 605.4) / 2), column.Left, 1);
            Assert.Equal(605.4, column.Width, 1);
            var text = Descendants<TextBlock>(view).First(block => block.Text == "9 + 10 is 19.");
            Assert.Equal(column.Left + 17, BoundsIn(root, text).Left, 1);

            // The first line's baseline is 50.96 below the window's top and the card under it is 73 down, 101.8 tall,
            // measured from the reference; the gallery ends 751.9 down, two rows of square tiles across the column (727.9 in the reference, and 24
            // more for the row with the copy button under the answer before it).
            Assert.Equal(34.8, BoundsIn(root, text).Top, 1);
            var card = BoundsIn(root, Descendants<ContentControl>(view).Single(control => control.Content is CalculationResult));
            Assert.Equal(column.Left, card.Left, 1);
            Assert.Equal(605.4, card.Width, 1);
            Assert.Equal(73.0, card.Top, 0.6);
            Assert.Equal(101.8, card.Height, 0.6);
            var gallery = BoundsIn(root, Descendants<GalleryPanel>(view).Single());
            Assert.Equal(column.Left, gallery.Left, 1);
            Assert.Equal(605.4, gallery.Width, 1);
            Assert.Equal(751.9, gallery.Bottom, 0.6);

            // The messages pass under the input bar and the buttons and fade out, but rest clear of both: the first
            // line is fully shown, and so is everything down to the gallery's last row.
            var top = view.Viewer.TopFade;
            var bottom = view.Viewer.BottomFade;
            Assert.Equal(1, FadingScrollViewer.GetOpacity(40, 824, top, bottom));
            Assert.Equal(1, FadingScrollViewer.GetOpacity(gallery.Bottom, 824, top, bottom));
            Assert.InRange(FadingScrollViewer.GetOpacity(798, 824, top, bottom), 0, 0.45);
            Assert.Equal(0, FadingScrollViewer.GetOpacity(815, 824, top, bottom));
            Assert.Equal(0, FadingScrollViewer.GetOpacity(3, 824, top, bottom));

            // The input bar, measured from the reference: a 31.8-tall pill between two glass discs 30.7 across, all
            // centered 26 above the window's bottom, the pill 11.4 from each disc.
            Assert.Equal(Visibility.Visible, composer.Visibility);
            var attach = BoundsIn(root, Named<Button>(window, "AttachButton"));
            var field = BoundsIn(root, Named<Grid>(window, "ComposerField"));
            var voice = BoundsIn(root, Named<Button>(window, "ComposerVoiceButton"));
            AssertRect(new Rect(334.85, 824 - 26 - 15.35, 30.7, 30.7), attach);
            AssertRect(new Rect(376.95, 824 - 26 - 15.9, 1382.6 - 376.95, 31.8), field);
            AssertRect(new Rect(1440 - 15.3 - 30.7, 824 - 26 - 15.35, 30.7, 30.7), voice);
            Assert.Equal(field.Top + (field.Height / 2), attach.Top + (attach.Height / 2), 1);
            Assert.True(BoundsIn(root, composer).Bottom <= 824 - 10);

            // The bar lies over the messages: the glass is the shared glass layer. The plus opens its menu; the microphone
            // waits for a voice input, which this window was made without; the pill takes typing.
            Assert.Equal(ThemeColor("Brush.Control.Glass"), SolidColor(Descendants<PanelShape>(composer).First().Fill));
            Assert.True(Named<Button>(window, "AttachButton").IsEnabled);
            Assert.False(Named<Button>(window, "ComposerVoiceButton").IsEnabled);
            Assert.Equal("Add", AutomationName(Named<Button>(window, "AttachButton")));
            Assert.Equal("Use microphone", AutomationName(Named<Button>(window, "ComposerVoiceButton")));
            var input = Named<PromptInputControl>(window, "ComposerInput");
            Assert.Equal("Ask Assistant", input.Placeholder);
            Assert.False(input.ShowMicrophone);
            Assert.True(input.IsEnabled);
            input.Text = "A draft";
            Assert.False(input.TrySubmit());
            Assert.Equal("A draft", input.Text);
            Assert.Equal(13.5, Descendants<TextBox>(input).Single().FontSize);

            RenderFixture(root, "history-conversation-2x.png", 2);
            RenderFixture(root, "history-conversation.png", 0.9722);

            // Closing the conversation takes the bar with it.
            history.Selected = null;
            Pump();
            Assert.Equal(Visibility.Collapsed, composer.Visibility);
            Assert.Equal(Visibility.Collapsed, view.Visibility);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void FloatingAndFullWindowConversationsAreDrawnByOneView() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        var (window, history, _) = CreateHistoryWindow();
        try
        {
            var messages = ReferenceConversation();
            foreach (var message in messages) model.Messages.Add(message);
            panel.ShowConversation();
            window.Show();
            history.Open(model.Id, messages, ReferenceNow);
            Pump();

            // Both are the same view over the same messages, so the same texts, bubbles and cards are drawn; only where
            // they rest and how wide the column is differ.
            var floating = Named<ConversationView>(panel, "Conversation");
            var full = Named<ConversationView>(window, "Conversation");
            Assert.Same(floating.GetType(), full.GetType());
            Assert.Equal(Descendants<TextBlock>(floating).Select(block => block.Text),
                Descendants<TextBlock>(full).Select(block => block.Text));
            Assert.Equal(Descendants<SpeechBubble>(floating).Count(), Descendants<SpeechBubble>(full).Count());
            Assert.Equal(Descendants<ContentControl>(floating).Count(control => control.Content is CalculationResult),
                Descendants<ContentControl>(full).Count(control => control.Content is CalculationResult));
            Assert.Equal(Descendants<GalleryPanel>(floating).Count(), Descendants<GalleryPanel>(full).Count());

            // The floating panel keeps its own measurements: 30 in, and cards reaching 14 past the text.
            Assert.Equal(new Thickness(30, 84.25, 30, 68), floating.ContentPadding);
            Assert.Equal(double.PositiveInfinity, floating.ColumnWidth);
            var floatingCard = Descendants<ContentControl>(floating).Single(control => control.Content is CalculationResult);
            Assert.Equal(418 - 32, floatingCard.ActualWidth, 1);
            Assert.Equal(106.5, floatingCard.ActualHeight, 1);
        }
        finally
        {
            window.CloseForGood();
            panel.Close();
        }
    }));
}
