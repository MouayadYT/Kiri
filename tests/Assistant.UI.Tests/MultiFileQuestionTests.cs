using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Activity;
using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Core.Domain;
using Assistant.Core.MultiFile;
using Assistant.Core.Orchestration;
using Assistant.Core.Storage;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.History;
using Assistant.UI.Messages;
using Assistant.UI.Search;
using Assistant.UI.ViewModels;
using Assistant.Windows.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- Several files, or one long one (PROJECT_SPEC §5.5, several files) ----------------------------------------------------------

    // A guide of several sections, each about a page long, every section's words its own.
    private static string GuideText(string title, params string[] sections)
    {
        var text = new StringBuilder($"# {title}\n\nAn introduction to the {title.ToLowerInvariant()}.\n\n");
        foreach (var section in sections)
        {
            text.Append("## ").Append(section).Append("\n\n");
            var lower = section.ToLowerInvariant();
            for (var paragraph = 0; paragraph < 4; paragraph++)
            {
                text.Append($"The {lower} follows plan number {paragraph}. Everyone looks after the {lower} and leaves it tidy for the next person, ");
                text.Append("putting every tool back where it belongs and writing down anything that is broken in the book by the door.\n\n");
            }
        }

        return text.ToString();
    }

    // The provider over the real orchestrator, the real readers and the real multi-file processor, asking the note-taking model.
    private static ModelAnswerProvider MultiFileAnswers(
        NoteTakingModel model, IPermissionPolicy? permissions, IActivityTracker? tracker = null, IConversationFiles? files = null)
    {
        var settings = new InMemorySettingsService();
        var documents = RealDocuments();
        var processor = new MultiFileProcessor(documents, model, settings, clock: new FixedClock(Now), tracker: tracker);
        return new ModelAnswerProvider(
            new AssistantOrchestrator(
                model, settings, new PromptBuilder(), new ImagePreprocessor(), new FixedClock(Now), NullLogger<AssistantOrchestrator>.Instance),
            new FixedClock(Now),
            permissions,
            new AttachedDocuments(permissions, documents, model, settings, processor),
            files: files);
    }

    [Fact]
    public void SeveralLongFilesAreReadInPieces_EachOnItsOwn_AndTheAnswerIsPutTogetherFromTheNotes() => RunSta(() =>
    {
        using var house = new TempDocument("house.md", GuideText("House guide", "Kitchen", "Garden", "Workshop", "Library", "Cellar", "Attic", "Studio", "Garage"));
        using var boat = new TempDocument("boat.md", GuideText("Boat guide", "Deck", "Galley", "Cabin", "Engine", "Sails", "Anchor", "Radio", "Dinghy"));
        using var farm = new TempDocument("farm.md", GuideText("Farm guide", "Barn", "Orchard", "Dairy", "Stable", "Pond", "Silo", "Meadow", "Fence"));
        var model = new NoteTakingModel();
        var known = new Assistant.Core.Files.ConversationFiles();
        var answers = MultiFileAnswers(model, FilesAllowed(), files: known);
        var conversation = Guid.NewGuid();
        var question = new MessageViewModel(
            MessageRole.User, "what are these about?", documents: [house.Attachment, boat.Attachment, farm.Attachment]);
        var shown = new List<MessageViewModel>();

        var asked = answers.StreamAnswerAsync(conversation, question, shown.Add, CancellationToken.None);
        WaitUntil(() => model.Answers.Count == 1, "The model was never asked the question.");
        model.Write("They are guides to a house, a boat and a farm.");
        model.End();
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        // Notes were taken on each file's pieces, one request each, every request holding one file's passages alone.
        var notes = model.NoteRequests;
        Assert.InRange(notes.Count, 3, MultiFileLimits.DefaultMaxMapCalls);
        string[] names = ["house.md", "boat.md", "farm.md"];
        foreach (var request in notes)
        {
            var prompt = request.Messages[^1].Text;
            Assert.Single(Regex.Matches(prompt, "<untrusted_context "));
            var name = NoteTakingModel.NameIn(request);
            Assert.Contains(names, file => name.StartsWith(file, StringComparison.Ordinal));
            Assert.Contains("follows plan number", prompt, StringComparison.Ordinal);
            Assert.EndsWith(AssistantInstructions.NoteRequest("what are these about?", name), prompt, StringComparison.Ordinal);
        }

        Assert.All(names, file => Assert.Contains(notes, request => NoteTakingModel.NameIn(request).StartsWith(file, StringComparison.Ordinal)));

        // The question itself is asked with the notes, marked as notes, and none of the files' own text.
        var answer = Assert.Single(model.Answers);
        var final = answer.Messages[^1].Text;
        Assert.EndsWith(AssistantInstructions.FileNotesGuidance, answer.Instructions, StringComparison.Ordinal);
        Assert.Equal(notes.Count, Regex.Matches(final, "kind=\"file_notes\"").Count);
        Assert.All(names, file => Assert.Contains($"- {file}", final, StringComparison.Ordinal));
        Assert.DoesNotContain("follows plan number", final, StringComparison.Ordinal);
        Assert.EndsWith("</untrusted_context>\n\nwhat are these about?", final, StringComparison.Ordinal);

        // The user is told how the files were read, before the answer's words.
        var message = Assert.Single(shown);
        Assert.Equal(
            [
                "These 3 files are too long to read at once, so they were read in pieces, and this answer is put together from notes on each piece.",
                "They are guides to a house, a boat and a farm.",
            ],
            message.Content.OfType<TextContent>().Select(part => part.Text));
        Assert.Equal(MessageStatus.Complete, message.Status);

        // Every file is known to the conversation, for the model to read again by name later.
        Assert.Equal(["house.md", "boat.md", "farm.md"], known.Get(conversation, ["f1", "f2", "f3"]).Select(file => file.Name));
    });

    [Fact]
    public void AFileThatCannotBeReadAmongSeveralIsNamed_AndTheQuestionIsAskedAboutTheRest() => RunSta(() =>
    {
        using var notes = new TempDocument("notes.txt", "Buy milk.\nCall the plumber on Friday.");
        using var plan = new TempDocument("plan.md", "# Plan\n\nPaint the fence in May.");
        var gone = new DocumentAttachment("gone.md", Path.Combine(Path.GetDirectoryName(notes.Path)!, "gone.md"));
        var model = new NoteTakingModel();
        var answers = MultiFileAnswers(model, FilesAllowed());
        var question = new MessageViewModel(MessageRole.User, "what do I have to do?", documents: [notes.Attachment, gone, plan.Attachment]);
        var shown = new List<MessageViewModel>();

        var asked = answers.StreamAnswerAsync(Guid.NewGuid(), question, shown.Add, CancellationToken.None);
        WaitUntil(() => model.Answers.Count == 1, "The model was never asked the question.");
        model.Write("Call the plumber and paint the fence.");
        model.End();
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        // Two short files fit one prompt: no notes, their text goes as it is.
        Assert.Empty(model.NoteRequests);
        var prompt = Assert.Single(model.Answers).Messages[^1].Text;
        Assert.Equal(2, Regex.Matches(prompt, "kind=\"file\"").Count);
        Assert.Contains("Call the plumber on Friday.", prompt, StringComparison.Ordinal);
        Assert.Contains("Paint the fence in May.", prompt, StringComparison.Ordinal);
        Assert.Equal(
            [
                "“gone.md” can't be found any more. It may have been moved or deleted, so it was left out.",
                "Call the plumber and paint the fence.",
            ],
            Assert.Single(shown).Content.OfType<TextContent>().Select(part => part.Text));
    });

    [Fact]
    public void WhenNoneOfSeveralFilesCanBeRead_EachIsNamedWithWhy_AndNothingIsAsked() => RunSta(() =>
    {
        using var folder = new TempDocument("readable.txt", "Some text.");
        var directory = Path.GetDirectoryName(folder.Path)!;
        var empty = Path.Combine(directory, "empty.txt");
        File.WriteAllText(empty, "  ");
        var model = new NoteTakingModel();
        var shown = new List<MessageViewModel>();
        var question = new MessageViewModel(
            MessageRole.User, "what are these?", documents: [new DocumentAttachment("gone.md", Path.Combine(directory, "gone.md")), new DocumentAttachment("empty.txt", empty)]);

        var asked = MultiFileAnswers(model, FilesAllowed()).StreamAnswerAsync(Guid.NewGuid(), question, shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.Empty(model.Requests);
        Assert.Equal(
            AttachedDocuments.NoneReadText + " “gone.md” can't be found any more. It may have been moved or deleted. " +
            "“empty.txt” has no text the Assistant can read (a scanned document, say).",
            Assert.Single(shown).Text);
    });

    [Fact]
    public void StoppingFromTheChipWhileNotesAreTakenEndsTheAnswerAsStopped_AndTheQuestionIsNeverAsked() => RunSta(() =>
    {
        using var house = new TempDocument("house.md", GuideText("House guide", "Kitchen", "Garden", "Workshop", "Library", "Cellar", "Attic", "Studio", "Garage"));
        using var boat = new TempDocument("boat.md", GuideText("Boat guide", "Deck", "Galley", "Cabin", "Engine", "Sails", "Anchor", "Radio", "Dinghy"));
        var model = new NoteTakingModel { HoldNotes = true };
        var tracker = new ActivityTracker();
        var answers = MultiFileAnswers(model, FilesAllowed(), tracker);
        var question = new MessageViewModel(MessageRole.User, "summarize these", documents: [house.Attachment, boat.Attachment]);
        var shown = new List<MessageViewModel>();

        var asked = answers.StreamAnswerAsync(Guid.NewGuid(), question, shown.Add, CancellationToken.None);
        WaitUntil(() => model.NoteRequests.Count == 1, "No notes were asked for.");
        WaitUntil(() => tracker.Current?.Text.StartsWith("Reading 1 of ", StringComparison.Ordinal) == true, "The chip did not say how far it had got.");
        Assert.True(tracker.CancelCurrent());
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.True(model.NoteCancelled);
        Assert.Empty(model.Answers);
        var message = Assert.Single(shown);
        Assert.Equal(MessageStatus.Stopped, message.Status);
        Assert.Empty(message.Content);
    });

    [Fact]
    public void SeveralFilesAttachedToTheConversationAreAllAskedAbout_AndShowAsChipsAboveTheComposerAndTheMessage() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        var budget = Attached("Quarterly budget review.pdf");
        var minutes = Attached("Meeting minutes.docx");
        var plan = Attached("Project plan.md");
        try
        {
            model.StartWithFiles([], [budget, minutes, plan], null);
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();

            Assert.Equal([budget, minutes, plan], model.Documents);
            Assert.Equal(3, model.Chips.Count);
            Assert.All(model.Chips, chip => Assert.Equal(AttachmentKind.File, chip.Kind));
            Assert.Equal("Ask about these 3 files", Named<Assistant.UI.Controls.PromptInputControl>(panel, "ComposerInput").Placeholder);
            RenderGlass(panel, "several-documents-composer-2x.png", 2);

            // One more than a question reads is not attached.
            for (var index = 0; index < ConversationViewModel.MaxDocuments - 3; index++)
            {
                Assert.True(model.Attach(Attached($"extra {index}.txt")));
            }

            Assert.False(model.Attach(Attached("one too many.txt")));
            foreach (var extra in model.Documents.Skip(3).ToArray())
            {
                model.RemoveAttachmentCommand.Execute(extra);
            }

            model.Draft = "what changed between these?";
            model.AskCommand.Execute(null);
            Pump();
            System.Threading.Thread.Sleep(100);
            Pump();

            var asked = Assert.Single(model.Messages, message => message.Role == MessageRole.User);
            Assert.Equal([budget, minutes, plan], asked.Documents);
            Assert.Empty(model.Documents);
            var list = Descendants<ItemsControl>(Named<Grid>(panel, "ConversationLayer")).Single(control => control.Name == "Documents" && control.Items.Count > 0);
            Assert.Equal(
                ["Quarterly budget review.pdf", "Meeting minutes.docx", "Project plan.md"],
                Descendants<TextBlock>(list).Select(text => text.Text));
            RenderGlass(panel, "several-documents-asked-2x.png", 2);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void TheHistoryWindowsComposerTakesSeveralFiles_AndTheMessageIsAskedAboutThemAll() => RunSta(() =>
    {
        var model = new NoteTakingModel();
        var history = new HistoryViewModel(new FixedClock(Now), null, MultiFileAnswers(model, FilesAllowed()));
        var card = history.Open(Guid.NewGuid(), [new MessageViewModel(MessageRole.User, "hello"), new MessageViewModel(MessageRole.Assistant, "Hi.")], Now);
        var first = Attached("a.md");
        var second = Attached("b.md");

        Assert.True(history.Attach(first));
        Assert.True(history.Attach(second));
        history.Draft = "compare them";
        history.SendCommand.Execute(null);

        var question = card.Messages.Last(message => message.Role == MessageRole.User);
        Assert.Equal([first, second], question.Documents);
        Assert.Empty(history.Documents);
        Assert.Empty(history.Chips);
        WaitUntil(() => !history.IsAnswering, "The answer did not end.");
    });

    [Fact]
    public void EveryFileOnAMessageIsSavedAsItsNameAndPlace_AndComesBackInOrder()
    {
        var mapper = new MessageMapper(new FakeClipboard());
        var asked = new MessageViewModel(
            MessageRole.User, "compare these", documents: [Attached("Budget.pdf"), Attached("Plan.docx"), Attached("budget.PDF", @"C:\DOCS\BUDGET.PDF")])
        {
            CreatedAt = Now,
        };

        var saved = mapper.ToDomain(asked);

        Assert.Equal(["Budget.pdf", "Plan.docx"], asked.Documents.Select(document => document.Name));
        Assert.Equal(
            [(ContextItemType.File, "Budget.pdf", @"C:\Docs\Budget.pdf"), (ContextItemType.File, "Plan.docx", @"C:\Docs\Plan.docx")],
            saved.ContextItems.Select(item => (item.Type, item.DisplayName, item.FilePath)));
        Assert.All(saved.ContextItems, item => Assert.Null(item.Text));
        Assert.Equal(["Budget.pdf", "Plan.docx"], mapper.ToViewModel(saved).Documents.Select(document => document.Name));

        // A conversation opened from the history knows every file again, for the model to read by name.
        var known = new Assistant.Core.Files.ConversationFiles();
        var conversation = Guid.NewGuid();
        ConversationReplay.Rebuild([mapper.ToViewModel(saved)], conversation, known);
        Assert.Equal(["Budget.pdf", "Plan.docx"], known.Get(conversation, ["f1", "f2"]).Select(file => file.Name));
    }

    // A model that takes notes at once (named by the piece it was given) and answers the question as the test writes it; it can hold
    // the notes until it is stopped.
    private sealed class NoteTakingModel : IModelService
    {
        private readonly Channel<AssistantResponseChunk> _answer = Channel.CreateUnbounded<AssistantResponseChunk>();
        private readonly List<ModelRequest> _requests = [];

        public ModelInfo? Active { get; init; } = new("test-model", 8192);

        public bool HoldNotes { get; init; }

        public bool NoteCancelled { get; private set; }

        public IReadOnlyList<ModelRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public IReadOnlyList<ModelRequest> NoteRequests => [.. Requests.Where(IsNote)];

        public IReadOnlyList<ModelRequest> Answers => [.. Requests.Where(request => !IsNote(request) && !IsCombining(request))];

        public void Write(string text) => _answer.Writer.TryWrite(AssistantResponseChunk.ForTextDelta(text));

        public void End() => _answer.Writer.TryComplete();

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) => Task.FromResult(Active);

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            if (IsNote(request) || IsCombining(request))
            {
                if (HoldNotes)
                {
                    using var registration = cancellationToken.Register(() => NoteCancelled = true);
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                yield return AssistantResponseChunk.ForTextDelta($"- {NameIn(request)} says what it says.");
                yield break;
            }

            await foreach (var chunk in _answer.Reader.ReadAllAsync(cancellationToken))
            {
                yield return chunk;
            }
        }

        public static string NameIn(ModelRequest request) =>
            Regex.Match(request.Messages[^1].Text, "<untrusted_context [^>]*name=\"([^\"]*)\"").Groups[1].Value;

        private static bool IsNote(ModelRequest request) =>
            request.Instructions.StartsWith(AssistantInstructions.NoteTaking, StringComparison.Ordinal);

        private static bool IsCombining(ModelRequest request) =>
            request.Instructions.StartsWith(AssistantInstructions.NoteCombining, StringComparison.Ordinal);
    }
}

/// <summary>How the app wires the reading of several files (PROJECT_SPEC §5.5, several files).</summary>
public sealed class MultiFileWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-ui-multifile-tests", Guid.NewGuid().ToString("N"));

    public MultiFileWiringTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is harmless.
        }
    }

    [Fact]
    public async Task TheAttachedDocumentsAreReadByTheOneMultiFileProcessor_WhichTakesNotesWithTheDefaultLimits()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Assistant",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Development",
        });
        builder.Services.AddAssistantServices().AddUserInterface();
        builder.Services.AddSingleton(new AppPaths(_root));
        using var host = builder.Build();

        var processor = Assert.IsType<MultiFileProcessor>(host.Services.GetRequiredService<IMultiFileProcessor>());
        Assert.Same(processor, host.Services.GetRequiredService<IMultiFileProcessor>());
        Assert.True(processor.Limits.TakeNotes);
        Assert.Equal(MultiFileLimits.Default.Resolve(), processor.Limits);

        var attached = host.Services.GetRequiredService<AttachedDocuments>();
        var used = typeof(AttachedDocuments).GetField("_processor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(attached);
        Assert.Same(processor, used);

        // Two short files are read and go to the model as they are (no model is set up here, so no notes could be taken anyway).
        var first = Path.Combine(_root, "first.txt");
        var second = Path.Combine(_root, "second.md");
        await File.WriteAllTextAsync(first, "The first file.");
        await File.WriteAllTextAsync(second, "# Second\n\nThe second file.");
        var result = await processor.ProcessAsync([new("first.txt", first), new("second.md", second)], "what are these?");
        Assert.Equal(MultiFileStrategy.Direct, result.Strategy);
        Assert.Equal(["first.txt", "second.md"], result.Items.Select(item => item.DisplayName));
        Assert.Contains("The second file.", result.Items[1].Text, StringComparison.Ordinal);
    }
}
