using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Assistant.Core.Activity;
using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Core.Domain;
using Assistant.Core.ModelHosting;
using Assistant.Core.MultiFile;
using Assistant.Core.Orchestration;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// Questions about several files, or one long one (PROJECT_SPEC §5.5, several files): what fits one prompt goes to the model directly,
/// and what does not is read in pieces the model takes notes on, one at a time and each on its own, combined while there are too
/// many, within the limits on files, reads and requests at once, requests in all and text held in memory.
/// </summary>
public sealed partial class MultiFileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // ---- The planner ----------------------------------------------------------------------------------------------------------------

    private static DocumentPassage Passage(int index, int length, int overlap = 0) => new()
    {
        Index = index,
        Text = new string('a', length),
        Location = DocumentLocation.ForPage(index + 1),
        OverlapLength = overlap,
    };

    private static PassageSelection Selection(PassageSelectionReason reason, int total, params (int Index, double Score)[] passages) => new()
    {
        Passages = [.. passages.Select(passage => new SelectedPassage(Passage(passage.Index, 1000), passage.Score))],
        TotalPassages = total,
        Reason = reason,
    };

    private static PassageSelection Run(int count, PassageSelectionReason reason = PassageSelectionReason.NoQueryTerms) =>
        Selection(reason, count, [.. Enumerable.Range(0, count).Select(index => (index, 1.0))]);

    [Fact]
    public void PassagesAreGatheredInOrderIntoPiecesThatFit_AndTheirOverlapIsCountedOnce()
    {
        // Ten passages of 1,000 that follow each other: the first costs its marker too, so 3,048 + 1,000 does not fit 4,000.
        var pieces = MapPlanner.Pack(Run(10), 4_000);
        Assert.Equal([3, 3, 3, 1], pieces.Select(piece => piece.Count));
        Assert.Equal(Enumerable.Range(0, 10), pieces.SelectMany(piece => piece).Select(selected => selected.Passage.Index));

        // Passages that repeat the end of the one before cost only their new text, and one that does not follow pays its marker again.
        var overlapping = new PassageSelection
        {
            Passages = [new(Passage(0, 1000), 1), new(Passage(1, 1000, overlap: 500), 1), new(Passage(2, 1000, overlap: 500), 1), new(Passage(7, 1000), 1)],
            TotalPassages = 8,
        };
        Assert.Equal([3, 1], MapPlanner.Pack(overlapping, 2_100).Select(piece => piece.Count));

        // A passage longer than a piece is a piece of its own.
        Assert.Equal([1, 1], MapPlanner.Pack(Run(2), 500).Select(piece => piece.Count));
    }

    [Fact]
    public void TheRequestsAreSharedAsWater_ShortFilesWholeAndLongOnesTheRest()
    {
        Assert.Equal([1, 4, 4], MapPlanner.Share([1, 10, 10], 9));
        Assert.Equal([1, 1, 1], MapPlanner.Share([1, 1, 1], 16));
        Assert.Equal([2, 5, 5, 4], MapPlanner.Share([2, 9, 9, 4], 16));

        // What flooring leaves over goes to the first files that want more.
        Assert.Equal([4, 3, 3], MapPlanner.Share([9, 9, 9], 10));

        // Fewer requests than files: the first files have one each.
        Assert.Equal([1, 1, 0, 0], MapPlanner.Share([5, 5, 5, 5], 2));
        Assert.Equal([0, 1, 1], MapPlanner.Share([0, 3, 3], 2));
    }

    [Fact]
    public void AFileWithMorePiecesThanItsShareKeepsThoseTheQuestionNeeds()
    {
        static List<List<SelectedPassage>> Pieces(params double[] scores) =>
            [.. scores.Select((score, index) => new List<SelectedPassage> { new(Passage(index, 10), score) })];
        static int[] Kept(List<List<SelectedPassage>> chosen) => [.. chosen.Select(piece => piece[0].Passage.Index)];

        // About the whole file: spread from the first to the last.
        Assert.Equal([0, 5, 9], Kept(MapPlanner.Choose(Pieces(new double[10]), 3, PassageSelectionReason.NoQueryTerms)));
        Assert.Equal([0, 9], Kept(MapPlanner.Choose(Pieces(new double[10]), 2, PassageSelectionReason.WholeDocument)));
        Assert.Equal([0], Kept(MapPlanner.Choose(Pieces(new double[10]), 1, PassageSelectionReason.NoQueryTerms)));

        // With words to look for: the best, kept in the order of the file.
        Assert.Equal([1, 3], Kept(MapPlanner.Choose(Pieces(1, 9, 2, 8, 3), 2, PassageSelectionReason.Matched)));

        // Nothing matched: its start.
        Assert.Equal([0, 1], Kept(MapPlanner.Choose(Pieces(1, 9, 2, 8, 3), 2, PassageSelectionReason.NoMatch)));
        Assert.Empty(MapPlanner.Choose(Pieces(1, 2), 0, PassageSelectionReason.Matched));
    }

    [Fact]
    public void ThePlanNamesEachPieceAndCountsWhatItHolds_TheSameEveryTime()
    {
        var files = new[]
        {
            new FileSelection(0, new QuestionFile("short.md", @"C:\Docs\short.md"), Run(2)),
            new FileSelection(1, new QuestionFile("long.pdf", @"C:\Docs\long.pdf"), Run(40)),
            new FileSelection(3, new QuestionFile("none.txt", @"C:\Docs\none.txt"), Run(30, PassageSelectionReason.NoMatch)),
        };

        var plan = MapPlanner.Plan(files, 4_000, 6);
        var again = MapPlanner.Plan(files, 4_000, 6);

        // short.md one piece, none.txt never more than one, long.pdf the four left of six, spread over its fourteen (pieces 1, 5, 10 and
        // 14, the last of one passage).
        Assert.Equal([1, 4, 1], plan.PartsOf);
        Assert.Equal([2, 10, 3], plan.PassagesRead);
        Assert.Equal(["short.md", "long.pdf (part 1 of 4)", "long.pdf (part 2 of 4)", "long.pdf (part 3 of 4)", "long.pdf (part 4 of 4)", "none.txt"],
            plan.Parts.Select(part => part.Label));
        Assert.Equal([0, 1, 1, 1, 1, 3], plan.Parts.Select(part => part.FileIndex));
        Assert.Equal(39, plan.Parts[4].Passages.Passages[^1].Passage.Index);
        Assert.Equal(40, plan.Parts[1].Passages.TotalPassages);
        Assert.Equal(
            plan.Parts.Select(part => string.Join(',', part.Passages.Passages.Select(selected => selected.Passage.Index))),
            again.Parts.Select(part => string.Join(',', part.Passages.Passages.Select(selected => selected.Passage.Index))));

        // Its text never shows in a log.
        Assert.DoesNotContain("long.pdf", plan.Parts[1].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("aaaa", plan.Parts[1].ToString(), StringComparison.Ordinal);
    }

    // ---- Notes ----------------------------------------------------------------------------------------------------------------------

    private static readonly QuestionFile[] ThreeFiles =
        [new("a.pdf", @"C:\Docs\a.pdf"), new("b.docx", @"C:\Docs\b.docx"), new("c.md", @"C:\Docs\c.md")];

    [Fact]
    public void NotesThatFoundNothingAreLeftOut_UnlessTheyAreAllAFileHas()
    {
        var notes = new FileNote[]
        {
            new([0], "a.pdf (part 1 of 2)", "- The budget is $4m (page 2).", @"C:\Docs\a.pdf"),
            new([0], "a.pdf (part 2 of 2)", "Nothing relevant.", @"C:\Docs\a.pdf"),
            new([1], "b.docx (part 1 of 2)", "nothing relevant", @"C:\Docs\b.docx"),
            new([1], "b.docx (part 2 of 2)", "  ", @"C:\Docs\b.docx"),
            new([2], "c.md", "- Nothing relevant was decided, the meeting moved to Friday.", @"C:\Docs\c.md"),
        };

        var kept = FileNotes.Collapse(notes, ThreeFiles);

        Assert.Equal(["a.pdf (part 1 of 2)", "b.docx", "c.md"], kept.Select(note => note.Label));
        Assert.Equal(FileNote.NothingRelevant, kept[1].Text);
        Assert.Equal(@"C:\Docs\b.docx", kept[1].Path);
        Assert.Equal("- Nothing relevant was decided, the meeting moved to Friday.", kept[2].Text);
    }

    [Fact]
    public void NotesAreGroupedInOrder_AsManyAsFitARequest()
    {
        var notes = Enumerable.Range(0, 5).Select(index => new FileNote([index % 3], $"n{index}", new string('x', 10), null)).ToArray();
        var groups = FileNotes.Group(notes, note => note.Label == "n2" ? 50 : 10, 30);
        Assert.Equal([["n0", "n1"], ["n2"], ["n3", "n4"]], groups.Select(group => group.Select(note => note.Label).ToArray()));

        Assert.Equal("a.pdf, b.docx", FileNotes.CombinedLabel([notes[0], notes[1], notes[3]], ThreeFiles));
        Assert.Equal("a.pdf", FileNotes.CombinedLabel([notes[0], notes[3]], ThreeFiles));
        var many = Enumerable.Range(0, 5).Select(index => new QuestionFile($"f{index}.txt", $@"C:\f{index}.txt")).ToArray();
        Assert.Equal(
            "f0.txt, f1.txt, f2.txt and 2 more files",
            FileNotes.CombinedLabel([.. Enumerable.Range(0, 5).Select(index => new FileNote([index], "x", "y", null))], many));
    }

    [Fact]
    public void TheModelsNotesAreTidiedAndCutWhereALineEnds()
    {
        Assert.Equal("- one\n- two\n\n- three", FileNote.Clean("\r\n- one  \r\n- two\r\n\r\n\r\n- three\n\n", 100));
        Assert.Equal("- first line", FileNote.Clean("- first line\n- second line that runs on", 25));
        Assert.Equal(new string('y', 10), FileNote.Clean(new string('y', 30), 10));

        // Notes the model's length limit cut off lose the line it cut through, unless it is all there is.
        Assert.Equal("- one\n- two", FileNote.Clean("- one\n- two\n- thr", 100, cutShort: true));
        Assert.Equal("- only one, cut", FileNote.Clean("- only one, cut", 100, cutShort: true));
        Assert.True(FileNote.IsNothingText(""));
        Assert.True(FileNote.IsNothingText("**Nothing relevant.**"));
        Assert.False(FileNote.IsNothingText("- The plan changed."));
    }

    // ---- What the user is told ------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheUserIsToldTheFilesWereReadInPiecesAndWhatWasLeftOut()
    {
        var file = new QuestionFile("Quarterly review.pdf", @"C:\Docs\Quarterly review.pdf");
        var whole = new FileOutcome(file, DocumentReadStatus.Success) { TotalPassages = 12, PassagesRead = 12, Parts = 2 };
        Assert.Equal(
            ["“Quarterly review.pdf” is too long to read at once, so it was read in pieces, and this answer is put together from notes on each piece."],
            MultiFileNotices.ForNotes([whole]));

        var spread = whole with { TotalPassages = 40, PassagesRead = 24, Reason = PassageSelectionReason.NoQueryTerms, Truncated = true };
        var matched = new FileOutcome(new QuestionFile("plan.docx", @"C:\plan.docx"), DocumentReadStatus.Success)
        {
            TotalPassages = 30,
            PassagesRead = 5,
            Reason = PassageSelectionReason.Matched,
            Parts = 1,
        };
        var gone = new FileOutcome(new QuestionFile("gone.md", @"C:\gone.md"), DocumentReadStatus.NotFound);
        Assert.Equal(
            [
                "These 2 files are too long to read at once, so they were read in pieces, and this answer is put together from notes on each piece.",
                "“Quarterly review.pdf” is very long, or some of it couldn't be read, so the answer may leave something out.",
                "“Quarterly review.pdf” is long, so only 24 parts spread across it were read, out of 40.",
                "Only the 5 parts of “plan.docx” that best match your question were read, out of 30.",
            ],
            MultiFileNotices.ForNotes([spread, gone, matched]));

        Assert.Equal(
            "Only 10 files can be read for one question, so “k.txt” was left out.",
            MultiFileNotices.LeftOut([new("k.txt", @"C:\k.txt")], 10));
        Assert.Equal(
            "Only 3 files can be read for one question, so “a”, “b”, “c” and 2 other files were left out.",
            MultiFileNotices.LeftOut([.. "abcde".Select(letter => new QuestionFile(letter.ToString(), $@"C:\{letter}"))], 3));
    }

    // ---- Prompts --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void NotesGoToTheModelMarkedAsNotes_AndItIsToldWhatTheyAre()
    {
        var notes = new ContextItem(Guid.NewGuid(), ContextItemType.FileNotes, "budget.pdf (part 2 of 3)") { Text = "- Total $4m (page 7)." };
        var built = new PromptBuilder().Build(
            null, [new Message(Guid.NewGuid(), MessageRole.User, "What is the total?", Now) { ContextItems = [notes] }], new ModelInfo("m", 8192));

        Assert.EndsWith(AssistantInstructions.FileNotesGuidance, built.Request.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain(AssistantInstructions.FileContextGuidance, built.Request.Instructions, StringComparison.Ordinal);
        Assert.StartsWith(
            "<untrusted_context id=\"1\" kind=\"file_notes\" name=\"budget.pdf (part 2 of 3)\">", built.Request.Messages[0].Text, StringComparison.Ordinal);
        Assert.Equal(ContextSource.UserSelected, Budgeting.ContextPriorityRules.SourceOf(notes));

        // A file's own text still has its own guidance, and only that.
        var file = new ContextItem(Guid.NewGuid(), ContextItemType.File, "budget.pdf") { Text = "Total: $4m" };
        var plain = new PromptBuilder().Build(
            null, [new Message(Guid.NewGuid(), MessageRole.User, "What is the total?", Now) { ContextItems = [file] }], new ModelInfo("m", 8192));
        Assert.EndsWith(AssistantInstructions.FileContextGuidance, plain.Request.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain(AssistantInstructions.FileNotesGuidance, plain.Request.Instructions, StringComparison.Ordinal);
    }

    // ---- The processor --------------------------------------------------------------------------------------------------------------

    private static MultiFileProcessor Processor(
        FakeDocuments documents,
        IModelService? model,
        MultiFileLimits? limits = null,
        IActivityTracker? tracker = null,
        ILogger<MultiFileProcessor>? logger = null,
        AppSettings? settings = null) =>
        // These passage-count fixtures use an explicit 8K window rather than the app's configurable default.
        new(documents, model, new FixedSettings(settings ?? new AppSettings
        {
            ContextLimits = new ContextLimitSettings { NormalContextTokens = 8192, HeavyContextTokens = 8192 },
        }), clock: new TestClock(Now), tracker: tracker, logger: logger, limits: limits);

    private static QuestionFile File(string name) => new(name, @"C:\Docs\" + name);

    [Fact]
    public async Task FilesThatFitOnePromptGoToTheModelDirectly_AndNoNotesAreTaken()
    {
        var documents = new FakeDocuments().Add("a.md", 1).Add("b.txt", 2).Add("c.pdf", 1);
        var model = new NoteModel();

        var result = await Processor(documents, model).ProcessAsync([File("a.md"), File("b.txt"), File("c.pdf")], "what are these about?");

        Assert.Equal(MultiFileStrategy.Direct, result.Strategy);
        Assert.Equal(["a.md", "b.txt", "c.pdf"], result.Items.Select(item => item.DisplayName));
        Assert.All(result.Items, item => Assert.Equal(ContextItemType.File, item.Type));
        Assert.All(result.Items, item => Assert.Equal(ContextSource.UserSelected, item.Source));
        Assert.Contains("[Passage 2: page 2]", result.Items[1].Text, StringComparison.Ordinal);
        Assert.Contains("B-TXT passage 1", result.Items[1].Text, StringComparison.Ordinal);
        Assert.Equal(@"C:\Docs\b.txt", result.Items[1].FilePath);
        Assert.Empty(result.Notices);
        Assert.Equal(0, result.ModelCalls);
        Assert.Empty(model.Requests);

        // Each file was read once.
        Assert.Equal(3, documents.Reads.Count);
    }

    [Fact]
    public async Task FilesTooLongForOnePromptAreReadInPieces_EachOnItsOwn_AndTheAnswersContextIsTheNotes()
    {
        var documents = new FakeDocuments().Add("alpha.pdf", 10).Add("beta.docx", 10).Add("gamma.md", 10);
        var model = new NoteModel();

        var result = await Processor(documents, model).ProcessAsync([File("alpha.pdf"), File("beta.docx"), File("gamma.md")], "what are these about?");

        Assert.Equal(MultiFileStrategy.MapReduce, result.Strategy);
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(3, result.ModelCalls);
        Assert.Equal(["alpha.pdf", "beta.docx", "gamma.md"], result.Items.Select(item => item.DisplayName));
        Assert.All(result.Items, item => Assert.Equal(ContextItemType.FileNotes, item.Type));
        Assert.Equal("- A note on alpha.pdf.", result.Items[0].Text);
        Assert.Equal(@"C:\Docs\alpha.pdf", result.Items[0].FilePath);
        Assert.All(result.Files, file => Assert.True(file.IsComplete));
        Assert.Equal(
            ["These 3 files are too long to read at once, so they were read in pieces, and this answer is put together from notes on each piece."],
            result.Notices);

        // Each request is one file's piece alone, marked as the user's file, and asks for notes for the question, briefly.
        foreach (var (request, tag) in model.Requests.Zip(["ALPHA", "BETA", "GAMMA"]))
        {
            Assert.StartsWith(AssistantInstructions.NoteTaking, request.Instructions, StringComparison.Ordinal);
            var message = Assert.Single(request.Messages);
            Assert.Single(Regex.Matches(message.Text, "<untrusted_context "));
            Assert.Contains($"{tag}-", message.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(new[] { "ALPHA-", "BETA-", "GAMMA-" }.Where(other => !other.StartsWith(tag, StringComparison.Ordinal)), other => message.Text.Contains(other, StringComparison.Ordinal));
            Assert.EndsWith(AssistantInstructions.NoteRequest("what are these about?", request.Label()), message.Text, StringComparison.Ordinal);
            Assert.StartsWith("Question: what are these about?\n\nTake the notes on " + request.Label() + " for this question:", AssistantInstructions.NoteRequest("what are these about?", request.Label()), StringComparison.Ordinal);
            Assert.Equal(MultiFileProcessor.NoteTemperature, request.Temperature);
            Assert.InRange(request.MaxOutputTokens ?? 0, MultiFileLimits.DefaultMinNoteTokens, MultiFileLimits.DefaultMaxNoteTokens);
        }
    }

    [Fact]
    public async Task AQuestionAboutTheWholeOfOneLongFileReadsAllOfItInPieces()
    {
        var documents = new FakeDocuments().Add("handbook.md", 30);
        var model = new NoteModel();

        var result = await Processor(documents, model).ProcessAsync([File("handbook.md")], "summarize it");

        // Read once for one prompt (it did not fit), then again whole, in three pieces of eleven, eleven and eight passages.
        Assert.Equal(2, documents.Reads.Count);
        Assert.Equal(MultiFileStrategy.MapReduce, result.Strategy);
        Assert.Equal(["handbook.md (part 1 of 3)", "handbook.md (part 2 of 3)", "handbook.md (part 3 of 3)"], result.Items.Select(item => item.DisplayName));
        var file = Assert.Single(result.Files);
        Assert.Equal((30, 30, 3), (file.TotalPassages, file.PassagesRead, file.Parts));
        Assert.Equal(
            ["“handbook.md” is too long to read at once, so it was read in pieces, and this answer is put together from notes on each piece."],
            result.Notices);
        Assert.Contains("[Passage 1: page 23]", model.Requests[2].Messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQuestionWithWordsToLookForInOneFileReadsTheMatchingPassagesAsBefore()
    {
        var documents = new FakeDocuments().Add("handbook.md", 30);
        var model = new NoteModel();

        var result = await Processor(documents, model).ProcessAsync([File("handbook.md")], "when is the workshop open?");

        Assert.Equal(MultiFileStrategy.Direct, result.Strategy);
        Assert.Single(documents.Reads);
        Assert.Empty(model.Requests);
        var item = Assert.Single(result.Items);
        Assert.Equal(ContextItemType.File, item.Type);
        Assert.Equal(["Only the 8 parts of “handbook.md” that best match your question were read, out of 30."], result.Notices);
    }

    [Fact]
    public async Task ReadsAndModelRequestsRunNoMoreAtOnceThanTheLimitsAllow()
    {
        var names = Enumerable.Range(0, 6).Select(index => $"file{index}.txt").ToArray();
        var documents = new FakeDocuments { Delay = TimeSpan.FromMilliseconds(30) };
        foreach (var name in names)
        {
            documents.Add(name, 12);
        }

        var model = new NoteModel { Delay = TimeSpan.FromMilliseconds(20) };
        var result = await Processor(documents, model).ProcessAsync([.. names.Select(File)], "summarize these");

        Assert.Equal(MultiFileStrategy.MapReduce, result.Strategy);
        Assert.Equal(MultiFileLimits.DefaultMaxConcurrentReads, documents.MostAtOnce);
        Assert.Equal(1, model.MostAtOnce);

        // A limit that allows more is what bounds them.
        var wider = new FakeDocuments { Delay = TimeSpan.FromMilliseconds(30) };
        foreach (var name in names)
        {
            wider.Add(name, 12);
        }

        var parallel = new NoteModel { Delay = TimeSpan.FromMilliseconds(40) };
        await Processor(wider, parallel, new MultiFileLimits { MaxConcurrentReads = 3, MaxConcurrentModelCalls = 3 })
            .ProcessAsync([.. names.Select(File)], "summarize these");
        Assert.Equal(3, wider.MostAtOnce);
        Assert.Equal(3, parallel.MostAtOnce);
        Assert.InRange(parallel.Requests.Count, 6, 12);
    }

    [Fact]
    public async Task EachFileKeepsNoMoreTextThanItsShareOfWhatIsHeld_AndThePiecesNeverExceedTheRequests()
    {
        var names = Enumerable.Range(0, 4).Select(index => $"long{index}.pdf").ToArray();
        var documents = new FakeDocuments();
        foreach (var name in names)
        {
            documents.Add(name, 60);
        }

        var model = new NoteModel();
        var limits = new MultiFileLimits { MaxHeldCharacters = 40_000, MaxMapCalls = 5, MaxPartCharacters = 4_000 };
        var result = await Processor(documents, model, limits).ProcessAsync([.. names.Select(File)], "summarize these");

        Assert.All(documents.Reads, read => Assert.Equal(10_000, read.Options!.Selection!.MaxCharacters));
        Assert.All(documents.Reads, read => Assert.True(read.Selected <= 10_000));
        Assert.Equal(5, model.Requests.Count);
        Assert.Equal(5, result.Files.Sum(file => file.Parts));
        Assert.All(result.Files, file => Assert.InRange(file.Parts, 1, 2));
        Assert.All(result.Files, file => Assert.Equal(60, file.TotalPassages));

        // The user is told that each was read only in part, spread across it.
        Assert.Equal(5, result.Notices.Count);
        Assert.Contains(result.Notices, notice => notice.StartsWith("“long0.pdf” is long, so only ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotesThatAreTooManyToAnswerFromAreCombined_WithinTheLimit()
    {
        var documents = new FakeDocuments().Add("a.txt", 3).Add("b.txt", 3).Add("c.txt", 3);
        var model = new NoteModel { NoteLength = 400, ContextLength = 2048 };

        var result = await Processor(documents, model).ProcessAsync([File("a.txt"), File("b.txt"), File("c.txt")], "what are these about?");

        var combining = model.Requests.Where(request => request.Instructions.StartsWith(AssistantInstructions.NoteCombining, StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(combining);
        Assert.InRange(combining.Length, 1, MultiFileLimits.DefaultMaxCombineCalls);
        Assert.Equal(model.Requests.Count, result.ModelCalls);
        Assert.True(result.Items.Count < model.Requests.Count - combining.Length);
        Assert.Contains(result.Items, item => item.Text == "- Combined." && item.DisplayName.Contains(',', StringComparison.Ordinal));
        Assert.All(combining, request => Assert.Contains("kind=\"file_notes\"", request.Messages[0].Text, StringComparison.Ordinal));
        Assert.All(combining, request => Assert.EndsWith(AssistantInstructions.CombineRequest("what are these about?"), request.Messages[0].Text, StringComparison.Ordinal));

        // One combining request at most: the notes it could not reach are left as they are, for the answer's budget to fit.
        var once = new NoteModel { NoteLength = 400, ContextLength = 2048 };
        await Processor(new FakeDocuments().Add("a.txt", 3).Add("b.txt", 3).Add("c.txt", 3), once, new MultiFileLimits { MaxCombineCalls = 1 })
            .ProcessAsync([File("a.txt"), File("b.txt"), File("c.txt")], "what are these about?");
        Assert.Single(once.Requests, request => request.Instructions.StartsWith(AssistantInstructions.NoteCombining, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFileThatCannotBeReadIsReportedOnItsOwn_AndTheRestAreUsed()
    {
        var documents = new FakeDocuments().Add("a.pdf", 10).Add("b.pdf", 10).Fail("locked.docx", DocumentReadStatus.Encrypted);
        var model = new NoteModel();

        var result = await Processor(documents, model).ProcessAsync([File("a.pdf"), File("locked.docx"), File("b.pdf")], "summarize these");

        Assert.Equal(["a.pdf", "locked.docx", "b.pdf"], result.Files.Select(file => file.File.Name));
        Assert.Equal(
            [DocumentReadStatus.Success, DocumentReadStatus.Encrypted, DocumentReadStatus.Success], result.Files.Select(file => file.Status));
        Assert.Equal(["a.pdf", "b.pdf"], result.Items.Select(item => item.DisplayName));
        Assert.Equal(2, model.Requests.Count);

        // None that can be read: nothing to ask with, nothing asked of the model.
        var none = await Processor(new FakeDocuments().Fail("x.pdf", DocumentReadStatus.NotFound).Fail("y.pdf", DocumentReadStatus.Corrupt), model)
            .ProcessAsync([File("x.pdf"), File("y.pdf")], "summarize these");
        Assert.False(none.HasContext);
        Assert.Equal([DocumentReadStatus.NotFound, DocumentReadStatus.Corrupt], none.Files.Select(file => file.Status));
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task FilesOverTheLimitAreLeftOutAndNamed_AndTheSameFileIsReadOnce()
    {
        var documents = new FakeDocuments();
        var names = Enumerable.Range(0, 12).Select(index => $"f{index}.txt").ToArray();
        foreach (var name in names)
        {
            documents.Add(name, 1);
        }

        var result = await Processor(documents, new NoteModel())
            .ProcessAsync([File("f0.txt"), new QuestionFile("F0.TXT", @"c:\docs\F0.TXT"), .. names.Skip(1).Select(File)], "what are these?");

        Assert.Equal(10, result.Files.Count);
        Assert.Equal(["f10.txt", "f11.txt"], result.LeftOut.Select(file => file.Name));
        Assert.Equal(10, documents.Reads.Count);
        Assert.Equal("Only 10 files can be read for one question, so “f10.txt” and “f11.txt” were left out.", result.Notices[0]);

        // The user's own limit, when it is lower, is the limit.
        var settings = new AppSettings { ContextLimits = new ContextLimitSettings { MaxAttachedFiles = 2 } };
        var fewer = await Processor(documents, new NoteModel(), settings: settings).ProcessAsync([.. names.Take(3).Select(File)], "what are these?");
        Assert.Equal(2, fewer.Files.Count);
        Assert.Equal(["f2.txt"], fewer.LeftOut.Select(file => file.Name));
    }

    [Fact]
    public async Task TheUsersOwnLimitCanBeHigherThanTheDefaultOfTen()
    {
        var documents = new FakeDocuments();
        var names = Enumerable.Range(0, 12).Select(index => $"f{index}.txt").ToArray();
        foreach (var name in names)
        {
            documents.Add(name, 1);
        }

        var settings = new AppSettings { ContextLimits = new ContextLimitSettings { MaxAttachedFiles = 12 } };
        var result = await Processor(documents, new NoteModel(), settings: settings).ProcessAsync([.. names.Select(File)], "what are these?");

        Assert.Equal(12, result.Files.Count);
        Assert.Empty(result.LeftOut);
    }

    [Fact]
    public async Task WithoutAModelToTakeNotes_TheFilesShareOnePrompt()
    {
        var documents = new FakeDocuments().Add("a.pdf", 30).Add("b.pdf", 30);

        var result = await Processor(documents, new NoteModel { Active = null }).ProcessAsync([File("a.pdf"), File("b.pdf")], "summarize these");
        var off = await Processor(new FakeDocuments().Add("a.pdf", 30), new NoteModel(), new MultiFileLimits { TakeNotes = false })
            .ProcessAsync([File("a.pdf")], "summarize it");

        Assert.Equal(MultiFileStrategy.Direct, result.Strategy);
        Assert.Equal(2, result.Items.Count);
        Assert.All(documents.Reads, read => Assert.True(read.Options!.Selection!.MaxCharacters < 6_000));
        Assert.Equal(MultiFileStrategy.Direct, off.Strategy);
        Assert.Contains("“a.pdf” is long, so only ", Assert.Single(off.Notices), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheChipSaysHowFarTheReadingHasGot_NeverAName_AndStoppingItStopsTheWork()
    {
        var tracker = new ActivityTracker();
        var shown = new ConcurrentQueue<string>();
        tracker.Changed += (_, _) =>
        {
            if (tracker.Current?.Text is { } text)
            {
                shown.Enqueue(text);
            }
        };
        var documents = new FakeDocuments().Add("alpha.pdf", 10).Add("beta.pdf", 10).Add("gamma.pdf", 10);

        await Processor(documents, new NoteModel(), tracker: tracker).ProcessAsync([File("alpha.pdf"), File("beta.pdf"), File("gamma.pdf")], "summarize these");

        Assert.Contains("Reading", shown);
        Assert.Contains("Reading 1 of 3", shown);
        Assert.Contains("Reading 3 of 3", shown);
        Assert.DoesNotContain(shown, text => text.Contains("pdf", StringComparison.OrdinalIgnoreCase));
        Assert.Null(tracker.Current);

        // Stopped from the chip while a note is taken: the work ends there, and the model is told to stop.
        var waiting = new NoteModel { WaitForCancel = true };
        var work = Processor(new FakeDocuments().Add("alpha.pdf", 10).Add("beta.pdf", 10), waiting, tracker: tracker)
            .ProcessAsync([File("alpha.pdf"), File("beta.pdf")], "summarize these");
        Assert.True(SpinWait.SpinUntil(() => waiting.Requests.Count == 1, TimeSpan.FromSeconds(10)), "No note was asked for.");
        Assert.True(tracker.CancelCurrent());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.True(waiting.Cancelled);
        Assert.Single(waiting.Requests);
    }

    [Fact]
    public async Task StoppingTheQuestionStopsTheWork_AndAModelThatFailsFailsIt()
    {
        using var stop = new CancellationTokenSource();
        var waiting = new NoteModel { WaitForCancel = true };
        var work = Processor(new FakeDocuments().Add("a.pdf", 10).Add("b.pdf", 10), waiting).ProcessAsync([File("a.pdf"), File("b.pdf")], "summarize these", stop.Token);
        Assert.True(SpinWait.SpinUntil(() => waiting.Requests.Count == 1, TimeSpan.FromSeconds(10)), "No note was asked for.");
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);

        var failing = new NoteModel { FailOn = 2 };
        var failure = await Assert.ThrowsAsync<ModelHostException>(() => Processor(new FakeDocuments().Add("a.pdf", 10).Add("b.pdf", 10).Add("c.pdf", 10), failing)
            .ProcessAsync([File("a.pdf"), File("b.pdf"), File("c.pdf")], "summarize these"));
        Assert.Equal(ModelHostErrorCode.GenerationFailed, failure.Code);
        Assert.Equal(2, failing.Requests.Count);
    }

    [Fact]
    public async Task ItLogsCountsOnly_NeverANameAPathTheQuestionOrAnyText()
    {
        using var capture = new CapturingLoggerProvider();
        using var factory = capture.CreateFactory();
        var documents = new FakeDocuments().Add("secret-plan.pdf", 10).Add("salary-list.docx", 10).Fail("diary.md", DocumentReadStatus.Unreadable);

        await Processor(documents, new NoteModel(), logger: factory.CreateLogger<MultiFileProcessor>())
            .ProcessAsync([File("secret-plan.pdf"), File("salary-list.docx"), File("diary.md")], "who earns the most?");

        Assert.Contains(capture.Entries, entry => entry.Contains(" 2500 ", StringComparison.Ordinal) && entry.Contains("3 files (2 read", StringComparison.Ordinal));
        Assert.Contains(capture.Entries, entry => entry.Contains(" 2501 ", StringComparison.Ordinal));
        foreach (var secret in new[] { "secret", "salary", "diary", @"C:\Docs", "earns", "PASSAGE", "passage", "A note on" })
        {
            Assert.DoesNotContain(secret, capture.AllText, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("secret", new QuestionFile("secret.pdf", @"C:\secret.pdf").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotesTheLengthLimitCutOffLoseTheLineItCutThrough()
    {
        var model = new NoteModel { CutShort = true };

        var result = await Processor(new FakeDocuments().Add("a.pdf", 10).Add("b.pdf", 10), model)
            .ProcessAsync([File("a.pdf"), File("b.pdf")], "summarize these");

        Assert.Equal(["- A note on a.pdf.", "- A note on b.pdf."], result.Items.Select(item => item.Text));
    }

    // ---- Doubles --------------------------------------------------------------------------------------------------------------------

    // Files of numbered passages of 1,000 characters, one to a page, selected the way the real selector does in outline: all of them
    // when they fit, otherwise spread for a question with nothing to look for ("summarize", "about", "what are these"), or the best
    // matches (every third passage scores highest) for one with words.
    private sealed class FakeDocuments : IDocumentContextService
    {
        private readonly Dictionary<string, (int Passages, DocumentReadStatus Status)> _files = new(StringComparer.OrdinalIgnoreCase);
        private int _running;
        private int _most;

        public TimeSpan Delay { get; init; }

        public ConcurrentQueue<(string Path, DocumentContextOptions? Options, int Selected)> Reads { get; } = new();

        public int MostAtOnce => _most;

        public FakeDocuments Add(string name, int passages)
        {
            _files[@"C:\Docs\" + name] = (passages, DocumentReadStatus.Success);
            return this;
        }

        public FakeDocuments Fail(string name, DocumentReadStatus status)
        {
            _files[@"C:\Docs\" + name] = (0, status);
            return this;
        }

        public async Task<DocumentContextResult> GetContextAsync(
            string filePath, string question, DocumentContextOptions? options = null, CancellationToken cancellationToken = default)
        {
            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref _most, running);
            try
            {
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken);
                }

                if (!_files.TryGetValue(filePath, out var file) || file.Status != DocumentReadStatus.Success)
                {
                    Reads.Enqueue((filePath, options, 0));
                    return DocumentContextResult.Failed(file.Status == DocumentReadStatus.Success ? DocumentReadStatus.NotFound : file.Status);
                }

                var tag = Path.GetFileName(filePath).Replace('.', '-').ToUpperInvariant();
                var passages = Enumerable.Range(0, file.Passages).Select(index => new DocumentPassage
                {
                    Index = index,
                    Text = $"{tag} passage {index}: ".PadRight(1000, 'w'),
                    Location = DocumentLocation.ForPage(index + 1),
                }).ToArray();
                var limit = options?.Selection?.MaxCharacters ?? PassageSelectionOptions.DefaultMaxCharacters;
                var most = options?.Selection?.MaxPassages ?? PassageSelectionOptions.DefaultMaxPassages;
                var overview = Regex.IsMatch(question, "summar|about|what are these", RegexOptions.IgnoreCase);
                SelectedPassage[] chosen;
                PassageSelectionReason reason;
                if (passages.Sum(passage => passage.Text.Length) <= limit && passages.Length <= most)
                {
                    (chosen, reason) = ([.. passages.Select(passage => new SelectedPassage(passage, 0))], PassageSelectionReason.WholeDocument);
                }
                else
                {
                    var count = Math.Min(Math.Min(limit / 1000, most), passages.Length);
                    IEnumerable<int> indexes = overview
                        ? Enumerable.Range(0, count).Select(step => count == 1 ? 0 : (int)Math.Round(step * (passages.Length - 1) / (double)(count - 1))).Distinct()
                        : Enumerable.Range(0, passages.Length).OrderByDescending(index => index % 3 == 0 ? 2 : 1).ThenBy(index => index).Take(count).Order();
                    chosen = [.. indexes.Select(index => new SelectedPassage(passages[index], overview ? 0 : (index % 3 == 0 ? 2 : 1)))];
                    reason = overview ? PassageSelectionReason.NoQueryTerms : PassageSelectionReason.Matched;
                }

                var selection = new PassageSelection { Passages = chosen, TotalPassages = passages.Length, Reason = reason, QueryTermCount = overview ? 0 : 2 };
                Reads.Enqueue((filePath, options, selection.CharacterCount));
                return new DocumentContextResult { Status = DocumentReadStatus.Success, Text = selection.ToText(), Selection = selection };
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    // A model that answers every request at once with a note named by the piece it was given (or "- Combined." for notes), counts
    // how many requests run at once, and can wait to be stopped or fail a given request.
    private sealed class NoteModel : IModelService
    {
        private int _running;
        private int _most;
        private int _count;

        public ModelInfo? Active { get; init; } = new("test-model", 8192);

        public int ContextLength { init => Active = new("test-model", value); }

        public TimeSpan Delay { get; init; }

        public int NoteLength { get; init; }

        public bool WaitForCancel { get; init; }

        public int FailOn { get; init; }

        public bool CutShort { get; init; }

        private readonly ConcurrentQueue<ModelRequest> _requests = new();

        public IReadOnlyList<ModelRequest> Requests => [.. _requests];

        public bool Cancelled { get; private set; }

        public int MostAtOnce => _most;

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) => Task.FromResult(Active);

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _requests.Enqueue(request);
            var number = Interlocked.Increment(ref _count);
            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref _most, running);
            try
            {
                if (WaitForCancel)
                {
                    using var registration = cancellationToken.Register(() => Cancelled = true);
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken);
                }

                if (number == FailOn)
                {
                    throw new ModelHostException(ModelHostErrorCode.GenerationFailed);
                }

                var combining = request.Instructions.StartsWith(AssistantInstructions.NoteCombining, StringComparison.Ordinal);
                var note = combining ? "- Combined." : $"- A note on {request.Label()}.";
                if (!combining && NoteLength > note.Length)
                {
                    note = note + " " + new string('z', NoteLength - note.Length - 1);
                }

                yield return AssistantResponseChunk.ForTextDelta(note[..(note.Length / 2)]);
                yield return AssistantResponseChunk.ForTextDelta(note[(note.Length / 2)..]);
                if (CutShort && !combining)
                {
                    yield return AssistantResponseChunk.ForTextDelta("\n- And then the limit cut thr");
                    yield return AssistantResponseChunk.ForNotice(LocalModelService.OutputLimitNotice);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}

/// <summary>Reads what a request is about in a test.</summary>
internal static partial class MultiFileRequestExtensions
{
    /// <summary>The name of the first piece of context in the request's last message.</summary>
    public static string Label(this ModelRequest request)
    {
        var match = NameAttribute().Match(request.Messages[^1].Text);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    [GeneratedRegex("<untrusted_context [^>]*name=\"([^\"]*)\"")]
    private static partial Regex NameAttribute();
}
