using System.Diagnostics;
using System.Globalization;
using System.Text;
using Assistant.Core.Budgeting;
using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Core.Domain;
using Assistant.Core.ModelHosting;
using Assistant.Core.Orchestration;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Core.MultiFile;

/// <summary>
/// The app's <see cref="IMultiFileProcessor"/> (PROJECT_SPEC §5.5, several files): reads the question's files a few at a time
/// through the <see cref="IDocumentContextService"/>, and gives the model their passages directly when what the question needs of
/// them fits one prompt, or else the notes the model takes on each piece of them (map, <see cref="MapPlanner"/>), combined while
/// there are too many to read at once (reduce). Every prompt it sends is laid out by the <see cref="PromptBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// One file is read as one attached file always was: the passages its question needs, sized for one prompt
/// (<see cref="DocumentContextBudget"/>). Only when that question is about the whole file and the file does not fit is it read again
/// whole, as far as the limits allow, for notes on every piece of it. Several files are read once each, every file keeping no more of
/// its passages than its share of <see cref="MultiFileLimits.MaxHeldCharacters"/>; they go to the model directly when their passages
/// together fit what one file may take, and are taken notes on otherwise.
/// </para>
/// <para>
/// Limits (<see cref="MultiFileLimits"/>): at most <see cref="MultiFileLimits.MaxFiles"/> files (and the user's
/// <see cref="ContextLimitSettings.MaxAttachedFiles"/>); <see cref="MultiFileLimits.MaxConcurrentReads"/> files read at once and
/// <see cref="MultiFileLimits.MaxConcurrentModelCalls"/> requests to the model at once; <see cref="MultiFileLimits.MaxMapCalls"/>
/// pieces and <see cref="MultiFileLimits.MaxCombineCalls"/> combining requests in all; the text of a piece is let go once its notes
/// are taken, and only the notes, at most <see cref="MultiFileLimits.MaxNoteCharacters"/> each, are kept. A piece is as long as the
/// model's window allows next to the instructions, the question and room for its notes, within
/// <see cref="MultiFileLimits.MinPartCharacters"/> and <see cref="MultiFileLimits.MaxPartCharacters"/>, and the notes on all the pieces
/// are sized to fit <see cref="MultiFileLimits.NotesShareOfPrompt"/> of the answer's prompt together.
/// </para>
/// <para>
/// While it works the Searching chip says "Reading", then "Reading 3 of 9" as the notes on each piece are taken (never a name), and
/// stopping it there stops the work. Without a model to take notes (none is set up, none was given, or
/// <see cref="MultiFileLimits.TakeNotes"/> is off) the files are read directly, each to an equal share of one prompt. It logs counts, the strategy and the time; never a path, a name, the question, any text or
/// the notes.
/// </para>
/// </remarks>
public sealed partial class MultiFileProcessor : IMultiFileProcessor
{
    /// <summary>What the Searching chip says while the files are read.</summary>
    public const string ReadingText = "Reading";

    /// <summary>What the Searching chip says while notes are combined.</summary>
    public const string CombiningText = "Thinking";

    /// <summary>How freely the model words its notes: low, since notes should say what the text says.</summary>
    public const double NoteTemperature = 0.2;

    // The share of what a piece's prompt leaves that the piece's passages may take, at three characters to a token as for one file's
    // passages: the estimate is rough, and the budgeter cuts what is still too long.
    private const double CharactersPerToken = 3;
    private const double Headroom = 0.85;

    // The most passages a file's selection may have; its characters are what really bound it.
    private const int SelectionPassageLimit = 1_000;

    // The fewest characters each file is read to when there is no model to take notes and the files share one prompt.
    private const int MinShareCharacters = 1_000;

    // How long a piece's name is counted as when its prompt is measured; a longer name only makes the piece a little shorter.
    private const int ProbeLabelLength = 100;

    private readonly IDocumentContextService _documents;
    private readonly IModelService? _models;
    private readonly ISettingsService? _settings;
    private readonly PromptBuilder _prompts;
    private readonly ITokenEstimator _estimator;
    private readonly TimeProvider _clock;
    private readonly IActivityTracker? _tracker;
    private readonly ILogger _logger;
    private readonly MultiFileLimits _limits;

    /// <summary>Creates the processor.</summary>
    /// <param name="documents">Reads a file and chooses its passages for a question.</param>
    /// <param name="models">
    /// The local model that takes the notes, or <see langword="null"/> for none: the files are then always read directly. It is best
    /// not reported to the activity tracker itself, since the processor says how far it has got.
    /// </param>
    /// <param name="settings">
    /// The user's settings, for their context limits and the largest file read, or <see langword="null"/> for the default limits.
    /// </param>
    /// <param name="prompts">Lays out the prompts that take and combine notes, or <see langword="null"/> for one of its own.</param>
    /// <param name="estimator">Estimates tokens, or <see langword="null"/> for the <see cref="HeuristicTokenEstimator"/>.</param>
    /// <param name="clock">The time, or <see langword="null"/> for the system's.</param>
    /// <param name="tracker">Shows the Searching chip while it works and lets the user stop it, or <see langword="null"/> for none.</param>
    /// <param name="logger">Logs counts, or <see langword="null"/> for none.</param>
    /// <param name="limits">The limits, or <see langword="null"/> for <see cref="MultiFileLimits.Default"/>.</param>
    public MultiFileProcessor(
        IDocumentContextService documents,
        IModelService? models,
        ISettingsService? settings,
        PromptBuilder? prompts = null,
        ITokenEstimator? estimator = null,
        TimeProvider? clock = null,
        IActivityTracker? tracker = null,
        ILogger<MultiFileProcessor>? logger = null,
        MultiFileLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents;
        _models = models;
        _settings = settings;
        _estimator = estimator ?? new HeuristicTokenEstimator();
        _prompts = prompts ?? new PromptBuilder(new ContextBudgeter(_estimator));
        _clock = clock ?? TimeProvider.System;
        _tracker = tracker;
        _logger = logger ?? NullLogger<MultiFileProcessor>.Instance;
        _limits = (limits ?? MultiFileLimits.Default).Resolve();
    }

    /// <summary>The limits it works within, each in its range.</summary>
    public MultiFileLimits Limits => _limits;

    /// <summary>What the Searching chip says while the notes on a piece are taken: how far it has got, never a name.</summary>
    public static string ReadingPieceText(int piece, int pieces) =>
        string.Create(CultureInfo.InvariantCulture, $"{ReadingText} {piece} of {pieces}");

    /// <inheritdoc/>
    public async Task<MultiFileResult> ProcessAsync(
        IReadOnlyList<QuestionFile> files, string question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(question);
        var start = Stopwatch.GetTimestamp();
        var settings = _settings is null
            ? new ContextLimitSettings()
            : (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).ContextLimits;

        // Each file once, in the order given, and no more than a question reads.
        // The user's own number counts, up or down; the processor's is only what a question reads when the settings do not say.
        var most = settings.MaxAttachedFiles > 0 ? settings.MaxAttachedFiles : _limits.MaxFiles;
        var distinct = new List<QuestionFile>(files.Count);
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (!distinct.Any(known => known.IsSameFile(file)))
            {
                distinct.Add(file);
            }
        }

        QuestionFile[] used = [.. distinct.Take(most)];
        QuestionFile[] leftOut = [.. distinct.Skip(most)];
        if (used.Length == 0)
        {
            return new MultiFileResult { LeftOut = leftOut };
        }

        var model = await ModelAsync(cancellationToken).ConfigureAwait(false);
        var takesNotes = _limits.TakeNotes && _models is not null && model is not null;
        using var activity = _tracker?.Begin(Domain.ActivityKind.FileSearch, ReadingText, cancellationToken);
        var token = activity?.CancellationToken ?? cancellationToken;
        var direct = DocumentContextBudget.For(model, settings);

        MultiFileResult result;
        if (used.Length == 1)
        {
            result = await OneFileAsync(used[0], question, model, takesNotes, settings, direct, activity, token).ConfigureAwait(false);
        }
        else if (!takesNotes)
        {
            // Nothing can take notes, so the files share one prompt.
            var each = Math.Max(direct.Selection!.MaxCharacters / used.Length, MinShareCharacters);
            var share = direct with
            {
                Selection = new PassageSelectionOptions
                {
                    MaxCharacters = each,
                    MaxPassages = Math.Max(DocumentContextBudget.MaxPassages / used.Length, 1),
                },
            };
            result = Direct(await ReadAllAsync(used, question, share, token).ConfigureAwait(false));
        }
        else
        {
            var sizing = SizeFor(model!, settings, question);
            var reads = await ReadAllAsync(used, question, MapOptions(settings, sizing, used.Length), token).ConfigureAwait(false);
            var characters = reads.Where(read => read.Read.Status == DocumentReadStatus.Success)
                .Sum(read => (long)read.Read.Selection.CharacterCount);
            var anyRead = reads.Any(read => read.Read.Status == DocumentReadStatus.Success);
            result = !anyRead || characters <= direct.Selection!.MaxCharacters
                ? Direct(reads)
                : await MapReduceAsync(reads, used, question, model!, settings, sizing, activity, token).ConfigureAwait(false);
        }

        // The files over the limit are named first, before how the others were read.
        result = result with
        {
            LeftOut = leftOut,
            Notices = leftOut.Length > 0 ? [MultiFileNotices.LeftOut(leftOut, most), .. result.Notices] : result.Notices,
        };
        LogProcessed(
            _logger,
            used.Length,
            result.Files.Count(file => file.IsRead),
            leftOut.Length,
            result.Strategy,
            result.Files.Sum(file => file.Parts),
            result.ModelCalls,
            result.Items.Sum(item => (long)(item.Text?.Length ?? 0)),
            ElapsedMs(start));
        return result;
    }

    // One file: the passages its question needs, as for an attached file; a question about the whole of a file that does not fit
    // reads it again, whole as far as the limits go, for notes on every piece.
    private async Task<MultiFileResult> OneFileAsync(
        QuestionFile file,
        string question,
        ModelInfo? model,
        bool takesNotes,
        ContextLimitSettings settings,
        DocumentContextOptions direct,
        IActivityScope? activity,
        CancellationToken token)
    {
        var read = await _documents.GetContextAsync(file.Path, question, direct, token).ConfigureAwait(false);
        if (!takesNotes
            || read.Status != DocumentReadStatus.Success
            || read.Selection.IsComplete
            || read.Selection.Reason != PassageSelectionReason.NoQueryTerms)
        {
            return Direct([(file, read)]);
        }

        var sizing = SizeFor(model!, settings, question);
        var whole = await ReadAllAsync([file], question, MapOptions(settings, sizing, 1), token).ConfigureAwait(false);

        // A file that went away between the two reads is answered from what the first read.
        return whole[0].Read.Status == DocumentReadStatus.Success
            ? await MapReduceAsync(whole, [file], question, model!, settings, sizing, activity, token).ConfigureAwait(false)
            : Direct([(file, read)]);
    }

    // Reads the files, a few at a time, each into the passages the question needs. Only the passages are kept: the text laid out from
    // them is made again when it is needed, so a file's text is held once.
    private async Task<(QuestionFile File, DocumentContextResult Read)[]> ReadAllAsync(
        IReadOnlyList<QuestionFile> files, string question, DocumentContextOptions options, CancellationToken token)
    {
        var reads = new (QuestionFile File, DocumentContextResult Read)[files.Count];
        await Parallel.ForEachAsync(
                Enumerable.Range(0, files.Count),
                new ParallelOptions { MaxDegreeOfParallelism = _limits.MaxConcurrentReads, CancellationToken = token },
                async (index, cancel) =>
                {
                    var read = await _documents.GetContextAsync(files[index].Path, question, options, cancel).ConfigureAwait(false);
                    reads[index] = (files[index], read.Selection.Passages.Count > 0 ? read with { Text = string.Empty } : read);
                })
            .ConfigureAwait(false);
        return reads;
    }

    // The files' passages go to the model as they are, one item for each file that was read.
    private static MultiFileResult Direct(IReadOnlyList<(QuestionFile File, DocumentContextResult Read)> reads)
    {
        var items = new List<ContextItem>(reads.Count);
        var outcomes = new List<FileOutcome>(reads.Count);
        var notices = new List<string>();
        foreach (var (file, read) in reads)
        {
            if (read.Status != DocumentReadStatus.Success)
            {
                outcomes.Add(new FileOutcome(file, read.Status));
                continue;
            }

            items.Add(new ContextItem(Guid.NewGuid(), ContextItemType.File, file.Name)
            {
                FilePath = file.Path,
                Text = read.Text.Length > 0 ? read.Text : read.Selection.ToText(),
                Source = ContextSource.UserSelected,
            });
            notices.AddRange(DocumentContextNotices.For(file.Name, read));
            outcomes.Add(Outcome(file, read, read.Selection.Passages.Count, 0));
        }

        return new MultiFileResult { Strategy = MultiFileStrategy.Direct, Items = items, Files = outcomes, Notices = notices };
    }

    // Notes on every piece, combined while there are too many, and the answer's context is the notes.
    private async Task<MultiFileResult> MapReduceAsync(
        (QuestionFile File, DocumentContextResult Read)[] reads,
        IReadOnlyList<QuestionFile> files,
        string question,
        ModelInfo model,
        ContextLimitSettings settings,
        Sizing sizing,
        IActivityScope? activity,
        CancellationToken token)
    {
        var (parts, outcomes) = Plan(reads, sizing);

        // The files' passages are let go: only the pieces notes are taken on are held, each until its notes are taken.
        Array.Clear(reads);

        var notesBudget = NotesBudget(model, settings);
        var noteTokens = Math.Clamp(
            (notesBudget / Math.Max(parts.Length, 1)) - ContextBudgeter.ContextBlockOverheadTokens, _limits.MinNoteTokens, _limits.MaxNoteTokens);
        var notes = await TakeNotesAsync(parts, question, model, sizing.NoteLimits, noteTokens, activity, token).ConfigureAwait(false);
        var (kept, combineCalls) = await CombineAsync(FileNotes.Collapse(notes, files), files, question, model, settings, notesBudget, activity, token)
            .ConfigureAwait(false);

        var items = kept.Select(note => new ContextItem(Guid.NewGuid(), ContextItemType.FileNotes, note.Label)
        {
            FilePath = note.Path,
            Text = note.Text,
            Source = ContextSource.UserSelected,
        }).ToArray();
        return new MultiFileResult
        {
            Strategy = MultiFileStrategy.MapReduce,
            Items = items,
            Files = outcomes,
            Notices = MultiFileNotices.ForNotes(outcomes),
            ModelCalls = notes.Length + combineCalls,
        };
    }

    // The pieces to take notes on, and what became of each file, by its place among the files.
    private (MapPart?[] Parts, FileOutcome[] Outcomes) Plan((QuestionFile File, DocumentContextResult Read)[] reads, Sizing sizing)
    {
        var selections = new List<FileSelection>(reads.Length);
        for (var index = 0; index < reads.Length; index++)
        {
            if (reads[index].Read.Status == DocumentReadStatus.Success)
            {
                selections.Add(new FileSelection(index, reads[index].File, reads[index].Read.Selection));
            }
        }

        var plan = MapPlanner.Plan(selections, sizing.PartCharacters, _limits.MaxMapCalls);
        // The plan counts the files it was given, which are the ones that were read, in order.
        var outcomes = new FileOutcome[reads.Length];
        var position = 0;
        for (var index = 0; index < reads.Length; index++)
        {
            var (file, read) = reads[index];
            if (read.Status != DocumentReadStatus.Success)
            {
                outcomes[index] = new FileOutcome(file, read.Status);
                continue;
            }

            outcomes[index] = Outcome(file, read, plan.PassagesRead[position], plan.PartsOf[position]);
            position++;
        }

        return ([.. plan.Parts], outcomes);
    }

    // Notes on each piece, as many requests at once as the limit allows, in the order of the pieces.
    private async Task<FileNote[]> TakeNotesAsync(
        MapPart?[] parts,
        string question,
        ModelInfo model,
        ContextLimitSettings noteLimits,
        int noteTokens,
        IActivityScope? activity,
        CancellationToken token)
    {
        var notes = new FileNote[parts.Length];
        var started = 0;
        await Parallel.ForEachAsync(
                Enumerable.Range(0, parts.Length),
                new ParallelOptions { MaxDegreeOfParallelism = _limits.MaxConcurrentModelCalls, CancellationToken = token },
                async (index, cancel) =>
                {
                    // The piece's text is let go as soon as its notes are taken.
                    var part = parts[index]!;
                    parts[index] = null;
                    activity?.Update(ReadingPieceText(Interlocked.Increment(ref started), parts.Length));
                    notes[index] = await TakeNoteAsync(part, question, model, noteLimits, noteTokens, cancel).ConfigureAwait(false);
                })
            .ConfigureAwait(false);
        return notes;
    }

    private async Task<FileNote> TakeNoteAsync(
        MapPart part, string question, ModelInfo model, ContextLimitSettings noteLimits, int noteTokens, CancellationToken token)
    {
        var start = Stopwatch.GetTimestamp();
        var text = part.Passages.ToText();
        var item = new ContextItem(Guid.NewGuid(), ContextItemType.File, part.Label)
        {
            FilePath = part.File.Path,
            Text = text,
            Source = ContextSource.UserSelected,
        };
        var request = BuildRequest(
            AssistantInstructions.NoteTaking, AssistantInstructions.NoteRequest(question, part.Label), [item], model, noteLimits, noteTokens);
        var notes = await GenerateAsync(request, token).ConfigureAwait(false);
        LogNoteTaken(_logger, text.Length, notes.Length, ElapsedMs(start));
        return new FileNote([part.FileIndex], part.Label, notes, part.File.Path);
    }

    // While the notes cost more than the answer can read, notes that follow each other are combined into one, as many in a request
    // as fit, for as many requests as the limit allows; what still does not fit is cut by the answer's budget, and the user is told.
    private async Task<(IReadOnlyList<FileNote> Notes, int Calls)> CombineAsync(
        IReadOnlyList<FileNote> notes,
        IReadOnlyList<QuestionFile> files,
        string question,
        ModelInfo model,
        ContextLimitSettings settings,
        int notesBudget,
        IActivityScope? activity,
        CancellationToken token)
    {
        var calls = 0;
        var combineLimits = settings with { ReservedOutputTokens = _limits.CombinedNoteTokens };
        var capacity = Capacity(
            model, combineLimits, AssistantInstructions.NoteCombining, AssistantInstructions.CombineRequest(question), ContextItemType.FileNotes);
        while (calls < _limits.MaxCombineCalls && notes.Sum(Cost) > notesBudget)
        {
            var groups = FileNotes.Group(notes, Cost, capacity);
            if (groups.Count >= notes.Count)
            {
                // No two notes fit one request together.
                break;
            }

            activity?.Update(CombiningText);
            var next = new List<FileNote>(groups.Count);
            foreach (var group in groups)
            {
                if (group.Count == 1 || calls >= _limits.MaxCombineCalls)
                {
                    next.AddRange(group);
                    continue;
                }

                next.Add(await CombineGroupAsync(group, files, question, model, combineLimits, token).ConfigureAwait(false));
                calls++;
            }

            notes = next;
        }

        return (notes, calls);
    }

    private async Task<FileNote> CombineGroupAsync(
        IReadOnlyList<FileNote> group,
        IReadOnlyList<QuestionFile> files,
        string question,
        ModelInfo model,
        ContextLimitSettings combineLimits,
        CancellationToken token)
    {
        var items = group.Select(note => new ContextItem(Guid.NewGuid(), ContextItemType.FileNotes, note.Label)
        {
            FilePath = note.Path,
            Text = note.Text,
            Source = ContextSource.UserSelected,
        }).ToArray();
        var request = BuildRequest(
            AssistantInstructions.NoteCombining, AssistantInstructions.CombineRequest(question), items, model, combineLimits, _limits.CombinedNoteTokens);
        var text = await GenerateAsync(request, token).ConfigureAwait(false);
        int[] indexes = [.. group.SelectMany(note => note.FileIndexes).Distinct()];
        LogNotesCombined(_logger, group.Count, text.Length);
        return new FileNote(indexes, FileNotes.CombinedLabel(group, files), text, indexes.Length == 1 ? group[0].Path : null);
    }

    // A request of one user message, its context and what is asked, laid out and fitted by the prompt builder, asking for no more
    // than the notes' tokens.
    private ModelRequest BuildRequest(
        string instructions, string ask, IReadOnlyList<ContextItem> items, ModelInfo model, ContextLimitSettings limits, int outputTokens)
    {
        var message = new Message(Guid.NewGuid(), MessageRole.User, ask, _clock.GetUtcNow()) { ContextItems = items };
        var built = _prompts.Build(instructions, [message], model, limits);
        return built.Request with
        {
            MaxOutputTokens = Math.Min(outputTokens, built.Request.MaxOutputTokens ?? outputTokens),
            Temperature = NoteTemperature,
        };
    }

    // The model's words, tidied; a model that writes on past what it was asked is stopped once there is more than enough, and notes
    // the length limit cut off lose the line it cut through.
    private async Task<string> GenerateAsync(ModelRequest request, CancellationToken token)
    {
        var builder = new StringBuilder();
        var cut = false;
        await foreach (var chunk in _models!.GenerateAsync(request, token).WithCancellation(token).ConfigureAwait(false))
        {
            if (chunk is { Type: AssistantResponseChunkType.TextDelta, Text.Length: > 0 })
            {
                builder.Append(chunk.Text);
                if (builder.Length > _limits.MaxNoteCharacters * 2)
                {
                    cut = true;
                    break;
                }
            }
            else if (chunk is { Type: AssistantResponseChunkType.Notice, Text: LocalModelService.OutputLimitNotice })
            {
                cut = true;
            }
        }

        var notes = FileNote.Clean(builder.ToString(), _limits.MaxNoteCharacters, cut);
        return FileNote.IsNothingText(notes) ? FileNote.NothingRelevant : notes;
    }

    // How long a piece is, and the limits its notes are taken under.
    private Sizing SizeFor(ModelInfo model, ContextLimitSettings settings, string question)
    {
        var noteLimits = settings with { ReservedOutputTokens = _limits.MaxNoteTokens };
        var tokens = Capacity(
            model,
            noteLimits,
            AssistantInstructions.NoteTaking,
            AssistantInstructions.NoteRequest(question, new string('x', ProbeLabelLength)),
            ContextItemType.File);
        var characters = (int)Math.Clamp(tokens * CharactersPerToken * Headroom, _limits.MinPartCharacters, _limits.MaxPartCharacters);
        return new Sizing(characters, noteLimits);
    }

    // The tokens a request's context may take: the prompt's share of the window, less the instructions, what is asked and the
    // fixed costs, measured on the prompt the builder lays out around a context of almost nothing.
    private int Capacity(ModelInfo model, ContextLimitSettings limits, string instructions, string ask, ContextItemType kind)
    {
        var budget = ContextBudget.Resolve(model, limits, ContextBudgetMode.Heavy);
        var probe = new ContextItem(Guid.Empty, kind, new string('x', ProbeLabelLength)) { Text = "x" };
        var built = _prompts.Build(instructions, [new Message(Guid.Empty, MessageRole.User, ask, _clock.GetUtcNow()) { ContextItems = [probe] }], model);
        var fixedTokens = _estimator.Estimate(built.Request.Instructions)
            + built.Request.Messages.Sum(message => _estimator.Estimate(message.Text))
            + ContextBudgeter.RequestOverheadTokens
            + (2 * ContextBudgeter.MessageOverheadTokens)
            + ContextBudgeter.ContextBlockOverheadTokens;
        return Math.Max(budget.PromptTokens - fixedTokens, 0);
    }

    // What the notes may take of the answer's prompt together.
    private int NotesBudget(ModelInfo model, ContextLimitSettings settings) =>
        (int)(ContextBudget.Resolve(model, settings, ContextBudgetMode.Heavy).PromptTokens * _limits.NotesShareOfPrompt);

    // What one note costs in the answer's prompt.
    private int Cost(FileNote note) =>
        _estimator.Estimate(note.Text) + _estimator.Estimate(note.Label) + ContextBudgeter.ContextBlockOverheadTokens;

    // Each file is read for as much as the pieces of a question can hold, and no more than its share of the text held at once.
    private DocumentContextOptions MapOptions(ContextLimitSettings settings, Sizing sizing, int fileCount)
    {
        var pieces = (long)_limits.MaxMapCalls * sizing.PartCharacters;
        var share = _limits.MaxHeldCharacters / Math.Max(fileCount, 1);
        return new DocumentContextOptions
        {
            Read = new DocumentReadOptions { MaxFileBytes = settings.MaxFileSizeBytes },
            Selection = new PassageSelectionOptions
            {
                MaxCharacters = (int)Math.Max(Math.Min(pieces, share), _limits.MinPartCharacters),
                MaxPassages = SelectionPassageLimit,
            },
        };
    }

    private static FileOutcome Outcome(QuestionFile file, DocumentContextResult read, int passagesRead, int parts) =>
        new(file, DocumentReadStatus.Success)
        {
            TotalPassages = read.Selection.TotalPassages,
            PassagesRead = passagesRead,
            Reason = read.Selection.Reason,
            Truncated = read.Truncated,
            Parts = parts,
        };

    // The model the work is sized for, when one is set up; whatever goes wrong asking is left to the answer to report.
    private async Task<ModelInfo?> ModelAsync(CancellationToken cancellationToken)
    {
        if (_models is null)
        {
            return null;
        }

        try
        {
            return await _models.GetActiveModelAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static long ElapsedMs(long start) => (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    // How long a piece is, and the limits (the room kept for the notes) its request is fitted under.
    private readonly record struct Sizing(int PartCharacters, ContextLimitSettings NoteLimits);

    [LoggerMessage(
        EventId = 2500,
        Level = LogLevel.Information,
        Message = "Files for a question: {Files} files ({Read} read, {LeftOut} left out over the limit), {Strategy}, {Parts} pieces, " +
            "{ModelCalls} model calls, {ContextCharacters} characters of context, in {ElapsedMs} ms")]
    private static partial void LogProcessed(
        ILogger logger,
        int files,
        int read,
        int leftOut,
        MultiFileStrategy strategy,
        int parts,
        int modelCalls,
        long contextCharacters,
        long elapsedMs);

    [LoggerMessage(
        EventId = 2501,
        Level = LogLevel.Debug,
        Message = "Notes taken on a piece: {TextCharacters} characters read, {NoteCharacters} characters of notes, in {ElapsedMs} ms")]
    private static partial void LogNoteTaken(ILogger logger, int textCharacters, int noteCharacters, long elapsedMs);

    [LoggerMessage(EventId = 2502, Level = LogLevel.Debug, Message = "Notes combined: {Notes} into {NoteCharacters} characters")]
    private static partial void LogNotesCombined(ILogger logger, int notes, int noteCharacters);
}
