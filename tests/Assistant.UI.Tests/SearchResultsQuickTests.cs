using System.Windows.Input;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Routing;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The list under the bar as the providers answer (PROJECT_SPEC §4.1): progressive answers, narrowing, a result's other actions. ----

    // A source of results that the test answers by hand, as many times as it likes for each search.
    private sealed class ScriptedQuickSource : IQuickSearchResultsSource
    {
        public List<Ask> Asked { get; } = [];

        public Task SearchAsync(
            string query, QuickSearchResultType? scope, Action<SearchResultsSnapshot> report, CancellationToken cancellationToken)
        {
            var ask = new Ask(query, scope, report, cancellationToken);
            lock (Asked)
            {
                Asked.Add(ask);
            }

            return ask.Finished.Task;
        }

        public sealed record Ask(string Query, QuickSearchResultType? Scope, Action<SearchResultsSnapshot> Report, CancellationToken Token)
        {
            public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private static SearchResultViewModel QuickRow(
        string title, string? key = null, bool alternates = false, RecordingCommand? run = null, RecordingCommand? alternate = null) =>
        new(SearchResultKind.App, title, run ?? new RecordingCommand(),
            actionHint: new SearchResultActionHint("Open", "enter"),
            alternates: alternates
                ? [new SearchResultAlternate("Show", alternate ?? new RecordingCommand(), "Shift+Enter"), new SearchResultAlternate("Copy path", new RecordingCommand())]
                : null)
        {
            Key = key ?? "key:" + title,
        };

    private static SearchResultsSnapshot Snapshot(SearchResultViewModel? highlighted, params SearchResultViewModel[] rows) =>
        new([new SearchResultSectionViewModel(null, rows)]) { Highlighted = highlighted };

    [Fact]
    public void AnswersAreShownAsTheyComeAndOnlyTheNewestQuerysAreKept() => RunSta(() =>
    {
        var source = new ScriptedQuickSource();
        var results = new SearchResultsViewModel(quickSource: source);

        // The source is asked at once: it has pauses of its own.
        results.Update("bra");
        WaitUntil(() => source.Asked.Count == 1, "The source was not asked.");
        Assert.True(results.IsSearching);
        Assert.Equal(("bra", (QuickSearchResultType?)null), (source.Asked[0].Query, source.Asked[0].Scope));

        // The first thing it finds is listed while it carries on, and what it finds next replaces it.
        var browser = QuickRow("Brave Browser");
        source.Asked[0].Report(Snapshot(browser, browser));
        WaitUntil(() => results.HasResults, "The first answer was not shown.");
        Pump();
        Assert.True(results.IsSearching);
        Assert.Same(browser, results.SelectedItem);
        source.Asked[0].Report(Snapshot(browser, browser, QuickRow("Notes")));
        WaitUntil(() => results.Items.Count == 2, "The next answer was not shown.");
        Pump();

        // Typing on cancels the search, and what it reports after that is never shown.
        results.Update("brav");
        WaitUntil(() => source.Asked.Count == 2, "The newer query was not asked.");
        Assert.True(source.Asked[0].Token.IsCancellationRequested);
        source.Asked[0].Report(Snapshot(null, QuickRow("stale")));
        source.Asked[1].Report(Snapshot(null, QuickRow("fresh")));
        source.Asked[1].Finished.SetResult();
        WaitUntil(() => !results.IsSearching, "The search did not end.");
        Pump();
        Assert.Equal(["fresh"], results.Items.Select(item => item.Title));
        Assert.Null(results.SelectedItem);
    });

    [Fact]
    public void TheHighlightTheSourceGivesStaysUntilTheUserMovesAndThenStaysWhereTheyPutIt() => RunSta(() =>
    {
        var source = new ScriptedQuickSource();
        var results = new SearchResultsViewModel(quickSource: source);
        results.Update("a");
        WaitUntil(() => source.Asked.Count == 1, "The source was not asked.");
        var (one, two, three) = (QuickRow("One"), QuickRow("Two"), QuickRow("Three"));
        source.Asked[0].Report(Snapshot(one, one, two, three));
        WaitUntil(() => results.SelectedItem is not null, "The source's highlight was not applied.");
        Pump();
        Assert.Same(one, results.SelectedItem);

        // A better answer moves the highlight to what is best now, since the user has not moved it.
        var best = QuickRow("Best");
        source.Asked[0].Report(Snapshot(best, best, QuickRow("One"), QuickRow("Two"), QuickRow("Three")));
        WaitUntil(() => results.SelectedItem?.Title == "Best", "The better answer did not take the highlight.");
        Pump();

        // Once the user moves the highlight, later answers keep it on the same result, found again by its key.
        results.MoveSelection(2);
        Assert.Equal("Two", results.SelectedItem!.Title);
        source.Asked[0].Report(Snapshot(best, QuickRow("Zero"), best, QuickRow("One"), QuickRow("Two"), QuickRow("Three")));
        WaitUntil(() => results.Items.Count == 5, "The answer was not shown.");
        Pump();
        Assert.Equal("Two", results.SelectedItem!.Title);

        // A result that is gone leaves nothing highlighted rather than another one.
        source.Asked[0].Report(Snapshot(best, best, QuickRow("One")));
        WaitUntil(() => results.Items.Count == 2, "The answer was not shown.");
        Pump();
        Assert.Null(results.SelectedItem);
    });

    [Fact]
    public void AFailingSourceListsNothingAndNeverInterruptsTyping() => RunSta(() =>
    {
        var source = new ScriptedQuickSource();
        var results = new SearchResultsViewModel(quickSource: source);
        results.Update("a");
        WaitUntil(() => source.Asked.Count == 1, "The source was not asked.");
        source.Asked[0].Report(Snapshot(null, QuickRow("Listed")));
        WaitUntil(() => results.HasResults, "The answer was not shown.");
        results.Update("ab");
        WaitUntil(() => source.Asked.Count == 2, "The next query was not asked.");

        source.Asked[1].Finished.SetException(new InvalidOperationException("the index fell over"));

        WaitUntil(() => !results.HasResults, "A failed search did not clear the list.");
        Pump();
        Assert.False(results.IsSearching);
    });

    [Fact]
    public void ChoosingAKindNarrowsTheListAndTheChipsAndTheShortcutsDoItAlike() => RunSta(() =>
    {
        var source = new ScriptedQuickSource();
        var results = new SearchResultsViewModel(quickSource: source);
        Assert.Equal(["Applications", "Files", "Actions", "Clipboard"], results.Chips.Select(chip => chip.Title));
        Assert.Equal(["Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4"], results.Chips.Select(chip => chip.ShortcutText));
        Assert.False(results.ShowsPanel);

        // With nothing typed, a kind that is chosen is browsed: the source is asked with an empty query and the kind.
        results.SetScope(QuickSearchResultType.Files);
        WaitUntil(() => source.Asked.Count == 1, "The kind was not browsed.");
        Assert.Equal(("", (QuickSearchResultType?)QuickSearchResultType.Files), (source.Asked[0].Query, source.Asked[0].Scope));
        Assert.Equal(QuickSearchResultType.Files, results.Scope);
        Assert.Equal([false, true, false, false], results.Chips.Select(chip => chip.IsActive));
        Assert.True(results.ShowsPanel, "The chips stay while the kind has nothing to show.");
        Assert.False(results.HasResults);

        // Typing narrows what is browsed; choosing the same kind again, or its chip, lifts the narrowing.
        results.Update("budget");
        WaitUntil(() => source.Asked.Count == 2, "The query was not asked.");
        Assert.Equal(QuickSearchResultType.Files, source.Asked[1].Scope);
        Assert.True(results.Chips[1].Command.CanExecute(null));
        results.Chips[1].Command.Execute(null);
        WaitUntil(() => source.Asked.Count == 3, "The list was not looked up again.");
        Assert.Null(results.Scope);
        Assert.Null(source.Asked[2].Scope);
        Assert.Equal("budget", source.Asked[2].Query);
        Assert.All(results.Chips, chip => Assert.False(chip.IsActive));

        // Ctrl+1 to Ctrl+4 are the chips' keys while the panel shows (never without Ctrl, and not for another modifier).
        source.Asked[2].Report(Snapshot(null, QuickRow("Budget.xlsx")));
        WaitUntil(() => results.HasResults, "The answer was not shown.");
        Pump();
        Assert.False(results.HandleKey(Key.D3, ModifierKeys.None));
        Assert.False(results.HandleKey(Key.D3, ModifierKeys.Alt));
        Assert.True(results.HandleKey(Key.D3, ModifierKeys.Control));
        Assert.Equal(QuickSearchResultType.Actions, results.Scope);
        Assert.True(results.HandleKey(Key.D3, ModifierKeys.Control));
        Assert.Null(results.Scope);
        Assert.True(results.HandleKey(Key.NumPad4, ModifierKeys.Control));
        Assert.Equal(QuickSearchResultType.Clipboard, results.Scope);
        Assert.False(results.HandleKey(Key.D7, ModifierKeys.Control));
    });

    [Fact]
    public void WhatTheNarrowedListSaysWhenItIsEmptyIsOnlyThereWhileNothingIsListed() => RunSta(() =>
    {
        var source = new ScriptedQuickSource();
        var results = new SearchResultsViewModel(quickSource: source);
        results.SetScope(QuickSearchResultType.Files);
        WaitUntil(() => source.Asked.Count == 1, "The kind was not browsed.");

        source.Asked[0].Report(new SearchResultsSnapshot([]) { EmptyMessage = "No recent files" });
        WaitUntil(() => results.EmptyMessage.Length > 0, "The message was not shown.");
        Pump();
        Assert.Equal("No recent files", results.EmptyMessage);
        Assert.True(results.HasEmptyMessage);

        source.Asked[0].Report(Snapshot(null, QuickRow("Report.docx")));
        WaitUntil(() => results.HasResults, "The result was not shown.");
        Pump();
        Assert.Equal("", results.EmptyMessage);
        Assert.False(results.HasEmptyMessage);
    });

    [Fact]
    public void TabListsWhatTheHighlightedResultCanDoAndEnterRunsOneAndTabOrEscapeGoesBack() => RunSta(() =>
    {
        var (run, show) = (new RecordingCommand(), new RecordingCommand());
        var browser = QuickRow("Brave Browser", alternates: true, run: run, alternate: show);
        var plain = QuickRow("Notes");
        var results = new SearchResultsViewModel(new FakeResultsSource([new SearchResultSectionViewModel(null, [browser, plain])]));
        results.Update("sample");
        results.MoveSelection(1);
        Assert.Same(browser, results.SelectedItem);

        // Tab lists them in place of the results, the first highlighted, each with the key it has on the row.
        Assert.True(results.HandleKey(Key.Tab, ModifierKeys.None));
        Assert.True(results.IsShowingAlternates);
        Assert.Equal(["Show", "Copy path"], results.Items.Select(item => item.Title));
        Assert.Equal("Actions for Brave Browser", results.Sections[0].Title);
        Assert.Equal("Show", results.SelectedItem!.Title);
        Assert.Equal(("", "Shift+Enter"), (results.Items[0].ActionHint!.Text ?? "", results.Items[0].ActionHint!.Key));
        Assert.Null(results.Items[1].ActionHint);

        // Enter runs the highlighted one, and not the result's own; the arrows move through them.
        results.HandleKey(Key.Down, ModifierKeys.None);
        results.HandleKey(Key.Up, ModifierKeys.None);
        Assert.True(results.HandleKey(Key.Enter, ModifierKeys.None));
        Assert.Equal((1, 0), (show.Count, run.Count));

        // Tab goes back, with the result highlighted again; so does Esc.
        Assert.True(results.HandleKey(Key.Tab, ModifierKeys.None));
        Assert.False(results.IsShowingAlternates);
        Assert.Equal(["Brave Browser", "Notes"], results.Items.Select(item => item.Title));
        Assert.Same(browser, results.SelectedItem);
        results.HandleKey(Key.Tab, ModifierKeys.None);
        Assert.True(results.HandleEscape());
        Assert.False(results.IsShowingAlternates);
        Assert.Same(browser, results.SelectedItem);
        Assert.False(results.HandleEscape());

        // A result with nothing else to do leaves Tab swallowed, with the list as it is (it never moves the focus to the microphone).
        results.MoveSelection(1);
        Assert.Same(plain, results.SelectedItem);
        Assert.True(results.HandleKey(Key.Tab, ModifierKeys.None));
        Assert.False(results.IsShowingAlternates);
        Assert.False(results.ShowAlternates(plain));

        // With none highlighted, Tab highlights the first result that has something to offer and lists it.
        results.ClearSelection();
        Assert.True(results.HandleKey(Key.Tab, ModifierKeys.None));
        Assert.True(results.IsShowingAlternates);
        Assert.Equal("Actions for Brave Browser", results.Sections[0].Title);
    });

    [Fact]
    public void TypingLeavesTheListOfActionsAndAnAnswerThatCameWhileItShowedIsListedWhenItGoesAway() => RunSta(() =>
    {
        var source = new ScriptedQuickSource();
        var results = new SearchResultsViewModel(quickSource: source);
        results.Update("a");
        WaitUntil(() => source.Asked.Count == 1, "The source was not asked.");
        var browser = QuickRow("Brave Browser", alternates: true);
        source.Asked[0].Report(Snapshot(browser, browser));
        WaitUntil(() => results.SelectedItem is not null, "The highlight was not applied.");
        Pump();
        results.HandleKey(Key.Tab, ModifierKeys.None);
        Assert.True(results.IsShowingAlternates);

        // What the source finds meanwhile waits: the actions are not taken away from under the user.
        var later = QuickRow("Brave Browser", alternates: true);
        source.Asked[0].Report(Snapshot(later, later, QuickRow("Notes")));
        Pump();
        Assert.True(results.IsShowingAlternates);
        Assert.Equal(["Show", "Copy path"], results.Items.Select(item => item.Title));

        // Going back lists the newer answer, keeping the same result highlighted.
        results.CloseAlternates();
        Assert.Equal(["Brave Browser", "Notes"], results.Items.Select(item => item.Title));
        Assert.Equal("Brave Browser", results.SelectedItem!.Title);

        // Typing on while the actions show gives them up for the new query's results.
        results.HandleKey(Key.Tab, ModifierKeys.None);
        Assert.True(results.IsShowingAlternates);
        results.Update("ab");
        Assert.False(results.IsShowingAlternates);
    });

    [Fact]
    public void ATabPressedBeforeTheResultsCameListsTheActionsOfTheFirstResultThatHasThem() => RunSta(() =>
    {
        var source = new ScriptedQuickSource();
        var results = new SearchResultsViewModel(quickSource: source);
        results.Update("brave");
        WaitUntil(() => source.Asked.Count == 1, "The source was not asked.");

        // Typed ahead, as a person who knows what they want does: it waits for the results.
        Assert.True(results.HandleKey(Key.Tab, ModifierKeys.None));
        Assert.False(results.IsShowingAlternates);
        source.Asked[0].Report(Snapshot(null, QuickRow("Plain"), QuickRow("Brave Browser", alternates: true)));
        source.Asked[0].Finished.SetResult();
        WaitUntil(() => results.IsShowingAlternates, "The waiting Tab did not list the actions.");
        Pump();
        Assert.Equal("Actions for Brave Browser", results.Sections[0].Title);
    });

    [Fact]
    public void WhatWasChosenCanBeToldToTheUserAboveTheResultsUntilTheyTypeAgain() => RunSta(() =>
    {
        var results = new SearchResultsViewModel(new FakeResultsSource([new SearchResultSectionViewModel(null, [QuickRow("Sample")])]));
        var changes = new List<string?>();
        results.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        results.ShowNotice("That could not be opened.");

        Assert.Equal("That could not be opened.", results.Notice);
        Assert.True(results.HasNotice);
        Assert.Contains(nameof(SearchResultsViewModel.Notice), changes);
        results.Update("sample");
        Assert.Equal("", results.Notice);
        Assert.False(results.HasNotice);
        results.ShowNotice(null!);
        Assert.Equal("", results.Notice);
    });

    [Fact]
    public void KeysThatEditTheTextAreNeverTakenWhileAListShowsAndARowIsHighlighted() => RunSta(() =>
    {
        var browser = QuickRow("Brave Browser", alternates: true);
        var results = new SearchResultsViewModel(new FakeResultsSource([new SearchResultSectionViewModel(null, [browser, QuickRow("Notes")])]));
        results.Update("sample");
        results.MoveSelection(1);

        // Caret movement, selection, deletion and the clipboard shortcuts stay the editor's.
        foreach (var key in new[]
        {
            Key.Left, Key.Right, Key.Home, Key.End, Key.Back, Key.Delete, Key.PageUp, Key.PageDown, Key.Space, Key.A, Key.Z, Key.OemPeriod,
        })
        {
            Assert.False(results.HandleKey(key, ModifierKeys.None), key.ToString());
        }

        foreach (var key in new[] { Key.A, Key.C, Key.V, Key.X, Key.Z, Key.Y, Key.Left, Key.Right, Key.Back, Key.Delete })
        {
            Assert.False(results.HandleKey(key, ModifierKeys.Control), "Ctrl+" + key);
        }

        foreach (var key in new[] { Key.Left, Key.Right, Key.Home, Key.End })
        {
            Assert.False(results.HandleKey(key, ModifierKeys.Shift), "Shift+" + key);
            Assert.False(results.HandleKey(key, ModifierKeys.Control | ModifierKeys.Shift), "Ctrl+Shift+" + key);
        }

        // The keys that are the results' are only these: the arrows, Enter, Tab, and a row's own keys.
        Assert.False(results.HandleKey(Key.Enter, ModifierKeys.Control));
        Assert.False(results.HandleKey(Key.Tab, ModifierKeys.Shift));
        Assert.False(results.HandleKey(Key.Escape, ModifierKeys.None));
    });

    // ---- The bar around the list: what it adds to the typed text, where the words go, and Esc. ----

    [Fact]
    public void TheBarDrawsTheRestOfTheHighlightedNameAfterWhatWasTyped() => RunSta(() =>
    {
        var browser = QuickRow("Brave Browser");
        string Completion(string typed, SearchResultViewModel? row) => SearchOrAskViewModel.CompletionOf(typed, row);

        Assert.Equal(" Browser " + EmDash + " Open", Completion("brave", browser));
        Assert.Equal(" " + EmDash + " Open", Completion("Brave Browser", browser));
        Assert.Equal("Browser " + EmDash + " Open", Completion("brave ", browser));
        Assert.Equal(" Browser " + EmDash + " Open", Completion("BRAVE", browser));

        // A highlighted result whose name does not begin with the text is named whole after a dash, as the reference does
        // ("word — Microsoft Word"): what Enter opens is always said.
        Assert.Equal(" " + EmDash + " Brave Browser", Completion("browser", browser));
        Assert.Equal(" " + EmDash + " Brave Browser", Completion("brave browsers", browser));

        // Nothing is added when no result is highlighted or nothing is typed.
        Assert.Equal("", Completion("brave", null));
        Assert.Equal("", Completion("", browser));

        // A result that says nothing about what Enter does adds only the rest of its name.
        var bare = new SearchResultViewModel(SearchResultKind.App, "Brave Browser", new RecordingCommand());
        Assert.Equal(" Browser", Completion("brave", bare));
    });

    [Fact]
    public void TheBarFollowsTheHighlightWithItsCompletionAndIconAndSaysWhereTheWordsGo() => RunSta(() =>
    {
        var browser = QuickRow("Brave Browser");
        var results = new SearchResultsViewModel(new FakeResultsSource([new SearchResultSectionViewModel(null, [browser, QuickRow("Notes")])]));
        var bar = CreateBarModel(results: results, router: new QueryRouter());
        var changed = new List<string?>();
        bar.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        bar.Query = "sample";
        Assert.Equal("", bar.Completion);
        Assert.False(bar.HasTrailingIcon);
        Assert.Equal(QueryRouteKind.InstantSearch, bar.Route.Kind);

        // Highlighting a result gives the field its completion and its icon.
        results.MoveSelection(1);
        Assert.Contains(nameof(SearchOrAskViewModel.Completion), changed);
        Assert.Same(browser.Icon, bar.TrailingIcon);
        Assert.True(bar.HasTrailingIcon);

        // A question is routed to the model and the words say so, in the place where the icon would be; a name says nothing.
        results.ClearSelection();
        bar.Query = "what is the capital of France";
        Assert.Equal(QueryRouteKind.DirectAnswer, bar.Route.Kind);
        Assert.Equal("Ask", bar.RouteLabel);
        bar.Query = "what is the pdf I downloaded about my files";
        Assert.Equal("Ask with tools", bar.RouteLabel);
        bar.Query = "brave";
        Assert.Equal("", bar.RouteLabel);
        bar.Query = "";
        Assert.Equal("", bar.RouteLabel);
        Assert.Equal(QueryRouteReason.Empty, bar.Route.Reason);
    });

    [Fact]
    public void EscapeBacksOutOneStepAtATimeFromTheActionsToTheTextToTheNarrowingToTheBar() => RunSta(() =>
    {
        var browser = QuickRow("Brave Browser", alternates: true);
        var source = new ScriptedQuickSource();
        var results = new SearchResultsViewModel(quickSource: source);
        var bar = CreateBarModel(results: results);
        results.SetScope(QuickSearchResultType.Applications);
        WaitUntil(() => source.Asked.Count == 1, "The kind was not browsed.");
        source.Asked[0].Report(Snapshot(browser, browser));
        bar.Query = "brave";
        WaitUntil(() => source.Asked.Count == 2, "The query was not asked.");
        source.Asked[1].Report(Snapshot(browser, browser));
        WaitUntil(() => results.SelectedItem is not null, "The highlight was not applied.");
        Pump();
        results.HandleKey(Key.Tab, ModifierKeys.None);
        Assert.True(results.IsShowingAlternates);

        Assert.False(bar.HandleEscape());
        Assert.False(results.IsShowingAlternates);
        Assert.Equal("brave", bar.Query);
        Assert.False(bar.HandleEscape());
        Assert.Equal("", bar.Query);
        Assert.Equal(QuickSearchResultType.Applications, results.Scope);
        Assert.False(bar.HandleEscape());
        Assert.Null(results.Scope);
        Assert.True(bar.HandleEscape());
    });

    [Fact]
    public void AKindChosenFromTheCategoriesTakesTheCategoryPanelAwayAndGivingItUpBringsItBack() => RunSta(() =>
    {
        var (launcher, _) = CreateLauncher();
        var results = new SearchResultsViewModel(quickSource: new ScriptedQuickSource());
        var bar = CreateBarModel(launcher: launcher, results: results);
        Assert.True(bar.IsLauncherVisible);
        Assert.False(bar.IsResultsVisible);

        results.SetScope(QuickSearchResultType.Actions);

        Assert.False(bar.IsLauncherVisible);
        Assert.True(bar.IsResultsVisible);
        results.SetScope(null);
        Assert.True(bar.IsLauncherVisible);
        Assert.False(bar.IsResultsVisible);
    });

    [Fact]
    public void TheLauncherCategoriesNarrowTheResultsWithTheirCommandsAndShortcuts() => RunSta(() =>
    {
        var source = new ScriptedQuickSource();
        var results = new SearchResultsViewModel(quickSource: source);
        var launcher = new LauncherViewModel(Assistant.UI.Search.QuickSearchLauncherCommands.Create(results));

        Assert.Equal(["Applications", "Files", "Actions", "Clipboard"], launcher.Items.Select(item => item.Title));
        Assert.Equal(["Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4"], launcher.Items.Select(item => item.ShortcutText));

        // Ctrl+3 chooses Actions, from the categories as from the chips, and runs it as a click does.
        Assert.True(launcher.HandleKey(Key.D3, ModifierKeys.Control));
        Assert.Equal(QuickSearchResultType.Actions, results.Scope);
        launcher.Items[0].Activate();
        Assert.Equal(QuickSearchResultType.Applications, results.Scope);
        Assert.Throws<ArgumentNullException>(() => Assistant.UI.Search.QuickSearchLauncherCommands.Create(null!));
    });
}
