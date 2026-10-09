using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Assistant.Core.Domain;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Assistant.Windows.Backdrop;
using Assistant.Windows.Frame;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // The reference's clock: Monday, September 28, 2026, 12:30 PM.
    private static readonly DateTimeOffset ReferenceNow = new(2026, 9, 28, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public void HistoryShellMatchesTheReferenceLayout() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        var (window, history, frames) = CreateHistoryWindow(source: new SampleHistorySource(new FixedClock(ReferenceNow)));
        try
        {
            window.Show();
            Pump();
            var root = Named<Grid>(window, "Root");
            Assert.Equal(new Size(1440, 824), new Size(root.ActualWidth, root.ActualHeight));
            Assert.Null(history.Selected);

            // Windows draws the frame: dark Mica behind the whole window, with a neutral hairline a little lighter than
            // the workspace.
            var (handle, style) = Assert.Single(frames.Applied);
            Assert.Equal(new System.Windows.Interop.WindowInteropHelper(window).Handle, handle);
            Assert.Equal(new WindowFrameStyle(SystemBackdropKind.Mica, 0x3C3C3C), style);

            // Two columns: the 320-wide sidebar and the workspace.
            Assert.Equal(new Rect(0, 0, 320, 824), BoundsIn(root, Named<Grid>(window, "Sidebar")));
            Assert.Equal(new Rect(320, 0, 1120, 824), BoundsIn(root, Named<Grid>(window, "Workspace")));

            // The window's own buttons, 13.5 across and 21.75 apart, centered 24.25 down; the glass buttons, 36
            // across, 6.25 down, by the sidebar's edges and the window's right.
            Assert.Equal(new Rect(17.75, 17.5, 13.5, 13.5), BoundsIn(root, Named<Button>(window, "CloseButton")));
            Assert.Equal(new Rect(39.5, 17.5, 13.5, 13.5), BoundsIn(root, Named<Button>(window, "MinimizeButton")));
            Assert.Equal(new Rect(61.25, 17.5, 13.5, 13.5), BoundsIn(root, Named<Button>(window, "MaximizeButton")));
            Assert.Equal(new Rect(276.75, 6.25, 36, 36), BoundsIn(root, Named<Button>(window, "FilterButton")));
            Assert.Equal(new Rect(326.25, 6.25, 36, 36), BoundsIn(root, Named<Button>(window, "ComposeButton")));
            Assert.Equal(new Rect(1397, 6.25, 36, 36), BoundsIn(root, Named<Button>(window, "SearchButton")));

            // A new conversation cannot be started where nothing can answer one, as in this window, so its button waits; the
            // window's own buttons, the filter button, which opens the view menu, and the search button, which opens the search field, work.
            Assert.False(Named<Button>(window, "ComposeButton").IsEnabled);
            Assert.True(Named<Button>(window, "SearchButton").IsEnabled);
            Assert.True(Named<Button>(window, "FilterButton").IsEnabled);
            Assert.Equal("View options", AutomationName(Named<Button>(window, "FilterButton")));
            Assert.Equal("Close", AutomationName(Named<Button>(window, "CloseButton")));
            Assert.Equal("New conversation", AutomationName(Named<Button>(window, "ComposeButton")));
            Assert.Equal("Search conversations", AutomationName(Named<Button>(window, "SearchButton")));

            // The cards: two columns 15.4 in and 15.4 apart, the right one 49.5 lower, each 184 tall and 14.5 above the
            // next, newest first, taking turns between the columns.
            var list = Named<ListBox>(window, "Conversations");
            var cards = Enumerable.Range(0, 8)
                .Select(i => (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(i)).ToArray();
            var cardWidth = (320 - (3 * 15.4)) / 2;
            AssertRect(new Rect(15.4, 49.8, cardWidth, 184), BoundsIn(root, cards[0]));
            AssertRect(new Rect(15.4 + cardWidth + 15.4, 49.8 + 49.5, cardWidth, 184), BoundsIn(root, cards[1]));
            AssertRect(new Rect(15.4, 49.8 + 198.5, cardWidth, 184), BoundsIn(root, cards[2]));
            AssertRect(new Rect(15.4 + cardWidth + 15.4, 49.8 + 49.5 + 198.5, cardWidth, 184), BoundsIn(root, cards[3]));
            Assert.Equal("Yesterday’s Photos", AutomationName(cards[0]));

            // A card shows when, the title and as much of the answer as fits. A title too long for three lines of
            // the large type is small, and cut short; the others stay large.
            var titles = cards.Select(card => Descendants<FittedTitle>(card).Single()).ToArray();
            Assert.Equal([false, true, false, false, false, false, false, false], titles.Select(title => title.IsReduced));
            Assert.Equal(18.25, titles[0].TextBlock.FontSize);
            Assert.Equal(14, titles[1].TextBlock.FontSize);
            Assert.Equal(3 * 13.4, titles[1].TextBlock.ActualHeight, 1);
            Assert.Equal("12:29 PM", Descendants<TextBlock>(cards[0]).First().Text);
            Assert.All(cards.Skip(1).Take(4), card => Assert.Equal("9:41 AM", Descendants<TextBlock>(card).First().Text));
            Assert.Equal("Saturday", Descendants<TextBlock>(cards[6]).First().Text);
            var firstText = BoundsIn(root, Descendants<TextBlock>(cards[0]).First());
            Assert.Equal(15.4 + 18.1, firstText.Left, 1);

            // Previews stop at a whole line, as the reference's do: seven lines under a two-line title, five under three.
            Assert.Equal(7, PreviewLines(cards[4]));
            Assert.Equal(7, PreviewLines(cards[5]));
            Assert.Equal(5, PreviewLines(cards[3]));
            Assert.Equal(5, PreviewLines(cards[1]));

            // A conversation with an attached image shows the newest one in place of the preview, set into the card:
            // across its whole width from under the title down to its bottom, in a column 18.6 from its sides.
            foreach (var (index, name) in new[] { (6, "Sample photo of a mountain lake"), (7, "Sample photo of a mountain valley") })
            {
                var thumbnail = Descendants<CardThumbnail>(cards[index]).Single();
                Assert.Equal("Thumbnail", thumbnail.Name);
                Assert.Equal(Visibility.Visible, thumbnail.Visibility);
                Assert.Equal(Visibility.Collapsed, Descendants<TextBlock>(cards[index]).Last().Visibility);
                Assert.Same(history.Conversations[index].Image!.Thumbnail, thumbnail.Source);
                var bounds = BoundsIn(cards[index], thumbnail);
                Assert.Equal(0, bounds.Left, 1);
                Assert.Equal(cardWidth, bounds.Right, 1);
                Assert.Equal(184, bounds.Bottom, 1);
                Assert.Equal(BoundsIn(cards[index], Descendants<FittedTitle>(cards[index]).Single()).Bottom + 5.4, bounds.Top, 1);
                Assert.Equal((46.5, 18.6, 32.0, 5.8, 7.2, 12.5), (thumbnail.CornerSize, thumbnail.Inset,
                    thumbnail.TopCornerSize, thumbnail.TipWidth, thumbnail.TipHeight, thumbnail.TipLift));
                Assert.Equal(name, AutomationName(thumbnail));
            }

            Assert.All(cards.Take(6), card => Assert.Equal(Visibility.Collapsed,
                Descendants<CardThumbnail>(card).Single().Visibility));

            // However long the history, only the cards in view are created. The list of rows is not shown, so it has
            // none.
            Assert.Equal(SampleHistorySource.ReferenceCount + SampleHistorySource.OlderCount, list.Items.Count);
            Assert.Equal(Visibility.Collapsed, Named<ListBox>(window, "ConversationRows").Visibility);
            Assert.Empty(Descendants<ListBoxItem>(Named<ListBox>(window, "ConversationRows")));
            Assert.InRange(Descendants<VirtualizingStaggeredColumnsPanel>(list).Single().Children.Count, 8, 14);

            // With nothing open, the workspace says so, centered below the top band.
            var empty = Named<TextBlock>(window, "EmptyState");
            Assert.Equal(Visibility.Visible, empty.Visibility);
            Assert.Equal(Visibility.Collapsed, Named<ConversationView>(window, "Conversation").Visibility);
            Assert.False(Named<FadingScrollViewer>(window, "Transcript").IsVisible);
            var emptyBounds = BoundsIn(root, empty);
            Assert.Equal(320 + (1120 / 2.0), emptyBounds.Left + (emptyBounds.Width / 2), 1);
            Assert.Equal(43 + ((824 - 43) / 2.0), emptyBounds.Top + (emptyBounds.Height / 2), 1);
            Assert.Equal(21, empty.FontSize);

            // Opaque colors while Windows has no backdrop to show, and the backdrop showing through once it has
            // (HistoryGlassIsNeutralAndTakesItsHueFromTheBackdrop checks the colors themselves).
            Assert.Equal(ThemeColor("Brush.Surface.SidebarOpaque"), SolidColor(Named<Grid>(window, "Sidebar").Background));
            Assert.Equal(ThemeColor("Brush.Surface.WorkspaceOpaque"), SolidColor(Named<Grid>(window, "Workspace").Background));
            frames.Frame!.SetTranslucent(true);
            Assert.True(Backdrop.GetIsBlurred(window));
            Assert.Equal(ThemeColor("Brush.Surface.Sidebar"), SolidColor(Named<Grid>(window, "Sidebar").Background));
            Assert.Equal(ThemeColor("Brush.Surface.Workspace"), SolidColor(Named<Grid>(window, "Workspace").Background));
            frames.Frame.SetTranslucent(false);

            RenderFixture(root, "history-shell.png", 0.9722);
            RenderFixture(root, "history-shell-2x.png", 2);
        }
        finally
        {
            window.CloseForGood();
        }

        Assert.True(frames.Frame!.IsDisposed);
    })));

    [Fact]
    public void OpeningAConversationShowsItInTheWorkspaceAsTheFloatingConversationDrawsIt() => RunSta(() => WithTheme(() =>
    {
        var (window, history, _) = CreateHistoryWindow();
        AddReferenceConversations(history);
        history.Selected = null;
        try
        {
            window.Show();
            Pump();
            var answer = new MessageViewModel(MessageRole.Assistant, "9 + 10 is 19.");
            answer.Content.Add(new CalculationResult("9 + 10", "19"));
            var opened = history.Open(Guid.NewGuid(), [new MessageViewModel(MessageRole.User, "What is 9+10"), answer],
                ReferenceNow);
            Pump();

            // Its card comes first and is selected.
            var list = Named<ListBox>(window, "Conversations");
            Assert.Same(opened, list.Items[0]);
            Assert.Same(opened, list.SelectedItem);
            Assert.True(((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)).IsSelected);

            // The workspace shows it with the shared message templates: the question in a bubble, the answer's prose
            // open and its result in a card.
            Assert.Equal(Visibility.Collapsed, Named<TextBlock>(window, "EmptyState").Visibility);
            var transcript = Named<FadingScrollViewer>(window, "Transcript");
            Assert.Equal(Visibility.Visible, transcript.Visibility);
            Assert.Single(Descendants<SpeechBubble>(transcript));
            Assert.Contains(Descendants<TextBlock>(transcript), text => text.Text == "9 + 10 is 19.");
            Assert.Contains(Descendants<TextBlock>(transcript), text => text.Text == "19");

            // Choosing another card opens that one; none leaves the workspace empty again.
            list.SelectedIndex = 3;
            Pump();
            Assert.Same(history.Conversations[3], history.Selected);
            Assert.Contains(Descendants<TextBlock>(transcript), text => text.Text == history.Conversations[3].Title);
            list.SelectedItem = null;
            Pump();
            Assert.False(history.HasSelection);
            Assert.Equal(Visibility.Visible, Named<TextBlock>(window, "EmptyState").Visibility);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void HistoryWindowOpensCenteredOnceAndClosingOnlyHidesIt() => RunSta(() => WithTheme(() =>
    {
        var placement = new FakePlacement();
        var (window, _, _) = CreateHistoryWindow(placement);
        try
        {
            window.ShowAndActivate();
            Pump();
            Assert.True(window.IsVisible);
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Assert.Equal([(handle, 1440.0, 824.0, 24.0)], placement.CenterCalls);
            Assert.True(window.ShowInTaskbar);
            Assert.Equal(ResizeMode.CanResize, window.ResizeMode);

            // Its close button, like Alt+F4, hides it; it opens again where it was, as it was.
            Click(Named<Button>(window, "CloseButton"));
            Pump();
            Assert.False(window.IsVisible);
            Assert.True(window.IsLoaded);
            window.ShowAndActivate();
            Pump();
            Assert.True(window.IsVisible);
            Assert.Single(placement.CenterCalls);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void OpeningInHistoryMovesTheFloatingConversationIntoTheHistoryWindow()
    {
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), new FakeAnswers(),
            new FixedClock(ReferenceNow));
        var history = new HistoryViewModel(new FixedClock(ReferenceNow));
        var assistant = new FakeAssistantWindow();
        var historyWindow = new FakeHistoryWindow();
        var created = 0;
        _ = new HistoryWindowController(conversation, history, assistant, () =>
        {
            created++;
            return historyWindow;
        });

        // Nothing to open until there is a conversation, and the History window is not even created.
        Assert.False(conversation.OpenInHistoryCommand.CanExecute(null));
        Assert.Equal(0, created);

        conversation.StartNew("What is 9+10");
        Assert.True(conversation.OpenInHistoryCommand.CanExecute(null));
        conversation.OpenInHistoryCommand.Execute(null);

        var card = Assert.Single(history.Conversations);
        Assert.Same(card, history.Selected);
        Assert.Equal(conversation.Id, card.Id);
        Assert.Equal(ReferenceNow, card.UpdatedAt);
        Assert.Equal(conversation.Messages, card.Messages);
        Assert.Equal(1, historyWindow.Shown);
        Assert.Equal(1, assistant.Dismissals);

        // The card keeps its messages when the panel starts another conversation.
        conversation.StartNew("Another question");
        Assert.Equal("What is 9+10", card.Title);
        Assert.Single(card.Messages);

        // The next conversation opened there gets a card of its own, first, in the same History window.
        conversation.OpenInHistoryCommand.Execute(null);
        Assert.Equal(2, history.Conversations.Count);
        Assert.Equal("Another question", history.Conversations[0].Title);
        Assert.Equal(2, historyWindow.Shown);
        Assert.Equal(1, created);
    }

    [Fact]
    public void HistoryListKeepsTheNewestFirstAndSelectsWhatIsOpened()
    {
        var history = new HistoryViewModel(new FixedClock(ReferenceNow));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        history.Open(first, [new MessageViewModel(MessageRole.User, "First")], ReferenceNow.AddHours(-2));
        history.Open(second, [new MessageViewModel(MessageRole.User, "Second")], ReferenceNow.AddHours(-1));
        Assert.Equal(["Second", "First"], history.Conversations.Select(item => item.Title));
        Assert.Equal(second, history.Selected?.Id);

        var reopened = history.Open(first, [new MessageViewModel(MessageRole.User, "First, again")], ReferenceNow);
        Assert.Equal(["First, again", "Second"], history.Conversations.Select(item => item.Title));
        Assert.Same(reopened, history.Selected);
        Assert.Equal(2, history.Conversations.Count);

        Assert.Throws<ArgumentException>(() => history.Selected = new HistoryConversationViewModel(
            Guid.NewGuid(), [], ReferenceNow, new FixedClock(ReferenceNow)));
        var changes = new List<string?>();
        reopened.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        history.RefreshTimes();
        Assert.Equal([nameof(HistoryConversationViewModel.TimeLabel)], changes);
    }

    [Fact]
    public void CardsShowTheQuestionAndTheLatestAnswerOnOneLine()
    {
        var answer = new MessageViewModel(MessageRole.Assistant, "# Plans\nFirst  paragraph.\n\n- one\n- two");
        answer.Content.Add(new CalculationResult("1 + 1", "2"));
        var card = new HistoryConversationViewModel(Guid.NewGuid(),
        [
            new MessageViewModel(MessageRole.User, "  What are\n my   plans?  "),
            new MessageViewModel(MessageRole.Assistant, "An earlier answer."),
            new MessageViewModel(MessageRole.User, "And then?"),
            answer,
            new MessageViewModel(MessageRole.Assistant),
        ], ReferenceNow, new FixedClock(ReferenceNow));

        Assert.Equal("What are my plans?", card.Title);
        Assert.Equal("Plans First paragraph. one two", card.Preview);
        Assert.Equal("", new HistoryConversationViewModel(Guid.NewGuid(), [], ReferenceNow, new FixedClock(ReferenceNow)).Preview);
    }

    [Fact]
    public void CardTimesReadAsTheReferenceDoes() => WithCulture("en-US", () =>
    {
        Assert.Equal("12:29 PM", HistoryConversationViewModel.FormatTime(ReferenceNow.AddMinutes(-1), ReferenceNow));
        Assert.Equal("12:05 AM", HistoryConversationViewModel.FormatTime(new DateTimeOffset(2026, 9, 28, 0, 5, 0, TimeSpan.Zero), ReferenceNow));
        Assert.Equal("Yesterday", HistoryConversationViewModel.FormatTime(new DateTimeOffset(2026, 9, 27, 23, 59, 0, TimeSpan.Zero), ReferenceNow));
        Assert.Equal("Saturday", HistoryConversationViewModel.FormatTime(ReferenceNow.AddDays(-2), ReferenceNow));
        Assert.Equal("Tuesday", HistoryConversationViewModel.FormatTime(ReferenceNow.AddDays(-6), ReferenceNow));
        Assert.Equal("9/21/2026", HistoryConversationViewModel.FormatTime(ReferenceNow.AddDays(-7), ReferenceNow));

        // Times are read in the clock's time zone.
        var later = new DateTimeOffset(2026, 9, 28, 23, 30, 0, TimeSpan.FromHours(-7));
        Assert.Equal("11:30 PM", HistoryConversationViewModel.FormatTime(later.ToUniversalTime(), later));
    });

    [Fact]
    public void FittedTitleUsesLargeTypeWhileItFitsAndHyphenatesOnlyWordsTooLongForALine() => RunSta(() => WithTheme(() =>
    {
        FittedTitle Measure(string text)
        {
            var title = new FittedTitle
            {
                Text = text, LargeFontSize = 18.25, LargeLineHeight = 17.5, SmallFontSize = 14, SmallLineHeight = 13.4,
            };
            title.SetValue(TextElement.FontWeightProperty, FontWeights.Bold);
            title.Measure(new Size(100.6, double.PositiveInfinity));
            title.Arrange(new Rect(title.DesiredSize));
            return title;
        }

        var shortTitle = Measure("MVP Wall Photo");
        Assert.False(shortTitle.IsReduced);
        Assert.Equal(2 * 17.5, shortTitle.DesiredSize.Height, 1);
        Assert.False(shortTitle.TextBlock.IsHyphenationEnabled);

        var longTitle = Measure("Teenage Engineering Design Language");
        Assert.True(longTitle.IsReduced);
        Assert.Equal(14, longTitle.TextBlock.FontSize);
        Assert.Equal(3 * 13.4, longTitle.DesiredSize.Height, 1);
        Assert.False(longTitle.TextBlock.IsHyphenationEnabled);

        var longWord = Measure("Location Identification");
        Assert.False(longWord.IsReduced);
        Assert.True(longWord.TextBlock.IsHyphenationEnabled);
    }));

    [Fact]
    public void StaggeredColumnsTakeTurnsAndTheRightOneStartsLower() => RunSta(() =>
    {
        var panel = new VirtualizingStaggeredColumnsPanel
        {
            Columns = 2, ColumnSpacing = 10, RowSpacing = 5, Stagger = 20, ItemHeight = 50,
            Padding = new Thickness(4, 8, 6, 12),
        };

        // Columns share the width inside the padding; items take turns between them, the right one starting lower.
        Assert.Equal(95, panel.ColumnWidth(210));
        Assert.Equal(new Rect(4, 8, 95, 50), panel.SlotOf(0, 210));
        Assert.Equal(new Rect(109, 28, 95, 50), panel.SlotOf(1, 210));
        Assert.Equal(new Rect(4, 63, 95, 50), panel.SlotOf(2, 210));
        Assert.Equal(new Rect(109, 83, 95, 50), panel.SlotOf(3, 210));
        Assert.Equal(new Rect(4, 118, 95, 50), panel.SlotOf(4, 210));

        // The scrolled area reaches the lower of the two columns' last items, plus the padding.
        Assert.Equal(0 + 8 + 12, panel.ExtentHeightFor(0));
        Assert.Equal(8 + 50 + 12, panel.ExtentHeightFor(1));
        Assert.Equal(8 + 20 + 50 + 12, panel.ExtentHeightFor(2));
        Assert.Equal(8 + 105 + 12, panel.ExtentHeightFor(3));
        Assert.Equal(8 + 160 + 12, panel.ExtentHeightFor(5));
    });

    [Fact]
    public void LongHistoryCreatesCardsOnlyForWhatIsInView() => RunSta(() => WithTheme(() =>
    {
        const int count = 5000;
        var source = new ListSource(Enumerable.Range(0, count).Select(i => new HistoryConversation(Guid.NewGuid(),
            [new MessageViewModel(MessageRole.User, $"Question {i}"), new MessageViewModel(MessageRole.Assistant, $"Answer {i}.")],
            ReferenceNow.AddMinutes(-i))).ToArray());
        var (window, history, _) = CreateHistoryWindow(source: source);
        try
        {
            window.Show();
            Pump();
            var list = Named<ListBox>(window, "Conversations");
            var panel = Descendants<VirtualizingStaggeredColumnsPanel>(list).Single();
            var viewer = Descendants<FadingScrollViewer>(list).Single();
            Assert.Equal(count, list.Items.Count);

            // Only the cards in view, and a row or so beyond, exist; the list scrolls through all of them.
            Assert.InRange(panel.Children.Count, 8, 14);
            Assert.Equal(panel.ExtentHeightFor(count), viewer.ExtentHeight, 3);
            Assert.Equal(824, viewer.ViewportHeight);

            viewer.ScrollToVerticalOffset(250_000);
            Pump();
            Assert.Equal(250_000, viewer.VerticalOffset);
            Assert.InRange(panel.Children.Count, 8, 14);
            var root = Named<Grid>(window, "Root");
            foreach (var card in panel.Children.Cast<ListBoxItem>())
            {
                var index = list.ItemContainerGenerator.IndexFromContainer(card);
                var slot = panel.SlotOf(index, panel.ActualWidth);
                AssertRect(new Rect(slot.X, slot.Y - 250_000, slot.Width, slot.Height), BoundsIn(root, card));
                Assert.Equal($"Question {index}", AutomationName(card));
            }

            // Scrolling to the end shows the oldest card clear of the fade, and it can be opened from there.
            list.ScrollIntoView(list.Items[count - 1]);
            Pump();
            var last = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(count - 1));
            var bounds = BoundsIn(root, last);
            Assert.InRange(bounds.Bottom, 49.8, 824);
            Assert.InRange(panel.Children.Count, 8, 14);
            list.SelectedIndex = count - 1;
            Pump();
            Assert.Equal("Question 4999", history.Selected?.Title);

            // Opening a conversation from far down the list brings its card to the top, and the list back to it.
            var reopened = history.Conversations[count - 1];
            history.Open(reopened.Id, reopened.Messages, ReferenceNow.AddMinutes(1));
            list.ScrollIntoView(reopened);
            Pump();
            var top = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.Same(reopened, top.DataContext);
            Assert.True(top.IsSelected);
            AssertRect(new Rect(15.4, 49.8, panel.ColumnWidth(320), 184), BoundsIn(root, top));
            Assert.Equal("Question 0", AutomationName(
                Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(1))));
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void OpenConversationsCardIsOutlined() => RunSta(() => WithTheme(() =>
    {
        var (window, history, _) = CreateHistoryWindow(source: new SampleHistorySource(new FixedClock(ReferenceNow)));
        try
        {
            window.Show();
            Pump();
            var list = Named<ListBox>(window, "Conversations");
            PanelShape Outline(int index) => Descendants<PanelShape>(list.ItemContainerGenerator.ContainerFromIndex(index))
                .Single(shape => shape.Name == "SelectedOutline");
            Assert.All(Enumerable.Range(0, 8), index => Assert.Equal(Visibility.Collapsed, Outline(index).Visibility));

            history.Selected = history.Conversations[2];
            Pump();
            Assert.Equal(Visibility.Visible, Outline(2).Visibility);
            Assert.Equal(ThemeColor("Brush.Stroke.HistoryCardSelected"), SolidColor(Outline(2).Stroke));
            Assert.Equal(2.5, Outline(2).StrokeThickness);
            Assert.Equal(Visibility.Collapsed, Outline(3).Visibility);

            // One blue ring, centered on the card's outline, and no other: the card keeps its size, and the only other
            // edge it has is its faint rim, which the ring covers.
            var card = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2);
            Assert.Equal(new Thickness(-1.25), Outline(2).Margin);
            Assert.Equal(46.5, Outline(2).CornerSize);
            var other = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(3);
            Assert.Equal(other.RenderSize, card.RenderSize);
            Assert.Equal(184, card.ActualHeight);
            var blue = ThemeColor("Brush.Stroke.HistoryCardSelected");
            Assert.True(blue.B > 200 && blue.B > blue.R + 120, "The ring is blue.");
            Assert.Equal(["Surface", "StateLayer", "SelectedOutline", "FocusRing"], Descendants<PanelShape>(card)
                .Where(shape => shape.TemplatedParent == card).Select(shape => shape.Name));
            Assert.Equal(["Surface", "SelectedOutline"], Descendants<PanelShape>(card)
                .Where(shape => shape.TemplatedParent == card && shape.Stroke is not null && shape.IsVisible)
                .Select(shape => shape.Name));

            // Across the card's left edge, from the sidebar in: the sidebar, the blue ring, then the card's glass,
            // with no second line anywhere.
            var sidebar = Named<Grid>(window, "Sidebar");
            var edge = BoundsIn(sidebar, card);
            var colors = ScanRow(sidebar, new Point(edge.Left - 5, edge.Top + 92), 13, 2);
            var ringPixels = colors.Select((color, i) => (color, i))
                .Where(pair => pair.color.B > 150 && pair.color.B > pair.color.R + 80).Select(pair => pair.i).ToArray();
            Assert.InRange(ringPixels.Length, 4, 6);
            Assert.Equal(ringPixels.Length - 1, ringPixels[^1] - ringPixels[0]);
            Assert.InRange(ringPixels[0], 7, 8);
            Assert.DoesNotContain(colors, color => Math.Min(color.R, Math.Min(color.G, color.B)) > 110);

            RenderFixture(Named<Grid>(window, "Sidebar"), "history-selected-2x.png", 2);

            // With the keyboard on the open card, as after clicking it, it still has only its blue ring. The keyboard's
            // ring shows on a card it is on without opening it, in the blue ring's place.
            window.Activate();
            card.Focus();
            Pump();
            if (card.IsKeyboardFocused)
            {
                Assert.Equal(Visibility.Collapsed, TemplatePart<PanelShape>(card, "FocusRing").Visibility);
                Assert.Equal(Visibility.Visible, Outline(2).Visibility);
                other.Focus();
                Pump();
                Assert.True(other.IsKeyboardFocused);
                other.IsSelected = false;
                Pump();
                var ring = TemplatePart<PanelShape>(other, "FocusRing");
                Assert.Equal(Visibility.Visible, ring.Visibility);
                Assert.Equal(Visibility.Collapsed, Outline(3).Visibility);
                Assert.Equal(Outline(2).Margin, ring.Margin);
                Assert.Equal(Outline(2).StrokeThickness, ring.StrokeThickness);
                other.IsSelected = true;
                Pump();
                Assert.Equal(Visibility.Collapsed, ring.Visibility);
                Assert.Equal(Visibility.Visible, Outline(3).Visibility);
            }
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void SampleHistoryStartsWithTheReferencesConversations()
    {
        var conversations = new SampleHistorySource(new FixedClock(ReferenceNow)).Load();
        Assert.Equal(SampleHistorySource.ReferenceCount + SampleHistorySource.OlderCount, conversations.Count);
        Assert.Equal(
            ["Yesterday’s Photos", "Teenage Engineering Design Language", "MVP Wall Photo", "7-Eleven Cheese Pizza",
             "December 3 Date", "Apple Card Cashback", "Location Identification", "Location Identification",
             "Exam Summary", "Voice Memo Summary"],
            conversations.Take(SampleHistorySource.ReferenceCount).Select(item => item.Title));
        Assert.Equal([6, 7], conversations.Select((item, index) => (item, index))
            .Where(pair => pair.item.Messages!.Any(message => message.Attachments.Count > 0)).Select(pair => pair.index));
        Assert.All(conversations, item => Assert.True(item.UpdatedAt < ReferenceNow));
        Assert.Equal(conversations.Count, conversations.Select(item => item.Id).Distinct().Count());

        // The view model lists them newest first, with none open.
        var history = LoadedHistory(new FixedClock(ReferenceNow), new ListSource(conversations.Reverse().ToArray()));
        Assert.Equal(conversations.Select(item => item.Id), history.Conversations.Select(item => item.Id));
        Assert.Null(history.Selected);
        Assert.Same(conversations[6].Messages![2].Attachments[0], history.Conversations[6].Image);
        Assert.Same(conversations[7].Messages![2].Attachments[0], history.Conversations[7].Image);
    }

    private static (HistoryWindow Window, HistoryViewModel History, FakeFrameFactory Frames) CreateHistoryWindow(
        FakePlacement? placement = null, IHistorySource? source = null, IWindowBackdropFactory? backdrops = null,
        TimeProvider? clock = null)
    {
        var history = source is null
            ? new HistoryViewModel(clock ?? new FixedClock(ReferenceNow), searchDelay: TimeSpan.Zero)
            : LoadedHistory(clock ?? new FixedClock(ReferenceNow), source);
        var frames = new FakeFrameFactory();
        var window = new HistoryWindow(history, frames, placement ?? new FakePlacement(), backdrops ?? new MenuBackdrops())
        {
            Left = -10000, Top = -10000, ShowActivated = false,
        };
        return (window, history, frames);
    }

    // The conversations in the reference's sidebar, newest first, as synthetic questions and answers.
    private static void AddReferenceConversations(HistoryViewModel history)
    {
        (string Question, string Answer, DateTimeOffset At)[] conversations =
        [
            ("Yesterday’s Photos", "I found 4 photos from yesterday.", ReferenceNow.AddMinutes(-1)),
            ("Teenage Engineering Design Language", "The design of this build shares several key characteristics with the aesthetic of Teenage Engineering products.", ReferenceNow.AddMinutes(-169)),
            ("MVP Wall Photo", "It’s sent.", ReferenceNow.AddMinutes(-169)),
            ("7-Eleven Cheese Pizza", "A large cheese pizza from 7-Eleven typically costs around $7.00, though prices vary by location.", ReferenceNow.AddMinutes(-169)),
            ("December 3 Date", "Black Friday is on November 27, 2026. I don’t see any exams scheduled for that week, but you have an Anatomy exam on December 3.", ReferenceNow.AddMinutes(-169)),
            ("Apple Card Cashback", "To check the lifetime Daily Cash you’ve received on your Apple Card, open the Wallet app on your iPhone and tap your card, then scroll down.", ReferenceNow.AddMinutes(-185)),
            ("Location Identification", "This looks like a valley in the Canadian Rockies.", ReferenceNow.AddDays(-2)),
            ("Location Identification", "This looks like a valley in the Canadian Rockies.", ReferenceNow.AddDays(-2)),
        ];
        foreach (var (question, answer, at) in conversations.Reverse())
        {
            history.Open(Guid.NewGuid(),
                [new MessageViewModel(MessageRole.User, question), new MessageViewModel(MessageRole.Assistant, answer)], at);
        }
    }

    // How many lines of a card's preview show, from its height and line spacing.
    private static int PreviewLines(ListBoxItem card)
    {
        var preview = Descendants<TextBlock>(card).Last();
        return (int)Math.Round(preview.ActualHeight / preview.LineHeight);
    }

    // Clicks a button as the user would, so its command is routed from the button.
    private static void Click(Button button) =>
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(button)
            .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();

    private static void AssertRect(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 1);
        Assert.Equal(expected.Y, actual.Y, 1);
        Assert.Equal(expected.Width, actual.Width, 1);
        Assert.Equal(expected.Height, actual.Height, 1);
    }

    private static Color ThemeColor(string key) => Assert.IsType<SolidColorBrush>(Application.Current.FindResource(key)).Color;

    private static Color SolidColor(Brush? brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    private static void WithCulture(string name, Action action)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        try { action(); }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, uiCulture); }
    }

    private sealed class FakeHistoryWindow : IHistoryWindow
    {
        public int Shown { get; private set; }
        public void ShowAndActivate() => Shown++;
    }

    // A source that lists the conversations it was given, messages and all, at once.
    private sealed class ListSource(IReadOnlyList<HistoryConversation> conversations) : IHistorySource
    {
        public Task<IReadOnlyList<HistoryConversation>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(conversations);

        public Task<IReadOnlyList<MessageViewModel>?> LoadMessagesAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(conversations.FirstOrDefault(item => item.Id == id)?.Messages);

        public Task<IReadOnlyList<HistorySearchHit>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HistorySearchHit>>([]);
    }

    // A history view model that has read its source, which a source that answers at once has done before this returns.
    private static HistoryViewModel LoadedHistory(TimeProvider clock, IHistorySource source)
    {
        // The search runs at once, so a test does not wait for a pause in typing.
        var history = new HistoryViewModel(clock, source, searchDelay: TimeSpan.Zero);
        history.RefreshAsync().GetAwaiter().GetResult();
        return history;
    }

    private sealed class FakeFrameFactory : IWindowFrameFactory
    {
        public List<(nint Window, WindowFrameStyle Style)> Applied { get; } = [];
        public FakeFrame? Frame { get; private set; }

        public IWindowFrame Apply(nint window, WindowFrameStyle style)
        {
            Applied.Add((window, style));
            return Frame = new FakeFrame();
        }
    }

    private sealed class FakeFrame : IWindowFrame
    {
        public bool IsTranslucent { get; private set; }
        public bool IsDisposed { get; private set; }
        public event EventHandler? IsTranslucentChanged;

        public void SetTranslucent(bool value)
        {
            IsTranslucent = value;
            IsTranslucentChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() => IsDisposed = true;
    }
}
