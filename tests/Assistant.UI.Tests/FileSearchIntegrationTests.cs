using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.QuickSearch;
using Assistant.Core.Settings;
using Assistant.Search;
using Assistant.Search.Files;
using Assistant.Search.Planning;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Messages;
using Assistant.UI.Search;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Assistant.Windows.Shell;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- File search in the compact assistant (PROJECT_SPEC §4.1, §4.2, §4.7) -------------------------------------------

    private static readonly FileSearchQuery ScreenshotsQuery = new()
    {
        Filename = "Screenshot",
        Extensions = ImageFileTypes.Extensions,
        Types = [SearchResultItemType.File],
        Order = FileSearchOrder.ModifiedDescending,
        MaxResultsPerType = 5,
    };

    private static SearchResultItem FoundFile(string path, DateTimeOffset? modified = null) =>
        new(SearchResultItemType.File, System.IO.Path.GetFileName(path), path)
        {
            Extension = System.IO.Path.GetExtension(path).ToLowerInvariant(),
            ModifiedAt = modified,
        };

    private static SearchResultItem FoundFolder(string path) =>
        new(SearchResultItemType.Folder, System.IO.Path.GetFileName(path), path);

    private static SearchResultSectionViewModel TitledRows(params string[] titles) =>
        new(null, titles.Select(title => new SearchResultViewModel(SearchResultKind.File, title, new RecordingCommand())));

    // -- Results that take time: Windows Search under the bar as the user types. --

    [Fact]
    public void LiveResults_AreAskedForAfterThePause_AndOnlyTheNewestQueryIsShown() => RunSta(() =>
    {
        var live = new ScriptedAsyncResults();
        var results = new SearchResultsViewModel(liveSource: live, liveDelay: TimeSpan.Zero);

        results.Update("bud");
        WaitUntil(() => live.Asked.Count == 1, "The live source was not asked.");
        Assert.Equal("bud", live.Asked[0].Query);
        Assert.False(results.HasResults);

        // Typing on cancels the question that is on its way, and its late answer is never shown.
        results.Update("budg");
        WaitUntil(() => live.Asked.Count == 2, "The newer query was not asked.");
        Assert.True(live.Asked[0].Token.IsCancellationRequested);
        live.Asked[0].Answer.TrySetResult([TitledRows("stale")]);
        live.Asked[1].Answer.SetResult([TitledRows("fresh", "fresher")]);
        WaitUntil(() => results.HasResults, "The newest answer was not shown.");
        Pump();

        Assert.Equal(["fresh", "fresher"], results.Items.Select(item => item.Title));
        Assert.Null(results.SelectedItem);
    });

    [Fact]
    public void LiveResults_SamplesComeFirst_TheListStaysWhileTheNextIsOnItsWay_AndAFailureListsNothing() => RunSta(() =>
    {
        var samples = new FakeResultsSource([TitledRows("Sample Browser")]);
        var live = new ScriptedAsyncResults();
        var results = new SearchResultsViewModel(samples, live, TimeSpan.Zero);

        // What the sample source has is the answer, and the slow source is not asked.
        results.Update("sample");
        Assert.Equal(["Sample Browser"], results.Items.Select(item => item.Title));
        Assert.Empty(live.Asked);

        // Nothing sample for this: the list stays, unhighlighted, until what Windows Search finds replaces it.
        results.MoveSelection(1);
        results.Update("budget");
        WaitUntil(() => live.Asked.Count == 1, "The live source was not asked.");
        Assert.True(results.HasResults);
        Assert.Null(results.SelectedItem);
        live.Asked[0].Answer.SetResult([TitledRows("Budget.xlsx")]);
        WaitUntil(() => results.Items.Select(item => item.Title).SequenceEqual(["Budget.xlsx"]), "The answer was not shown.");

        // A search that finds nothing, or fails, lists nothing; typing is never interrupted.
        results.Update("budgets");
        WaitUntil(() => live.Asked.Count == 2, "The next query was not asked.");
        live.Asked[1].Answer.SetResult([]);
        WaitUntil(() => !results.HasResults, "An empty answer did not clear the list.");
        results.Update("travel");
        WaitUntil(() => live.Asked.Count == 3, "The next query was not asked.");
        live.Asked[2].Answer.SetResult([TitledRows("Trip.pdf")]);
        WaitUntil(() => results.HasResults, "The answer was not shown.");
        results.Update("travels");
        WaitUntil(() => live.Asked.Count == 4, "The next query was not asked.");
        live.Asked[3].Answer.SetException(new InvalidOperationException("the index fell over"));
        WaitUntil(() => !results.HasResults, "A failed search did not clear the list.");

        // Emptying the field clears the list and drops the answer that is still on its way.
        results.Update("abc");
        WaitUntil(() => live.Asked.Count == 5, "The next query was not asked.");
        results.Update("");
        Assert.True(live.Asked[4].Token.IsCancellationRequested);
        live.Asked[4].Answer.TrySetResult([TitledRows("late")]);
        Pump();
        Assert.False(results.HasResults);
    });

    [Fact]
    public void ARowsOwnKeys_AreTakenBeforeTheEditorSeesThem_AndEnterStillRunsTheRow() => RunSta(() =>
    {
        var (open, reveal, attach) = (new RecordingCommand(), new RecordingCommand(), new RecordingCommand());
        var image = new SearchResultViewModel(SearchResultKind.File, "a.png", open, actions:
        [
            new SearchResultAction(Key.Enter, ModifierKeys.Shift, "Show in Explorer", reveal),
            new SearchResultAction(Key.Tab, ModifierKeys.None, "Attach to conversation", attach),
        ]);
        var plain = new SearchResultViewModel(SearchResultKind.File, "b.txt", new RecordingCommand());
        var results = new SearchResultsViewModel(new FakeResultsSource([new SearchResultSectionViewModel(null, [image, plain])]));
        results.Update("sample");

        // With nothing highlighted the keys are the editor's, except Tab, which attaches the first row that can be attached.
        Assert.False(results.HandleKey(Key.Enter, ModifierKeys.Shift));
        Assert.Null(results.SelectedItem);
        Assert.True(results.HandleKey(Key.Tab, ModifierKeys.None));
        Assert.Equal((0, 1, 0), (reveal.Count, attach.Count, open.Count));
        Assert.False(results.HandleKey(Key.Tab, ModifierKeys.Shift));

        results.MoveSelection(1);
        Assert.Same(image, results.SelectedItem);
        Assert.True(results.HandleKey(Key.Enter, ModifierKeys.Shift));
        Assert.True(results.HandleKey(Key.Tab, ModifierKeys.None));
        Assert.Equal((1, 2, 0), (reveal.Count, attach.Count, open.Count));

        // Only the exact keys are the row's; Enter alone still runs the row itself.
        Assert.False(results.HandleKey(Key.Enter, ModifierKeys.Control));
        Assert.False(results.HandleKey(Key.Enter, ModifierKeys.Shift | ModifierKeys.Control));
        Assert.True(results.HandleKey(Key.Enter, ModifierKeys.None));
        Assert.Equal((1, 2, 1), (reveal.Count, attach.Count, open.Count));

        // An action that cannot run now does not run, but its key is still the row's.
        reveal.Enabled = false;
        Assert.True(results.HandleKey(Key.Enter, ModifierKeys.Shift));
        Assert.Equal(1, reveal.Count);

        // A row without such an action leaves Shift+Enter alone; Tab is still the results' (it never moves the focus to the
        // microphone while a list shows), and attaches nothing.
        results.MoveSelection(1);
        Assert.Same(plain, results.SelectedItem);
        Assert.True(results.HandleKey(Key.Tab, ModifierKeys.None));
        Assert.Equal(2, attach.Count);
        Assert.False(results.HandleKey(Key.Tab, ModifierKeys.Shift));
        Assert.False(results.HandleKey(Key.Enter, ModifierKeys.Shift));
    });

    [Fact]
    public void TabIsTheResultsWhileAListShowsOrIsOnItsWay_AndNeverLeavesTheEditorForTheMicrophone() => RunSta(() =>
    {
        var (first, second) = (new RecordingCommand(), new RecordingCommand());
        SearchResultViewModel Row(string title, RecordingCommand? attach = null) => new(SearchResultKind.File, title, new RecordingCommand(),
            actions: attach is null ? [] : [new SearchResultAction(Key.Tab, ModifierKeys.None, "Attach to conversation", attach)]);
        var live = new ScriptedAsyncResults();
        var results = new SearchResultsViewModel(null, live, TimeSpan.Zero);

        // Nothing is listed or on its way: the key is the editor's.
        Assert.False(results.HandleKey(Key.Tab, ModifierKeys.None));

        // A Tab pressed while the results are still on their way waits for them, as typing ahead does.
        results.Update("mile");
        WaitUntil(() => live.Asked.Count == 1, "The live source was not asked.");
        Assert.True(results.IsSearching);
        Assert.True(results.HandleKey(Key.Tab, ModifierKeys.None));
        Assert.Equal((0, 0), (first.Count, second.Count));
        live.Asked[0].Answer.SetResult([new SearchResultSectionViewModel(null, [Row("Milestone.js"), Row("Milestone.docx", first), Row("Other.docx", second)])]);
        WaitUntil(() => results.HasResults, "The answer was not shown.");
        Pump();
        Assert.False(results.IsSearching);
        Assert.Equal((1, 0), (first.Count, second.Count));

        // Typing on drops a Tab that was waiting: it was meant for the results of what was typed then.
        results.Update("milest");
        WaitUntil(() => live.Asked.Count == 2, "The next query was not asked.");
        Assert.True(results.HandleKey(Key.Tab, ModifierKeys.None));
        results.Update("milesto");
        WaitUntil(() => live.Asked.Count == 3, "The next query was not asked.");
        live.Asked[2].Answer.SetResult([new SearchResultSectionViewModel(null, [Row("Milestone.docx", first)])]);
        WaitUntil(() => results.Items.Count == 1, "The answer was not shown.");
        Pump();
        Assert.Equal(1, first.Count);

        // While the results of a newer query are on their way, the list on show is the older query's: Tab waits for the new one.
        results.Update("milestone");
        WaitUntil(() => live.Asked.Count == 4, "The next query was not asked.");
        Assert.True(results.HasResults);
        Assert.True(results.HandleKey(Key.Tab, ModifierKeys.None));
        Assert.Equal(1, first.Count);
        live.Asked[3].Answer.SetResult([new SearchResultSectionViewModel(null, [Row("Milestone four.docx", second)])]);
        WaitUntil(() => results.Items[0].Title == "Milestone four.docx", "The answer was not shown.");
        Pump();
        Assert.Equal((1, 1), (first.Count, second.Count));

        // With a list showing and none highlighted, the first row that can be attached is; with nothing attachable, nothing is.
        results.SetSections([new SearchResultSectionViewModel(null, [Row("a.js"), Row("b.map")])]);
        Assert.True(results.HandleKey(Key.Tab, ModifierKeys.None));
        Assert.Equal((1, 1), (first.Count, second.Count));
    });

    [Fact]
    public void TheFileAnswersAndTheAttachedImageRenderInThePanel() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        var clock = new FixedClock(Now);
        var shots = new MessageViewModel(MessageRole.Assistant, "Here are your 5 most recent screenshots.");
        shots.Content.Add(new ImageCollection(DemoImages.Screenshots(5)));
        var files = new MessageViewModel(MessageRole.Assistant, "Here are your 3 most recent files.");
        files.Content.Add(new FileCollection(
        [
            new FileItem(SearchResultItemType.File, "Biology notes.pdf", @"C:\Users\someone\Documents\Biology notes.pdf", Now.AddHours(-2), clock: clock),
            new FileItem(SearchResultItemType.File, "Biology lab.docx", @"C:\Users\someone\Documents\Biology lab.docx", Now.AddDays(-1), clock: clock),
            new FileItem(SearchResultItemType.Folder, "Biology", @"C:\Users\someone\Documents\Biology", Now.AddDays(-4), clock: clock),
        ]));
        model.Messages.Add(new MessageViewModel(MessageRole.User, "show me the last 5 screenshots I took"));
        model.Messages.Add(shots);
        model.Messages.Add(new MessageViewModel(MessageRole.User, "find the PDF about biology I edited last Tuesday"));
        model.Messages.Add(files);
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();
            RenderGlass(panel, "file-answers-2x.png", 2);

            // A conversation that only has a picture attached: the picture above the composer, in the same glass.
            model.StartWithAttachment(DemoImages.Photos()[0]);
            Pump();
            Assert.Empty(model.Messages);
            RenderGlass(panel, "attached-composer-2x.png", 2);
        }
        finally { panel.Close(); }
    }));

    // -- The answer to a request to find files. --

    private static FileRequestAnswers CreateAnswers(ScriptedFileRequests requests, Assistant.Core.Contracts.IConversationFiles? known = null) =>
        new(requests, new FixedClock(Now), known);

    private static FileRequestResult Found(FileSearchQuery query, params SearchResultItem[] items) =>
        new(FileRequestStatus.Found) { Plan = new PlannedFileSearch(query, FileSearchPlanSource.Template), Items = items };

    [Fact]
    public async Task ARequestForScreenshotsIsAnsweredWithAnImageCollection_NotFileRowsOrACard()
    {
        var requests = new ScriptedFileRequests
        {
            Result = Found(
                ScreenshotsQuery,
                [.. Enumerable.Range(1, 5).Select(number => FoundFile($@"C:\Users\someone\Pictures\Screenshots\Screenshot {number}.png", Now))]),
        };

        var answer = await CreateAnswers(requests).AnswerAsync("show me the last 5 screenshots I took", CancellationToken.None);

        Assert.Equal(["show me the last 5 screenshots I took"], requests.Found);
        Assert.Equal(MessageRole.Assistant, answer.Role);
        Assert.Equal(MessageStatus.Complete, answer.Status);
        Assert.Equal("Here are your 5 most recent screenshots.", answer.Text);

        // Prose, then the gallery the image-collection template draws three across; no file list and no card.
        Assert.Equal(2, answer.Content.Count);
        Assert.IsType<TextContent>(answer.Content[0]);
        var gallery = Assert.IsType<ImageCollection>(answer.Content[1]);
        Assert.Equal(
            Enumerable.Range(1, 5).Select(number => $@"C:\Users\someone\Pictures\Screenshots\Screenshot {number}.png"),
            gallery.Images.Select(image => image.Path));
        Assert.Equal(Enumerable.Range(1, 5).Select(number => $"Screenshot {number}.png"), gallery.Images.Select(image => image.Name));
        Assert.DoesNotContain(answer.Content, part => part is FileCollection or MessageCard);

        var one = Found(ScreenshotsQuery, FoundFile(@"C:\Pictures\Screenshot 1.png"));
        Assert.Equal("Here is your most recent screenshot.", CreateAnswers(requests).Describe(one).Text);
        var images = new FileSearchQuery { Extensions = ImageFileTypes.Extensions, Kind = null };
        Assert.Equal("I found 2 images.", CreateAnswers(requests).Describe(
            Found(images, FoundFile(@"C:\a.png"), FoundFile(@"C:\b.jpg"))).Text);
    }

    [Fact]
    public async Task ARequestForOtherFilesIsAnsweredWithAFileListThatHasTheRowActions()
    {
        var query = new FileSearchQuery
        {
            Filename = "biology",
            Extensions = [".pdf"],
            Types = [SearchResultItemType.File],
            Order = FileSearchOrder.ModifiedDescending,
        };
        var requests = new ScriptedFileRequests
        {
            Result = Found(query, FoundFile(@"C:\Docs\Biology 1.pdf", Now), FoundFile(@"C:\Docs\Biology 2.pdf", Now.AddDays(-2))),
        };

        var answer = await CreateAnswers(requests).AnswerAsync("find the PDF about biology", CancellationToken.None);

        Assert.Equal("Here are your 2 most recent files.", answer.Text);
        var list = Assert.IsType<FileCollection>(answer.Content[1]);
        Assert.Equal(["Biology 1.pdf", "Biology 2.pdf"], list.Files.Select(file => file.Name));
        Assert.Equal([@"C:\Docs\Biology 1.pdf", @"C:\Docs\Biology 2.pdf"], list.Files.Select(file => file.Path));
        Assert.DoesNotContain(answer.Content, part => part is ImageCollection);

        // Ordered by how well they match, it says how many it found and what it looked for; folders have their own word.
        var byName = new FileSearchQuery("biology") { Types = [SearchResultItemType.File] };
        Assert.Equal("I found 1 file with “biology” in the name.", CreateAnswers(requests).Describe(Found(byName, FoundFile(@"C:\a.pdf"))).Text);
        var folders = new FileSearchQuery("tax") { Types = [SearchResultItemType.Folder] };
        Assert.Equal("I found 2 folders with “tax” in the name.", CreateAnswers(requests).Describe(
            Found(folders, FoundFolder(@"C:\Tax"), FoundFolder(@"C:\Tax 2"))).Text);
    }

    [Fact]
    public void AnAnswerPickedFromTheClosestNamesSaysSo_AndNeverSoundsLikeAnExactMatch()
    {
        var requests = new ScriptedFileRequests();
        var query = new FileSearchQuery("annas archive") { Extensions = [".pdf"], Types = [SearchResultItemType.File] };
        FileRequestResult Reviewed(params SearchResultItem[] items) => new(FileRequestStatus.Found)
        {
            Plan = new PlannedFileSearch(query, FileSearchPlanSource.Read) { Keywords = ["annas", "archive"], KindName = ("PDF", "PDFs") },
            Items = items,
            Reviewed = true,
        };

        var many = CreateAnswers(requests).Describe(Reviewed(FoundFile(@"C:\Docs\Anna’s Archi.pdf"), FoundFile(@"C:\Docs\Anna’s A.pdf")));
        var one = CreateAnswers(requests).Describe(Reviewed(FoundFile(@"C:\Docs\Anna’s Archi.pdf")));

        Assert.Equal("No name has “annas archive” exactly, so here are the 2 closest PDFs.", many.Text);
        Assert.Equal("No name has “annas archive” exactly, so here is the closest PDF.", one.Text);
        var list = Assert.IsType<FileCollection>(many.Content[1]);
        Assert.Equal(2, list.Files.Count);

        // Pictures found the same way still come back as the gallery.
        var pictures = new FileSearchQuery("beach") { Extensions = ImageFileTypes.Extensions, Types = [SearchResultItemType.File] };
        var gallery = CreateAnswers(requests).Describe(new FileRequestResult(FileRequestStatus.Found)
        {
            Plan = new PlannedFileSearch(pictures, FileSearchPlanSource.Read) { Keywords = ["beach"], KindName = ("photo", "photos") },
            Items = [FoundFile(@"C:\Pictures\Beach day.jpg")],
            Reviewed = true,
        });
        Assert.Equal("No name has “beach” exactly, so here is the closest photo.", gallery.Text);
        Assert.IsType<ImageCollection>(gallery.Content[1]);
    }

    [Fact]
    public void AnAnswerSaysWhatWasLookedFor_SoAnEmptyAnswerIsClearAboutWhy()
    {
        var answers = CreateAnswers(new ScriptedFileRequests());

        // The clock stands on Tuesday 29 September 2026, two hours ahead of UTC.
        static DateRange LocalDays(int month, int day, int toMonth, int toDay) => new(
            new DateTimeOffset(2026, month, day, 0, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, toMonth, toDay, 0, 0, 0, TimeSpan.FromHours(2)).AddDays(1));
        FileRequestResult Nothing(FileSearchQuery query, (string, string)? kind = null) => new(FileRequestStatus.NothingFound)
        {
            Plan = new PlannedFileSearch(query, FileSearchPlanSource.Read) { KindName = kind },
        };

        var biology = new FileSearchQuery("biology") { MatchContents = true, Extensions = [".pdf"], Modified = LocalDays(9, 25, 9, 25) };
        Assert.Equal(
            "I couldn't find any PDFs with “biology” in the name or text, changed on Friday, Sep 25. " + FileRequestAnswers.OnlyIndexedPlacesText,
            answers.Describe(Nothing(biology, ("PDF", "PDFs"))).Text);

        var yesterday = new FileSearchQuery { Extensions = [".docx"], Created = LocalDays(9, 28, 9, 28), Folder = @"C:\Users\Test\Downloads" };
        Assert.Equal(
            "I couldn't find any documents made yesterday, in Downloads. " + FileRequestAnswers.OnlyIndexedPlacesText,
            answers.Describe(Nothing(yesterday, ("document", "documents"))).Text);

        var lastMonth = new FileSearchQuery("budget") { Modified = LocalDays(8, 1, 8, 31), Types = [SearchResultItemType.File] };
        Assert.Equal(
            "I couldn't find any files with “budget” in the name, changed between Aug 1 and Aug 31. " + FileRequestAnswers.OnlyIndexedPlacesText,
            answers.Describe(Nothing(lastMonth)).Text);

        var recent = new FileSearchQuery { Extensions = [".pdf"], Modified = LocalDays(9, 1, 9, 29), Order = FileSearchOrder.ModifiedDescending };
        Assert.Equal(
            "Here are your 2 most recent PDFs changed since Sep 1.",
            answers.Describe(new FileRequestResult(FileRequestStatus.Found)
            {
                Plan = new PlannedFileSearch(recent, FileSearchPlanSource.Read) { KindName = ("PDF", "PDFs") },
                Items = [FoundFile(@"C:\a.pdf"), FoundFile(@"C:\b.pdf")],
            }).Text);

        // Nothing found says so in words, never as an empty gallery or list.
        var nothing = answers.Describe(new FileRequestResult(FileRequestStatus.NothingFound)
        {
            Plan = new PlannedFileSearch(ScreenshotsQuery, FileSearchPlanSource.Template),
        });
        Assert.Equal("I couldn't find any screenshots. " + FileRequestAnswers.OnlyIndexedPlacesText, nothing.Text);
        Assert.Single(nothing.Content);

        // An empty answer from files the index cannot read is not "no file has it".
        var inside = new FileSearchQuery { ContentTerm = "quarterly revenue", Extensions = [".md"] };
        var limited = answers.Describe(new FileRequestResult(FileRequestStatus.NothingFound)
        {
            Plan = new PlannedFileSearch(inside, FileSearchPlanSource.Read),
            ContentSearch = new ContentSearchCapability
            {
                Support = ContentSearchSupport.Unavailable,
                Limits = ContentSearchLimits.FileTypeNotContentIndexed | ContentSearchLimits.LocationNotIndexed,
                UnsupportedExtensions = [".md"],
            },
        });
        Assert.StartsWith("I couldn't find any files that mention “quarterly revenue”.", limited.Text);
        Assert.Contains("that folder isn't indexed and it keeps no text for .md files", limited.Text);
        Assert.Contains("doesn't mean none of them has it", limited.Text);

        // The search could not run, or may not: each has its words.
        Assert.Equal(FileRequestAnswers.FilesTurnedOffText, answers.Describe(new FileRequestResult(FileRequestStatus.FilesTurnedOff)).Text);
        Assert.Equal(FileRequestAnswers.NothingToSearchText, answers.Describe(new FileRequestResult(FileRequestStatus.NothingToSearch)).Text);
        Assert.Equal(FileRequestAnswers.SearchUnavailableText, answers.Describe(new FileRequestResult(FileRequestStatus.SearchUnavailable)).Text);
        Assert.Equal(FileRequestAnswers.SearchTimedOutText, answers.Describe(new FileRequestResult(FileRequestStatus.SearchTimedOut)).Text);
        Assert.Equal(FileRequestAnswers.SearchFailedText, answers.Describe(new FileRequestResult(FileRequestStatus.SearchFailed)).Text);
        Assert.All(
            new[] { FileRequestStatus.FilesTurnedOff, FileRequestStatus.SearchFailed, FileRequestStatus.NothingToSearch },
            status => Assert.Single(answers.Describe(new FileRequestResult(status)).Content));
    }

    [Fact]
    public async Task AFollowUpInTheSameConversationAsksTheLastFileRequestAgain_AndOnlyThere()
    {
        var requests = new ScriptedFileRequests
        {
            IsRequest = question => question.Contains("annas", StringComparison.Ordinal),
            FollowUp = question => question is "find that pdf" or "find it",
        };
        var answers = CreateAnswers(requests);
        var conversation = Guid.NewGuid();

        // Before any request to find files, "find that pdf" is a question for the model.
        Assert.False(answers.IsFileRequest("find that pdf", conversation));

        await answers.AnswerAsync("find annas arhciv pdff", conversation, CancellationToken.None);
        Assert.True(answers.IsFileRequest("find that pdf", conversation));
        Assert.False(answers.IsFileRequest("find that pdf", Guid.NewGuid()));
        Assert.False(answers.IsFileRequest("find that pdf", null));

        await answers.AnswerAsync("find that pdf", conversation, CancellationToken.None);
        await answers.AnswerAsync("find it", conversation, CancellationToken.None);

        Assert.Equal(
            ["find annas arhciv pdff", "find annas arhciv pdff find that pdf", "find annas arhciv pdff find that pdf find it"],
            requests.Found);
    }

    [Fact]
    public void AFileRequestInTheConversationIsAnsweredWithWhatWasFound_AndTheModelIsNotAsked() => RunSta(() =>
    {
        var requests = new ScriptedFileRequests
        {
            IsRequest = question => question.Contains("screenshots", StringComparison.OrdinalIgnoreCase),
            Result = Found(ScreenshotsQuery, FoundFile(@"C:\Pictures\Screenshot 1.png"), FoundFile(@"C:\Pictures\Screenshot 2.png")),
        };
        var model = new ScriptedModel();
        var demo = new DemoAnswerProvider(
            new FakeClipboard(), new FixedClock(Now), localModel: LocalAnswers(model), files: CreateAnswers(requests));
        var shown = new List<MessageViewModel>();

        var asked = demo.StreamAnswerAsync("Show me the last 5 screenshots I took", shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The request was not answered.");

        // Real results, not the sample pictures; nothing was asked of the model.
        var answer = Assert.Single(shown);
        Assert.Equal("Here are your 2 most recent screenshots.", answer.Text);
        Assert.Equal(@"C:\Pictures\Screenshot 1.png", Assert.IsType<ImageCollection>(answer.Content[1]).Images[0].Path);
        Assert.Empty(model.Requests);
        Assert.Equal(["Show me the last 5 screenshots I took"], requests.Found);

        // The synchronous sample answers are unchanged, for the demo commands and for anything that has no search behind it.
        Assert.StartsWith("Here are 5 sample screenshots.", demo.Answer("Show me the last 5 screenshots I took")!.Text);
        Assert.Equal("I found 4 photos from yesterday.", demo.Answer("Find the image I took yesterday")!.Text);

        // Any other question goes to the model, as before.
        var question = demo.StreamAnswerAsync("Why is the sky blue?", shown.Add, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        model.End();
        WaitUntil(() => question.IsCompleted, "The question did not end.");
        Assert.Single(requests.Found);

        // Without file search wired in, the two sample questions still show their samples.
        var without = new DemoAnswerProvider(new FakeClipboard(), new FixedClock(Now));
        var samples = new List<MessageViewModel>();
        var sample = without.StreamAnswerAsync("Show me the last 5 screenshots I took", samples.Add, CancellationToken.None);
        WaitUntil(() => sample.IsCompleted, "The sample was not shown.");
        Assert.StartsWith("Here are 5 sample screenshots.", Assert.Single(samples).Text);
    });

    // -- What can be done with a file in an answer. --

    [Fact]
    public void TheFileActionsKnowWhichPathAndWhichImageAnItemIs() => RunSta(() =>
    {
        var clock = new FixedClock(Now);
        var picture = new FileItem(SearchResultItemType.File, "shot.PNG", @"C:\Pictures\shot.PNG", clock: clock);
        var document = new FileItem(SearchResultItemType.File, "Budget.xlsx", @"C:\Docs\Budget.xlsx", clock: clock);
        var folder = new FileItem(SearchResultItemType.Folder, "Finance", @"C:\Docs\Finance", clock: clock);
        var app = new FileItem(SearchResultItemType.App, "Calculator", "Microsoft.WindowsCalculator!App", clock: clock);
        var tile = new ImageItem("tile.jpg", @"C:\Pictures\tile.jpg");
        var inMemory = new ImageItem("memory", new System.Windows.Media.Imaging.WriteableBitmap(
            2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null));

        Assert.Equal(
            new string?[] { @"C:\Pictures\shot.PNG", @"C:\Docs\Budget.xlsx", @"C:\Docs\Finance", null, @"C:\Pictures\tile.jpg", null, null, null },
            new object?[] { picture, document, folder, app, tile, inMemory, "text", null }.Select(FileActions.PathOf));

        // Only an image can be attached: a picture file is one by its type, and a tile that is a file is one as it is.
        Assert.Equal(("shot.PNG", @"C:\Pictures\shot.PNG"), (FileActions.ImageOf(picture)!.Name, FileActions.ImageOf(picture)!.Path));
        Assert.Same(tile, FileActions.ImageOf(tile));
        Assert.All(new object?[] { document, folder, app, inMemory, "text", null }, item => Assert.Null(FileActions.ImageOf(item)));
    });

    [Fact]
    public void AnAnswersFilesAndImagesOpenOnClick_AndTheirMenuShowsThemInExplorerOrAttachesThemToTheConversation() => RunSta(() => WithTheme(() =>
    {
        var launcher = new RecordingLauncher();
        var assistant = CreateAssistant(fileLauncher: launcher);
        var (panel, _, model) = assistant;
        var clock = new FixedClock(Now);
        var tile = new ImageItem("shot.png", @"C:\Pictures\shot.png");
        var document = new FileItem(SearchResultItemType.File, "Budget.xlsx", @"C:\Docs\Budget.xlsx", clock: clock);
        var picture = new FileItem(SearchResultItemType.File, "chart.png", @"C:\Docs\chart.png", clock: clock);
        var answer = new MessageViewModel(MessageRole.Assistant, "Found these.");
        answer.Content.Add(new ImageCollection([tile]));
        answer.Content.Add(new FileCollection([document, picture]));
        model.Messages.Add(answer);
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();
            var area = Named<Grid>(panel, "ConversationLayer");
            Grid RootOf(object content) => Assert.IsType<Grid>(VisualTreeHelper.GetChild(
                Descendants<ContentPresenter>(area).Single(presenter => ReferenceEquals(presenter.Content, content)), 0));

            // Each is clicked to open, with a hand over it, and has the menu.
            foreach (var (content, root) in new (object, Grid)[] { (tile, RootOf(tile)), (document, RootOf(document)), (picture, RootOf(picture)) })
            {
                Assert.Equal(Cursors.Hand, root.Cursor);
                var click = Assert.IsType<MouseBinding>(Assert.Single(root.InputBindings));
                Assert.Equal(MouseAction.LeftClick, click.MouseAction);
                Assert.Same(FileActions.Open, click.Command);
                Assert.Same(content, click.CommandParameter);

                // The menu takes the file's data from the row it opens over, and each item acts on it.
                var menu = Assert.IsType<ContextMenu>(root.ContextMenu);
                menu.PlacementTarget = root;
                Assert.Same(content, menu.DataContext);
                var items = menu.Items.OfType<MenuItem>().ToArray();
                Assert.Equal(["Open", "Show in Explorer", "Attach to conversation"], items.Select(item => item.Header));
                Assert.Equal([FileActions.Open, FileActions.Reveal, FileActions.Attach], items.Select(item => item.Command));
                Assert.All(items, item => Assert.Same(content, item.CommandParameter));
            }

            // They open and show through the launcher, and work on an item as it is.
            FileActions.Open.Execute(tile, RootOf(tile));
            FileActions.Reveal.Execute(document, RootOf(document));
            FileActions.Open.Execute(picture, RootOf(picture));
            Assert.Equal([@"C:\Pictures\shot.png", @"C:\Docs\chart.png"], launcher.Opened);
            Assert.Equal([@"C:\Docs\Budget.xlsx"], launcher.Revealed);
            Assert.True(FileActions.Open.CanExecute(document, RootOf(document)));
            Assert.False(FileActions.Open.CanExecute("text", RootOf(document)));

            // An image, or a document the Assistant reads, can be attached. A spreadsheet cannot be read, so its item is dimmed.
            Assert.True(FileActions.Attach.CanExecute(tile, RootOf(tile)));
            Assert.True(FileActions.Attach.CanExecute(picture, RootOf(picture)));
            Assert.False(FileActions.Attach.CanExecute(document, RootOf(document)));
            FileActions.Attach.Execute(picture, RootOf(picture));
            FileActions.Attach.Execute(tile, RootOf(tile));
            FileActions.Attach.Execute(new ImageItem("again.png", @"C:\DOCS\CHART.PNG"), RootOf(tile));
            Assert.Equal([@"C:\Docs\chart.png", @"C:\Pictures\shot.png"], model.Attachments.Select(image => image.Path));
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void WithoutALauncherOrAConversationToAttachTo_TheCommandsCannotRun() => RunSta(() => WithTheme(() =>
    {
        var view = new ConversationView();
        var file = new FileItem(SearchResultItemType.File, "a.png", @"C:\a.png");
        var attached = new List<ImageItem>();
        view.AttachRequested += (_, image) => attached.Add(image);

        Assert.False(FileActions.Open.CanExecute(file, view));
        Assert.False(FileActions.Reveal.CanExecute(file, view));
        Assert.False(FileActions.Attach.CanExecute(file, view));

        var launcher = new RecordingLauncher();
        view.FileLauncher = launcher;
        Assert.True(FileActions.Open.CanExecute(file, view));
        Assert.False(FileActions.Attach.CanExecute(file, view));
        view.CanAttach = true;
        Assert.True(FileActions.Attach.CanExecute(file, view));
        FileActions.Reveal.Execute(file, view);
        FileActions.Attach.Execute(file, view);
        Assert.Equal([@"C:\a.png"], launcher.Revealed);
        Assert.Equal([@"C:\a.png"], attached.Select(image => image.Path));
    }));

    // -- Attaching an image to the conversation. --

    [Fact]
    public void AnImageAttachedToTheConversation_IsWhatTheNextQuestionIsAbout()
    {
        var answers = new RecordingQuestionAnswers();
        var conversation = CreateConversationModel(answers: answers);
        var image = new ImageItem("shot.png", @"C:\Pictures\shot.png");

        // A conversation that only has an image attached so far can be typed into.
        Assert.False(conversation.CanCompose);
        conversation.StartWithAttachment(image);
        Assert.Empty(conversation.Messages);
        Assert.Same(image, Assert.Single(conversation.Attachments));
        Assert.True(conversation.CanCompose);
        Assert.False(conversation.OpenInHistoryCommand.CanExecute(null));

        // The same file is not attached twice, in any case of letters.
        Assert.False(conversation.Attach(new ImageItem("again.png", @"C:\PICTURES\SHOT.PNG")));
        Assert.True(conversation.Attach(new ImageItem("other.png", @"C:\Pictures\other.png")));
        Assert.Equal(2, conversation.Attachments.Count);

        // A picture comes off again with its button.
        conversation.RemoveAttachmentCommand.Execute(conversation.Attachments[1]);
        Assert.Same(image, Assert.Single(conversation.Attachments));
        conversation.RemoveAttachmentCommand.Execute("not an image");
        Assert.Single(conversation.Attachments);

        // Asking puts the images on the question, which is the conversation's first message, and the provider is handed it.
        conversation.Draft = "What does this show?";
        Assert.True(conversation.AskCommand.CanExecute(null));
        conversation.AskCommand.Execute(null);
        WaitUntil(() => answers.Asked.Count == 1, "The question was not asked.");
        var question = Assert.Single(conversation.Messages);
        Assert.Equal("What does this show?", question.Text);
        Assert.Same(image, Assert.Single(question.Attachments));
        Assert.Same(question, answers.Asked[0]);
        Assert.Empty(conversation.Attachments);
        Assert.Equal("", conversation.Draft);

        // A follow-up carries the images attached since.
        WaitUntil(() => !conversation.IsAnswering, "The answer did not end.");
        conversation.Attach(new ImageItem("next.png", @"C:\Pictures\next.png"));
        Assert.True(conversation.Ask("And this one?"));
        Assert.Equal("next.png", Assert.Single(conversation.Messages[^1].Attachments).Name);
        Assert.Empty(conversation.Attachments);

        // A new conversation starts without them.
        conversation.Attach(image);
        conversation.StartNew("Something else");
        Assert.Empty(conversation.Attachments);
    }

    [Fact]
    public void EscTakesOffTheAttachedImagesBeforeItClosesThePanel()
    {
        var conversation = CreateConversationModel();
        conversation.StartWithAttachment(new ImageItem("shot.png", @"C:\Pictures\shot.png"));
        conversation.Draft = "typing";

        Assert.False(conversation.HandleEscape());
        Assert.Equal("", conversation.Draft);
        Assert.Single(conversation.Attachments);
        Assert.False(conversation.HandleEscape());
        Assert.Empty(conversation.Attachments);
        Assert.False(conversation.CanCompose);
        Assert.True(conversation.HandleEscape());
    }

    [Fact]
    public void AnImageAttachedFromTheBarsResults_GrowsTheBarIntoAConversationThatHasItAttached() => RunSta(() => WithTheme(() =>
    {
        var requests = new AttachRequests();
        var assistant = CreateAssistant(animations: false, attachRequests: requests);
        var (window, bar, conversation) = assistant;
        var image = new ImageItem("shot.png", @"C:\Pictures\shot.png");
        try
        {
            window.ShowAndFocus();
            bar.Query = "screenshot";
            Assert.Equal(AssistantWindowState.Compact, window.State);

            requests.Request(image);

            // The panel is up, with no message and the picture attached, and the bar's draft goes with the bar.
            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
            Assert.Empty(conversation.Messages);
            Assert.Same(image, Assert.Single(conversation.Attachments));
            Assert.True(conversation.CanCompose);
            WaitUntil(() => bar.Query == "", "The bar's draft was not cleared.");

            // Only the bar attaches: while a conversation is open, another request is ignored.
            requests.Request(new ImageItem("other.png", @"C:\Pictures\other.png"));
            Assert.Same(image, Assert.Single(conversation.Attachments));
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheComposerShowsTheAttachedImagesAboveWhatIsTyped_AndTakesThemOffWithTheirButton() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        var image = new ImageItem("shot.png", @"C:\Pictures\shot.png");
        model.StartWithAttachment(image);
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();

            var composer = Named<Grid>(panel, "Composer");
            var chips = Named<Assistant.UI.Controls.AttachmentChipList>(panel, "ComposerAttachments");
            Assert.Equal(Visibility.Visible, composer.Visibility);
            Assert.Equal(Visibility.Visible, chips.Visibility);
            Assert.Same(image, Assert.Single(chips.Items.Cast<AttachmentChip>()).Source);
            Assert.Equal("Attachments", System.Windows.Automation.AutomationProperties.GetName(chips));

            // The pictures lie inside the same glass, above the input.
            var input = Named<Assistant.UI.Controls.PromptInputControl>(panel, "ComposerInput");
            Assert.True(BoundsIn(composer, chips).Bottom <= BoundsIn(composer, input).Top + 0.5);
            Assert.True(BoundsIn(composer, chips).Height > 0);

            var remove = Descendants<Button>(chips).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Remove attachment");
            Assert.Same(model.RemoveAttachmentCommand, remove.Command);
            Assert.Same(model.Chips.Single(), remove.CommandParameter);
            remove.Command.Execute(remove.CommandParameter);
            Pump();

            // With nothing attached and no message, the composer is gone again, and so are the pictures.
            Assert.Empty(model.Attachments);
            Assert.Equal(Visibility.Collapsed, composer.Visibility);
        }
        finally { panel.Close(); }
    }));

    // -- Asking the model about an attached image. --

    [Fact]
    public void AnAttachedImageGoesToTheModelWithTheQuestion_WhileFilesArePermitted() => RunSta(() =>
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"assistant-attach-test-{Guid.NewGuid():N}.png");
        var image = Png(200, 100);
        File.WriteAllBytes(file, image);
        try
        {
            var model = new ScriptedModel { Active = new ModelInfo("vision-model", 8192) { SupportsVision = true } };
            var answers = LocalAnswers(model, new SettingsPermissionPolicy(new InMemorySettingsService()));
            var question = new MessageViewModel(MessageRole.User, "What is this?", [new ImageItem("p.png", file)]);
            var shown = new List<MessageViewModel>();

            var asked = answers.StreamAnswerAsync(Guid.NewGuid(), question, shown.Add, CancellationToken.None);
            WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
            model.Write(AssistantResponseChunk.ForTextDelta("A picture."));
            model.End();
            WaitUntil(() => asked.IsCompleted, "The answer did not end.");

            var request = Assert.Single(model.Requests);
            Assert.Equal("What is this?", request.Messages[^1].Text);
            Assert.Equal(image, Assert.Single(request.Images).ToArray());
            Assert.Equal("A picture.", Assert.Single(shown).Text);
            Assert.Equal(image, File.ReadAllBytes(file));
        }
        finally
        {
            File.Delete(file);
        }
    });

    [Fact]
    public void AnAttachedImageThatCannotBeReadOrMaynotBe_IsSaidSo_AndNothingIsAsked() => RunSta(() =>
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"assistant-attach-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(file, Png(64, 64));
        try
        {
            var model = new ScriptedModel();
            var settings = new InMemorySettingsService();
            var question = new MessageViewModel(MessageRole.User, "What is this?", [new ImageItem("p.png", file)]);
            var shown = new List<MessageViewModel>();
            Task Ask(ModelAnswerProvider answers) => answers.StreamAnswerAsync(Guid.NewGuid(), question, shown.Add, CancellationToken.None);

            // The Files permission off: the file is not read.
            settings.SaveAsync(new AppSettings { Permissions = new PermissionSettings { Files = false } }).GetAwaiter().GetResult();
            var off = Ask(LocalAnswers(model, new SettingsPermissionPolicy(settings)));
            WaitUntil(() => off.IsCompleted, "The answer did not end.");

            // No way to ask the permission: nothing is read.
            var none = Ask(LocalAnswers(model));
            WaitUntil(() => none.IsCompleted, "The answer did not end.");

            // A file that is gone.
            File.Delete(file);
            var gone = Ask(LocalAnswers(model, new SettingsPermissionPolicy(new InMemorySettingsService())));
            WaitUntil(() => gone.IsCompleted, "The answer did not end.");

            Assert.Empty(model.Requests);
            Assert.Equal(
                [AttachedImages.FilesTurnedOffText, AttachedImages.NotAvailableText, AttachedImages.UnreadableText],
                shown.Select(message => message.Text));
        }
        finally
        {
            File.Delete(file);
        }
    });

    [Fact]
    public void AQuestionWithAnImageAttachedIsAboutTheImage_EvenWhenItsWordsAskToFindFiles() => RunSta(() =>
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"assistant-attach-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(file, Png(64, 64));
        try
        {
            var requests = new ScriptedFileRequests { IsRequest = _ => true };
            var model = new ScriptedModel { Active = new ModelInfo("vision-model", 8192) { SupportsVision = true } };
            var demo = new DemoAnswerProvider(
                new FakeClipboard(), new FixedClock(Now),
                localModel: LocalAnswers(model, new SettingsPermissionPolicy(new InMemorySettingsService())),
                files: CreateAnswers(requests));
            var question = new MessageViewModel(MessageRole.User, "find the screenshots like this one", [new ImageItem("p.png", file)]);

            var asked = demo.StreamAnswerAsync(Guid.NewGuid(), question, _ => { }, CancellationToken.None);
            WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
            model.End();
            WaitUntil(() => asked.IsCompleted, "The answer did not end.");

            Assert.Empty(requests.Found);
            Assert.Single(Assert.Single(model.Requests).Images);
        }
        finally
        {
            File.Delete(file);
        }
    });

    // -- How the app wires it all. --

    [Fact]
    public void TheAppWiresFileSearchIntoTheBarTheConversationAndTheShell()
    {
        using var host = AppHost.Create();
        var services = host.Services;

        Assert.IsType<FileSearchPlanner>(services.GetRequiredService<IFileSearchPlanner>());
        Assert.IsType<FileRequestService>(services.GetRequiredService<IFileRequestService>());
        Assert.IsType<ModelFileMatchReviewer>(services.GetRequiredService<IFileMatchReviewer>());
        Assert.IsType<ShellFileLauncher>(services.GetRequiredService<IFileLauncher>());
        Assert.NotNull(services.GetRequiredService<FileRequestAnswers>());

        // The bar's results come from the quick-search providers, one of which looks in the Windows Search index as the user types.
        Assert.IsType<QuickSearchResultsSource>(services.GetRequiredService<IQuickSearchResultsSource>());
        Assert.Contains(services.GetRequiredService<IEnumerable<IQuickSearchProvider>>(), provider => provider is FilesQuickSearchProvider);
        Assert.NotNull(services.GetRequiredService<SearchResultsViewModel>());
        Assert.NotNull(services.GetRequiredService<SearchOrAskViewModel>());
        Assert.Same(services.GetRequiredService<AttachRequests>(), services.GetRequiredService<AttachRequests>());

        // A request to find files is planned by rule, without the model, and is a file request before anything is searched.
        var files = services.GetRequiredService<IFileRequestService>();
        Assert.True(files.IsFileRequest("show me the last 5 screenshots I took"));
        Assert.True(files.IsFileRequest("find the PDF about biology I edited last Tuesday"));
        Assert.False(files.IsFileRequest("What is 9+10"));
        var plan = services.GetRequiredService<IFileSearchPlanner>().PlanKnownShape("show me the last 5 screenshots I took")!;
        Assert.Equal(5, plan.Query!.MaxResultsPerType);
        Assert.True(plan.AsksForImages);
    }

    [Fact]
    public void TheAppAnswersTheScreenshotsRequestWithWhatWindowsSearchFinds_NotTheSamplePictures() => RunSta(() =>
    {
        using var host = AppHost.Create();
        var answers = host.Services.GetRequiredService<IAnswerProvider>();
        var shown = new List<MessageViewModel>();

        var asked = answers.StreamAnswerAsync(Guid.NewGuid(), "show me the last 5 screenshots I took", shown.Add, CancellationToken.None);
        WaitUntilFor(TimeSpan.FromSeconds(30), () => asked.IsCompleted, "The request was not answered.");

        // Through this PC's real index when it has one: pictures it found, or words that say what could not be done.
        var answer = Assert.Single(shown);
        Assert.DoesNotContain("sample", answer.Text, StringComparison.OrdinalIgnoreCase);
        if (answer.Content.OfType<ImageCollection>().SingleOrDefault() is { } gallery)
        {
            Assert.InRange(gallery.Images.Count, 1, 5);
            Assert.All(gallery.Images, image => Assert.True(ImageFileTypes.IsImageExtension(System.IO.Path.GetExtension(image.Path))));
        }
        else
        {
            Assert.Single(answer.Content);
        }
    });

    // Waits as WaitUntil does, for as long as the test says: a search of this PC's own index can take seconds.
    private static void WaitUntilFor(TimeSpan time, Func<bool> condition, string failure)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var deadline = DateTime.UtcNow + time;
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        timer.Tick += (_, _) =>
        {
            if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false;
        };
        timer.Start();
        try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), failure);
    }

    // -- Test doubles. --

    private sealed class RecordingLauncher : IFileLauncher
    {
        public List<string> Opened { get; } = [];

        public List<string> Revealed { get; } = [];

        public bool Open(string path)
        {
            Opened.Add(path);
            return true;
        }

        public bool Reveal(string path)
        {
            Revealed.Add(path);
            return true;
        }
    }

    private sealed class ScriptedFileRequests : IFileRequestService
    {
        public Func<string, bool> IsRequest { get; set; } = _ => false;

        public FileRequestResult Result { get; set; } = new(FileRequestStatus.NothingFound);

        public IReadOnlyList<SearchResultItem> Typed { get; set; } = [];

        public List<string> Found { get; } = [];

        public List<string> LookedUp { get; } = [];

        public Func<string, bool> FollowUp { get; set; } = _ => false;

        public Func<string, bool> Likely { get; set; } = _ => false;

        public Func<string, FileRequestResult>? ResultFor { get; set; }

        public bool IsFileRequest(string request) => IsRequest(request);

        public bool IsLikelyFileRequest(string request) => Likely(request);


        public bool IsFollowUp(string request) => FollowUp(request);

        public Task<FileRequestResult> FindAsync(string request, CancellationToken cancellationToken = default)
        {
            Found.Add(request);
            return Task.FromResult(ResultFor?.Invoke(request) ?? Result);
        }

        public Task<IReadOnlyList<SearchResultItem>> LookUpAsync(string typed, CancellationToken cancellationToken = default)
        {
            LookedUp.Add(typed);
            return Task.FromResult(Typed);
        }
    }

    // A source of results that answers when the test says: each question waits for its own answer.
    private sealed class ScriptedAsyncResults : IAsyncSearchResultsSource
    {
        private readonly object _gate = new();
        private readonly List<Question> _asked = [];

        public IReadOnlyList<Question> Asked
        {
            get
            {
                lock (_gate)
                {
                    return [.. _asked];
                }
            }
        }

        public Task<IReadOnlyList<SearchResultSectionViewModel>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<IReadOnlyList<SearchResultSectionViewModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken));
            lock (_gate)
            {
                _asked.Add(new Question(query, answer, cancellationToken));
            }

            return answer.Task;
        }

        public sealed record Question(
            string Query, TaskCompletionSource<IReadOnlyList<SearchResultSectionViewModel>> Answer, CancellationToken Token);
    }
}
