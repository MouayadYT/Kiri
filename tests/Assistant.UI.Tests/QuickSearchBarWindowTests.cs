using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core.QuickSearch;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The compact result panel as the application-result reference draws it (PROJECT_SPEC §4.1). ----

    private const char EmDash = (char)0x2014;

    // A 72-pixel icon like the ones the shell gives for applications: a rounded orange tile, as the reference's browser icon is.
    private static byte[] AppIconPng()
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(
                new LinearGradientBrush(Color.FromRgb(0xFF, 0x7A, 0x3C), Color.FromRgb(0xE2, 0x3B, 0x14), 90), null, new Rect(2, 2, 68, 68), 16, 16);
        }

        var bitmap = new RenderTargetBitmap(72, 72, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static T QuickPart<T>(Control control, string name) where T : class =>
        Assert.IsType<T>(control.Template.FindName(name, control));

    // The kit and the window for a bar that lists the three applications that begin with "brave", the first with a path to show.
    private (QuickKit Kit, AssistantSetup Assistant, SearchResultsViewModel Results) CreateBraveBar(List<FakeFrames> frames)
    {
        var browser = QuickApp("Brave Browser", "brave", icon: AppIconPng(), alternates:
        [
            new QuickSearchAction(QuickSearchActionKind.RevealPath, "Show in File Explorer", @"C:\Program Files\Brave\brave.exe"),
            new QuickSearchAction(QuickSearchActionKind.CopyPath, "Copy path", @"C:\Program Files\Brave\brave.exe"),
        ]);
        var apps = new FakeQuickProvider("applications", QuickSearchResultType.Applications, 100, _ =>
            [browser, QuickApp("Brave Browser Beta", "beta", icon: AppIconPng()), QuickApp("Brave Browser Nightly", "night", icon: AppIconPng())]);
        var kit = new QuickKit([apps]).Listen();
        var results = kit.NewResults();
        var launcher = new LauncherViewModel(Assistant.UI.Search.QuickSearchLauncherCommands.Create(results));
        var assistant = CreateAssistant(frames: frames, launcher: launcher, results: results, router: kit.Router, attachRequests: kit.Attach, runner: kit.Runner);
        return (kit, assistant, results);
    }

    [Fact]
    public void TheTopHitLeadsWithTheRestOfItsNameItsIconAndTheChipsAsInTheReference() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var (kit, assistant, results) = CreateBraveBar(frames);
        var (window, bar, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var host = GridNamed(window, "SurfaceHost");
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "brave";
            WaitUntil(() => results.SelectedItem is not null, "The best match was not highlighted.");
            Pump();
            FinishPanel(frames);

            // The typed text stays as typed, with the rest of the top hit's name and what Enter does after it, on a plate.
            Assert.Equal("brave", input.Text);
            Assert.Equal(" Browser " + EmDash + " Open", bar.Completion);
            Assert.Equal(bar.Completion, input.CompletionText);
            Assert.True(input.HasCompletion);
            var completion = QuickPart<CompletionPanel>(input, "PART_Completion");
            Assert.Equal(Visibility.Visible, completion.Visibility);
            var plate = Assert.IsType<Border>(completion.Children[1]);
            Assert.True(plate.ActualWidth > 60 && plate.RenderSize.Height > 20, "The plate is drawn.");

            // Its icon is at the right of the field, before the microphone, 32 across.
            Assert.True(bar.HasTrailingIcon);
            Assert.NotNull(input.TrailingContent);
            var trailing = QuickPart<ContentPresenter>(input, "PART_Trailing");
            Assert.Equal(Visibility.Visible, trailing.Visibility);
            Assert.Equal(32, trailing.ActualWidth, 1);
            var microphone = QuickPart<Button>(input, "Microphone");
            Assert.True(BoundsIn(host, trailing).Right < BoundsIn(host, microphone).Left);
            Assert.Equal("", bar.RouteLabel);

            // The panel under it: the chips, 19.5 below its top and 24 tall, the top hit's row 17 under them, the others after it.
            var panelTop = 129.5;
            var chips = Descendants<Button>(Named<ItemsControl>(window, "ScopeChips")).ToArray();
            Assert.Equal(["Applications", "Files", "Actions", "Clipboard"], chips.Select(chip => AutomationName(chip)).Select(name => name.Split(' ')[0]));
            Assert.Equal(["Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4"], results.Chips.Select(chip => chip.ShortcutText));
            Assert.Equal(45 + 20, BoundsIn(host, chips[0]).Left, 1);
            Assert.Equal(panelTop + 19.5, BoundsIn(host, chips[0]).Top, 1);
            Assert.Equal(24, chips[0].ActualHeight, 1);
            Assert.Equal(9, BoundsIn(host, chips[1]).Left - BoundsIn(host, chips[0]).Right, 1);

            var rows = Descendants<SearchResultRow>(Named<ItemsControl>(window, "ResultsList")).ToArray();
            Assert.Equal(["Brave Browser", "Brave Browser Beta", "Brave Browser Nightly"], rows.Select(row => row.Title));
            Assert.Equal(panelTop + 60.5, BoundsIn(host, rows[0]).Top, 1);
            Assert.True(rows[0].IsSelected);
            Assert.Equal(("Open", "enter"), (rows[0].ActionText, rows[0].ActionKey));
            Assert.Equal(("Actions", "tab"), (rows[0].SecondaryActionText, rows[0].SecondaryActionKey));

            // A section of its own for the top hit, and none of the groups has a header (there is only one).
            Assert.Equal([1, 2], results.Sections.Select(section => section.Items.Count));
            RenderFixture(host, "quick-search-brave-2x.png", 2, new Rect(33, 17, 544, 340));

            // Emptying the field takes it all away, and the categories come back.
            input.Text = "";
            Pump();
            Assert.False(input.HasCompletion);
            Assert.False(input.HasTrailingContent);
            Assert.True(window.IsLauncherShown);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TabListsWhatTheHighlightedResultCanDoAndEnterRunsOne() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var (kit, assistant, results) = CreateBraveBar(frames);
        var (window, bar, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var host = GridNamed(window, "SurfaceHost");
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "brave";
            WaitUntil(() => results.SelectedItem is not null, "The best match was not highlighted.");
            Pump();

            // Tab, pressed in the editor, lists the other things the highlighted result can do in place of the results, which have their
            // chips taken away, and never moves the focus to the microphone.
            PressPreviewKey(window, Key.Tab);
            Pump();
            Assert.True(results.IsShowingAlternates);
            var rows = Descendants<SearchResultRow>(Named<ItemsControl>(window, "ResultsList")).ToArray();
            Assert.Equal(["Show in File Explorer", "Copy path"], rows.Select(row => row.Title));
            Assert.Equal("Actions for Brave Browser", results.Sections[0].Title);
            Assert.Equal(Visibility.Collapsed, Named<ItemsControl>(window, "ScopeChips").Visibility);
            Assert.True(rows[0].IsSelected);
            Assert.Equal("Shift+Enter", rows[0].ActionKey);
            Assert.Equal("brave", input.Text);
            Assert.True(window.IsResultsShown);
            RenderFixture(host, "quick-search-actions-2x.png", 2, new Rect(33, 17, 544, 280));

            // The arrow keys move through them and Enter runs the highlighted one, then the bar goes.
            PressPreviewKey(window, Key.Down);
            PressPreviewKey(window, Key.Enter);
            Assert.Equal([@"C:\Program Files\Brave\brave.exe"], kit.Clipboard.Copied);
            Assert.Equal(1, kit.Finished);
            Assert.Empty(kit.Failures);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void EscapeGoesBackOneStepAtATime_TheListOfActionsThenTheTextThenTheBar() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var (_, assistant, results) = CreateBraveBar(frames);
        var (window, bar, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "brave";
            WaitUntil(() => results.SelectedItem is not null, "The best match was not highlighted.");
            PressPreviewKey(window, Key.Tab);
            Pump();
            Assert.True(results.IsShowingAlternates);

            // The first Escape goes back to the results, with the same result highlighted, and keeps the text.
            PressEscape(window);
            Pump();
            Assert.False(results.IsShowingAlternates);
            Assert.Equal("brave", input.Text);
            Assert.Equal("Brave Browser", results.SelectedItem!.Title);

            // The next clears the text, which brings the categories back; and the next closes the bar.
            PressEscape(window);
            Pump();
            Assert.Equal("", input.Text);
            Assert.True(window.IsVisible);
            Assert.True(window.IsLauncherShown);
            PressEscape(window);
            Advance(frames[0], 400);
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void AKindChosenFromTheCategoriesIsBrowsedAndKeepsItsChipsWhenItIsEmpty() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var apps = new FakeQuickProvider("applications", QuickSearchResultType.Applications, 100, request =>
            request.Query.Length == 0 ? [QuickApp("Calculator", "calc"), QuickApp("Mail", "mail")] : []);
        var files = new FakeQuickProvider("files", QuickSearchResultType.Files, 60, _ => []);
        var kit = new QuickKit([apps, files]);
        var results = kit.NewResults();
        var launcher = new LauncherViewModel(Assistant.UI.Search.QuickSearchLauncherCommands.Create(results));
        var assistant = CreateAssistant(frames: frames, launcher: launcher, results: results, router: kit.Router);
        var (window, bar, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            Assert.True(window.IsLauncherShown);

            // Ctrl+1 (the Applications category) lists the applications with nothing typed; the categories give way to the chips.
            Assert.True(launcher.HandleKey(Key.D1, ModifierKeys.Control));
            WaitUntil(() => results.HasResults, "The applications were not listed.");
            Pump();
            Assert.Equal(QuickSearchResultType.Applications, results.Scope);
            Assert.True(results.Chips[0].IsActive);
            Assert.True(window.IsResultsShown);
            Assert.False(window.IsLauncherShown);
            Assert.Equal(["Calculator", "Mail"], results.Items.Select(item => item.Title));
            Assert.Null(results.SelectedItem);
            Assert.Equal("", bar.Query);

            // Another kind that has nothing to list says so, and the chips stay so the user can move on.
            results.ToggleScope(QuickSearchResultType.Files);
            WaitUntil(() => results.EmptyMessage.Length > 0, "The empty list did not say so.");
            Pump();
            Assert.Equal("No recent files", results.EmptyMessage);
            Assert.True(window.IsResultsShown);
            Assert.Equal(Visibility.Visible, Named<TextBlock>(window, "ResultsEmpty").Visibility);
            Assert.Equal("No recent files", Named<TextBlock>(window, "ResultsEmpty").Text);

            // Esc lifts the narrowing, which brings the categories back, and a press after that closes the bar.
            Assert.False(bar.HandleEscape());
            Assert.Null(results.Scope);
            Assert.True(window.IsLauncherShown);
            Assert.True(bar.HandleEscape());
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void WhatWasChosenFromTheListGoesAwayWithTheBarAndAFailureStaysAndSaysWhy() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var (kit, assistant, results) = CreateBraveBar(frames);
        var (window, bar, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "brave";
            WaitUntil(() => results.SelectedItem is not null, "The best match was not highlighted.");
            Pump();

            // A failure keeps the bar and what was typed, and says why above the results.
            kit.Apps.Succeeds = false;
            PressPreviewKey(window, Key.Enter);
            Pump();
            Assert.True(window.IsVisible);
            Assert.Equal("brave", input.Text);
            Assert.Equal("That application could not be opened.", results.Notice);
            Assert.Equal(Visibility.Visible, Named<TextBlock>(window, "ResultsNotice").Visibility);

            // Typing on takes the notice away. Enter on the highlighted result then opens it, and the bar goes with what was typed.
            input.Text = "brav";
            Pump();
            Assert.Equal("", results.Notice);
            WaitUntil(() => results.SelectedItem is not null, "The best match was not highlighted.");
            kit.Apps.Succeeds = true;
            PressPreviewKey(window, Key.Enter);
            Assert.Equal(["brave", "brave"], kit.Apps.Launched);
            Advance(frames[0], 400);
            WaitUntil(() => !window.IsVisible, "The bar did not go after the application was opened.");
            Assert.Equal("", bar.Query);
            Assert.Null(results.Scope);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void AQuestionShowsWhereItGoesAtTheRightOfTheFieldAndNothingIsListedForIt() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var (kit, assistant, results) = CreateBraveBar(frames);
        var (window, bar, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var input = Named<PromptInputControl>(window, "PromptInput");

            input.Text = "what is the capital of France";
            Pump();
            Assert.Equal("Ask", bar.RouteLabel);
            Assert.Equal("Ask", input.TrailingLabel);
            Assert.True(input.HasTrailingLabel);
            Assert.False(input.HasTrailingContent);
            Assert.Equal(Visibility.Visible, QuickPart<Border>(input, "PART_TrailingLabel").Visibility);
            Assert.False(window.IsResultsShown);

            // A name is just searched, and says nothing.
            input.Text = "brave";
            WaitUntil(() => results.HasResults, "Nothing was listed for the name.");
            Pump();
            Assert.Equal("", bar.RouteLabel);
            Assert.Equal(Visibility.Collapsed, QuickPart<Border>(input, "PART_TrailingLabel").Visibility);

            // Ctrl+Enter asks whatever the results say.
            Assert.True(input.HandleEnter(ModifierKeys.Control, isRepeat: false));
            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
        }
        finally { window.Close(); }
    }));
}
