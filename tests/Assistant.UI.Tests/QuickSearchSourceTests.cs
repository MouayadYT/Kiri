using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.QuickSearch.Clipboard;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The bar's results: what the quick-search providers find, ranked, and what each row does (PROJECT_SPEC §4.1). ----

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task TheBarListsFilesAndFoldersWithWhatEachRowDoes()
    {
        var (files, _) = QuickFiles(
        [
            FoundFile(@"C:\Users\someone\Documents\Finance\Budget 2026.pdf", QuickNow.AddDays(-1)),
            FoundFile(@"C:\Users\someone\Pictures\Budget chart.PNG"),
            FoundFile(@"D:\Budget.txt"),
            FoundFolder(@"C:\Users\someone\Documents\Budget"),
        ]);
        var kit = new QuickKit([files]).Listen();
        var documents = new List<DocumentAttachment>();
        var images = new List<ImageItem>();
        kit.Attach.DocumentRequested += (_, document) => documents.Add(document);
        kit.Attach.Requested += (_, image) => images.Add(image);

        var snapshot = (await kit.SearchAsync("budget"))[^1];

        // The folder that is named exactly what was typed leads in a section of its own; the files follow in one group, with no header
        // since there is only one group.
        Assert.Equal(2, snapshot.Sections.Count);
        Assert.All(snapshot.Sections, section => Assert.Equal("", section.Title));
        Assert.Equal(["Budget"], snapshot.Sections[0].Items.Select(row => row.Title));
        Assert.Equal(["Budget 2026.pdf", "Budget chart.PNG", "Budget.txt"], snapshot.Sections[1].Items.Select(row => row.Title));
        Assert.Same(snapshot.Sections[0].Items[0], snapshot.Highlighted);

        // A row says what it is, where it is, when it changed, what Enter does, and that Tab lists what else it can do.
        var pdf = snapshot.Sections[1].Items[0];
        Assert.Equal(SearchResultKind.File, pdf.Kind);
        Assert.Equal(@"~\Documents\Finance", pdf.Subtitle);
        Assert.Equal("Yesterday", pdf.Detail);
        Assert.Equal("Result.Icon.File", pdf.Icon.GlyphKey);
        Assert.Equal(("Open", "enter"), (pdf.ActionHint!.Text, pdf.ActionHint.Key));
        Assert.Equal(("Actions", "tab"), (pdf.SecondaryHint!.Text, pdf.SecondaryHint.Key));
        Assert.Equal(@"file:C:\Users\someone\Documents\Finance\Budget 2026.pdf", pdf.Key);
        Assert.Equal(("D:\\", ""), (snapshot.Sections[1].Items[2].Subtitle, snapshot.Sections[1].Items[2].Detail));
        Assert.Equal("Result.Icon.Folder", snapshot.Sections[0].Items[0].Icon.GlyphKey);

        // Its other actions are listed by Tab, the picture's and the document's first: attaching it to a conversation, showing it in File
        // Explorer and copying its path, the last two also by a key of their own straight from the row.
        Assert.Equal(["Attach to conversation", "Open file location", "Copy file", "Copy path"], pdf.Alternates.Select(alternate => alternate.Title));
        Assert.Equal([null, "Shift+Enter", null, "Ctrl+Shift+C"], pdf.Alternates.Select(alternate => alternate.KeyText));
        Assert.Equal(
            [(Key.Enter, ModifierKeys.Shift), (Key.C, ModifierKeys.Control | ModifierKeys.Shift)],
            pdf.Actions.Select(action => (action.Key, action.Modifiers)));
        Assert.Equal(["Open file location", "Copy folder", "Copy path"], snapshot.Sections[0].Items[0].Alternates.Select(alternate => alternate.Title));

        // Enter opens it; Shift+Enter shows it in File Explorer; Ctrl+Shift+C copies its path; Attach attaches it, and the bar goes after the
        // first three (an attached file grows the bar into the conversation instead).
        var path = @"C:\Users\someone\Documents\Finance\Budget 2026.pdf";
        pdf.Command.Execute(null);
        Assert.Equal([path], kit.Files.Opened);
        pdf.Actions[0].Command.Execute(null);
        Assert.Equal([path], kit.Files.Revealed);
        pdf.Actions[1].Command.Execute(null);
        Assert.Equal([path], kit.Clipboard.Copied);
        Assert.Equal(3, kit.Finished);
        pdf.Alternates[0].Command.Execute(null);
        Assert.Equal(("Budget 2026.pdf", path), (Assert.Single(documents).Name, documents[0].Path));
        Assert.Equal(3, kit.Finished);

        var picture = snapshot.Sections[1].Items[1];
        picture.Alternates[0].Command.Execute(null);
        Assert.Equal((@"C:\Users\someone\Pictures\Budget chart.PNG", "Budget chart.PNG"), (Assert.Single(images).Path, images[0].Name));
        Assert.Empty(kit.Failures);
    }

    [Fact]
    public async Task AtMostFiveFilesAndThreeFoldersAreListedWhileTypingAndNothingFoundListsNothing()
    {
        var (files, search) = QuickFiles(
        [
            .. Enumerable.Range(1, 7).Select(number => FoundFile($@"C:\Users\someoneelse\File {number}.txt")),
            .. Enumerable.Range(1, 5).Select(number => FoundFolder($@"C:\Folder {number}")),
        ]);
        var kit = new QuickKit([files]);

        var snapshot = (await kit.SearchAsync("file"))[^1];

        var rows = snapshot.Sections.SelectMany(section => section.Items).ToArray();
        Assert.Equal(8, rows.Length);
        Assert.Equal(5, rows.Count(row => row.Icon.GlyphKey == "Result.Icon.File"));
        Assert.Equal(3, rows.Count(row => row.Icon.GlyphKey == "Result.Icon.Folder"));
        Assert.Equal(@"C:\Users\someoneelse", rows[0].Subtitle);
        Assert.Equal("file", Assert.Single(search.Queries).Text);

        var (none, _) = QuickFiles([]);
        Assert.Empty((await new QuickKit([none]).SearchAsync("zzz"))[^1].Sections);
    }

    [Fact]
    public async Task TabOnAReadableFileOffersToAttachItAndOnAnyOtherFileDoesNot()
    {
        var (files, _) = QuickFiles(
        [
            FoundFile(@"C:\Docs\Budget.pdf"), FoundFile(@"C:\Docs\Budget.xlsx"), FoundFile(@"C:\Docs\shot.png"), FoundFolder(@"C:\Docs\Budget"),
        ]);
        var kit = new QuickKit([files]);
        var documents = new List<DocumentAttachment>();
        var images = new List<ImageItem>();
        kit.Attach.DocumentRequested += (_, document) => documents.Add(document);
        kit.Attach.Requested += (_, image) => images.Add(image);

        var rows = (await kit.SearchAsync("budget"))[^1].Sections.SelectMany(section => section.Items).ToDictionary(row => row.Title);

        string[] AttachTitles(string name) => [.. rows[name].Alternates.Select(alternate => alternate.Title).Where(title => title.StartsWith("Attach"))];
        Assert.Equal(["Attach to conversation"], AttachTitles("Budget.pdf"));
        Assert.Equal(["Attach to conversation"], AttachTitles("shot.png"));
        Assert.Empty(AttachTitles("Budget.xlsx"));
        Assert.Empty(AttachTitles("Budget"));

        rows["Budget.pdf"].Alternates[0].Command.Execute(null);
        rows["shot.png"].Alternates[0].Command.Execute(null);

        var attached = Assert.Single(documents);
        Assert.Equal(("Budget.pdf", @"C:\Docs\Budget.pdf"), (attached.Name, attached.Path));
        Assert.Equal("PDF", attached.TypeLabel);
        Assert.Equal(@"C:\Docs\shot.png", Assert.Single(images).Path);
    }

    [Fact]
    public async Task ARowThatWasRunCountsAsUsedAndAFailureStaysAndSaysWhy()
    {
        var provider = new FakeQuickProvider("applications", QuickSearchResultType.Applications, 100, _ => [QuickApp("Brave Browser", "id:brave")]);
        var kit = new QuickKit([provider]).Listen();

        var row = (await kit.SearchAsync("brave"))[^1].Sections[0].Items[0];
        row.Command.Execute(null);

        Assert.Equal(["id:brave"], kit.Apps.Launched);
        Assert.Equal(1, kit.Finished);
        Assert.Equal(1, kit.Usage.Find("app:id:brave")!.Uses);

        // An application that cannot be started leaves the bar where it is and says so; it is not counted as used.
        kit.Apps.Succeeds = false;
        row.Command.Execute(null);
        Assert.Equal(["That application could not be opened."], kit.Failures);
        Assert.Equal(1, kit.Finished);
        Assert.Equal(1, kit.Usage.Find("app:id:brave")!.Uses);
    }

    [Fact]
    public async Task AnActionRunsThroughTheExecutorWithItsArgumentAndAFailureSaysWhy()
    {
        var provider = new FakeQuickProvider("actions", QuickSearchResultType.Actions, 80, _ =>
            [QuickAction("Set volume to 30%", "sound.set-volume", "30"), QuickAction("New conversation", "assistant.new-conversation")]);
        var kit = new QuickKit([provider]).Listen();

        var rows = (await kit.SearchAsync("volume"))[^1].Sections.SelectMany(section => section.Items).ToArray();
        var volume = rows.Single(row => row.Title.StartsWith("Set volume"));
        volume.Command.Execute(null);

        Assert.Equal([("sound.set-volume", "30")], kit.Actions.Ran);
        Assert.Equal(1, kit.Finished);
        Assert.Equal(("Run", "enter"), (volume.ActionHint!.Text, volume.ActionHint.Key));
        Assert.Null(volume.SecondaryHint);

        // An action that deals with the bar itself leaves it alone, and one that failed says why.
        rows.Single(row => row.Title == "New conversation").Command.Execute(null);
        Assert.Equal(1, kit.Finished);
        kit.Actions.Outcome = QuickActionOutcome.Failed("The volume could not be changed.");
        volume.Command.Execute(null);
        Assert.Equal(["The volume could not be changed."], kit.Failures);
        Assert.Equal(1, kit.Finished);
    }

    [Fact]
    public async Task TheBestMatchLeadsAndIsHighlightedWhenItsNameOrItsWordsBeginWithWhatWasTyped()
    {
        var provider = new FakeQuickProvider("applications", QuickSearchResultType.Applications, 100, _ =>
        [
            QuickApp("Atlanta Braves", "braves"), QuickApp("Brave Browser", "browser"), QuickApp("Visual Studio Code", "code"),
        ]);
        var kit = new QuickKit([provider]);

        var brave = (await kit.SearchAsync("brave"))[^1];

        // The name that begins with it leads and is highlighted; the one with a word that begins with it follows; one that does not match
        // (this provider returns everything it has, and a real one finds only what matches) comes last.
        Assert.Equal(["Brave Browser"], brave.Sections[0].Items.Select(row => row.Title));
        Assert.Same(brave.Sections[0].Items[0], brave.Highlighted);
        Assert.Equal(["Atlanta Braves", "Visual Studio Code"], brave.Sections[1].Items.Select(row => row.Title));

        // A name whose words begin with what was typed leads and is highlighted too, as the reference's best match is ("word" offers
        // Microsoft Word): Enter opens it, and the bar says so beside the text.
        var tokens = (await kit.SearchAsync("studio code"))[^1];
        Assert.Equal(["Visual Studio Code"], tokens.Sections[0].Items.Select(row => row.Title));
        Assert.Same(tokens.Sections[0].Items[0], tokens.Highlighted);
        var initials = (await kit.SearchAsync("vsc"))[^1];
        Assert.Equal("Visual Studio Code", initials.Highlighted?.Title);

        // Text that is only somewhere inside a name is less certain: it is listed, but not highlighted, and Enter still asks.
        var inside = (await kit.SearchAsync("rave"))[^1];
        Assert.NotEmpty(inside.Sections);
        Assert.Null(inside.Highlighted);

        // The name that is what was typed is highlighted.
        Assert.NotNull((await kit.SearchAsync("visual studio code"))[^1].Highlighted);

        // Nothing is highlighted while nothing is typed, and nothing is listed by an empty query unless a kind is browsed.
        Assert.Empty((await kit.SearchAsync(""))[^1].Sections);
    }

    [Fact]
    public async Task GroupsHaveHeadersOnlyWhereThereAreSeveralToTellApart()
    {
        var apps = new FakeQuickProvider("applications", QuickSearchResultType.Applications, 100, _ =>
            [QuickApp("Notes One", "n1"), QuickApp("Notes Two", "n2")]);
        var (files, _) = QuickFiles([FoundFile(@"C:\Docs\Notes.txt"), FoundFile(@"C:\Docs\Notes 2.txt")]);

        var alone = (await new QuickKit([apps]).SearchAsync("notes"))[^1];
        Assert.All(alone.Sections, section => Assert.Equal("", section.Title));

        var together = (await new QuickKit([apps, files]).SearchAsync("notes"))[^1];
        Assert.Equal(["", "Applications", "Files"], together.Sections.Select(section => section.Title));
    }

    [Fact]
    public async Task AQuestionIsNotLookedUpAndARequestForFilesLooksOnlyInFiles()
    {
        var apps = new FakeQuickProvider("applications", QuickSearchResultType.Applications, 100, _ => [QuickApp("Anything", "a")]);
        var other = new FakeQuickProvider("files", QuickSearchResultType.Files, 60, _ => [QuickApp("Report.docx", "r")]);
        var kit = new QuickKit([apps, other], isFileRequest: text => text.StartsWith("find ", StringComparison.Ordinal));

        // A question is for Enter: nothing is looked up for it.
        var question = await kit.SearchAsync("what is the capital of France");
        Assert.Empty(question[^1].Sections);
        Assert.Empty(apps.Asked);
        Assert.Empty(other.Asked);

        // A request for files looks in files and nowhere else.
        await kit.SearchAsync("find report");
        Assert.Empty(apps.Asked);
        Assert.Single(other.Asked);

        // A name is looked up everywhere; a narrowed list asks even for a question's words.
        await kit.SearchAsync("any");
        Assert.Single(apps.Asked);
        await kit.SearchAsync("what is the capital of France", QuickSearchResultType.Applications);
        Assert.Equal(2, apps.Asked.Count);
    }

    [Fact]
    public async Task ResultsAreReportedAsEachProviderAnswersSoTheQuickOnesAreNotHeldUp()
    {
        var gate = new TaskCompletionSource();
        var apps = new FakeQuickProvider("applications", QuickSearchResultType.Applications, 100, _ => [QuickApp("Notes One", "n1")]);
        var slow = new FakeQuickProvider("files", QuickSearchResultType.Files, 60, _ => [QuickApp("Notes.txt", "f1")]) { Gate = gate };
        var kit = new QuickKit([apps, slow]);
        var reported = new List<SearchResultsSnapshot>();
        var first = new TaskCompletionSource();

        var search = kit.Source.SearchAsync("notes", null, snapshot =>
        {
            lock (reported)
            {
                reported.Add(snapshot);
            }

            first.TrySetResult();
        }, CancellationToken.None);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The applications are listed while the files are still being looked for.
        Assert.False(search.IsCompleted);
        Assert.Equal(["Notes One"], reported[0].Sections.SelectMany(section => section.Items).Select(row => row.Title));
        gate.SetResult();
        await search.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, reported[^1].Sections.Sum(section => section.Items.Count));
    }

    [Fact]
    public async Task ANarrowedListSaysWhyItIsEmptyOnlyOnceEveryProviderHasAnswered()
    {
        var empty = new FakeQuickProvider("files", QuickSearchResultType.Files, 60, _ => []);
        var kit = new QuickKit([empty]);

        var snapshots = await kit.SearchAsync("", QuickSearchResultType.Files);

        Assert.Equal("No recent files", snapshots[^1].EmptyMessage);
        Assert.All(snapshots.Take(snapshots.Count - 1), snapshot => Assert.Equal("", snapshot.EmptyMessage));
        Assert.Equal("No files found", (await kit.SearchAsync("zzz", QuickSearchResultType.Files))[^1].EmptyMessage);
        Assert.Equal("No applications found", (await kit.SearchAsync("zzz", QuickSearchResultType.Applications))[^1].EmptyMessage);
        Assert.Equal("No actions found", (await kit.SearchAsync("zzz", QuickSearchResultType.Actions))[^1].EmptyMessage);

        // The clipboard says it is off, rather than that it is empty, while its history is off.
        Assert.Contains("Clipboard history is off", (await kit.SearchAsync("", QuickSearchResultType.Clipboard))[^1].EmptyMessage);
        kit.History.SetEnabled(true);
        Assert.Equal("Nothing copied yet", (await kit.SearchAsync("", QuickSearchResultType.Clipboard))[^1].EmptyMessage);
        Assert.Equal("Nothing copied matches", (await kit.SearchAsync("x", QuickSearchResultType.Clipboard))[^1].EmptyMessage);
    }

    [Fact]
    public async Task ANarrowedListHasNoTopHitAndListsOnlyThatKind()
    {
        var apps = new FakeQuickProvider("applications", QuickSearchResultType.Applications, 100, _ => [QuickApp("Notes One", "n1")]);
        var actions = new FakeQuickProvider("actions", QuickSearchResultType.Actions, 80, _ => [QuickAction("New note", "assistant.note")]);
        var kit = new QuickKit([apps, actions]);

        var narrowed = (await kit.SearchAsync("note", QuickSearchResultType.Actions))[^1];

        Assert.Equal(["New note"], narrowed.Sections.SelectMany(section => section.Items).Select(row => row.Title));
        Assert.Empty(apps.Asked);
        Assert.Single(narrowed.Sections);
        Assert.Equal(QuickSearchGroups.ScopedLimit, actions.Asked[0].MaxResults);
    }

    [Fact]
    public void AnApplicationsOwnIconIsDrawnAndBytesThatAreNotAPictureFallBackOnTheGlyph() => RunSta(() =>
    {
        var provider = new FakeQuickProvider("applications", QuickSearchResultType.Applications, 100, _ =>
            [QuickApp("Brave Browser", "b", icon: TinyPng), QuickApp("Broken App", "x", icon: [1, 2, 3])]);
        var kit = new QuickKit([provider]);

        var rows = kit.SearchAsync("br").GetAwaiter().GetResult()[^1].Sections.SelectMany(section => section.Items).ToArray();

        var brave = rows.Single(row => row.Title == "Brave Browser");
        Assert.Null(brave.Icon.GlyphKey);
        var image = Assert.IsAssignableFrom<ImageSource>(brave.Icon.Image);
        Assert.True(image.IsFrozen);

        // The same bytes are one image however many times they are listed.
        var again = kit.SearchAsync("br").GetAwaiter().GetResult()[^1].Sections.SelectMany(section => section.Items).Single(row => row.Title == "Brave Browser");
        Assert.Same(image, again.Icon.Image);

        var broken = rows.Single(row => row.Title == "Broken App");
        Assert.Equal("Result.Icon.App", broken.Icon.GlyphKey);
    });

    [Fact]
    public async Task ACopiedTextIsPutBackAttachedOrTakenOutOfTheHistory()
    {
        var history = new ClipboardHistory(clock: new FixedClock(QuickNow));
        history.SetEnabled(true);
        history.Add("the quarterly figures are due Friday");
        var clip = new ClipboardQuickSearchProvider(history, new ClipboardAllowedPolicy());
        var kit = new QuickKit([clip], history: history).Listen();
        var attached = new List<TextAttachment>();
        kit.Attach.TextRequested += (_, text) => attached.Add(text);

        var row = (await kit.SearchAsync("quarterly"))[^1].Sections.SelectMany(section => section.Items).Single();

        Assert.Equal(SearchResultKind.Clipboard, row.Kind);
        Assert.Equal(("Copy", "enter"), (row.ActionHint!.Text, row.ActionHint.Key));
        Assert.Equal("Result.Icon.Clipboard", row.Icon.GlyphKey);
        Assert.Equal(["Attach to conversation", "Remove from history"], row.Alternates.Select(alternate => alternate.Title));

        row.Command.Execute(null);
        Assert.Equal(["the quarterly figures are due Friday"], kit.Clipboard.Copied);
        Assert.Equal(1, kit.Finished);

        row.Alternates[0].Command.Execute(null);
        Assert.Equal("the quarterly figures are due Friday", Assert.Single(attached).Text);

        // Taking it out forgets it and asks for the list again; copying a copy that is gone says so.
        var listChanged = 0;
        kit.Runner.ListChanged += (_, _) => listChanged++;
        row.Alternates[1].Command.Execute(null);
        Assert.Empty(history.Items);
        Assert.Equal(1, listChanged);
        row.Command.Execute(null);
        Assert.Equal("That is no longer in the clipboard history.", Assert.Single(kit.Failures));
        Assert.Null(kit.Usage.Find(row.Key!));
    }

    private sealed class ClipboardAllowedPolicy : IPermissionPolicy
    {
        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PermissionDecision(
                capability, capability == PermissionCapability.ClipboardHistory ? PermissionDecisionReason.Granted : PermissionDecisionReason.TurnedOff));
    }
}
