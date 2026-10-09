using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Ipc;
using Assistant.Core.Settings;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Explorer;
using Assistant.UI.Messages;
using Assistant.UI.Windowing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- File Explorer's Ask Assistant (PROJECT_SPEC §4.4) ------------------------------------------------------------------------

    // -- What the checked files become. --

    [Fact]
    public void ExplorerFilesArePicturesAndDocuments_AndEveryFileLeftOutIsNamed()
    {
        var files = ExplorerFiles.From(
        [
            new(@"C:\Pictures\cat.png", InvokedFileProblem.None),
            new(@"C:\Docs\plan.docx", InvokedFileProblem.None),
            new(@"C:\Pictures\dog.JPG", InvokedFileProblem.None),
            new(@"C:\Docs\notes.md", InvokedFileProblem.None),
            new(@"C:\Docs\gone.pdf", InvokedFileProblem.NotFound),
            new(@"C:\Docs\tool.exe", InvokedFileProblem.NotSupported),
            new(@"C:\Docs\plan.docx", InvokedFileProblem.Repeated),
            new(@"C:\Docs\more.md", InvokedFileProblem.OverLimit),
            new(@"C:\Docs\more2.md", InvokedFileProblem.OverLimit),
        ]);

        Assert.Equal([@"C:\Pictures\cat.png", @"C:\Pictures\dog.JPG"], files.Pictures);
        Assert.Equal([@"C:\Docs\plan.docx", @"C:\Docs\notes.md"], files.Documents);
        Assert.True(files.HasFiles);
        Assert.Equal(
            "gone.pdf couldn't be found. The Assistant can't read tool.exe. Only 10 files can be sent at once, so 2 more were left out.",
            files.Notice);

        // Nothing to say when everything was attached; long lists are counted.
        Assert.Null(ExplorerFiles.From([new(@"C:\Docs\plan.docx", InvokedFileProblem.None)]).Notice);
        var gone = ExplorerFiles.From([.. "abcde".Select(letter => new InvokedFile($@"C:\{letter}.pdf", InvokedFileProblem.NotFound))]);
        Assert.False(gone.HasFiles);
        Assert.Equal("a.pdf, b.pdf, c.pdf and 2 other files couldn't be found.", gone.Notice);

        // Several documents are all attached: a question reads them all.
        var documents = ExplorerFiles.From([.. "abc".Select(letter => new InvokedFile($@"C:\{letter}.md", InvokedFileProblem.None))]);
        Assert.Null(documents.Notice);
        Assert.Equal([@"C:\a.md", @"C:\b.md", @"C:\c.md"], documents.Documents);
    }

    [Fact]
    public void FilesThatComeTogetherAreHandedOnAsOneBatch_InOrder_AndALaterOneStartsAnother()
    {
        var batches = new System.Collections.Concurrent.BlockingCollection<IReadOnlyList<string>>();
        using var batcher = new InvocationBatcher(batches.Add, quiet: TimeSpan.FromMilliseconds(150), maxWait: TimeSpan.FromSeconds(5));

        batcher.Add([@"C:\a.md"]);
        batcher.Add([@"C:\b.md"]);
        batcher.Add([@"C:\c.png", @"C:\d.png"]);
        Assert.True(batches.TryTake(out var first, TimeSpan.FromSeconds(5)));
        Assert.Equal([@"C:\a.md", @"C:\b.md", @"C:\c.png", @"C:\d.png"], first);

        batcher.Add([@"C:\e.md"]);
        Assert.True(batches.TryTake(out var second, TimeSpan.FromSeconds(5)));
        Assert.Equal([@"C:\e.md"], second);
        Assert.False(batches.TryTake(out _, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void ABatchWaitsNoLongerThanItsLimit_EvenWhileFilesKeepComing()
    {
        var batches = new System.Collections.Concurrent.BlockingCollection<IReadOnlyList<string>>();
        using var batcher = new InvocationBatcher(batches.Add, quiet: TimeSpan.FromMilliseconds(400), maxWait: TimeSpan.FromMilliseconds(500));

        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20 && batches.Count == 0; i++)
        {
            batcher.Add([$@"C:\{i}.md"]);
            Thread.Sleep(100);
        }

        Assert.True(batches.TryTake(out var batch, TimeSpan.FromSeconds(5)));
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1900));
        Assert.InRange(batch.Count, 3, 15);
    }

    [Fact]
    public void ABatchThatComesBeforeTheWindowWaitsForIt_AndLaterOnesGoStraightThere()
    {
        var requests = new ExplorerFileRequests();
        var late = new ExplorerFiles([], [@"C:\b.md"], null);

        // Two batches that came while the app started are one choice of the menu: the window opens once, with both.
        requests.Post(new ExplorerFiles([@"C:\old.png"], [], null));
        requests.Post(new ExplorerFiles([@"C:\a.png", @"C:\OLD.png"], [], null));

        var opened = new List<ExplorerFiles>();
        var posted = 0;
        requests.Connect(opened.Add, action =>
        {
            posted++;
            action();
        });
        var first = Assert.Single(opened);
        Assert.Equal([@"C:\old.png", @"C:\a.png"], first.Pictures);

        requests.Post(late);
        Assert.Equal(2, opened.Count);
        Assert.Same(late, opened[1]);
        Assert.Equal(2, posted);
    }

    [Fact]
    public void BatchesThatWaitAreJoined_WithEveryDocument_AndEachNoticeOnce()
    {
        var joined = ExplorerFiles.Combine(
            new ExplorerFiles([@"C:\a.png"], [@"C:\Docs\plan.docx"], "gone.pdf couldn't be found."),
            new ExplorerFiles([@"C:\b.png"], [@"C:\Docs\other.md"], "gone.pdf couldn't be found."));

        Assert.Equal([@"C:\a.png", @"C:\b.png"], joined.Pictures);
        Assert.Equal([@"C:\Docs\plan.docx", @"C:\Docs\other.md"], joined.Documents);
        Assert.Equal("gone.pdf couldn't be found.", joined.Notice);

        // The same document twice is one document, and nothing to say.
        var same = ExplorerFiles.Combine(new ExplorerFiles([], [@"C:\Docs\plan.docx"], null), new ExplorerFiles([], [@"C:\DOCS\plan.docx"], null));
        Assert.Equal([@"C:\Docs\plan.docx"], same.Documents);
        Assert.Null(same.Notice);

        // Joined batches still take no more documents than one question reads.
        var many = ExplorerFiles.Combine(
            new ExplorerFiles([], [.. Enumerable.Range(0, 7).Select(i => $@"C:\Docs\a{i}.md")], null),
            new ExplorerFiles([], [.. Enumerable.Range(0, 6).Select(i => $@"C:\Docs\b{i}.md")], null));
        Assert.Equal(10, many.Documents.Count);
        Assert.Equal("Only 10 files can be sent at once, so 3 more were left out.", many.Notice);
    }

    [Fact]
    public void AFolderInTheSelectionIsNamedAsOne()
    {
        var files = ExplorerFiles.From(
        [
            new(@"C:\Shots\a.png", InvokedFileProblem.None),
            new(@"C:\Shots\Old", InvokedFileProblem.Folder),
        ]);
        Assert.Equal([@"C:\Shots\a.png"], files.Pictures);
        Assert.Equal("Old is a folder, and only files can be attached.", files.Notice);

        var several = ExplorerFiles.From([new(@"C:\A", InvokedFileProblem.Folder), new(@"C:\B", InvokedFileProblem.Folder)]);
        Assert.False(several.HasFiles);
        Assert.Equal("A and B are folders, and only files can be attached.", several.Notice);
    }

    [Fact]
    public async Task TheSameSelectionArrivingAgainWhileTheProcessesStartIsOneChoice_AnotherSelectionIsAnother()
    {
        var requests = new ExplorerFileRequests();
        var opened = new System.Collections.Concurrent.BlockingCollection<ExplorerFiles>();
        requests.Connect(opened.Add, action => action());
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Shots\a.png", @"C:\Shots\b.png", @"C:\Shots\c.png" };
        using var integration = new ExplorerIntegration(
            new ExplorerIntegrationOptions(LocalPipe.CreateUniqueName("Assistant.Tests.Repeat")), requests, new FakeExplorerMenu(),
            new InMemorySettingsService(), NullLogger<InvocationServer>.Instance, NullLogger<ExplorerIntegration>.Instance,
            fileExists: existing.Contains, directoryExists: _ => false);

        string[] selection = [@"C:\Shots\a.png", @"C:\Shots\b.png"];
        integration.Handle(new(InvocationAction.AskAboutFiles, selection));
        Assert.True(opened.TryTake(out var first, TimeSpan.FromSeconds(10)));
        Assert.Equal(selection, first.Pictures);

        // The straggling processes of the same choice send it again, in another spelling and order.
        integration.Handle(new(InvocationAction.AskAboutFiles, [@"c:/shots/B.png", @"\\?\C:\Shots\a.png"]));
        Assert.False(opened.TryTake(out _, TimeSpan.FromMilliseconds(1200)), "The same selection opened the conversation twice.");

        integration.Handle(new(InvocationAction.AskAboutFiles, [@"C:\Shots\c.png"]));
        Assert.True(opened.TryTake(out var other, TimeSpan.FromSeconds(10)));
        Assert.Equal([@"C:\Shots\c.png"], other.Pictures);
        await integration.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void FilesThatCameWhileTheAppWasStartingOpenTheConversationOnceTheWindowIsUp() => RunSta(() => WithTheme(() =>
    {
        // What AppBootstrapper does: the pipe is served from the host's start, the window is made after, and what came in between
        // waits for it; the bar opens at startup, and the files open the conversation.
        var requests = new ExplorerFileRequests();
        requests.Post(new ExplorerFiles([@"C:\Pictures\a.png", @"C:\Pictures\b.png"], [@"C:\Docs\plan.docx"], "notes.md was left out."));
        var assistant = CreateAssistant();
        var (panel, _, model) = assistant;
        try
        {
            requests.Connect(assistant.Controller.OpenWithFiles, action => System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(action));
            assistant.Controller.Invoke();

            WaitUntil(() => model.Chips.Count == 3, "The files were never attached.");
            WaitUntil(() => assistant.Controller.State == AssistantWindowState.FloatingConversation, "The window did not become the conversation.");
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The conversation did not finish showing.");
            Pump();

            Assert.Empty(model.Messages);
            Assert.Equal(["a.png", "b.png"], model.Attachments.Select(image => image.Name));
            Assert.Equal("plan.docx", model.Document!.Name);
            Assert.Equal("notes.md was left out.", model.AttachNotice);
            Assert.True(panel.IsVisible);
            Assert.Equal(Visibility.Visible, Named<Grid>(panel, "Composer").Visibility);
            Assert.Equal(Visibility.Visible, Named<Grid>(panel, "ComposerNotice").Visibility);
        }
        finally { panel.Close(); }
    }));

    // -- The window. --

    [Fact]
    public void ExplorerFilesOpenTheFloatingConversationWithThemAttached_WhateverItShowed()
    {
        var (controller, window, bar, conversation, _, _) = CreateController();
        bar.Query = "half typed";

        controller.OpenWithFiles(new ExplorerFiles([@"C:\Pictures\cat.png"], [@"C:\Docs\plan.docx", @"C:\Docs\notes.md"], "x.zip was left out."));

        Assert.Single(window.ConversationsShown);
        Assert.Equal(AssistantWindowState.FloatingConversation, controller.State);
        Assert.Empty(conversation.Messages);
        Assert.Equal("cat.png", Assert.Single(conversation.Attachments).Name);
        Assert.Equal(@"C:\Pictures\cat.png", conversation.Attachments[0].Path);
        Assert.Equal(["plan.docx", "notes.md"], conversation.Documents.Select(document => document.Name));
        Assert.Equal([AttachmentKind.Image, AttachmentKind.File, AttachmentKind.File], conversation.Chips.Select(chip => chip.Kind));
        Assert.Equal("Ask about these 3 files", conversation.ComposerPlaceholder);
        Assert.Equal("x.zip was left out.", conversation.AttachNotice);
        Assert.True(conversation.CanCompose);
        Assert.Equal("half typed", bar.Query);

        // From a conversation that shows, the files start a new one.
        var first = conversation.Id;
        Assert.True(conversation.Ask("What is in the plan?"));
        controller.OpenWithFiles(new ExplorerFiles([], [@"C:\Docs\other.md"], null));
        Assert.NotEqual(first, conversation.Id);
        Assert.Empty(conversation.Messages);
        Assert.Empty(conversation.Attachments);
        Assert.Equal("other.md", Assert.Single(conversation.Documents).Name);
        Assert.False(conversation.HasAttachNotice);
        Assert.Equal(2, window.ConversationsShown.Count);
    }

    [Fact]
    public void TheNoticeGoesWithTheQuestion_OrWithEsc()
    {
        var conversation = CreateConversationModel(new FakeMicrophone());
        conversation.StartWithFiles([ChipPicture("cat.png")], [], "gone.pdf couldn't be found.");
        Assert.True(conversation.HasAttachNotice);
        Assert.True(conversation.Ask("What is this?"));
        Assert.False(conversation.HasAttachNotice);
        Assert.Equal("cat.png", Assert.Single(conversation.Messages[0].Attachments).Name);

        // Nothing could be attached: the composer still shows, to say so, and Esc takes the notice away.
        conversation.StartWithFiles([], [], "gone.pdf couldn't be found.");
        Assert.True(conversation.CanCompose);
        Assert.False(conversation.HandleEscape());
        Assert.False(conversation.HasAttachNotice);
        Assert.False(conversation.CanCompose);
        Assert.True(conversation.HandleEscape());

        // Asked anyway, the question starts the conversation without the notice.
        conversation.StartWithFiles([], [], "gone.pdf couldn't be found.");
        conversation.Draft = "Hello";
        Assert.True(conversation.AskCommand.CanExecute(null));
        conversation.AskCommand.Execute(null);
        Assert.False(conversation.HasAttachNotice);
        Assert.Equal("Hello", Assert.Single(conversation.Messages, message => message.Role == Assistant.Core.Domain.MessageRole.User).Text);
    }

    [Fact]
    public void TheNoticeShowsAboveTheChipsInTheComposer() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        try
        {
            model.StartWithFiles(
                [ChipPicture("Screenshot 2026-09-30 at 10.12.04.png")], [Attached("Quarterly budget review.pdf"), Attached("Meeting notes.docx")],
                "The Assistant can't read Budget 2026.xlsx.");
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();

            var composer = Named<Grid>(panel, "Composer");
            var notice = Named<Grid>(panel, "ComposerNotice");
            var chips = Named<Assistant.UI.Controls.AttachmentChipList>(panel, "ComposerAttachments");
            Assert.Equal(Visibility.Visible, composer.Visibility);
            Assert.Equal(Visibility.Visible, notice.Visibility);
            Assert.True(BoundsIn(composer, notice).Bottom <= BoundsIn(composer, chips).Top + 0.5);
            Assert.True(BoundsIn(composer, notice).Right <= composer.ActualWidth + 0.5);
            RenderGlass(panel, "explorer-files-2x.png", 2);

            model.Draft = "What changed?";
            model.AskCommand.Execute(null);
            Pump();
            Assert.Equal(Visibility.Collapsed, notice.Visibility);
        }
        finally { panel.Close(); }
    }));

    // -- Opening the panel so the user can ask at once. --

    private static ExplorerFiles ManyFiles() => new(
        [.. Enumerable.Range(1, 8).Select(i => $@"C:\Pictures\Screenshot {i}.png")], [@"C:\Docs\plan.docx"], null);

    [Fact]
    public void TheFilesOpenThePanelWithTheComposerFocused_FromAHiddenWindow_AndFromTheBar() => RunSta(() => WithTheme(() =>
    {
        foreach (var barFirst in new[] { false, true })
        {
            var assistant = CreateAssistant();
            var (panel, _, model) = assistant;
            try
            {
                if (barFirst)
                {
                    assistant.Controller.Invoke();
                    WaitUntil(() => panel.IsVisible, "The bar did not open.");
                }

                assistant.Controller.OpenWithFiles(ManyFiles());
                WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
                Pump();

                // The files are attached and nothing is asked yet; what is typed goes to the composer and nowhere else.
                Assert.Equal(9, model.Chips.Count);
                Assert.Empty(model.Messages);
                var editor = Editor(Named<Assistant.UI.Controls.PromptInputControl>(panel, "ComposerInput"));
                Assert.Same(editor, System.Windows.Input.FocusManager.GetFocusedElement(panel));
                Assert.True(editor.IsVisible);
                Assert.Equal(Visibility.Visible, Named<Grid>(panel, "Composer").Visibility);

                model.Draft = "what are these?";
                Assert.True(model.AskCommand.CanExecute(null));
                model.AskCommand.Execute(null);
                var asked = Assert.Single(model.Messages, message => message.Role == Assistant.Core.Domain.MessageRole.User);
                Assert.Equal("what are these?", asked.Text);
                Assert.Equal(8, asked.Attachments.Count);
                Assert.Equal("plan.docx", asked.Document!.Name);
            }
            finally { panel.Close(); }
        }
    }));

    [Fact]
    public void TheEmptyComposerSaysWhatTheFilesAreFor_ThenIsAFollowUpOnceAskedAbout()
    {
        var conversation = CreateConversationModel(new FakeMicrophone());
        Assert.Equal("Ask a follow-up", conversation.ComposerPlaceholder);

        conversation.StartWithFiles([ChipPicture("a.png")], [], null);
        Assert.Equal("Ask about this picture", conversation.ComposerPlaceholder);

        conversation.StartWithFiles([ChipPicture("a.png"), ChipPicture("b.png")], [], null);
        Assert.Equal("Ask about these 2 pictures", conversation.ComposerPlaceholder);

        conversation.StartWithFiles([], [Attached("Plan.docx")], null);
        Assert.Equal("Ask about this file", conversation.ComposerPlaceholder);

        conversation.StartWithFiles([ChipPicture("a.png")], [Attached("Plan.docx")], null);
        Assert.Equal("Ask about these 2 files", conversation.ComposerPlaceholder);

        conversation.StartWithFiles([], [Attached("Plan.docx"), Attached("Budget.pdf"), Attached("Notes.md")], null);
        Assert.Equal("Ask about these 3 files", conversation.ComposerPlaceholder);

        // A note alone, with nothing attached, has nothing to be about; the first question starts the conversation.
        conversation.StartWithFiles([], [], "gone.pdf couldn't be found.");
        Assert.Equal("Ask a follow-up", conversation.ComposerPlaceholder);

        var changed = new List<string?>();
        conversation.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        conversation.StartWithFiles([ChipPicture("a.png")], [], null);
        Assert.Contains("ComposerPlaceholder", changed);

        Assert.True(conversation.Ask("what is this?"));
        Assert.Equal("Ask a follow-up", conversation.ComposerPlaceholder);
    }

    // Opt-in render (ASSISTANT_UI_RENDER_DIR) of the panel as it opens from File Explorer with ten pictures and a document, and after
    // the first question: the files wait above the composer, which says what they are for, and then sit on the user's message.
    [Fact]
    public void TheMultiFilePanelRenders() => RunSta(() => WithTheme(() =>
    {
        var assistant = CreateAssistant();
        var (panel, _, model) = assistant;
        try
        {
            var pictures = DemoImages.Screenshots(10);
            model.StartWithFiles(pictures, [Attached("Quarterly budget review.pdf")], "Only 10 files can be sent at once, so 10 more were left out.");
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();
            System.Threading.Thread.Sleep(100);
            Pump();
            Assert.Equal("Ask about these 11 files", Named<Assistant.UI.Controls.PromptInputControl>(panel, "ComposerInput").Placeholder);
            RenderGlass(panel, "explorer-panel-open-2x.png", 2);

            model.Draft = "what are these?";
            model.AskCommand.Execute(null);
            Pump();
            System.Threading.Thread.Sleep(100);
            Pump();
            RenderGlass(panel, "explorer-panel-asked-2x.png", 2);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void ManyPicturesOnAMessageAreSmallTilesThreeAcross_AndOneOrTwoStayAtTheirOwnSize() => RunSta(() => WithTheme(() =>
    {
        var (panel, _, model) = CreateAssistant();
        try
        {
            model.StartWithFiles(DemoImages.Screenshots(10), [], null);
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            model.Draft = "what are these?";
            model.AskCommand.Execute(null);
            Pump();

            var area = Named<Grid>(panel, "ConversationLayer");
            var list = Descendants<ItemsControl>(area).Single(control => control.Name == "Attachments");
            Assert.IsType<WrapPanel>(Descendants<Panel>(list).First(panelInList => panelInList.IsItemsHost));
            var tiles = Descendants<System.Windows.Controls.Image>(list)
                .Select(image => BoundsIn(area, (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(image)))
                .ToArray();
            Assert.Equal(10, tiles.Length);
            Assert.All(tiles, tile => Assert.Equal((88, 88), (tile.Width, tile.Height)));

            // Three across, 6 apart, four rows (3 + 3 + 3 + 1), none past the bubble's width, the last at the message's right edge.
            Assert.Equal([3, 3, 3, 1], tiles.GroupBy(tile => tile.Top).OrderBy(row => row.Key).Select(row => row.Count()));
            Assert.All(tiles, tile => Assert.True(tile.Right <= BoundsIn(area, list).Right + 0.5));
            Assert.True(BoundsIn(area, list).Width <= 286 + 0.5);
            var columns = tiles.Select(tile => tile.Left).Distinct().Order().ToArray();
            Assert.Equal(3, columns.Length);
            Assert.Equal([94.0, 94.0], [columns[1] - columns[0], columns[2] - columns[1]]);

            // One picture is drawn at its own proportions, as before.
            model.StartWithFiles([DemoImages.Screenshots(1)[0]], [], null);
            model.Draft = "what is this?";
            model.AskCommand.Execute(null);
            Pump();
            var single = Descendants<ItemsControl>(area).Single(control => control.Name == "Attachments");
            var image = Descendants<System.Windows.Controls.Image>(single).Single();
            Assert.True(image.ActualWidth > 200, $"A single picture is {image.ActualWidth} wide.");
        }
        finally { panel.Close(); }
    }));

    // -- The settings switch. --

    [Fact]
    public void SettingsRecognizesAnExplorerEntryRegisteredByAnOlderInstaller() => RunSta(() =>
    {
        var menu = new FakeExplorerMenu { Installed = true };
        var kit = CreateSettingsKit(explorerMenu: menu);
        Assert.True(kit.Model.Integrations.ExplorerContextMenu);
        Assert.True(kit.Saved.Integrations.ExplorerContextMenuEnabled);
        Assert.Equal(0, menu.Installs);
        kit.Model.Integrations.ExplorerContextMenu = false;
        kit.Settle();
        menu.Installed = false;
        SettingsWait(kit.Model.LoadAsync());
        Assert.False(kit.Model.Integrations.ExplorerContextMenu);
    });

    [Fact]
    public void TheFileExplorerSwitchAddsAndRemovesTheEntry_AndIsSavedOnlyWhenThatWorked() => RunSta(() =>
    {
        var menu = new FakeExplorerMenu();
        var kit = CreateSettingsKit(explorerMenu: menu);
        var page = kit.Model.Integrations;

        page.ExplorerContextMenu = true;
        kit.Settle();
        Assert.Equal(1, menu.Installs);
        Assert.True(kit.Saved.Integrations.ExplorerContextMenuEnabled);
        Assert.False(kit.Model.HasNotice);

        page.ExplorerContextMenu = false;
        kit.Settle();
        Assert.Equal(1, menu.Removals);
        Assert.False(kit.Saved.Integrations.ExplorerContextMenuEnabled);

        // A registry that cannot be written: the switch goes back, nothing is saved, and the window says so.
        menu.Works = false;
        page.ExplorerContextMenu = true;
        kit.Settle();
        Assert.False(page.ExplorerContextMenu);
        Assert.False(kit.Saved.Integrations.ExplorerContextMenuEnabled);
        Assert.Equal("Ask Assistant couldn't be added to File Explorer's menu.", kit.Model.Notice);

        Assert.Equal(2, menu.Installs);

        // Showing the saved settings never installs anything.
        var shown = CreateSettingsKit(new AppSettings { Integrations = new IntegrationSettings { ExplorerContextMenuEnabled = true } }, explorerMenu: menu);
        Assert.True(shown.Model.Integrations.ExplorerContextMenu);
        Assert.Equal(2, menu.Installs);
        Assert.Equal(1, menu.Removals);
    });

    [Fact]
    public void WhileTheEntryIsOnlyUnderShowMoreOptions_SettingsSaysWhyAndHowToGetItIntoTheFirstMenu() => RunSta(() =>
    {
        // Windows 11 takes the Assistant's first-menu entry only while Developer Mode is on, so with it off the entry is under Show more options and the page says so.
        var menu = new FakeExplorerMenu { InFirstMenu = false };
        var kit = CreateSettingsKit(explorerMenu: menu);
        var page = kit.Model.Integrations;
        var opened = new List<string>();
        page.OpenSettingsPage = opened.Add;
        Assert.False(page.HasExplorerFirstMenuNotice);

        page.ExplorerContextMenu = true;
        kit.Settle();
        Assert.True(page.HasExplorerFirstMenuNotice);
        Assert.Contains("Show more options", page.ExplorerFirstMenuNotice, StringComparison.Ordinal);
        Assert.Contains("Developer Mode", page.ExplorerFirstMenuNotice, StringComparison.Ordinal);

        // The button opens Windows' own page for it and changes nothing itself.
        page.OpenDeveloperSettingsCommand.Execute(null);
        Assert.Equal(["ms-settings:developers"], opened);

        // Once Windows has accepted the package, Check again adds the entry again and the notice goes.
        menu.InFirstMenu = true;
        var installs = menu.Installs;
        page.CheckExplorerMenuAgainCommand.Execute(null);
        kit.Settle();
        Assert.Equal(installs + 1, menu.Installs);
        Assert.False(page.HasExplorerFirstMenuNotice);

        // And with the switch off there is nothing to say.
        menu.InFirstMenu = false;
        page.ExplorerContextMenu = false;
        kit.Settle();
        Assert.False(page.HasExplorerFirstMenuNotice);
    });

    // -- The app's side, through its pipe. --

    [Fact]
    public async Task FilesSentOverThePipeReachTheWindowAsOneCheckedBatch_AndTheEntryIsRefreshedAtStart()
    {
        var settings = new InMemorySettingsService();
        await settings.SaveAsync(new AppSettings { Integrations = new IntegrationSettings { ExplorerContextMenuEnabled = true } });
        var menu = new FakeExplorerMenu();
        var requests = new ExplorerFileRequests();
        var opened = new System.Collections.Concurrent.BlockingCollection<ExplorerFiles>();
        requests.Connect(opened.Add, action => action());
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Docs\plan.docx", @"C:\Pictures\cat.png" };
        var pipe = LocalPipe.CreateUniqueName("Assistant.Tests.Explorer");
        using var integration = new ExplorerIntegration(
            new ExplorerIntegrationOptions(pipe), requests, menu, settings, NullLogger<InvocationServer>.Instance,
            NullLogger<ExplorerIntegration>.Instance, fileExists: existing.Contains);

        await integration.StartAsync(CancellationToken.None);
        try
        {
            // File Explorer's one process per file.
            var replies = await Task.WhenAll(
                SendToAppAsync(pipe, @"C:\Docs\plan.docx"),
                SendToAppAsync(pipe, @"C:\Pictures\cat.png"),
                SendToAppAsync(pipe, @"C:\Docs\gone.md"));
            Assert.All(replies, reply => Assert.True(reply.IsAccepted));

            Assert.True(opened.TryTake(out var files, TimeSpan.FromSeconds(10)), "The files never reached the window.");
            Assert.Equal([@"C:\Pictures\cat.png"], files.Pictures);
            Assert.Equal([@"C:\Docs\plan.docx"], files.Documents);
            Assert.Equal("gone.md couldn't be found.", files.Notice);
            Assert.False(opened.TryTake(out _, TimeSpan.FromMilliseconds(700)), "The files came in more than one batch.");

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (menu.Installs == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.Equal(1, menu.Installs);
        }
        finally
        {
            await integration.StopAsync(CancellationToken.None);
        }

        Assert.True(integration.Serving.IsCompleted);
        Assert.Equal(InvocationErrorCode.Unavailable, integration.Handle(new(InvocationAction.AskAboutFiles, [@"C:\a.md"])).Error);
    }

    private static async Task<InvocationReply> SendToAppAsync(string pipeName, string path)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, LocalPipe.Options);
        await pipe.ConnectAsync(10_000);
        return await InvocationClient.SendAsync(pipe, new InvocationRequest(InvocationAction.AskAboutFiles, [path]));
    }

    private sealed class FakeExplorerMenu : IExplorerMenuInstaller
    {
        private int _installs;
        private int _removals;

        public bool Works { get; set; } = true;
        public bool? Installed { get; set; }
        public Task<bool?> IsInstalledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Installed);

        public bool InFirstMenu { get; set; } = true;

        public Task<bool> IsInFirstMenuAsync(CancellationToken cancellationToken = default) => Task.FromResult(InFirstMenu);

        public int Installs => Volatile.Read(ref _installs);

        public int Removals => Volatile.Read(ref _removals);

        public Task<bool> InstallAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _installs);
            return Task.FromResult(Works);
        }

        public Task<bool> RemoveAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _removals);
            return Task.FromResult(Works);
        }
    }
}
