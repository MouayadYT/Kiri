using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // The Search reference's sections, made of synthetic sample results: a top hit with the action it offers, a
    // definition with a larger icon, and three applications with badges on their icons. Its source has them for
    // anything starting with "sam".
    private static (SearchResultsViewModel Results, RecordingCommand[] Commands, FakeResultsSource Source) CreateResults()
    {
        var commands = Enumerable.Range(0, 5).Select(_ => new RecordingCommand()).ToArray();
        var phone = SearchResultBadge.FromGlyph("Result.Badge.Phone");
        var settings = SearchResultBadge.FromGlyph("Result.Badge.Settings");
        var source = new FakeResultsSource([
            new SearchResultSectionViewModel(null, [
                new SearchResultViewModel(SearchResultKind.App, "Sample Browser", commands[0],
                    actionHint: new SearchResultActionHint("Search Sample Browser", "tab"))]),
            new SearchResultSectionViewModel(null, [
                new SearchResultViewModel(SearchResultKind.Knowledge, "Sample Team", commands[1], subtitle: "MLB baseball team")]),
            new SearchResultSectionViewModel(null, [
                new SearchResultViewModel(SearchResultKind.App, "Sample — From Phone", commands[2], badges: [phone]),
                new SearchResultViewModel(SearchResultKind.App, "Sample — From Phone", commands[3], badges: [phone]),
                new SearchResultViewModel(SearchResultKind.App, "Sample Browser", commands[4], badges: [settings])]),
        ]);
        return (new SearchResultsViewModel(source), commands, source);
    }

    private sealed class FakeResultsSource(IReadOnlyList<SearchResultSectionViewModel> sections) : ISearchResultsSource
    {
        public int Searches { get; private set; }
        public string? LastQuery { get; private set; }

        public IReadOnlyList<SearchResultSectionViewModel> Search(string query)
        {
            Searches++;
            LastQuery = query;
            return query.StartsWith("sam", StringComparison.OrdinalIgnoreCase) ? sections : [];
        }
    }

    private static SearchResultViewModel Result(string title, ICommand? command = null, string? subtitle = null) =>
        new(SearchResultKind.File, title, command ?? new RecordingCommand(), subtitle);

    [Fact]
    public void TheArrowKeysMoveTheHighlightThroughEverySectionAndEnterRunsOne()
    {
        var (results, commands, _) = CreateResults();
        results.Update("sample");

        // Five results in three sections, and nothing highlighted, so Enter is not the results' to take.
        Assert.Equal(3, results.Sections.Count);
        Assert.Equal(5, results.Items.Count);
        Assert.True(results.HasResults);
        Assert.Null(results.SelectedItem);
        Assert.False(results.HandleKey(Key.Enter, ModifierKeys.None));
        Assert.All(commands, command => Assert.Equal(0, command.Count));

        // Down goes to the first, on through the sections, and around from the last to the first.
        Assert.True(results.HandleKey(Key.Down, ModifierKeys.None));
        Assert.Same(results.Items[0], results.SelectedItem);
        Assert.True(results.Items[0].IsSelected);
        results.HandleKey(Key.Down, ModifierKeys.None);
        Assert.Same(results.Items[1], results.SelectedItem);
        Assert.False(results.Items[0].IsSelected);
        Assert.True(results.Items[1].IsSelected);
        for (var i = 0; i < 3; i++)
        {
            results.HandleKey(Key.Down, ModifierKeys.None);
        }

        Assert.Same(results.Items[4], results.SelectedItem);
        results.HandleKey(Key.Down, ModifierKeys.None);
        Assert.Same(results.Items[0], results.SelectedItem);

        // Up goes back, and around from the first to the last. Only one row is ever highlighted.
        results.HandleKey(Key.Up, ModifierKeys.None);
        Assert.Same(results.Items[4], results.SelectedItem);
        Assert.Single(results.Items, item => item.IsSelected);

        // Enter runs the highlighted one, and only it.
        Assert.True(results.HandleKey(Key.Enter, ModifierKeys.None));
        Assert.Equal([0, 0, 0, 0, 1], commands.Select(command => command.Count));

        // From none, Up goes to the last. Other keys and modifiers are not the results'.
        results.ClearSelection();
        Assert.Null(results.SelectedItem);
        Assert.All(results.Items, item => Assert.False(item.IsSelected));
        results.HandleKey(Key.Up, ModifierKeys.None);
        Assert.Same(results.Items[4], results.SelectedItem);
        Assert.False(results.HandleKey(Key.A, ModifierKeys.None));
        Assert.False(results.HandleKey(Key.Down, ModifierKeys.Control));
        Assert.False(results.HandleKey(Key.Left, ModifierKeys.None));
    }

    [Fact]
    public void ResultsAreAskedForOnEveryChangeAndSectionsWithoutRowsAreLeftOut()
    {
        var (results, _, source) = CreateResults();
        var changes = new List<string?>();
        results.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        results.Update("sample");
        Assert.Equal("sample", source.LastQuery);
        Assert.Contains(nameof(SearchResultsViewModel.Sections), changes);
        Assert.Contains(nameof(SearchResultsViewModel.HasResults), changes);
        results.MoveSelection(1);
        var first = results.SelectedItem;

        // Nothing for a query, or none at all, lists nothing and highlights nothing; an empty query is not asked.
        results.Update("zzz");
        Assert.False(results.HasResults);
        Assert.Null(results.SelectedItem);
        Assert.False(first!.IsSelected);
        Assert.False(results.HandleKey(Key.Down, ModifierKeys.None));
        var searches = source.Searches;
        results.Update("");
        Assert.Equal(searches, source.Searches);

        results.SetSections([new SearchResultSectionViewModel("Empty", []), new SearchResultSectionViewModel("Files", [Result("a")])]);
        Assert.Equal("Files", Assert.Single(results.Sections).Title);
        results.Clear();
        Assert.False(results.HasResults);

        // A result that is not listed cannot be highlighted.
        results.SelectedItem = first;
        Assert.Null(results.SelectedItem);
    }

    [Fact]
    public void TheResultsShowWhileTheFieldHasTextAndOnlyWhenThereAreSome()
    {
        var (results, _, _) = CreateResults();
        var (launcher, _) = CreateLauncher();
        var bar = CreateBarModel(launcher: launcher, results: results);
        var changes = new List<string?>();
        bar.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        // Empty, the categories show and the results do not.
        Assert.True(bar.IsLauncherVisible);
        Assert.False(bar.IsResultsVisible);

        // Text with results takes the categories away and shows them; text without any shows neither.
        bar.Query = "sample";
        Assert.False(bar.IsLauncherVisible);
        Assert.True(bar.IsResultsVisible);
        Assert.Contains(nameof(SearchOrAskViewModel.IsResultsVisible), changes);
        results.MoveSelection(1);
        bar.Query = "sampl";
        Assert.Null(results.SelectedItem);
        bar.Query = "zzz";
        Assert.False(bar.IsLauncherVisible);
        Assert.False(bar.IsResultsVisible);

        // Emptying the field brings the categories back and clears the results.
        bar.Query = "sample";
        bar.Query = "";
        Assert.True(bar.IsLauncherVisible);
        Assert.False(bar.IsResultsVisible);
        Assert.False(results.HasResults);
    }

    [Fact]
    public void TheSampleResultsAreThereForTwoQueriesOnly()
    {
        var source = new PlaceholderSearchResults(new FixedTime(SampleNow));

        // The Search reference's sections: a top hit, a definition, and three applications, then the made-up
        // conversations that mention the query.
        var sections = source.Search("Brave");
        Assert.Equal([1, 1, 3, 2], sections.Select(section => section.Items.Count));
        var top = sections[0].Items[0];
        Assert.Equal(("Search Brave Browser", "tab"), (top.ActionHint!.Text, top.ActionHint.Key));
        Assert.Equal(SearchResultRowSize.Large, sections[1].Items[0].Size);
        Assert.All(sections[2].Items, item => Assert.Single(item.Badges));
        Assert.All(sections[3].Items, item => Assert.Equal(SearchResultKind.Message, item.Kind));

        // Every kind, with the row that would search Messages last.
        var demo = source.Search(" demo");
        Assert.Equal([5, 3, 1, 1, 1], demo.Select(section => section.Items.Count));
        Assert.Equal(
            [SearchResultKind.Message, SearchResultKind.Contact, SearchResultKind.File, SearchResultKind.Action, SearchResultKind.App],
            demo.Select(section => section.Items[0].Kind));
        Assert.Equal("Search Messages", demo[^1].Items[0].Title);

        // Ordinary typing is left alone.
        Assert.Empty(source.Search("hello"));
        Assert.Empty(source.Search(""));
    }

    [Fact]
    public void ARowDrawsWhateverKindOfResultItIsGiven() => RunSta(() => WithTheme(() =>
    {
        var command = new RecordingCommand();
        var row = new SearchResultRow
        {
            Kind = SearchResultKind.Message, Title = "Sample conversation", Subtitle = "The start of a message",
            Detail = "4/25/26", ActionText = "Reply", ActionKey = "tab", Command = command,
            Badges = [SearchResultBadge.FromGlyph("Result.Badge.Messages")],
        };
        Initialized(row);
        Layout(row);

        // Its parts, by name: the icon and its badges, the words, and the action, which shows only when highlighted.
        var icon = Part<ContentPresenter>(row, "Icon");
        var title = Part<TextBlock>(row, "Title");
        var subtitle = Part<TextBlock>(row, "Subtitle");
        var detail = Part<TextBlock>(row, "Detail");
        var action = Part<StackPanel>(row, "Action");
        Assert.Equal("Sample conversation", title.Text);
        Assert.Equal("The start of a message", subtitle.Text);
        Assert.Equal("4/25/26", detail.Text);
        Assert.Equal(Visibility.Visible, subtitle.Visibility);
        Assert.Equal(Visibility.Visible, detail.Visibility);
        Assert.Equal(Visibility.Collapsed, action.Visibility);

        // A row that was given no icon has its kind's.
        Assert.Equal("Result.Icon.Message", row.Icon.GlyphKey);
        Assert.NotEmpty(Descendants<System.Windows.Shapes.Shape>(icon));
        Assert.Single(Part<ItemsControl>(row, "Badges").Items);
        Assert.NotEmpty(Descendants<System.Windows.Shapes.Shape>(Part<ItemsControl>(row, "Badges")));

        // Highlighted, it offers its action, and a two-line row has a smaller, bold title.
        row.IsSelected = true;
        Layout(row);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("tab", Descendants<TextBlock>(Part<Border>(row, "ActionCap")).Single().Text);
        Assert.Equal(FontWeights.Bold, title.FontWeight);
        Assert.Equal(ThemeColor("Brush.Surface.CommandItemSelected"), SolidColor(Highlight(row).Fill));

        // Every kind has an icon of its own that draws.
        foreach (var kind in Enum.GetValues<SearchResultKind>())
        {
            row.Kind = kind;
            row.Icon = null!;
            Layout(row);
            Assert.NotNull(row.Icon.GlyphKey);
            Assert.NotEmpty(Descendants<System.Windows.Shapes.Shape>(Part<ContentPresenter>(row, "Icon")));
        }

        // Alone on its row, a title is the larger, medium one, and what is not given is not there.
        var plain = new SearchResultRow { Kind = SearchResultKind.App, Title = "Sample Browser" };
        Initialized(plain);
        Layout(plain);
        Assert.Equal(Visibility.Collapsed, Part<TextBlock>(plain, "Subtitle").Visibility);
        Assert.Equal(Visibility.Collapsed, Part<TextBlock>(plain, "Detail").Visibility);
        Assert.Equal(FontWeights.SemiBold, Part<TextBlock>(plain, "Title").FontWeight);
        Assert.Equal((double)Application.Current.FindResource("FontSize.ResultTitle"), Part<TextBlock>(plain, "Title").FontSize);
        Assert.Empty(Part<ItemsControl>(plain, "Badges").Items);
        plain.IsSelected = true;
        Layout(plain);
        Assert.Equal(Visibility.Visible, Part<StackPanel>(plain, "Action").Visibility);
        Assert.Equal(Visibility.Collapsed, Part<TextBlock>(plain, "ActionText").Visibility);
        Assert.Equal(Visibility.Collapsed, Part<Border>(plain, "ActionCap").Visibility);

        // A larger icon makes the row taller, and its words smaller.
        var large = new SearchResultRow { Kind = SearchResultKind.Knowledge, Title = "Sample Team", Subtitle = "A team", Size = SearchResultRowSize.Large };
        Initialized(large);
        Layout(large);
        Assert.Equal(82, large.ActualHeight);
        Assert.Equal(62, plain.ActualHeight);
        Assert.Equal(50, Part<Grid>(large, "IconHost").ActualWidth);
        Assert.Equal((double)Application.Current.FindResource("FontSize.ResultKnowledgeTitle"), Part<TextBlock>(large, "Title").FontSize);

        // Assistive technology can invoke it, which runs its command as a click does, when the command can run.
        var peer = Assert.IsAssignableFrom<IInvokeProvider>(UIElementAutomationPeer.CreatePeerForElement(row));
        Assert.Equal(AutomationControlType.ListItem, UIElementAutomationPeer.CreatePeerForElement(row).GetAutomationControlType());
        peer.Invoke();
        Assert.Equal(1, command.Count);
        row.Activate();
        Assert.Equal(2, command.Count);
        command.Enabled = false;
        row.Activate();
        Assert.Equal(2, command.Count);
    }));

    [Fact]
    public void ThePanelFitsItsSectionsAsInTheReference() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var (results, commands, _) = CreateResults();
        var (launcher, _) = CreateLauncher();
        var assistant = CreateAssistant(frames: frames, launcher: launcher, results: results);
        var (window, bar, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var host = GridNamed(window, "SurfaceHost");
            var layer = GridNamed(window, "LauncherLayer");
            var panel = Named<PanelShape>(window, "LauncherSurface");
            var list = Named<ItemsControl>(window, "ResultsList");
            var input = Named<PromptInputControl>(window, "PromptInput");

            // Nothing typed: the categories are in the panel, and the results are not.
            Assert.Equal(Visibility.Visible, Named<ListBox>(window, "LauncherList").Visibility);
            Assert.Equal(Visibility.Collapsed, GridNamed(window, "ResultsHost").Visibility);

            // Typing something with results swaps them in, and the panel fits them: the chips (19.5 above them, 24 tall), 17 more above
            // the sections (62 + 2, 82 + 2 and 3 x 62 + 2) and 9.75 below, in a window as much taller as the panel is.
            input.Text = "sample";
            Pump();
            Assert.True(window.IsResultsShown);
            Assert.False(window.IsLauncherShown);
            Assert.Equal(Visibility.Collapsed, Named<ListBox>(window, "LauncherList").Visibility);
            Assert.Equal(Visibility.Visible, GridNamed(window, "ResultsHost").Visibility);
            Assert.Equal(Visibility.Visible, layer.Visibility);
            const double panelHeight = 19.5 + 24 + 17 + 64 + 84 + 188 + 9.75;
            Assert.Equal(new Size(610, 28 + 91 + 10.5 + panelHeight + 64), new Size(window.ActualWidth, window.ActualHeight));
            Assert.Equal(new Rect(45, 28, 520, 91), GlassRegion(window));
            AssertRect(new Rect(45, 129.5, 520, panelHeight), BoundsIn(host, panel));

            // Five rows, each as wide as the panel: 62 tall, or 82 with the larger icon, touching within a section and
            // 2 apart between sections.
            var rows = Descendants<SearchResultRow>(list).ToArray();
            Assert.Equal(["Sample Browser", "Sample Team", "Sample — From Phone", "Sample — From Phone", "Sample Browser"],
                rows.Select(row => row.Title));
            double[] tops = [190, 254, 338, 400, 462];
            double[] heights = [62, 82, 62, 62, 62];
            for (var i = 0; i < 5; i++)
            {
                AssertRect(new Rect(45, tops[i], 520, heights[i]), BoundsIn(host, rows[i]));
            }

            // The icon is centered 43.5 in and 36 across, or 50 with the larger one, and the words begin 79.25 in, or 84
            // with it, where the ink's own margin makes them 79.5 and 84.5; a badge sits on the icon's lower right.
            AssertRect(new Rect(45 + 25.5, tops[0] + 13, 36, 36), BoundsIn(host, Part<Grid>(rows[0], "IconHost")));
            AssertRect(new Rect(45 + 18.5, tops[1] + 16, 50, 50), BoundsIn(host, Part<Grid>(rows[1], "IconHost")));
            Assert.Equal(45 + 79.25, BoundsIn(host, Part<TextBlock>(rows[0], "Title")).Left, 1);
            Assert.Equal(45 + 84, BoundsIn(host, Part<TextBlock>(rows[1], "Title")).Left, 1);
            var badges = Part<ItemsControl>(rows[2], "Badges");
            var iconBounds = BoundsIn(host, Part<Grid>(rows[2], "IconHost"));
            Assert.Equal(iconBounds.Right + 1.5, BoundsIn(host, badges).Right, 1);
            Assert.Equal(iconBounds.Bottom + 1.5, BoundsIn(host, badges).Bottom, 1);

            // Nothing is highlighted at first, nor is any action shown, and the panel has the blur of the launcher's.
            Assert.All(rows, row => Assert.Equal(Visibility.Collapsed, Part<StackPanel>(row, "Action").Visibility));
            Assert.Null(results.SelectedItem);
            var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            Assert.Equal(panelHeight * scale, assistant.Backdrops.Launcher!.Region.Height, 1);
            Assert.Equal(520 * scale, assistant.Backdrops.Launcher.Region.Width, 1);

            // The arrow keys, pressed in the editor, move the highlight through the sections and never reach it; the
            // top hit shows its action at its right, the cap ending 19.5 in.
            PressPreviewKey(window, Key.Down);
            Pump();
            Assert.Same(results.Items[0], results.SelectedItem);
            Assert.Equal(ThemeColor("Brush.Surface.CommandItemSelected"), SolidColor(Highlight(rows[0]).Fill));
            Assert.Null(SolidColorOrNull(Highlight(rows[1]).Fill));
            var action = Part<StackPanel>(rows[0], "Action");
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Search Sample Browser", Part<TextBlock>(rows[0], "ActionText").Text);
            var cap = Part<Border>(rows[0], "ActionCap");
            Assert.Equal(45 + 520 - 19.5, BoundsIn(host, cap).Right, 1);
            Assert.Equal(20, cap.ActualHeight, 1);
            Assert.Equal(tops[0] + 31, BoundsIn(host, cap).Top + 10, 1);
            Assert.Equal("sample", input.Text);
            RenderFixture(host, "results-2x.png", 2, new Rect(33, 17, 544, 529));

            PressPreviewKey(window, Key.Down);
            Assert.Same(results.Items[1], results.SelectedItem);
            Assert.Equal(Visibility.Collapsed, Part<StackPanel>(rows[0], "Action").Visibility);
            PressPreviewKey(window, Key.Up);
            PressPreviewKey(window, Key.Up);
            Assert.Same(results.Items[4], results.SelectedItem);
            PressPreviewKey(window, Key.Enter);
            Assert.Equal([0, 0, 0, 0, 1], commands.Select(command => command.Count));
            Assert.Equal(Visibility.Visible, layer.Visibility);

            // With none highlighted Enter is the bar's own: it asks, and the results go with the bar.
            results.ClearSelection();
            Assert.True(input.TrySubmit());
            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
            Assert.Equal(Visibility.Collapsed, layer.Visibility);
            Assert.False(window.IsResultsShown);
            Advance(frames[1], 600);
            bar.Query = "";
            Assert.False(results.HasResults);
            Assert.Equal(690, GridNamed(window, "Root").Height);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TypingAwayFromResultsBringsTheCategoriesBackAndTheWindowFollowsThePanel() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var (results, _, _) = CreateResults();
        var (launcher, _) = CreateLauncher();
        var assistant = CreateAssistant(frames: frames, launcher: launcher, results: results);
        var (window, bar, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var input = Named<PromptInputControl>(window, "PromptInput");
            var root = GridNamed(window, "Root");
            var layer = GridNamed(window, "LauncherLayer");
            Assert.Equal(28 + 91 + 10.5 + 215.5 + 64, root.Height);

            input.Text = "sample";
            Pump();
            Assert.Equal(28 + 91 + 10.5 + 406.25 + 64, root.Height);
            Assert.Equal(406.25, layer.Height);

            // Text with no results shrinks the window to the bar; the categories come back at their own height.
            input.Text = "zzz";
            Pump();
            Assert.Equal(Visibility.Collapsed, layer.Visibility);
            Assert.Equal(28 + 91 + 64, root.Height);
            input.Text = "";
            Pump();
            FinishPanel(frames);
            Assert.Equal(Visibility.Visible, layer.Visibility);
            Assert.Equal(215.5, layer.Height);
            Assert.Equal(Visibility.Visible, Named<ListBox>(window, "LauncherList").Visibility);
            Assert.Equal(Visibility.Collapsed, GridNamed(window, "ResultsHost").Visibility);
            Assert.Equal(28 + 91 + 10.5 + 215.5 + 64, root.Height);

            // Hidden, the highlight goes, and the bar is empty-handed again the next time.
            input.Text = "sample";
            Pump();
            results.MoveSelection(1);
            window.Dismiss();
            Advance(frames[0], 300);
            Assert.Null(results.SelectedItem);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void MoreSectionsThanFitScrollUnderThePanelsEdgesAndAHighlightedRowScrollsIntoView() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var sections = Enumerable.Range(0, 8).Select(section => new SearchResultSectionViewModel(
            section % 2 == 0 ? $"Section {section}" : null,
            Enumerable.Range(0, 2).Select(row => Result($"Result {section}.{row}", subtitle: "A file")))).ToList();
        var results = new SearchResultsViewModel(new FakeResultsSource(sections));
        var assistant = CreateAssistant(frames: frames, results: results);
        var (window, _, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var host = GridNamed(window, "SurfaceHost");
            var layer = GridNamed(window, "LauncherLayer");
            var scroller = Named<ScrollViewer>(window, "ResultsScroller");
            Named<PromptInputControl>(window, "PromptInput").Text = "sample";
            Pump();
            FinishPanel(frames);

            // The panel stops at the reference's height, and the rest scrolls behind a bar that is never shown.
            Assert.Equal(409, layer.Height);
            Assert.Equal(28 + 91 + 10.5 + 409 + 64, window.ActualHeight);
            Assert.True(scroller.ScrollableHeight > 0);
            Assert.Equal(ScrollBarVisibility.Hidden, scroller.VerticalScrollBarVisibility);
            Assert.Equal(0, scroller.VerticalOffset);
            Assert.NotNull(GridNamed(window, "ResultsHost").Clip);

            // A titled section has its header over its rows; one without has none.
            var headers = Descendants<TextBlock>(Named<ItemsControl>(window, "ResultsList"))
                .Where(block => block.Name == "Header").ToArray();
            Assert.Equal(8, headers.Length);
            Assert.Equal(["Section 0", "", "Section 2", "", "Section 4", "", "Section 6", ""], headers.Select(header => header.Text));
            Assert.Equal([Visibility.Visible, Visibility.Collapsed], headers.Take(2).Select(header => header.Visibility));

            // Highlighting the last row brings it into view.
            results.MoveSelection(-1);
            window.UpdateLayout();
            Pump();
            Assert.True(scroller.VerticalOffset > 0);
            var last = Descendants<SearchResultRow>(Named<ItemsControl>(window, "ResultsList")).Last();
            Assert.True(last.IsSelected);
            var bounds = BoundsIn(host, last);
            Assert.True(bounds.Bottom <= 129.5 + 409 + 0.5, $"The last row ends at {bounds.Bottom}, below the panel.");

            // New sections start again from the top.
            results.SetSections(sections.Take(7));
            Pump();
            Assert.Equal(0, scroller.VerticalOffset);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void AConversationRowShowsItsNoteAndBadgeAndAContactsPhotoIsCroppedToADisc() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var photo = new DrawingImage(new GeometryDrawing(Brushes.SteelBlue, null, new RectangleGeometry(new Rect(0, 0, 10, 10))));
        photo.Freeze();
        var results = new SearchResultsViewModel(new FakeResultsSource([
            new SearchResultSectionViewModel(null, [
                new SearchResultViewModel(SearchResultKind.Message, "Sample conversation", new RecordingCommand(),
                    subtitle: "(If you need a sample version (only works on sample) let me know I have there…",
                    detail: "4/25/26", icon: SearchResultIcon.FromGlyph("Result.Icon.Contact"),
                    badges: [SearchResultBadge.FromGlyph("Result.Badge.Messages")]),
                new SearchResultViewModel(SearchResultKind.Contact, "Sample Person", new RecordingCommand(),
                    icon: SearchResultIcon.FromImage(photo, SearchResultIconShape.Circle))]),
            new SearchResultSectionViewModel(null, [
                new SearchResultViewModel(SearchResultKind.App, "Search Messages", new RecordingCommand(),
                    icon: SearchResultIcon.FromGlyph("Result.Icon.Message"))]),
        ]));
        var assistant = CreateAssistant(frames: frames, results: results);
        var (window, _, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var host = GridNamed(window, "SurfaceHost");
            var list = Named<ItemsControl>(window, "ResultsList");
            Named<PromptInputControl>(window, "PromptInput").Text = "sample";
            Pump();
            FinishPanel(frames);
            RenderFixture(host, "results-messages-2x.png", 2, new Rect(33, 17, 544, 380));

            var rows = Descendants<SearchResultRow>(list).ToArray();
            var message = rows[0];

            // The note ends 29 in from the panel's right, on the title's line, and the long subtitle is cut short at the same place.
            var detail = BoundsIn(host, Part<TextBlock>(message, "Detail"));
            Assert.Equal(45 + 520 - 29, detail.Right, 1);
            Assert.Equal(BoundsIn(host, Part<TextBlock>(message, "Title")).Top, detail.Top, 1);
            var subtitle = Part<TextBlock>(message, "Subtitle");
            Assert.Equal(45 + 520 - 29, BoundsIn(host, subtitle).Right, 1);
            Assert.True(BoundsIn(host, subtitle).Top > BoundsIn(host, Part<TextBlock>(message, "Title")).Bottom - 1);

            // The photo is drawn as a disc, and not as a tile.
            var contactIcon = Part<ContentPresenter>(rows[1], "Icon");
            var disc = Descendants<System.Windows.Shapes.Ellipse>(contactIcon).Single(ellipse => ellipse.Name == "Disc");
            Assert.Equal(Visibility.Visible, disc.Visibility);
            Assert.Equal(Visibility.Collapsed, Descendants<Image>(contactIcon).Single(image => image.Name == "Picture").Visibility);
            Assert.Equal(36, disc.ActualWidth);
            Assert.Same(photo, Assert.IsType<ImageBrush>(disc.Fill).ImageSource);
        }
        finally { window.Close(); }
    }));

    // A fixed moment for the sample dates: late morning on a Tuesday.
    private static readonly DateTimeOffset SampleNow = new(2026, 9, 29, 10, 0, 0, TimeSpan.FromHours(-7));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("test", now.Offset, "test", "test");
    }

    // A control made in code takes its theme style once it is initialized.
    private static void Initialized(SearchResultRow row)
    {
        row.BeginInit();
        row.EndInit();
    }

    private static T Part<T>(SearchResultRow row, string name) where T : class =>
        Assert.IsType<T>(row.Template.FindName(name, row));

    private static PillShape Highlight(SearchResultRow row) => Part<PillShape>(row, "Highlight");
}
