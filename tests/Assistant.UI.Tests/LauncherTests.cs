using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // The launcher's four categories, with commands a test can count, as the bar lists them while it is empty.
    private static (LauncherViewModel Launcher, RecordingCommand[] Commands) CreateLauncher()
    {
        var commands = Enumerable.Range(0, 4).Select(_ => new RecordingCommand()).ToArray();
        string[] titles = ["Applications", "Files", "Actions", "Clipboard"];
        string[] icons = ["Glyph.Applications", "Glyph.Files", "Glyph.Actions", "Glyph.Clipboard"];
        var items = titles.Select((title, i) => new CommandItemViewModel(
            title, icons[i], commands[i], new KeyHint("Glyph.CommandKey", (i + 1).ToString()), new KeyGesture(Key.D1 + i, ModifierKeys.Control)));
        return (new LauncherViewModel(items), commands);
    }

    [Fact]
    public void TheArrowKeysMoveTheHighlightAroundTheCategoriesAndEnterRunsOne()
    {
        var (launcher, commands) = CreateLauncher();

        // Nothing is highlighted to begin with, and Enter has nothing to run.
        Assert.Null(launcher.SelectedItem);
        Assert.False(launcher.HandleKey(Key.Enter, ModifierKeys.None));
        Assert.All(commands, command => Assert.Equal(0, command.Count));

        // Down goes to the first, then on, and around from the last to the first.
        Assert.True(launcher.HandleKey(Key.Down, ModifierKeys.None));
        Assert.Equal("Applications", launcher.SelectedItem!.Title);
        launcher.HandleKey(Key.Down, ModifierKeys.None);
        launcher.HandleKey(Key.Down, ModifierKeys.None);
        launcher.HandleKey(Key.Down, ModifierKeys.None);
        Assert.Equal("Clipboard", launcher.SelectedItem!.Title);
        launcher.HandleKey(Key.Down, ModifierKeys.None);
        Assert.Equal("Applications", launcher.SelectedItem!.Title);

        // Up goes back, and around from the first to the last.
        Assert.True(launcher.HandleKey(Key.Up, ModifierKeys.None));
        Assert.Equal("Clipboard", launcher.SelectedItem!.Title);
        launcher.HandleKey(Key.Up, ModifierKeys.None);
        Assert.Equal("Actions", launcher.SelectedItem!.Title);

        // Enter runs the highlighted one, and only it.
        Assert.True(launcher.HandleKey(Key.Enter, ModifierKeys.None));
        Assert.Equal([0, 0, 1, 0], commands.Select(command => command.Count));

        // From none, Up goes to the last.
        launcher.ClearSelection();
        Assert.Null(launcher.SelectedItem);
        launcher.HandleKey(Key.Up, ModifierKeys.None);
        Assert.Equal("Clipboard", launcher.SelectedItem!.Title);
    }

    [Fact]
    public void ACategorysShortcutHighlightsAndRunsItAtOnce()
    {
        var (launcher, commands) = CreateLauncher();

        Assert.True(launcher.HandleKey(Key.D2, ModifierKeys.Control));
        Assert.Equal("Files", launcher.SelectedItem!.Title);
        Assert.Equal([0, 1, 0, 0], commands.Select(command => command.Count));

        // The number alone, another modifier, or a key that is nobody's is not the panel's to take.
        Assert.False(launcher.HandleKey(Key.D3, ModifierKeys.None));
        Assert.False(launcher.HandleKey(Key.D3, ModifierKeys.Alt));
        Assert.False(launcher.HandleKey(Key.D9, ModifierKeys.Control));
        Assert.False(launcher.HandleKey(Key.A, ModifierKeys.None));
        Assert.False(launcher.HandleKey(Key.Left, ModifierKeys.None));
        Assert.Equal([0, 1, 0, 0], commands.Select(command => command.Count));
        Assert.Equal("Ctrl+2", launcher.Items[1].ShortcutText);
    }

    [Fact]
    public void AnEmptyLauncherTakesNoKeysAndNeverShows()
    {
        var launcher = new LauncherViewModel([]);
        Assert.False(launcher.HandleKey(Key.Down, ModifierKeys.None));
        Assert.False(launcher.HandleKey(Key.Enter, ModifierKeys.None));
        launcher.MoveSelection(1);
        Assert.Null(launcher.SelectedItem);
        Assert.False(CreateBarModel(launcher: launcher).IsLauncherVisible);
    }

    [Fact]
    public void TheCategoriesShowWhileTheFieldIsEmptyAndTypingTakesThemAway()
    {
        var (launcher, _) = CreateLauncher();
        var bar = CreateBarModel(launcher: launcher);
        var changes = new List<string?>();
        bar.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.True(bar.IsLauncherVisible);
        launcher.MoveSelection(1);
        bar.Query = "a";
        Assert.False(bar.IsLauncherVisible);
        Assert.Contains(nameof(SearchOrAskViewModel.IsLauncherVisible), changes);

        // Emptying the field brings them back with nothing highlighted.
        bar.Query = "";
        Assert.True(bar.IsLauncherVisible);
        Assert.Null(launcher.SelectedItem);

        // A field of spaces is not empty.
        bar.Query = " ";
        Assert.False(bar.IsLauncherVisible);
    }

    [Fact]
    public void TheCategoryPanelHangsUnderTheBarAsInTheReference() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var (launcher, commands) = CreateLauncher();
        var assistant = CreateAssistant(frames: frames, launcher: launcher);
        var (window, bar, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var host = GridNamed(window, "SurfaceHost");
            var layer = GridNamed(window, "LauncherLayer");
            var panel = Named<PanelShape>(window, "LauncherSurface");
            var list = Named<ListBox>(window, "LauncherList");

            // Empty, the bar has its categories under it: the window is taller by the panel and the gap above it, the
            // pill is where it was, and the panel is as wide as the pill, 10.5 below it and 215.5 tall.
            Assert.Equal(Visibility.Visible, layer.Visibility);
            Assert.Equal(new Size(610, 28 + 91 + 10.5 + 215.5 + 64), new Size(window.ActualWidth, window.ActualHeight));
            Assert.Equal(new Rect(45, 28, 520, 91), GlassRegion(window));
            AssertRect(new Rect(45, 129.5, 520, 215.5), BoundsIn(host, panel));

            // Four rows, each 48 tall, the first 11.75 under the panel's top, with their glyphs, words and keys.
            var rows = Enumerable.Range(0, 4).Select(i => (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(i)).ToArray();
            Assert.All(rows, row => Assert.NotNull(row));
            for (var i = 0; i < 4; i++)
            {
                AssertRect(new Rect(45, 129.5 + 11.75 + (48 * i), 520, 48), BoundsIn(host, rows[i]));
            }

            Assert.Equal(["Applications", "Files", "Actions", "Clipboard"],
                rows.Select(row => Descendants<TextBlock>(row).Single(block => block.Name == "Title").Text));
            Assert.Equal(["1", "2", "3", "4"],
                rows.Select(row => Descendants<TextBlock>(row).Single(block => block.Name == "HintKey").Text));
            Assert.All(rows, row => Assert.NotEmpty(Descendants<System.Windows.Shapes.Path>(
                Descendants<ContentPresenter>(row).Single(presenter => presenter.Name == "HintModifier"))));
            Assert.All(rows, row => Assert.NotEmpty(Descendants<System.Windows.Shapes.Path>(row)));
            Assert.Equal(["Applications", "Files", "Actions", "Clipboard"], rows.Select(AutomationName));
            Assert.Equal("Categories", AutomationName(list));

            // The words begin 81.3 from the panel's left and the keys end 37 from its right; the glyph is centered 44 in.
            var title = Descendants<TextBlock>(rows[0]).Single(block => block.Name == "Title");
            var hint = Descendants<StackPanel>(rows[0]).Single(panel => panel.Name == "Hint");
            var glyph = Descendants<ContentPresenter>(rows[0]).Single(presenter => presenter.Name == "Glyph");
            Assert.Equal(45 + 81.3, BoundsIn(host, title).Left, 1);
            Assert.Equal(45 + 459, BoundsIn(host, hint).Left, 1);
            Assert.Equal(45 + 44, BoundsIn(host, glyph).Left + (glyph.ActualWidth / 2), 1);

            // Nothing is highlighted at first; the panel is a piece of the bar's glass with a blur of its own.
            Assert.All(rows, row => Assert.Null(SolidColorOrNull(Highlight(row).Fill)));
            Assert.Null(launcher.SelectedItem);
            Assert.NotNull(assistant.Backdrops.Launcher);
            var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            Assert.Equal(520 * scale, assistant.Backdrops.Launcher!.Region.Width, 1);
            Assert.Equal(215.5 * scale, assistant.Backdrops.Launcher.Region.Height, 1);

            // The blur's corners are ellipses fitted to the panel's smooth ones (49 DIPs of reach), not ellipses of the same reach, which stop short of them.
            Assert.Equal(PanelShape.GetBackdropCornerRadii(new Size(520, 215.5), 49).Width * scale, assistant.Backdrops.Launcher.Region.CornerRadiusX, 1);
            Assert.InRange(assistant.Backdrops.Launcher.Region.CornerRadiusX, 39 * scale, 40 * scale);
            Assert.Equal(new Rect(45, 28, 520, 91).Width * scale, assistant.Backdrops.Created!.Region.Width, 1);

            var input = Named<PromptInputControl>(window, "PromptInput");
            RenderFixture(host, "launcher-2x.png", 2, new Rect(33, 17, 544, 346));

            // The arrow keys, pressed in the bar's editor, move the highlight and never reach the editor; the editor
            // keeps the keyboard throughout.
            PressPreviewKey(window, Key.Down);
            PressPreviewKey(window, Key.Down);
            Assert.Equal("Files", launcher.SelectedItem!.Title);
            Assert.Equal(ThemeColor("Brush.Surface.CommandItemSelected"), SolidColor(Highlight(rows[1]).Fill));
            Assert.Null(SolidColorOrNull(Highlight(rows[0]).Fill));
            Assert.Equal("", input.Text);
            Pump();
            RenderFixture(host, "launcher-selected-2x.png", 2, new Rect(33, 17, 544, 346));

            PressPreviewKey(window, Key.Up);
            Assert.Equal("Applications", launcher.SelectedItem!.Title);
            PressPreviewKey(window, Key.Up);
            Assert.Equal("Clipboard", launcher.SelectedItem!.Title);
            PressPreviewKey(window, Key.Enter);
            Assert.Equal([0, 0, 0, 1], commands.Select(command => command.Count));
            Assert.Equal(Visibility.Visible, layer.Visibility);

            // Typing the first character takes the panel away and shrinks the window to the bar; emptying the field
            // brings it back with nothing highlighted.
            input.Text = "b";
            Pump();
            Assert.Equal(Visibility.Collapsed, layer.Visibility);
            Assert.Equal(new Size(610, 183), new Size(window.ActualWidth, window.ActualHeight));
            Assert.Equal(new Rect(45, 28, 520, 91), GlassRegion(window));
            Assert.Equal(0, assistant.Backdrops.Launcher.Region.Width);
            PressPreviewKey(window, Key.Down);
            Assert.Null(launcher.SelectedItem);
            input.Text = "";
            Pump();
            FinishPanel(frames);
            Assert.Equal(Visibility.Visible, layer.Visibility);
            Assert.Equal(28 + 91 + 10.5 + 215.5 + 64, window.ActualHeight);
            Assert.Null(launcher.SelectedItem);
            Assert.Equal(520 * scale, assistant.Backdrops.Launcher.Region.Width, 1);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheCategoryPanelStaysOutOfTheConversationAndComesBackWithTheBar() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var (launcher, _) = CreateLauncher();
        var assistant = CreateAssistant(frames: frames, launcher: launcher);
        var (window, bar, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var layer = GridNamed(window, "LauncherLayer");
            Assert.Equal(Visibility.Visible, layer.Visibility);

            // Asking grows the bar into the conversation: the panel goes at once and stays away, even when the bar's
            // draft is cleared under the conversation.
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "What is 9+10";
            Assert.Equal(Visibility.Collapsed, layer.Visibility);
            Assert.True(input.TrySubmit());
            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
            Assert.Equal(Visibility.Collapsed, layer.Visibility);
            Advance(frames[1], 600);
            bar.Query = "";
            Assert.Equal(Visibility.Collapsed, layer.Visibility);
            Assert.Equal(690, GridNamed(window, "Root").Height);
            PressPreviewKey(window, Key.Down);
            Assert.Null(launcher.SelectedItem);

            // Dismissed and invoked again, it is the empty bar, with its categories and nothing highlighted.
            window.Dismiss();
            Advance(frames[0], 300);
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            Assert.Equal(AssistantWindowState.Compact, window.State);
            Assert.Equal(Visibility.Visible, layer.Visibility);
            Assert.Equal(28 + 91 + 10.5 + 215.5 + 64, GridNamed(window, "Root").Height);
            Assert.Null(launcher.SelectedItem);
        }
        finally { window.Close(); }
    }));

    private static KeyEventArgs PressPreviewKey(Window window, Key key)
    {
        var target = Keyboard.FocusedElement as UIElement ?? window;
        var press = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        target.RaiseEvent(press);
        return press;
    }

    // The white pill that lights a row.
    private static PillShape Highlight(ListBoxItem row) =>
        Descendants<PillShape>(row).Single(shape => shape.Name == "Highlight");

    private static Color? SolidColorOrNull(Brush? brush) => brush is SolidColorBrush solid ? solid.Color : null;
}
