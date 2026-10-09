using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Assistant.Core.Domain;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Assistant.Windows.Backdrop;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    [Fact]
    public void HistoryListMatchesTheReferenceLayout() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        var (window, history, _) = CreateHistoryWindow(source: new SampleHistorySource(new FixedClock(ReferenceNow)));
        history.Selected = history.Conversations[0];
        history.Layout = HistoryLayout.List;
        try
        {
            window.Show();
            Pump();
            var root = Named<Grid>(window, "Root");
            var rows = Named<ListBox>(window, "ConversationRows");
            Assert.Equal(Visibility.Visible, rows.Visibility);
            Assert.Equal(Visibility.Collapsed, Named<ListBox>(window, "Conversations").Visibility);

            // The conversations fall in sections by when they last changed, the most recent first.
            Assert.Equal(["Today", "Previous 7 Days", "Previous 30 Days", "August", "July"], SectionTitles(rows));
            Assert.Equal(6, ((CollectionViewGroup)rows.Items.Groups[0]).ItemCount);

            // Each section's header: 32.9 tall, its words 12.9 from the sidebar's left, a baseline 24.8 below its top.
            var headers = Descendants<GroupItem>(rows).Take(2)
                .Select(section => Descendants<TextBlock>(section).First()).ToArray();
            Assert.Equal(["Today", "Previous 7 Days"], headers.Select(header => header.Text));
            AssertClose(new Rect(12.9, 37.05 + 11.04, headers[0].ActualWidth, headers[0].ActualHeight), BoundsIn(root, headers[0]));
            Assert.Equal(527.15 + 11.04, BoundsIn(root, headers[1]).Top, 0.1);
            Assert.All(headers, header =>
            {
                Assert.Equal(12.75, header.FontSize);
                Assert.Equal(FontWeights.Bold, header.FontWeight);
                Assert.Equal(ThemeColor("Brush.Text.HistorySection"), SolidColor(header.Foreground));
            });

            // Rows are 76.2 tall, 9.6 in from the sidebar's sides and touching one another; the next section's
            // header lies between the sixth and seventh.
            var containers = history.Conversations.Take(SampleHistorySource.ReferenceCount)
                .Select(conversation => RowOf(rows, conversation)).ToArray();
            double RowTop(int index) => 69.95 + (index * 76.2) + (index >= 6 ? 32.9 : 0);
            for (var i = 0; i < containers.Length; i++)
            {
                AssertClose(new Rect(9.6, RowTop(i), 300.8, 76.2), BoundsIn(root, containers[i]));
                Assert.Equal(history.Conversations[i].Title, AutomationName(containers[i]));
            }

            // A title over one line of preview has its baseline 33.07 below the row's top and the preview's 18.4
            // lower; under two lines, the block rises by half a line.
            var titleBaseline = 0.8113 * 14.1;
            var previewBaseline = 0.8113 * 16.4;
            var (title, preview) = RowText(containers[0]);
            Assert.Equal(RowTop(0) + 33.07, BoundsIn(root, title).Top + titleBaseline, 0.1);
            Assert.Equal(RowTop(0) + 33.07 + 18.4, BoundsIn(root, preview).Top + previewBaseline, 0.1);
            Assert.Equal(16.85, BoundsIn(root, title).Left, 0.1);
            Assert.Equal(16.85, BoundsIn(root, preview).Left, 0.1);
            Assert.Equal(1, Lines(preview));
            (title, preview) = RowText(containers[1]);
            Assert.Equal(2, Lines(preview));
            Assert.Equal(RowTop(1) + 33.07 - 8.2, BoundsIn(root, title).Top + titleBaseline, 0.1);
            Assert.Equal(13, title.FontSize);
            Assert.Equal(FontWeights.SemiBold, title.FontWeight);
            Assert.Equal(12, preview.FontSize);
            Assert.Equal(ThemeColor("Brush.Text.Message"), SolidColor(title.Foreground));
            Assert.Equal(ThemeColor("Brush.Text.HistoryCardSecondary"), SolidColor(preview.Foreground));
            Assert.Equal([1, 2, 1, 2, 2, 2], containers.Take(6).Select(container => Lines(RowText(container).Preview)));

            // A conversation that started with an image shows it at the row's right, 45.5 across, with its title
            // alone centered beside it.
            foreach (var index in new[] { 6, 7 })
            {
                var thumbnail = Descendants<System.Windows.Shapes.Rectangle>(containers[index]).Single(shape => shape.Name == "Thumbnail");
                Assert.Equal(Visibility.Visible, thumbnail.Visibility);
                AssertClose(new Rect(310.4 - 7.5 - 45.5, RowTop(index) + 15.35, 45.5, 45.5), BoundsIn(root, thumbnail));
                Assert.Equal(history.Conversations[index].Image!.Thumbnail, ((ImageBrush)thumbnail.Fill).ImageSource);
                Assert.Equal(11.25, CornerClip.GetCornerSize(thumbnail));
                (title, preview) = RowText(containers[index]);
                Assert.Equal(Visibility.Collapsed, preview.Visibility);
                Assert.Equal(RowTop(index) + 31.05, BoundsIn(root, title).Top, 0.1);
            }

            Assert.All(containers.Where((_, index) => index is not (6 or 7)), container => Assert.Equal(Visibility.Collapsed,
                Descendants<System.Windows.Shapes.Rectangle>(container).Single(shape => shape.Name == "Thumbnail").Visibility));

            // The open conversation's row is a lighter layer with smooth corners and no outline; the others have none.
            PanelShape Layer(ListBoxItem row) => Descendants<PanelShape>(row).Single(shape => shape.Name == "StateLayer");
            Assert.True(containers[0].IsSelected);
            Assert.Equal(ThemeColor("Brush.Surface.HistoryRowSelected"), SolidColor(Layer(containers[0]).Fill));
            Assert.Equal(9, Layer(containers[0]).CornerSize);
            Assert.Null(Layer(containers[0]).Stroke);
            Assert.All(containers.Skip(1), row => Assert.Null(Layer(row).Fill));

            // The window's buttons stay where they are.
            Assert.Equal(new Rect(276.75, 6.25, 36, 36), BoundsIn(root, Named<Button>(window, "FilterButton")));

            RenderFixture(root, "history-list.png", 0.9722);
            RenderFixture(root, "history-list-2x.png", 2);
        }
        finally
        {
            window.CloseForGood();
        }
    })));

    [Fact]
    public void HistorySectionsNameTheSpanOfTimeAndComeMostRecentFirst() => WithCulture("en-US", () =>
    {
        HistorySection At(DateTimeOffset at) => HistorySection.For(at, ReferenceNow);
        Assert.Equal(HistorySection.Today, At(ReferenceNow.AddMinutes(-1)));
        Assert.Equal(HistorySection.Today, At(new DateTimeOffset(2026, 9, 28, 0, 1, 0, TimeSpan.Zero)));
        Assert.Equal(HistorySection.Yesterday, At(new DateTimeOffset(2026, 9, 27, 23, 59, 0, TimeSpan.Zero)));
        Assert.Equal(HistorySection.PreviousSevenDays, At(ReferenceNow.AddDays(-2)));
        Assert.Equal(HistorySection.PreviousSevenDays, At(ReferenceNow.AddDays(-7)));
        Assert.Equal(HistorySection.PreviousThirtyDays, At(ReferenceNow.AddDays(-8)));
        Assert.Equal(HistorySection.PreviousThirtyDays, At(ReferenceNow.AddDays(-30)));
        Assert.Equal(new HistorySection(4, "August"), At(ReferenceNow.AddDays(-31)));
        Assert.Equal(new HistorySection(4, "August"), At(new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new HistorySection(11, "January"), At(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new HistorySection(12, "December 2025"), At(new DateTimeOffset(2025, 12, 31, 9, 0, 0, TimeSpan.Zero)));
        Assert.Equal("Previous 7 Days", HistorySection.PreviousSevenDays.ToString());

        // Days are counted where the clock is, and sections order by recency.
        var evening = new DateTimeOffset(2026, 9, 28, 23, 30, 0, TimeSpan.FromHours(-7));
        Assert.Equal(HistorySection.Today, HistorySection.For(evening.AddHours(-23), evening));
        Assert.Equal(HistorySection.Yesterday, HistorySection.For(evening.AddHours(-24), evening));
        HistorySection[] sections =
        [
            new(12, "December 2025"), HistorySection.Yesterday, new(4, "August"), HistorySection.Today,
            HistorySection.PreviousThirtyDays, HistorySection.PreviousSevenDays,
        ];
        Assert.Equal(["Today", "Yesterday", "Previous 7 Days", "Previous 30 Days", "August", "December 2025"],
            sections.Order().Select(section => section.Title));
        Assert.True(GroupNameOrder.Instance.Compare(HistorySection.Today, HistorySection.Yesterday) < 0);
    });

    [Fact]
    public void HistoryLayoutStartsAsAGridAndTheViewCommandsSwitchIt()
    {
        var history = new HistoryViewModel(new FixedClock(ReferenceNow));
        Assert.Equal(HistoryLayout.Grid, history.Layout);
        Assert.True(history.IsGrid);
        Assert.False(history.IsList);

        var changes = new List<string?>();
        history.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        history.ShowListCommand.Execute(null);
        Assert.Equal(HistoryLayout.List, history.Layout);
        Assert.True(history.IsList);
        Assert.False(history.IsGrid);
        Assert.Equal([nameof(HistoryViewModel.Layout), nameof(HistoryViewModel.IsGrid), nameof(HistoryViewModel.IsList)], changes);

        // Choosing the layout already shown changes nothing.
        history.ShowListCommand.Execute(null);
        Assert.Equal(3, changes.Count);
        history.ShowGridCommand.Execute(null);
        Assert.True(history.IsGrid);
        Assert.Throws<ArgumentOutOfRangeException>(() => history.Layout = (HistoryLayout)7);
    }

    [Fact]
    public void ListKeepsTheOpenConversationWhileRowsMoveBetweenSections() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        var clock = new MovableClock(ReferenceNow);
        HistoryConversation Sample(string question, DateTimeOffset at) => new(Guid.NewGuid(),
            [new MessageViewModel(MessageRole.User, question), new MessageViewModel(MessageRole.Assistant, $"About {question}.")], at);
        var source = new ListSource([Sample("Today’s", ReferenceNow.AddHours(-1)), Sample("Yesterday’s", ReferenceNow.AddDays(-1)),
            Sample("Earlier", ReferenceNow.AddDays(-3))]);
        var (window, history, _) = CreateHistoryWindow(source: source, clock: clock);
        history.Layout = HistoryLayout.List;
        var (today, yesterday, earlier) = (history.Conversations[0], history.Conversations[1], history.Conversations[2]);
        try
        {
            window.Show();
            Pump();
            var rows = Named<ListBox>(window, "ConversationRows");
            Assert.Equal(["Today", "Yesterday", "Previous 7 Days"], SectionTitles(rows));

            // Clicking a row opens its conversation.
            RowOf(rows, today).IsSelected = true;
            Pump();
            Assert.Same(today, history.Selected);

            // A day later, the rows move down a section each, and the open conversation stays open.
            clock.Now = ReferenceNow.AddDays(1);
            history.RefreshTimes();
            Pump();
            Assert.Equal(["Yesterday", "Previous 7 Days"], SectionTitles(rows));
            Assert.Equal([today], Section(rows, 0));
            Assert.Equal([yesterday, earlier], Section(rows, 1));
            Assert.Same(today, history.Selected);
            Assert.Same(today, rows.SelectedItem);
            Assert.True(RowOf(rows, today).IsSelected);

            // Opening an earlier conversation brings it to the top of Today, selected.
            history.Open(earlier.Id, earlier.Messages, clock.GetUtcNow());
            Pump();
            Assert.Equal(["Today", "Yesterday", "Previous 7 Days"], SectionTitles(rows));
            Assert.Equal([earlier], Section(rows, 0));
            Assert.Same(earlier, rows.SelectedItem);
            Assert.True(RowOf(rows, earlier).IsSelected);
            Assert.False(RowOf(rows, today).IsSelected);

            // So does a new one, above it.
            var opened = history.Open(Guid.NewGuid(), [new MessageViewModel(MessageRole.User, "Brand new")], clock.GetUtcNow().AddMinutes(1));
            Pump();
            Assert.Equal([opened, earlier], Section(rows, 0));
            Assert.Same(opened, rows.SelectedItem);
            Assert.Same(opened, Named<ListBox>(window, "Conversations").SelectedItem);
        }
        finally
        {
            window.CloseForGood();
        }
    })));

    private static (Color Color, double Offset)[] GradientOf(LinearGradientBrush brush) =>
        [.. brush.GradientStops.Select(stop => (stop.Color, stop.Offset))];

    [Fact]
    public void ViewMenuSwitchesBetweenGridAndList() => RunSta(() => WithTheme(() =>
    {
        var backdrops = new MenuBackdrops(blurred: true);
        var (window, history, _) = CreateHistoryWindow(source: new SampleHistorySource(new FixedClock(ReferenceNow)), backdrops: backdrops);
        try
        {
            window.Show();
            Pump();
            var button = Named<Button>(window, "FilterButton");
            var menu = Assert.IsType<ContextMenu>(button.ContextMenu);
            Assert.Equal(ThemeColor("Brush.Control.Glass"), SolidColor(button.Background));

            // The filter button opens the menu, lighter while it is open; the grid is checked.
            Click(button);
            Pump();
            Assert.True(menu.IsOpen);
            Assert.Equal(ThemeColor("Brush.Control.GlassOpen"), SolidColor(button.Background));
            var gridItem = Named<MenuItem>(window, "GridMenuItem");
            var listItem = Named<MenuItem>(window, "ListMenuItem");
            Assert.True(gridItem.IsChecked);
            Assert.False(listItem.IsChecked);
            Assert.Equal(["Grid", "List"], menu.Items.Cast<MenuItem>().Select(item => (string)item.Header));

            // It hangs below the button, reaching into the workspace: 93 by 59, its glass 5.25 left of the button's
            // left edge and 42.75 below its top, with items 24.05 tall.
            var glass = Assert.IsAssignableFrom<Grid>(menu.Template.FindName("Glass", menu));
            Assert.Equal(System.Windows.Controls.Primitives.PlacementMode.Relative, menu.Placement);
            Assert.Same(button, menu.PlacementTarget);
            Assert.Equal(-5.25, menu.HorizontalOffset + glass.TransformToAncestor(menu).Transform(default).X, 0.01);
            Assert.Equal(42.75, menu.VerticalOffset + glass.TransformToAncestor(menu).Transform(default).Y, 0.01);
            Assert.Equal(new Size(93, 59), new Size(Math.Round(glass.ActualWidth, 2), Math.Round(glass.ActualHeight, 2)));
            AssertClose(new Rect(0, 5.45, 93, 24.05), BoundsIn(glass, gridItem));
            AssertClose(new Rect(0, 29.5, 93, 24.05), BoundsIn(glass, listItem));
            var check = Descendants<System.Windows.Shapes.Path>(gridItem).Single(path => path.Name == "Check");
            Assert.Equal(Visibility.Visible, check.Visibility);
            Assert.Equal(13, BoundsIn(glass, check).Left, 0.1);
            Assert.Equal(Visibility.Hidden, Descendants<System.Windows.Shapes.Path>(listItem).Single(path => path.Name == "Check").Visibility);
            var label = Descendants<ContentPresenter>(listItem).Single(presenter => presenter.Name == "Label");
            Assert.Equal(50, BoundsIn(glass, label).Left, 0.1);
            Assert.Equal(13.5, Descendants<TextBlock>(label).Single().FontSize);
            Assert.Equal(ThemeColor("Brush.Text.Menu"), SolidColor(Descendants<TextBlock>(label).Single().Foreground));
            Assert.Equal(41.45, BoundsIn(glass, Descendants<ContentPresenter>(gridItem).Single(presenter => presenter.Name == "Glyph")).Right, 0.1);

            // What lies behind the glass is blurred, so the glass is translucent, and the blur covers it exactly.
            var backdrop = Assert.Single(backdrops.Created);
            var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(menu));
            Assert.Equal(source.Handle, backdrop.Target);
            Assert.True(Backdrop.GetIsBlurred(menu));
            var surface = Descendants<PanelShape>(glass).Single(shape => shape.Name == "Surface");
            // The same glass as the token, though not the same object: each control dictionary merges its own copy of the tokens.
            Assert.Equal(
                GradientOf((LinearGradientBrush)Application.Current.FindResource("Brush.Surface.Menu")),
                GradientOf(Assert.IsType<LinearGradientBrush>(surface.Fill)));
            var toDevice = source.CompositionTarget.TransformToDevice;
            var glassBounds = glass.TransformToAncestor(source.RootVisual).TransformBounds(new Rect(glass.RenderSize));
            Assert.Equal(glassBounds.X * toDevice.M11, backdrop.Region.X, 0.1);
            Assert.Equal(93 * toDevice.M11, backdrop.Region.Width, 0.1);
            Assert.Equal(59 * toDevice.M22, backdrop.Region.Height, 0.1);

            // An ellipse fitted to the menu's smooth corners (15.8 DIPs of reach), as every blur's are.
            Assert.Equal(PanelShape.GetBackdropCornerRadii(new Size(93, 59), 15.8).Width * toDevice.M11, backdrop.Region.CornerRadiusX, 0.1);

            RenderFixture(glass, "history-view-menu-2x.png", 2);

            // Choosing List shows the list and closes the menu, which releases its blur.
            Invoke(listItem);
            Pump();
            Assert.False(menu.IsOpen);
            WaitUntil(() => backdrop.IsDisposed, "The menu's blur was not released when it closed.");
            Assert.Equal(HistoryLayout.List, history.Layout);
            Assert.Equal(Visibility.Visible, Named<ListBox>(window, "ConversationRows").Visibility);
            Assert.Equal(Visibility.Collapsed, Named<ListBox>(window, "Conversations").Visibility);
            Assert.Equal(ThemeColor("Brush.Control.Glass"), SolidColor(button.Background));

            // Opened again, it checks the list; Escape closes it; Grid goes back to the cards.
            Click(button);
            Pump();
            Assert.True(menu.IsOpen);
            Assert.True(listItem.IsChecked);
            Assert.False(gridItem.IsChecked);
            Assert.Equal(2, backdrops.Created.Count);
            PressKey(menu, Key.Escape);
            Pump();
            Assert.False(menu.IsOpen);
            WaitUntil(() => backdrops.Created[1].IsDisposed, "The menu's blur was not released when it closed.");
            Click(button);
            Pump();
            Invoke(gridItem);
            Pump();
            Assert.Equal(HistoryLayout.Grid, history.Layout);
            Assert.Equal(Visibility.Visible, Named<ListBox>(window, "Conversations").Visibility);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void ViewMenuIsOpaqueWithoutABlurredBackdrop() => RunSta(() => WithTheme(() =>
    {
        var (window, _, _) = CreateHistoryWindow(backdrops: new MenuBackdrops(blurred: false));
        try
        {
            window.Show();
            Pump();
            var button = Named<Button>(window, "FilterButton");
            Click(button);
            Pump();
            var menu = button.ContextMenu;
            Assert.True(menu.IsOpen);
            Assert.False(Backdrop.GetIsBlurred(menu));
            var glass = Assert.IsAssignableFrom<Grid>(menu.Template.FindName("Glass", menu));
            Assert.Equal(ThemeColor("Brush.Surface.MenuOpaque"),
                SolidColor(Descendants<PanelShape>(glass).Single(shape => shape.Name == "Surface").Fill));
            menu.IsOpen = false;
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void LongHistoryListCreatesRowsOnlyForWhatIsInView() => RunSta(() => WithTheme(() =>
    {
        const int count = 5000;
        var source = new ListSource(Enumerable.Range(0, count).Select(i => new HistoryConversation(Guid.NewGuid(),
            [new MessageViewModel(MessageRole.User, $"Question {i}"), new MessageViewModel(MessageRole.Assistant, $"Answer {i}.")],
            ReferenceNow.AddMinutes(-15 * i))).ToArray());
        var (window, history, _) = CreateHistoryWindow(source: source);
        history.Layout = HistoryLayout.List;
        try
        {
            window.Show();
            Pump();
            var rows = Named<ListBox>(window, "ConversationRows");
            Assert.Equal(count, rows.Items.Count);
            Assert.InRange(Descendants<ListBoxItem>(rows).Count(), 8, 60);

            // Scrolling to the end shows the oldest row clear of the fade, still with few rows created, and it can be
            // opened from there.
            var last = history.Conversations[count - 1];
            rows.ScrollIntoView(last);
            Pump();
            var root = Named<Grid>(window, "Root");
            var bounds = BoundsIn(root, RowOf(rows, last));
            Assert.InRange(bounds.Top, 37.05, 824);
            Assert.InRange(bounds.Bottom, 37.05, 824);
            Assert.InRange(Descendants<ListBoxItem>(rows).Count(), 8, 60);
            RowOf(rows, last).IsSelected = true;
            Pump();
            Assert.Same(last, history.Selected);
            Assert.Equal("Question 4999", AutomationName(RowOf(rows, last)));
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void SwitchingLayoutsBringsTheOpenConversationIntoView() => RunSta(() => WithTheme(() =>
    {
        var (window, history, _) = CreateHistoryWindow(source: new SampleHistorySource(new FixedClock(ReferenceNow)));
        var open = history.Conversations[150];
        history.Selected = open;
        try
        {
            window.Show();
            Pump();
            var root = Named<Grid>(window, "Root");
            history.Layout = HistoryLayout.List;
            Pump();
            var row = RowOf(Named<ListBox>(window, "ConversationRows"), open);
            Assert.True(row.IsSelected);
            Assert.InRange(BoundsIn(root, row).Top, 37.05, 824 - 76.2);

            history.Layout = HistoryLayout.Grid;
            Pump();
            var cards = Named<ListBox>(window, "Conversations");
            var card = Assert.IsType<ListBoxItem>(cards.ItemContainerGenerator.ContainerFromItem(open));
            Assert.True(card.IsSelected);
            Assert.InRange(BoundsIn(root, card).Top, 49.8, 824 - 184);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    // Rects within a twentieth of a DIP, which decimal-place rounding cannot express.
    private static void AssertClose(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 0.05);
        Assert.Equal(expected.Y, actual.Y, 0.05);
        Assert.Equal(expected.Width, actual.Width, 0.05);
        Assert.Equal(expected.Height, actual.Height, 0.05);
    }

    // The section titles of a list of rows, in order.
    private static string[] SectionTitles(ListBox rows) =>
        rows.Items.Groups.Cast<CollectionViewGroup>().Select(group => ((HistorySection)group.Name).Title).ToArray();

    // The conversations in one section of a list of rows, in order.
    private static object[] Section(ListBox rows, int index) =>
        ((CollectionViewGroup)rows.Items.Groups[index]).Items.ToArray();

    // The row a conversation has been given in a list of rows.
    private static ListBoxItem RowOf(ListBox rows, HistoryConversationViewModel conversation) =>
        Descendants<ListBoxItem>(rows).Single(row => ReferenceEquals(row.DataContext, conversation));

    private static (TextBlock Title, TextBlock Preview) RowText(ListBoxItem row)
    {
        var texts = Descendants<TextBlock>(row).ToArray();
        return (texts.Single(text => text.Name == "Title"), texts.Single(text => text.Name == "Preview"));
    }

    // How many lines of text show, from a text block's height and line spacing.
    private static int Lines(TextBlock text) => (int)Math.Round(text.ActualHeight / text.LineHeight);

    private static void Invoke(MenuItem item) =>
        ((IInvokeProvider)new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("Test", now.Offset, "Test", "Test");
    }

    // Records the backdrops the view menu asks for, which blur or not as the test says.
    private sealed class MenuBackdrops(bool blurred = false) : IWindowBackdropFactory
    {
        public List<RecordedBackdrop> Created { get; } = [];

        public IWindowBackdrop Create(nint window)
        {
            var backdrop = new RecordedBackdrop(window, blurred);
            Created.Add(backdrop);
            return backdrop;
        }
    }

    private sealed class RecordedBackdrop(nint target, bool blurred) : IWindowBackdrop
    {
        public nint Target => target;
        public nint Handle => 0;
        public bool IsBlurred => blurred;
        public BackdropRegion Region { get; private set; }
        public double Opacity { get; private set; } = 1;
        public bool IsDisposed { get; private set; }
        public event EventHandler? IsBlurredChanged { add { } remove { } }
        public void SetRegion(BackdropRegion region) => Region = region;
        public void SetOpacity(double opacity) => Opacity = opacity;
        public void Dispose() => IsDisposed = true;
    }
}
