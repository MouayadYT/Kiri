using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Assistant.Core.Activity;
using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Assistant.Documents;
using Assistant.Documents.Context;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.History;
using Assistant.UI.Messages;
using Assistant.UI.Search;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Assistant.Windows.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- Asking about one attached file (PROJECT_SPEC §4.2, §4.7) ------------------------------------------------------------

    private static DocumentAttachment Attached(string name = "Handbook.md", string? path = null) =>
        new(name, path ?? @"C:\Docs\" + name);

    // The real document service over the five readers, as the app has it.
    private static IDocumentContextService RealDocuments() =>
        new ServiceCollection().AddLogging().AddAssistantDocuments().BuildServiceProvider().GetRequiredService<IDocumentContextService>();

    // The provider over the real orchestrator and the real readers, asking the scripted model.
    private static ModelAnswerProvider DocumentAnswers(
        ScriptedModel model, IPermissionPolicy? permissions, IDocumentContextService? documents = null, ISettingsService? settings = null,
        IConversationFiles? files = null)
    {
        settings ??= new InMemorySettingsService();
        return new ModelAnswerProvider(
            new AssistantOrchestrator(
                model, settings, new PromptBuilder(), new ImagePreprocessor(), new FixedClock(Now), NullLogger<AssistantOrchestrator>.Instance),
            new FixedClock(Now), permissions, new AttachedDocuments(permissions, documents, model, settings), files: files);
    }

    private static SettingsPermissionPolicy FilesAllowed() => new(new InMemorySettingsService());

    // A Markdown file with a section for each of several rooms, each about a page long: too much for a prompt, so only what the
    // question points to is sent.
    private static string HandbookText()
    {
        var rooms = new[] { "Arrival", "Kitchen", "Garden", "Workshop", "Library", "Cellar", "Attic", "Studio", "Garage", "Porch" };
        var text = new StringBuilder("# House handbook\n\nWelcome to the house.\n\n");
        foreach (var room in rooms)
        {
            text.Append("## ").Append(room).Append("\n\n");
            var lower = room.ToLowerInvariant();
            for (var paragraph = 0; paragraph < 4; paragraph++)
            {
                text.Append($"The {lower} is arranged for the {lower} routine number {paragraph}. Guests should treat the {lower} with care and leave it as they found it, ");
                text.Append("closing every door and window behind them and switching off the lights, which are on a timer that the owner sets each week.\n\n");
            }
        }

        return text.ToString();
    }

    private sealed class TempDocument : IDisposable
    {
        private readonly string _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "assistant-document-ui-" + Guid.NewGuid().ToString("N"));

        public TempDocument(string name, string content)
        {
            Directory.CreateDirectory(_folder);
            Path = System.IO.Path.Combine(_folder, name);
            File.WriteAllText(Path, content, new UTF8Encoding(false));
        }

        public string Path { get; }

        public DocumentAttachment Attachment => new(System.IO.Path.GetFileName(Path), Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary folder is harmless.
            }
        }
    }

    // -- The conversation holds one attached file. --

    [Fact]
    public void ADocumentAttachedToTheConversation_IsWhatTheNextQuestionIsAbout()
    {
        var conversation = CreateConversationModel(new FakeMicrophone());
        var document = Attached("Budget.pdf");

        conversation.StartWithDocument(document);

        Assert.Same(document, conversation.Document);
        Assert.Empty(conversation.Messages);
        Assert.Empty(conversation.Attachments);
        Assert.True(conversation.CanCompose);

        // The same file again (however its path is written) changes nothing; another file is attached beside it.
        Assert.False(conversation.Attach(Attached("budget.pdf", @"C:\DOCS\BUDGET.PDF")));
        Assert.Same(document, conversation.Document);
        var other = Attached("Plan.docx");
        Assert.True(conversation.Attach(other));
        Assert.Equal([document, other], conversation.Documents);
        Assert.Same(document, conversation.Document);

        // Asking puts them on the user's message, and the composer has nothing attached any more.
        conversation.Draft = "What do they say?";
        Assert.True(conversation.AskCommand.CanExecute(null));
        Assert.True(conversation.Ask("What do they say?"));
        var question = Assert.Single(conversation.Messages, message => message.Role == MessageRole.User);
        Assert.Equal([document, other], question.Documents);
        Assert.Same(document, question.Document);
        Assert.True(question.HasAttachments);
        Assert.Null(conversation.Document);
        Assert.Empty(conversation.Documents);
        Assert.Empty(conversation.Attachments);
    }

    [Fact]
    public void TheDocumentAndPicturesAttachedTogetherGoOntoTheSameQuestion()
    {
        var conversation = CreateConversationModel(new FakeMicrophone());
        var image = new ImageItem("shot.png", @"C:\Pictures\shot.png");
        var document = Attached("Notes.txt");
        conversation.StartWithAttachment(image);

        conversation.Attach(document);
        conversation.Ask("How do they relate?");

        var question = Assert.Single(conversation.Messages, message => message.Role == MessageRole.User);
        Assert.Same(image, Assert.Single(question.Attachments));
        Assert.Same(document, question.Document);
    }

    [Fact]
    public void TheAttachedFileCanBeTakenOffBeforeAsking_AndOnlyTheOneThatIsAttached()
    {
        var conversation = CreateConversationModel(new FakeMicrophone());
        var document = Attached("Budget.pdf");
        conversation.StartWithDocument(document);
        var changes = new List<string?>();
        conversation.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        conversation.RemoveAttachmentCommand.Execute(Attached("Budget.pdf"));
        Assert.Same(document, conversation.Document);
        conversation.RemoveAttachmentCommand.Execute(document);

        Assert.Null(conversation.Document);
        Assert.False(conversation.CanCompose);
        Assert.Contains(nameof(ConversationViewModel.Document), changes);
        Assert.Contains(nameof(ConversationViewModel.CanCompose), changes);
        Assert.False(conversation.AskCommand.CanExecute(null));
    }

    [Fact]
    public void EscTakesOffTheAttachedFileAfterWhatWasTyped_AndBeforeItClosesThePanel()
    {
        var conversation = CreateConversationModel(new FakeMicrophone());
        conversation.StartWithDocument(Attached());
        conversation.Draft = "half a question";

        Assert.False(conversation.HandleEscape());
        Assert.Equal("", conversation.Draft);
        Assert.NotNull(conversation.Document);

        Assert.False(conversation.HandleEscape());
        Assert.Null(conversation.Document);
        Assert.True(conversation.HandleEscape());
    }

    [Fact]
    public void ANewConversationStartsWithNothingAttached()
    {
        var conversation = CreateConversationModel(new FakeMicrophone());
        conversation.StartWithDocument(Attached());

        conversation.StartNew("A fresh question");
        Assert.Null(conversation.Document);

        conversation.StartWithDocument(Attached());
        conversation.StartWithAttachment(new ImageItem("a.png", @"C:\Pictures\a.png"));
        Assert.Null(conversation.Document);
        Assert.Single(conversation.Attachments);

        conversation.StartWithDocument(Attached());
        Assert.Empty(conversation.Attachments);
        Assert.NotNull(conversation.Document);
    }

    // -- Attaching from the bar, from an answer's list of files. --

    [Fact]
    public void ArrowKeysPickTheRowTabAttaches_InTheRealWindow_NotJustTheTopOne() => RunSta(() => WithTheme(() =>
    {
        var (first, second, third) = (new RecordingCommand(), new RecordingCommand(), new RecordingCommand());
        SearchResultViewModel Row(string title, RecordingCommand attach) => new(SearchResultKind.File, title, new RecordingCommand(), actions:
            [new SearchResultAction(Key.Tab, ModifierKeys.None, "Attach to conversation", attach)]);
        var assistant = CreateAssistant(animations: false);
        var (window, bar, _) = assistant;
        try
        {
            window.ShowAndFocus();
            bar.Query = "milestone";
            bar.Results.SetSections([new SearchResultSectionViewModel(null,
            [
                Row("One.docx", first), Row("Two.docx", second), Row("Three.docx", third),
                new SearchResultViewModel(SearchResultKind.File, "Notes.js", new RecordingCommand()),
            ])]);
            Pump();
            Assert.True(window.IsResultsShown);

            PressPreviewKey(window, Key.Down);
            PressPreviewKey(window, Key.Down);
            Assert.Equal("Two.docx", bar.Results.SelectedItem!.Title);
            var tab = PressPreviewKey(window, Key.Tab);

            Assert.True(tab.Handled);
            Assert.Equal((0, 1, 0), (first.Count, second.Count, third.Count));

            // Up again picks another; it is the highlighted row that is attached, never the top one by default.
            PressPreviewKey(window, Key.Down);
            Assert.Equal("Three.docx", bar.Results.SelectedItem!.Title);
            PressPreviewKey(window, Key.Tab);
            Assert.Equal((0, 1, 1), (first.Count, second.Count, third.Count));

            // A row that cannot be attached still keeps Tab from moving the focus to the microphone.
            PressPreviewKey(window, Key.Down);
            Assert.Equal("Notes.js", bar.Results.SelectedItem!.Title);
            Assert.True(PressPreviewKey(window, Key.Tab).Handled);
            Assert.Equal((0, 1, 1), (first.Count, second.Count, third.Count));
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void ADocumentAttachedFromTheBarsResults_GrowsTheBarIntoAConversationThatHasItAttached() => RunSta(() => WithTheme(() =>
    {
        var requests = new AttachRequests();
        var assistant = CreateAssistant(animations: false, attachRequests: requests);
        var (window, bar, conversation) = assistant;
        var document = Attached("Budget.pdf");
        try
        {
            window.ShowAndFocus();
            bar.Query = "budget";
            Assert.Equal(AssistantWindowState.Compact, window.State);

            requests.Request(document);

            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
            Assert.Empty(conversation.Messages);
            Assert.Same(document, conversation.Document);
            Assert.True(conversation.CanCompose);
            WaitUntil(() => bar.Query == "", "The bar's draft was not cleared.");

            // Only the bar attaches: while a conversation is open, another request is ignored.
            requests.Request(Attached("Other.docx"));
            Assert.Same(document, conversation.Document);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void AnAnswersFileMenuAttachesAReadableDocumentToTheConversation_AndNotOtherFiles() => RunSta(() => WithTheme(() =>
    {
        var assistant = CreateAssistant(fileLauncher: new RecordingLauncher());
        var (panel, _, model) = assistant;
        var clock = new FixedClock(Now);
        var pdf = new FileItem(SearchResultItemType.File, "Report.pdf", @"C:\Docs\Report.pdf", clock: clock);
        var sheet = new FileItem(SearchResultItemType.File, "Budget.xlsx", @"C:\Docs\Budget.xlsx", clock: clock);
        var folder = new FileItem(SearchResultItemType.Folder, "Docs.pdf", @"C:\Docs\Docs.pdf", clock: clock);
        var answer = new MessageViewModel(MessageRole.Assistant, "Found these.");
        answer.Content.Add(new FileCollection([pdf, sheet, folder]));
        model.Messages.Add(answer);
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();
            var area = Named<Grid>(panel, "ConversationLayer");
            Grid RootOf(object content) => Assert.IsType<Grid>(System.Windows.Media.VisualTreeHelper.GetChild(
                Descendants<ContentPresenter>(area).Single(presenter => ReferenceEquals(presenter.Content, content)), 0));

            Assert.True(FileActions.Attach.CanExecute(pdf, RootOf(pdf)));
            Assert.False(FileActions.Attach.CanExecute(sheet, RootOf(sheet)));
            Assert.False(FileActions.Attach.CanExecute(folder, RootOf(folder)));

            FileActions.Attach.Execute(pdf, RootOf(pdf));

            var attached = Assert.IsType<DocumentAttachment>(model.Document);
            Assert.Equal(("Report.pdf", @"C:\Docs\Report.pdf"), (attached.Name, attached.Path));

            // A second readable file is attached beside it.
            FileActions.Attach.Execute(new FileItem(SearchResultItemType.File, "Plan.docx", @"C:\Docs\Plan.docx", clock: clock), RootOf(pdf));
            Assert.Equal(["Report.pdf", "Plan.docx"], model.Documents.Select(document => document.Name));
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void TheFileActionsTellADocumentFromAnImageAndFromAnyOtherFile()
    {
        Assert.Equal("Notes.md", FileActions.DocumentOf(new FileItem(SearchResultItemType.File, "Notes", @"C:\Docs\Notes.md"))!.Name);
        Assert.Null(FileActions.DocumentOf(new FileItem(SearchResultItemType.File, "a.png", @"C:\Docs\a.png")));
        Assert.Null(FileActions.DocumentOf(new FileItem(SearchResultItemType.File, "b.xlsx", @"C:\Docs\b.xlsx")));
        Assert.Null(FileActions.DocumentOf(new ImageItem("a.png", @"C:\Docs\a.png")));
        Assert.Null(FileActions.DocumentOf("a.pdf"));
        Assert.Null(FileActions.ImageOf(new FileItem(SearchResultItemType.File, "a.pdf", @"C:\Docs\a.pdf")));
    }

    [Fact]
    public void TheConversationViewRaisesADocumentAttachRequest_OnlyWhereTheHostTakesAttachments() => RunSta(() => WithTheme(() =>
    {
        var view = new ConversationView();
        var file = new FileItem(SearchResultItemType.File, "a.pdf", @"C:\a.pdf");
        var documents = new List<DocumentAttachment>();
        var images = new List<ImageItem>();
        view.DocumentAttachRequested += (_, document) => documents.Add(document);
        view.AttachRequested += (_, image) => images.Add(image);

        // A host that takes pictures but no documents (and the other way round, as the History window's composer does).
        Assert.False(FileActions.Attach.CanExecute(file, view));
        view.CanAttach = true;
        Assert.False(FileActions.Attach.CanExecute(file, view));
        view.CanAttach = false;
        view.CanAttachDocuments = true;
        Assert.True(FileActions.Attach.CanExecute(file, view));
        Assert.False(FileActions.Attach.CanExecute(new FileItem(SearchResultItemType.File, "a.png", @"C:\a.png"), view));
        FileActions.Attach.Execute(file, view);

        Assert.Equal([@"C:\a.pdf"], documents.Select(document => document.Path));
        Assert.Empty(images);
    }));

    // -- How it looks. --

    [Fact]
    public void TheComposerShowsTheAttachedFileByItsNameAboveWhatIsTyped_AndTakesItOffWithItsButton() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        var document = Attached("Quarterly budget review.pdf");
        model.StartWithDocument(document);
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();

            var composer = Named<Grid>(panel, "Composer");
            var chips = Named<Assistant.UI.Controls.AttachmentChipList>(panel, "ComposerAttachments");
            var chip = Descendants<Border>(chips).Single(border => border.Name == "Chip");
            Assert.Equal(Visibility.Visible, composer.Visibility);
            Assert.Equal(Visibility.Visible, chips.Visibility);
            Assert.Same(document, Assert.Single(model.Chips).Source);
            Assert.Equal(AttachmentKind.File, Assert.Single(model.Chips).Kind);

            // The file's name is shown, and it names the chip for assistive technology.
            var name = Descendants<TextBlock>(chip).Single();
            Assert.Equal("Quarterly budget review.pdf", name.Text);
            Assert.Equal("Quarterly budget review.pdf", System.Windows.Automation.AutomationProperties.GetName(chip));

            // It lies inside the same glass, above the input, and no picture tiles are shown.
            var input = Named<Assistant.UI.Controls.PromptInputControl>(panel, "ComposerInput");
            Assert.True(BoundsIn(composer, chip).Bottom <= BoundsIn(composer, input).Top + 0.5);
            Assert.True(BoundsIn(composer, chip).Height > 0);

            var remove = Descendants<Button>(chip).Single();
            Assert.Equal("Remove attached file", System.Windows.Automation.AutomationProperties.GetName(remove));
            Assert.Same(model.RemoveAttachmentCommand, remove.Command);
            Assert.Same(model.Chips.Single(), remove.CommandParameter);
            remove.Command.Execute(remove.CommandParameter);
            Pump();

            // With nothing attached and no message, the composer is gone again, and so is the row of chips.
            Assert.Null(model.Document);
            Assert.Empty(model.Chips);
            Assert.Equal(Visibility.Collapsed, chips.Visibility);
            Assert.Equal(Visibility.Collapsed, composer.Visibility);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void AFileNameTooLongForTheComposerIsCutWithAnEllipsis_NotOverflowed() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        model.StartWithDocument(Attached(new string('n', 200) + ".pdf"));
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();

            var composer = Named<Grid>(panel, "Composer");
            var chip = Descendants<Border>(Named<Assistant.UI.Controls.AttachmentChipList>(panel, "ComposerAttachments"))
                .Single(border => border.Name == "Chip");

            Assert.True(BoundsIn(composer, chip).Right <= composer.ActualWidth + 0.5, $"{BoundsIn(composer, chip)} in {composer.ActualWidth}");
            Assert.Equal(System.Windows.TextTrimming.CharacterEllipsis, Descendants<TextBlock>(chip).Single().TextTrimming);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void TheUsersMessageShowsTheFileItWasAskedAbout_AboveItsBubble() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        var document = Attached("Budget.pdf");
        var asked = new MessageViewModel(MessageRole.User, "What is the total?", null, document);
        var plain = new MessageViewModel(MessageRole.User, "And the year before?");
        model.Messages.Add(asked);
        model.Messages.Add(plain);
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();

            var area = Named<Grid>(panel, "ConversationLayer");
            var lists = Descendants<ItemsControl>(area).Where(control => control.Name == "Documents").ToArray();
            var shown = Assert.Single(lists, control => control.Items.Count > 0);
            Assert.Same(document, Assert.Single(shown.Items.Cast<object>()));
            Assert.Equal("Budget.pdf", Descendants<TextBlock>(shown).Single().Text);

            // The message with no file draws no chip at all, and leaves no gap.
            Assert.All(lists.Where(control => !ReferenceEquals(control, shown)), control => Assert.Equal(0, control.ActualHeight));
        }
        finally { panel.Close(); }
    }));

    // Opt-in render (ASSISTANT_UI_RENDER_DIR) of the file chips, for looking at: above the composer, and above a sent question.
    [Fact]
    public void TheAttachedFileRendersAboveTheComposerAndAboveTheQuestionInThePanel() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        var answer = new MessageViewModel(MessageRole.Assistant)
        {
            Status = MessageStatus.Complete,
        };
        answer.Content.Add(new TextContent("Only the 3 parts of “Quarterly budget review.pdf” that best match your question were read, out of 14."));
        answer.Content.Add(new TextContent("The total for the year is **$1.2 million**, which the review gives on page 4."));
        model.Messages.Add(new MessageViewModel(MessageRole.User, "What is the total for the year?", null, Attached("Quarterly budget review.pdf")));
        model.Messages.Add(answer);
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();
            model.Attach(Attached("Quarterly budget review with a very long name that cannot fit the composer at all.pdf"));
            model.Attach(new ImageItem("sketch", DemoImages.Photos()[0].Thumbnail!));
            Pump();
            RenderGlass(panel, "attached-document-2x.png", 2);

            model.StartWithDocument(Attached("Budget.pdf"));
            Pump();
            RenderGlass(panel, "attached-document-composer-2x.png", 2);
        }
        finally { panel.Close(); }
    }));

    // -- The History window's composer takes a file too. --

    [Fact]
    public void TheHistoryWindowsOpenConversationTakesOneAttachedFile_AndTheNextMessageIsAskedAboutIt() => RunSta(() =>
    {
        using var file = new TempDocument("handbook.md", HandbookText());
        var model = new ScriptedModel { Active = new ModelInfo("test-model", 4096) };
        var answers = DocumentAnswers(model, FilesAllowed(), RealDocuments());
        var history = new HistoryViewModel(new FixedClock(Now), null, answers);
        var document = file.Attachment;

        // Nothing is open, so there is nothing to attach it to.
        Assert.False(history.Attach(document));
        Assert.Null(history.Document);

        var card = history.Open(Guid.NewGuid(), [new MessageViewModel(MessageRole.User, "find the handbook"),
            new MessageViewModel(MessageRole.Assistant, "Here it is.")], Now);
        var changes = new List<string?>();
        history.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.True(history.Attach(document));
        Assert.Same(document, history.Document);
        Assert.Contains(nameof(HistoryViewModel.Document), changes);

        // The same file again changes nothing; another is attached beside it; each is taken off on its own.
        Assert.False(history.Attach(Attached("HANDBOOK.MD", document.Path.ToUpperInvariant())));
        var other = Attached("Plan.docx");
        Assert.True(history.Attach(other));
        Assert.Equal([document, other], history.Documents);
        Assert.Equal(2, history.Chips.Count);
        history.RemoveAttachmentCommand.Execute(document);
        Assert.Same(other, history.Document);
        history.RemoveAttachmentCommand.Execute(other);
        Assert.Null(history.Document);

        // Sending puts it on the message and the composer has nothing attached any more.
        Assert.True(history.Attach(document));
        history.Draft = "How should guests treat the workshop?";
        history.SendCommand.Execute(null);
        Assert.Null(history.Document);
        var question = card.Messages.Last(message => message.Role == MessageRole.User);
        Assert.Same(document, question.Document);

        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        var prompt = model.Requests[0].Messages[^1].Text;
        Assert.StartsWith("<untrusted_context id=\"1\" kind=\"file\" name=\"handbook.md\">", prompt, StringComparison.Ordinal);
        Assert.Contains("workshop routine number", prompt, StringComparison.Ordinal);
        Assert.EndsWith("How should guests treat the workshop?", prompt, StringComparison.Ordinal);
        model.Write(AssistantResponseChunk.ForTextDelta("Carefully."));
        model.End();
        WaitUntil(() => !history.IsAnswering, "The answer did not end.");

        // A file attached for one conversation is not attached to the next one opened.
        history.Attach(document);
        history.Selected = history.Open(Guid.NewGuid(), [new MessageViewModel(MessageRole.User, "Another")], Now);
        Assert.Null(history.Document);
    });

    [Fact]
    public void TheHistoryWindowShowsTheAttachedFileAboveItsComposer_AndItsMenuCanAttachADocumentButNotAPicture() => RunSta(() => WithTheme(() =>
    {
        var model = new ScriptedModel();
        var history = new HistoryViewModel(new FixedClock(Now), null, LocalAnswers(model));
        var answer = new MessageViewModel(MessageRole.Assistant, "I found 1 file.");
        history.Open(Guid.NewGuid(), [new MessageViewModel(MessageRole.User, "find the handbook"), answer], Now);
        var window = new HistoryWindow(history, new FakeFrameFactory(), new FakePlacement(), new MenuBackdrops())
        {
            Left = -10000, Top = -10000, ShowActivated = false,
        };
        try
        {
            window.Show();
            Pump();
            var view = Named<ConversationView>(window, "Conversation");
            var chips = Named<Assistant.UI.Controls.AttachmentChipList>(window, "ComposerAttachments");
            Assert.Equal(Visibility.Collapsed, chips.Visibility);

            // The menu of a file in an answer is not dimmed for a document, and is for a picture.
            var doc = new FileItem(SearchResultItemType.File, "milestone.docx", @"C:\Docs\milestone.docx");
            Assert.True(FileActions.Attach.CanExecute(doc, view));
            Assert.False(FileActions.Attach.CanExecute(new FileItem(SearchResultItemType.File, "a.png", @"C:\Docs\a.png"), view));
            Assert.False(FileActions.Attach.CanExecute(new FileItem(SearchResultItemType.File, "b.xlsx", @"C:\Docs\b.xlsx"), view));
            FileActions.Attach.Execute(doc, view);
            Pump();

            Assert.Equal("milestone.docx", history.Document!.Name);
            Assert.Equal(Visibility.Visible, chips.Visibility);
            var chip = Descendants<Border>(chips).Single(border => border.Name == "Chip");
            Assert.Equal("milestone.docx", Descendants<TextBlock>(chip).Single().Text);
            var composer = Named<Grid>(window, "ComposerField");
            var input = Named<Assistant.UI.Controls.PromptInputControl>(window, "ComposerInput");
            Assert.True(BoundsIn(composer, chip).Bottom <= BoundsIn(composer, input).Top + 0.5);

            var remove = Descendants<Button>(chip).Single();
            Assert.Same(history.RemoveAttachmentCommand, remove.Command);
            remove.Command.Execute(remove.CommandParameter);
            Pump();
            Assert.Null(history.Document);
            Assert.Equal(Visibility.Collapsed, chips.Visibility);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheFileActionsMenuIsOpaque_BecauseNoBlurredBackdropIsPutBehindIt_EvenInABlurredWindow() => RunSta(() => WithTheme(() =>
    {
        // A menu inherits from its window that it is blurred, but the blur is only put behind a menu that has a backdrop
        // attached (the History window's view menu): without one, translucent glass is a faint tint over the text under it.
        var menu = (ContextMenu)Application.Current.FindResource("FileActionsMenu");
        Assert.Equal(0xFF, Assert.IsType<System.Windows.Media.SolidColorBrush>(Surface(menu, blurred: true).Fill).Color.A);

        Assistant.UI.Windowing.MenuBackdrop.Attach(menu, "Glass", size => size, new MenuBackdrops());
        Assert.IsType<System.Windows.Media.LinearGradientBrush>(Surface(menu, blurred: true).Fill);
        Assert.Equal(0xFF, Assert.IsType<System.Windows.Media.SolidColorBrush>(Surface(menu, blurred: false).Fill).Color.A);

        static Assistant.UI.Controls.PanelShape Surface(ContextMenu menu, bool blurred)
        {
            Assistant.UI.Windowing.Backdrop.SetIsBlurred(menu, blurred);
            menu.ApplyTemplate();
            return Assert.IsType<Assistant.UI.Controls.PanelShape>(menu.Template.FindName("Surface", menu));
        }
    }));

    // -- Answering with the passages of the file. --

    [Fact]
    public void AQuestionAboutAnAttachedFileIsAskedWithThePassagesItNeeds_AndTheUserIsToldWhatWasRead() => RunSta(() =>
    {
        using var file = new TempDocument("handbook.md", HandbookText());
        var model = new ScriptedModel { Active = new ModelInfo("test-model", 4096) };
        var answers = DocumentAnswers(model, FilesAllowed(), RealDocuments());
        var question = new MessageViewModel(MessageRole.User, "How should guests treat the workshop?", null, file.Attachment);
        var shown = new List<MessageViewModel>();

        var asked = answers.StreamAnswerAsync(Guid.NewGuid(), question, shown.Add, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        model.Write(AssistantResponseChunk.ForTextDelta("Carefully."));
        model.End();
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        var request = Assert.Single(model.Requests);
        var prompt = request.Messages[^1].Text;

        // The file's passages come first, wrapped as untrusted and marked with where they are, then the question.
        Assert.StartsWith("<untrusted_context id=\"1\" kind=\"file\" name=\"handbook.md\">\n[Passage 1: ", prompt, StringComparison.Ordinal);
        Assert.Contains("(House handbook > Workshop)]", prompt, StringComparison.Ordinal);
        Assert.Contains("workshop routine number", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("kitchen routine", prompt, StringComparison.Ordinal);
        Assert.EndsWith("</untrusted_context>\n\nHow should guests treat the workshop?", prompt, StringComparison.Ordinal);
        Assert.EndsWith(AssistantInstructions.FileContextGuidance, request.Instructions, StringComparison.Ordinal);
        Assert.True(prompt.Length < new FileInfo(file.Path).Length / 2, "Only passages of the file should be sent.");

        // The answer says how much of the file was read, then answers.
        var answer = Assert.Single(shown);
        var parts = answer.Content.OfType<TextContent>().Select(part => part.Text).ToArray();
        Assert.Equal(2, parts.Length);
        Assert.Matches(
            @"^Only the (\d+ parts of “handbook\.md” that best match your question were|part of “handbook\.md” that best matches your question was) read, out of \d+\.$",
            parts[0]);
        Assert.Equal("Carefully.", parts[1]);
        Assert.Equal(MessageStatus.Complete, answer.Status);
    });

    [Fact]
    public void AShortFileIsSentWhole_WithNoNoticeAndTheFileIsLeftAsItWas() => RunSta(() =>
    {
        using var file = new TempDocument("notes.txt", "Buy milk.\nCall the plumber on Friday.");
        var model = new ScriptedModel();
        var answers = DocumentAnswers(model, FilesAllowed(), RealDocuments());
        var question = new MessageViewModel(MessageRole.User, "When is the plumber?", null, file.Attachment);
        var shown = new List<MessageViewModel>();

        var asked = answers.StreamAnswerAsync(Guid.NewGuid(), question, shown.Add, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        model.Write(AssistantResponseChunk.ForTextDelta("On Friday."));
        model.End();
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        var prompt = Assert.Single(model.Requests).Messages[^1].Text;
        Assert.Equal(
            "<untrusted_context id=\"1\" kind=\"file\" name=\"notes.txt\">\n[Passage 1: document, lines 1-2]\nBuy milk.\nCall the plumber on Friday.\n</untrusted_context>\n\nWhen is the plumber?",
            prompt);
        Assert.Equal("On Friday.", Assert.Single(shown).Text);
        Assert.Equal("Buy milk.\nCall the plumber on Friday.", File.ReadAllText(file.Path));
    });

    [Fact]
    public void AFollowUpIsAnsweredInTheSameConversation_WhichStillHasTheFilesPassages() => RunSta(() =>
    {
        using var file = new TempDocument("notes.txt", "Buy milk.\nCall the plumber on Friday.");
        var model = new ScriptedModel();
        var answers = DocumentAnswers(model, FilesAllowed(), RealDocuments());
        var conversation = Guid.NewGuid();
        var first = new MessageViewModel(MessageRole.User, "When is the plumber?", null, file.Attachment);

        var one = answers.StreamAnswerAsync(conversation, first, _ => { }, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        model.Write(AssistantResponseChunk.ForTextDelta("On Friday."));
        model.End();
        WaitUntil(() => one.IsCompleted, "The answer did not end.");

        var second = answers.StreamAnswerAsync(conversation, "And what else?", _ => { }, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 2, "The follow-up was not asked.");
        model.End();
        WaitUntil(() => second.IsCompleted, "The follow-up did not end.");

        // The system message is the same for both turns, so the engine's prompt cache stays valid, and the earlier question still
        // has the passages it was asked with.
        Assert.Equal(model.Requests[0].Instructions, model.Requests[1].Instructions);
        Assert.Contains("Call the plumber on Friday.", model.Requests[1].Messages[0].Text, StringComparison.Ordinal);
        Assert.Equal("And what else?", model.Requests[1].Messages[^1].Text);
    });

    [Fact]
    public void AnAttachedFileThatCannotBeReadOrMaynotBe_IsSaidSo_AndNothingIsAsked() => RunSta(() =>
    {
        using var folder = new TempDocument("readable.txt", "Some text.");
        var directory = System.IO.Path.GetDirectoryName(folder.Path)!;
        var empty = System.IO.Path.Combine(directory, "empty.txt");
        File.WriteAllText(empty, "  ");
        var sheet = System.IO.Path.Combine(directory, "sheet.xlsx");
        File.WriteAllBytes(sheet, [0x50, 0x4B, 0x03, 0x04]);
        var damaged = System.IO.Path.Combine(directory, "damaged.docx");
        File.WriteAllText(damaged, "this is not a zip package");

        var model = new ScriptedModel();
        var settings = new InMemorySettingsService();
        var documents = RealDocuments();
        var shown = new List<MessageViewModel>();
        Task Ask(ModelAnswerProvider answers, string path) => answers.StreamAnswerAsync(
            Guid.NewGuid(), new MessageViewModel(MessageRole.User, "What is it?", null, new DocumentAttachment(System.IO.Path.GetFileName(path), path)),
            shown.Add, CancellationToken.None);
        void Done(Task task) => WaitUntil(() => task.IsCompleted, "The answer did not end.");

        // The Files permission off: the file is not read. No way to ask the permission, or to read a file: nothing is read.
        settings.SaveAsync(new AppSettings { Permissions = new PermissionSettings { Files = false } }).GetAwaiter().GetResult();
        Done(Ask(DocumentAnswers(model, new SettingsPermissionPolicy(settings), documents, settings), folder.Path));
        Done(Ask(DocumentAnswers(model, null, documents), folder.Path));
        Done(Ask(DocumentAnswers(model, FilesAllowed(), null), folder.Path));

        // Each way a file can fail to give text, with the file's name in the answer.
        var allowed = DocumentAnswers(model, FilesAllowed(), documents);
        Done(Ask(allowed, System.IO.Path.Combine(directory, "gone.pdf")));
        Done(Ask(allowed, empty));
        Done(Ask(allowed, sheet));
        Done(Ask(allowed, damaged));

        Assert.Empty(model.Requests);
        Assert.Equal(
            [
                AttachedDocuments.FilesTurnedOffText,
                AttachedDocuments.NotAvailableText,
                AttachedDocuments.NotAvailableText,
                AttachedDocuments.ProblemText("gone.pdf", DocumentReadStatus.NotFound),
                AttachedDocuments.ProblemText("empty.txt", DocumentReadStatus.NoText),
                AttachedDocuments.ProblemText("sheet.xlsx", DocumentReadStatus.Unsupported),
                AttachedDocuments.ProblemText("damaged.docx", DocumentReadStatus.Corrupt),
            ],
            shown.Select(message => message.Text));
        Assert.Contains("“gone.pdf” can't be found", shown[3].Text, StringComparison.Ordinal);
        Assert.Contains("scanned", shown[4].Text, StringComparison.Ordinal);
        Assert.Contains(DocumentFileTypes.Described, shown[5].Text, StringComparison.Ordinal);
    });

    [Fact]
    public void StoppingFromTheChipWhileTheFileIsReadEndsTheAnswerAsStopped_AndNothingIsAsked() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var tracker = new ActivityTracker();
        var slow = new ActivityDocumentContextService(new WaitingDocumentService(), tracker);
        var answers = DocumentAnswers(model, FilesAllowed(), slow);
        var question = new MessageViewModel(MessageRole.User, "What is it?", null, Attached("Big.pdf"));
        var shown = new List<MessageViewModel>();

        var asked = answers.StreamAnswerAsync(Guid.NewGuid(), question, shown.Add, CancellationToken.None);
        WaitUntil(() => tracker.Current is { Text: "Reading" }, "The file was not being read.");

        // Esc, or a press on the chip, cancels the activity and not the caller's token.
        Assert.True(tracker.CancelCurrent());
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        var stopped = Assert.Single(shown);
        Assert.Equal(MessageStatus.Stopped, stopped.Status);
        Assert.Empty(stopped.Content);
        Assert.Empty(model.Requests);
    });

    // A file that takes as long as it is let to: it ends only when it is cancelled.
    private sealed class WaitingDocumentService : IDocumentContextService
    {
        public async Task<DocumentContextResult> GetContextAsync(
            string filePath, string question, DocumentContextOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return DocumentContextResult.Failed(DocumentReadStatus.NoText);
        }
    }

    [Fact]
    public void EveryWayAFileCanFailIsSaidInWords_WithTheFilesNameAndNothingOfItsContent()
    {
        foreach (var status in Enum.GetValues<DocumentReadStatus>().Where(status => status != DocumentReadStatus.Success))
        {
            var text = AttachedDocuments.ProblemText("Report.pdf", status);

            Assert.Contains("“Report.pdf”", text, StringComparison.Ordinal);
            Assert.Contains("nothing was asked", text, StringComparison.Ordinal);
        }

        Assert.StartsWith("“" + new string('n', 59) + "…”", AttachedDocuments.ProblemText(new string('n', 100) + ".pdf", DocumentReadStatus.NoText), StringComparison.Ordinal);
        Assert.StartsWith("The attached file", AttachedDocuments.ProblemText("  ", DocumentReadStatus.NoText), StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoModelSetUp_TheAnswerSaysSo_AndNothingIsSaidAboutTheFile() => RunSta(() =>
    {
        using var file = new TempDocument("handbook.md", HandbookText());
        var model = new ScriptedModel { Active = null };
        var answers = DocumentAnswers(model, FilesAllowed(), RealDocuments());
        var question = new MessageViewModel(MessageRole.User, "How should guests treat the workshop?", null, file.Attachment);
        var shown = new List<MessageViewModel>();

        var asked = answers.StreamAnswerAsync(Guid.NewGuid(), question, shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.Equal(ModelAnswerProvider.NoModelText, Assert.Single(shown).Text);
    });

    [Fact]
    public void AQuestionWithADocumentAttachedGoesToTheModel_EvenWhenItsWordsAreASampleQuestion() => RunSta(() =>
    {
        using var file = new TempDocument("notes.txt", "Nine plus ten is a sum.");
        var model = new ScriptedModel();
        var demo = new DemoAnswerProvider(
            new FakeClipboard(), new FixedClock(Now), localModel: DocumentAnswers(model, FilesAllowed(), RealDocuments()));
        var question = new MessageViewModel(MessageRole.User, "what is 9+10", null, file.Attachment);
        var shown = new List<MessageViewModel>();

        var asked = demo.StreamAnswerAsync(Guid.NewGuid(), question, shown.Add, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        model.Write(AssistantResponseChunk.ForTextDelta("Nineteen."));
        model.End();
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.Contains("Nine plus ten is a sum.", Assert.Single(model.Requests).Messages[^1].Text, StringComparison.Ordinal);
        Assert.Equal("Nineteen.", Assert.Single(shown).Text);
    });

    // -- "What is it about?" after finding one file, or in the same message as finding it. --

    [Theory]
    [InlineData("what is it about?", true)]
    [InlineData("What's this about", true)]
    [InlineData("what is it about", true)]
    [InlineData("what does the document say", true)]
    [InlineData("summarize it", true)]
    [InlineData("Summarise this please", true)]
    [InlineData("explain the file", true)]
    [InlineData("tell me about it", true)]
    [InlineData("what's in it?", true)]
    [InlineData("what is the capital of France?", false)]
    [InlineData("summarize the french revolution", false)]
    [InlineData("tell me about Paris", false)]
    [InlineData("what is it called", false)]
    [InlineData("", false)]
    public void OnlyAClearReferenceToTheFileJustFoundIsAQuestionAboutIt(string text, bool expected) =>
        Assert.Equal(expected, DocumentCue.IsAboutTheFile(text));

    [Theory]
    [InlineData("find milesotne 4 doc, what is it about?", "find milesotne 4 doc", "what is it about?")]
    [InlineData("find the milestone doc and summarize it", "find the milestone doc", "summarize it")]
    [InlineData("Find my budget pdf. Then tell me about it", "Find my budget pdf", "tell me about it")]
    public void ARequestThatFindsAFileAndAsksAboutItIsSplitInTwo(string request, string search, string question)
    {
        Assert.True(DocumentCue.TrySplit(request, out var findPart, out var askPart));
        Assert.Equal((search, question), (findPart, askPart));
    }

    [Theory]
    [InlineData("what is it about?")]
    [InlineData("please summarize it")]
    [InlineData("find the milestone doc")]
    [InlineData("find the doc, what is the capital of France?")]
    public void ARequestWithOnlyOneOfTheTwoPartsIsNotSplit(string request) =>
        Assert.False(DocumentCue.TrySplit(request, out _, out _));

    private static MessageViewModel FoundMessage(params string[] paths)
    {
        var clock = new FixedClock(Now);
        var message = new MessageViewModel(MessageRole.Assistant, "I found some files.");
        message.Content.Add(new FileCollection(paths.Select(path => new FileItem(SearchResultItemType.File, System.IO.Path.GetFileName(path), path, clock: clock))));
        return message;
    }

    [Fact]
    public void ARequestToFindAFileAndSayWhatItIsAboutFindsItThenAnswersFromIt() => RunSta(() =>
    {
        using var file = new TempDocument("milestone.md", "# Milestone four\n\nThis draft argues that the harbor strike changed the city.");
        var requests = new ScriptedFileRequests
        {
            IsRequest = text => text.StartsWith("find", StringComparison.OrdinalIgnoreCase),
            Result = Found(new FileSearchQuery { Filename = "milestone" }, FoundFile(file.Path)),
        };
        var model = new ScriptedModel { Active = new ModelInfo("test-model", 4096) };
        var demo = new DemoAnswerProvider(
            new FakeClipboard(), new FixedClock(Now), localModel: DocumentAnswers(model, FilesAllowed(), RealDocuments()),
            files: CreateAnswers(requests));
        var shown = new List<MessageViewModel>();

        var asked = demo.StreamAnswerAsync(Guid.NewGuid(), "find milestone doc, what is it about?", shown.Add, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        model.Write(AssistantResponseChunk.ForTextDelta("It is about the harbor strike."));
        model.End();
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        // Only the finding part was searched for, and the file's words went to the model with just the question.
        Assert.Equal(["find milestone doc"], requests.Found);
        Assert.Contains("harbor strike", Assert.Single(model.Requests).Messages[^1].Text, StringComparison.Ordinal);
        Assert.EndsWith("what is it about?", model.Requests[0].Messages[^1].Text, StringComparison.Ordinal);
        Assert.Equal(2, shown.Count);
        Assert.IsType<FileCollection>(shown[0].Content.Last());
        Assert.Equal("It is about the harbor strike.", shown[1].Text);
    });

    [Fact]
    public void ARequestToFindSeveralFilesAndAskAboutThemFindsThemAndAsksNothing() => RunSta(() =>
    {
        var requests = new ScriptedFileRequests
        {
            IsRequest = text => text.StartsWith("find", StringComparison.OrdinalIgnoreCase),
            Result = Found(new FileSearchQuery { Filename = "milestone" }, FoundFile(@"C:\Docs\A.docx"), FoundFile(@"C:\Docs\B.docx")),
        };
        var model = new ScriptedModel();
        var demo = new DemoAnswerProvider(
            new FakeClipboard(), new FixedClock(Now), localModel: DocumentAnswers(model, FilesAllowed(), RealDocuments()),
            files: CreateAnswers(requests));
        var shown = new List<MessageViewModel>();

        var asked = demo.StreamAnswerAsync(Guid.NewGuid(), "find milestone doc, what is it about?", shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.Single(shown);
        Assert.Empty(model.Requests);
    });

    // -- A file named in the first message of a conversation is found at once, and the rest asked about it. --

    [Theory]
    [InlineData("summarize my milestone three doc", "find milestone three doc")]
    [InlineData("Can you read the budget pdf please", "find budget pdf")]
    [InlineData("what is the HIS-332 rubric about?", "find HIS-332 rubric")]
    [InlineData("summarize the whole attached document", null)]
    [InlineData("summarize the document", null)]
    [InlineData("summarize it", null)]
    [InlineData("summarize the first one", null)]
    public void AFileNamedInTheQuestionIsFoundByItsName(string request, string? find) =>
        Assert.Equal(find, DocumentCue.FindOfNamed(request));

    [Fact]
    public void AQuestionThatNamesTheFileItIsAboutFindsItAndAnswersFromIt() => RunSta(() =>
    {
        using var file = new TempDocument("milestone.md", "# Milestone three\n\nThis guideline asks for a harbor map.");
        var requests = new ScriptedFileRequests
        {
            IsRequest = text => text.StartsWith("find", StringComparison.OrdinalIgnoreCase),
            Result = Found(new FileSearchQuery { Filename = "milestone" }, FoundFile(file.Path)),
        };
        var model = new ScriptedModel { Active = new ModelInfo("test-model", 4096) };
        var demo = new DemoAnswerProvider(
            new FakeClipboard(), new FixedClock(Now), localModel: DocumentAnswers(model, FilesAllowed(), RealDocuments()),
            files: CreateAnswers(requests));
        var shown = new List<MessageViewModel>();

        var asked = demo.StreamAnswerAsync(Guid.NewGuid(), "summarize my milestone three doc", shown.Add, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        model.Write(AssistantResponseChunk.ForTextDelta("It asks for a harbor map."));
        model.End();
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.Equal(["find milestone three doc"], requests.Found);
        Assert.Contains("harbor map", Assert.Single(model.Requests).Messages[^1].Text, StringComparison.Ordinal);
        Assert.EndsWith("summarize my milestone three doc", model.Requests[0].Messages[^1].Text, StringComparison.Ordinal);
        Assert.IsType<FileCollection>(shown[0].Content.Last());
        Assert.Equal("It asks for a harbor map.", shown[1].Text);
    });

    // -- Said the way people say it: corrections, a request that only maybe asks for files, and what the model remembers. --

    [Fact]
    public async Task ACorrectionOfTheLastRequestPutsTheWordRight_OrAddsIt_AndOnlyWithARequestBefore()
    {
        var requests = new ScriptedFileRequests { IsRequest = text => text.StartsWith("find", StringComparison.OrdinalIgnoreCase), Result = new FileRequestResult(FileRequestStatus.NothingFound) };
        var answers = CreateAnswers(requests);
        var conversation = Guid.NewGuid();
        Assert.False(answers.IsFileRequest("3 not 4", conversation));

        await answers.AnswerAsync("find milesotne 4 doc", conversation, CancellationToken.None);
        Assert.True(answers.IsFileRequest("3 not 4", conversation));
        Assert.True(answers.IsFileRequest("No, three not four", conversation));
        Assert.True(answers.IsFileRequest("i meant three", conversation));
        Assert.False(answers.IsFileRequest("what about Paris", conversation));
        Assert.False(answers.IsFileRequest("3 not 4", Guid.NewGuid()));

        await answers.AnswerAsync("3 not 4", conversation, CancellationToken.None);
        Assert.Equal("find milesotne 3 doc", requests.Found[^1]);

        // The corrected request is the one that comes next ("not" a number that is in it, or the word added).
        await answers.AnswerAsync("i meant guidelines", conversation, CancellationToken.None);
        Assert.Equal("find milesotne 3 doc guidelines", requests.Found[^1]);
    }

    [Fact]
    public void ARequestThatOnlyMaybeAsksForFilesIsAFileRequestIfSomethingIsFound_AndTheModelsIfNot() => RunSta(() =>
    {
        using var file = new TempDocument("milestone.md", "# Milestone three\n\nThe guidelines.");
        var found = new FileRequestResult(FileRequestStatus.Found) { Plan = new PlannedFileSearch(new FileSearchQuery("milestone"), FileSearchPlanSource.Read), Items = [FoundFile(file.Path)] };
        var requests = new ScriptedFileRequests
        {
            Likely = text => text.Contains("FIND IT", StringComparison.Ordinal),
            ResultFor = text => text.StartsWith("none", StringComparison.Ordinal) ? new FileRequestResult(FileRequestStatus.NothingFound) : found,
        };
        var model = new ScriptedModel { Active = new ModelInfo("test-model", 4096) };
        var demo = new DemoAnswerProvider(
            new FakeClipboard(), new FixedClock(Now), localModel: DocumentAnswers(model, FilesAllowed(), RealDocuments()), files: CreateAnswers(requests));
        var shown = new List<MessageViewModel>();

        // Something is found: the list of files is the answer, and the model is not asked.
        var first = demo.StreamAnswerAsync(Guid.NewGuid(), "i said milestone three doc. FIND IT", shown.Add, CancellationToken.None);
        WaitUntil(() => first.IsCompleted, "The answer did not end.");
        Assert.Single(shown);
        Assert.IsType<FileCollection>(shown[0].Content.Last());
        Assert.Empty(model.Requests);

        // Nothing is found: it was not a request to find files after all, and the model answers it.
        var second = demo.StreamAnswerAsync(Guid.NewGuid(), "none: FIND IT in the dictionary", shown.Add, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        model.Write(AssistantResponseChunk.ForTextDelta("Here is what I know."));
        model.End();
        WaitUntil(() => second.IsCompleted, "The answer did not end.");
        Assert.Equal(2, shown.Count);
        Assert.Equal("Here is what I know.", shown[1].Text);
    });

    [Fact]
    public void TheModelRemembersWhatTheAssistantFoundForARequest_AsItsOwnToolCall_SoWhatIsSaidNextIsUnderstood() => RunSta(() =>
    {
        var found = new FileRequestResult(FileRequestStatus.Found)
        {
            Plan = new PlannedFileSearch(new FileSearchQuery("milestone"), FileSearchPlanSource.Read),
            Items = [FoundFile(@"C:\Users\me\Downloads\Milestone Four Draft.docx")],
        };
        var requests = new ScriptedFileRequests { IsRequest = text => text.StartsWith("find", StringComparison.OrdinalIgnoreCase), Result = found };
        var model = new ScriptedModel { Active = new ModelInfo("test-model", 4096) };
        var known = new Assistant.Core.Files.ConversationFiles();
        var demo = new DemoAnswerProvider(
            new FakeClipboard(), new FixedClock(Now),
            localModel: DocumentAnswers(model, FilesAllowed(), RealDocuments(), files: known), files: CreateAnswers(requests, known));
        var conversation = Guid.NewGuid();
        var shown = new List<MessageViewModel>();

        var finding = demo.StreamAnswerAsync(conversation, "find milestone four doc", shown.Add, CancellationToken.None);
        WaitUntil(() => finding.IsCompleted, "The files were not found.");
        var asking = demo.StreamAnswerAsync(conversation, "is that the draft?", shown.Add, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        model.Write(AssistantResponseChunk.ForTextDelta("Yes."));
        model.End();
        WaitUntil(() => asking.IsCompleted, "The answer did not end.");

        // The model was shown the find as its own call of the tool, and what it found by id and name (never the path), before the question.
        var request = Assert.Single(model.Requests);
        var messages = request.Messages;
        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant, MessageRole.User],
            messages.Select(message => message.Role));
        Assert.Equal("find milestone four doc", messages[0].Text);
        Assert.Equal("search_files", Assert.Single(messages[1].ToolCalls).ToolName);
        Assert.Contains("\"id\":\"f1\"", messages[2].Text, StringComparison.Ordinal);
        Assert.Contains("Milestone Four Draft.docx", messages[2].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\\Users", messages[2].Text, StringComparison.Ordinal);
        Assert.Equal("is that the draft?", messages[4].Text);
    });

    // -- What is kept of it. --

    [Fact]
    public void AnAttachedFileIsSavedAsItsNameAndPlaceOnly_AndComesBackOnTheMessage()
    {
        var mapper = new MessageMapper(new FakeClipboard());
        var image = new ImageItem("sign.jpg", @"C:\Pictures\sign.jpg");
        var asked = new MessageViewModel(MessageRole.User, "What does it say?", [image], Attached("Budget.pdf")) { CreatedAt = Now };

        var saved = mapper.ToDomain(asked);

        Assert.Equal(
            [(ContextItemType.Image, "sign.jpg"), (ContextItemType.File, "Budget.pdf")],
            saved.ContextItems.Select(item => (item.Type, item.DisplayName)));
        var descriptor = saved.ContextItems.Single(item => item.Type == ContextItemType.File);
        Assert.Equal(@"C:\Docs\Budget.pdf", descriptor.FilePath);
        Assert.Null(descriptor.Text);

        var back = mapper.ToViewModel(saved);
        Assert.Equal(asked.Id, back.Id);
        Assert.Equal(("Budget.pdf", @"C:\Docs\Budget.pdf"), (back.Document!.Name, back.Document.Path));
        Assert.Equal("sign.jpg", Assert.Single(back.Attachments).Name);
        Assert.True(back.HasAttachments);
    }

    [Fact]
    public void ASavedFileWithoutANameIsNamedAfterItsFile_AndAMessageWithNoneHasNoDocument()
    {
        var mapper = new MessageMapper(new FakeClipboard());
        var saved = new Message(Guid.NewGuid(), MessageRole.User, "Read this", Now)
        {
            ContextItems = [new ContextItem(Guid.NewGuid(), ContextItemType.File, " ") { FilePath = @"C:\p\notes.txt" }],
        };

        Assert.Equal("notes.txt", mapper.ToViewModel(saved).Document!.Name);
        Assert.Null(mapper.ToViewModel(saved with { ContextItems = [new ContextItem(Guid.NewGuid(), ContextItemType.File, "x")] }).Document);
        Assert.Null(mapper.ToViewModel(saved with { ContextItems = [] }).Document);
        Assert.Empty(mapper.ToDomain(new MessageViewModel(MessageRole.User, "plain")).ContextItems);
    }

    // -- How the app wires it. --

    [Fact]
    public void TheAppWiresTheDocumentServiceWithItsChip_AndTheLexicalSelector()
    {
        using var host = AppHost.Create();
        var services = host.Services;

        Assert.IsType<ActivityDocumentContextService>(services.GetRequiredService<IDocumentContextService>());
        Assert.IsType<LexicalPassageSelector>(services.GetRequiredService<IPassageSelector>());
        Assert.NotNull(services.GetRequiredService<DocumentContextService>());
        Assert.NotNull(services.GetRequiredService<AttachedDocuments>());
        Assert.NotNull(services.GetRequiredService<ModelAnswerProvider>());
        Assert.Same(services.GetRequiredService<IDocumentContextService>(), services.GetRequiredService<IDocumentContextService>());
    }

    [Fact]
    public async Task TheChipSaysReadingWhileAFileIsRead_AndNothingPrivateIsGivenToIt()
    {
        var tracker = new ActivityTracker();
        var inner = new ScriptedDocumentService(tracker);
        var service = new ActivityDocumentContextService(inner, tracker);

        var result = await service.GetContextAsync(@"C:\Docs\secret-name.pdf", "a secret question");

        Assert.Equal(DocumentReadStatus.NoText, result.Status);
        Assert.Equal("Reading", inner.SeenDuringTheRead?.Text);
        Assert.Equal(ActivityKind.FileSearch, inner.SeenDuringTheRead?.Kind);
        Assert.Null(tracker.Current);
    }

    private sealed class ScriptedDocumentService(IActivityTracker tracker) : IDocumentContextService
    {
        public ActivityStatus? SeenDuringTheRead { get; private set; }

        public Task<DocumentContextResult> GetContextAsync(
            string filePath, string question, DocumentContextOptions? options = null, CancellationToken cancellationToken = default)
        {
            SeenDuringTheRead = tracker.Current;
            return Task.FromResult(DocumentContextResult.Failed(DocumentReadStatus.NoText));
        }
    }
}
