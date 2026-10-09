using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Routing;
using Assistant.Tools.Calculator;
using Assistant.UI.Animation;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- What the bar answers while the user types (PROJECT_SPEC §4.1), as the reference recordings show it: a sum is worked out in the bar with
    // ---- each key, and the best match is beside the text at once while the whole list waits for the typing to pause.

    private static SearchOrAskViewModel CalculatingBar(FakeClipboard? clipboard = null, SearchResultsViewModel? results = null) =>
        new(new VoiceInputViewModel(new FakeMicrophone()), results: results, router: new QueryRouter(calculatorAvailable: () => true),
            calculator: new ArithmeticCalculator(), clipboard: clipboard);

    [Fact]
    public void ASumIsAnsweredWithEachKeyAndItsAnswerStaysUpWhileTheSumGoesOn()
    {
        var clipboard = new FakeClipboard();
        var bar = CalculatingBar(clipboard);
        var changes = 0;
        bar.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(SearchOrAskViewModel.HasCalculation) ? 1 : 0;

        // A number alone, or a sum that is not finished, is not a sum yet.
        bar.Query = "9";
        Assert.False(bar.HasCalculation);
        bar.Query = "9+";
        Assert.False(bar.HasCalculation);

        // The key that makes it a sum answers it, on the spot.
        bar.Query = "9+1";
        Assert.True(bar.HasCalculation);
        Assert.Equal(("9+1 =", "10"), (bar.Calculation.Caption, bar.Calculation.Value));
        bar.Query = "9+10";
        Assert.Equal(("9+10 =", "19"), (bar.Calculation.Caption, bar.Calculation.Value));
        Assert.Equal(1, changes);

        // A sign typed after it keeps the last answer up until the next number makes a new one: the card does not blink between two keys.
        bar.Query = "9+10-";
        Assert.True(bar.HasCalculation);
        Assert.Equal("19", bar.Calculation.Value);
        bar.Query = "9+10-4";
        Assert.Equal(("9+10-4 =", "15"), (bar.Calculation.Caption, bar.Calculation.Value));
        Assert.Equal(1, changes);

        // The button copies the value, and only the value.
        Assert.True(bar.Calculation.CopyCommand.CanExecute(null));
        bar.Calculation.CopyCommand.Execute(null);
        Assert.Equal(["15"], clipboard.Copied);

        // Words end it; and the same card is used for the next sum.
        var card = bar.Calculation;
        bar.Query = "9+10-4 apples";
        Assert.False(bar.HasCalculation);
        Assert.Equal(2, changes);
        bar.Query = "what is 100 + 11";
        Assert.True(bar.HasCalculation);
        Assert.Same(card, bar.Calculation);
        Assert.Equal(("what is 100 + 11 =", "111"), (bar.Calculation.Caption, bar.Calculation.Value));

        // A sum with no value shows none.
        bar.Query = "";
        bar.Query = "1/0";
        Assert.False(bar.HasCalculation);
    }

    [Fact]
    public void WithoutACalculatorNothingIsWorkedOutInTheBar()
    {
        var bar = CreateBarModel(router: new QueryRouter(calculatorAvailable: () => true));
        bar.Query = "9+10";
        Assert.False(bar.HasCalculation);

        // And a router that has no calculator to route to never asks one.
        var unrouted = new SearchOrAskViewModel(
            new VoiceInputViewModel(new FakeMicrophone()), router: new QueryRouter(), calculator: new ArithmeticCalculator());
        unrouted.Query = "9+10";
        Assert.False(unrouted.HasCalculation);
    }

    [Fact]
    public void TheBarGrowsToHoldTheAnswerToASumAndGivesTheRoomBackWhenItEnds() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var bar = CalculatingBar(new FakeClipboard());
        var conversation = CreateConversationModel();
        var window = new AssistantWindow(
            bar, conversation, new FakeBackdropFactory(), new FakePlacement(),
            () =>
            {
                var fake = new FakeFrames();
                frames.Add(fake);
                return fake;
            },
            () => true)
        {
            Left = -10000, Top = -10000, Opacity = 0,
        };
        var controller = new AssistantWindowStateController(window, bar, conversation);
        try
        {
            controller.Invoke();
            Advance(frames[0], 300);
            var input = Named<PromptInputControl>(window, "PromptInput");
            var card = GridNamed(window, "CalculationCard");
            Assert.Equal(new Rect(45, 28, 520, 91), GlassRegion(window));
            Assert.Equal(Visibility.Collapsed, card.Visibility);

            // The sum is answered with the key; the bar then grows to hold the answer in 0.15 s, easing, where it hangs.
            input.Text = "9+1";
            Assert.True(bar.HasCalculation);
            var growth = frames[2];
            Assert.True(growth.Running);
            growth.Tick(T0);
            var heights = new List<double> { GlassRegion(window).Height };
            while (growth.Running)
            {
                growth.Tick(growth.Last + TimeSpan.FromMilliseconds(10));
                var glass = GlassRegion(window);
                heights.Add(glass.Height);
                Assert.Equal((45.0, 28.0, 520.0), (glass.Left, glass.Top, glass.Width));
                Assert.InRange(heights.Count, 1, 60);
            }

            Assert.Equal(heights.Order(), heights);
            Assert.InRange((heights.Count - 1) * 10, 140, 160);
            Assert.Equal(new Rect(45, 28, 520, 170), GlassRegion(window));
            Assert.Equal(1, window.CalculationProgress);
            window.UpdateLayout();
            Assert.Equal(28 + 170 + 64, window.ActualHeight, 1);

            // The card: under the field, 20 in from the bar's sides, with the sum, its value and the copy button.
            Assert.Equal((Visibility.Visible, 1.0), (card.Visibility, card.Opacity));
            var bounds = BoundsIn(GridNamed(window, "SurfaceHost"), card);
            Assert.Equal(new Rect(45 + 20, 28 + 91, 480, 60), bounds);
            Assert.Equal("9+1 =", Named<TextBlock>(window, "CalculationCaption").Text);
            Assert.Equal("10", Named<TextBlock>(window, "CalculationValue").Text);
            RenderGlass(window, "bar-calculation.png", 2);

            // The next key changes the answer where it stands: nothing moves.
            input.Text = "9+10";
            Assert.False(growth.Running);
            Assert.Equal("19", Named<TextBlock>(window, "CalculationValue").Text);
            Assert.Equal(new Rect(45, 28, 520, 170), GlassRegion(window));

            // Put away and opened again, the bar is as it was: the sum selected, its answer under it.
            window.Dismiss();
            Advance(frames[0], 300);
            Assert.False(window.IsVisible);
            controller.Invoke();
            Advance(frames[0], 300);
            Assert.Equal(new Rect(45, 28, 520, 170), GlassRegion(window));
            Assert.Equal("9+10", input.SelectedText);

            // When what is typed stops being a sum, the bar gives the room back at once (0.07 s).
            input.Text = "hello";
            Assert.False(bar.HasCalculation);
            growth.Tick(growth.Last + TimeSpan.FromSeconds(1));
            var steps = 0;
            while (growth.Running)
            {
                growth.Tick(growth.Last + TimeSpan.FromMilliseconds(10));
                Assert.InRange(++steps, 1, 30);
            }

            Assert.InRange(steps * 10, 60, 90);
            Assert.Equal(new Rect(45, 28, 520, 91), GlassRegion(window));
            Assert.Equal(Visibility.Collapsed, card.Visibility);
            window.UpdateLayout();
            Assert.Equal(28 + 91 + 64, window.ActualHeight, 1);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheBestMatchIsBesideTheTextAtOnceAndTheListFollowsWhenTheTypingPauses() => RunSta(() =>
    {
        var clock = new ManualTime();
        var source = new ScriptedQuickSource();
        var results = new SearchResultsViewModel(quickSource: source, revealDelay: SearchResultsViewModel.ReferenceRevealDelay, clock: clock);
        var bar = CreateBarModel(results: results, router: new QueryRouter());
        Assert.Equal(TimeSpan.FromMilliseconds(650), SearchResultsViewModel.ReferenceRevealDelay);

        // The first results: the best match is highlighted and named beside the text, and Enter opens it; the list is not shown yet.
        var word = QuickRow("Microsoft Word");
        bar.Query = "word";
        WaitUntil(() => source.Asked.Count == 1, "The source was not asked.");
        source.Asked[0].Report(Snapshot(word, word, QuickRow("words.txt")));
        WaitUntil(() => results.HasResults, "The answer was not shown.");
        Pump();
        Assert.Same(word, results.SelectedItem);
        Assert.Equal(" " + EmDash + " Microsoft Word", bar.Completion);
        Assert.False(results.IsRevealed);
        Assert.False(results.ShowsPanel);
        Assert.False(bar.IsResultsVisible);

        // Typing on starts the pause again.
        clock.Advance(TimeSpan.FromMilliseconds(600));
        Pump();
        Assert.False(bar.IsResultsVisible);
        bar.Query = "words";
        WaitUntil(() => source.Asked.Count == 2, "The newer query was not asked.");
        source.Asked[1].Report(Snapshot(null, QuickRow("words.txt")));
        WaitUntil(() => results.Items.Count == 1, "The newer answer was not shown.");
        clock.Advance(TimeSpan.FromMilliseconds(600));
        Pump();
        Assert.False(bar.IsResultsVisible);

        // 0.65 s after the last key the list shows.
        var shown = new List<string?>();
        bar.PropertyChanged += (_, e) => shown.Add(e.PropertyName);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Pump();
        Assert.True(results.IsRevealed);
        Assert.True(bar.IsResultsVisible);
        Assert.Contains(nameof(SearchOrAskViewModel.IsResultsVisible), shown);

        // Shown, it follows the typing at once, as the reference's does.
        bar.Query = "wor";
        WaitUntil(() => source.Asked.Count == 3, "The newer query was not asked.");
        source.Asked[2].Report(Snapshot(null, QuickRow("Microsoft Word")));
        WaitUntil(() => results.Items.Count == 1 && results.Items[0].Title == "Microsoft Word", "The newer answer was not shown.");
        Pump();
        Assert.True(bar.IsResultsVisible);

        // An empty field starts again: the next text waits for its pause once more.
        bar.Query = "";
        Assert.False(results.IsRevealed);
        bar.Query = "f";
        WaitUntil(() => source.Asked.Count == 4, "The newer query was not asked.");
        source.Asked[3].Report(Snapshot(null, QuickRow("Fortnite"), QuickRow("Files")));
        WaitUntil(() => results.Items.Count == 2, "The answer was not shown.");
        Pump();
        Assert.False(bar.IsResultsVisible);

        // The arrow keys do not wait: moving through the list shows it.
        Assert.True(results.HandleKey(Key.Down, ModifierKeys.None));
        Assert.True(results.IsRevealed);
        Assert.True(bar.IsResultsVisible);
        Assert.Equal("Fortnite", results.SelectedItem!.Title);
    });

    [Fact]
    public void AListThatIsNarrowedToAKindOrHasNoDelayShowsWithItsFirstResults() => RunSta(() =>
    {
        // Without a delay (the default), the list is there with the first answer, as it always was.
        var plain = new ScriptedQuickSource();
        var immediate = new SearchResultsViewModel(quickSource: plain);
        Assert.True(immediate.IsRevealed);
        immediate.Update("a");
        WaitUntil(() => plain.Asked.Count == 1, "The source was not asked.");
        plain.Asked[0].Report(Snapshot(null, QuickRow("One")));
        WaitUntil(() => immediate.HasResults, "The answer was not shown.");
        Assert.True(immediate.ShowsPanel);

        // Browsing a kind is asking for a list: it shows at once, pause or no pause.
        var source = new ScriptedQuickSource();
        var results = new SearchResultsViewModel(quickSource: source, revealDelay: SearchResultsViewModel.ReferenceRevealDelay, clock: new ManualTime());
        results.SetScope(QuickSearchResultType.Applications);
        Assert.True(results.IsRevealed);
        Assert.True(results.ShowsPanel);
    });
}
