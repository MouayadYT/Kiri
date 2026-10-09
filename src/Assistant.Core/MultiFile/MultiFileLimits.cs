namespace Assistant.Core.MultiFile;

/// <summary>
/// The limits a question about several files (or one long one) is worked out within (PROJECT_SPEC §5.5, several files): how many
/// files, how many are read at once, how many model calls run at once and in all, how much of the files' text is held in memory,
/// and how long the notes are. A value of zero or less is the default.
/// </summary>
public sealed record MultiFileLimits
{
    /// <summary>The default for <see cref="MaxFiles"/> (PROJECT_SPEC §4.4: at most ten files per invocation).</summary>
    public const int DefaultMaxFiles = 10;

    /// <summary>The default for <see cref="MaxConcurrentReads"/>.</summary>
    public const int DefaultMaxConcurrentReads = 2;

    /// <summary>The default for <see cref="MaxConcurrentModelCalls"/>: the local engine answers one request at a time.</summary>
    public const int DefaultMaxConcurrentModelCalls = 1;

    /// <summary>The default for <see cref="MaxMapCalls"/>.</summary>
    public const int DefaultMaxMapCalls = 16;

    /// <summary>The default for <see cref="MaxCombineCalls"/>.</summary>
    public const int DefaultMaxCombineCalls = 4;

    /// <summary>The default for <see cref="MaxHeldCharacters"/>: about 800 KB of text.</summary>
    public const int DefaultMaxHeldCharacters = 400_000;

    /// <summary>The default for <see cref="MaxPartCharacters"/>.</summary>
    public const int DefaultMaxPartCharacters = 12_000;

    /// <summary>The default for <see cref="MinPartCharacters"/>.</summary>
    public const int DefaultMinPartCharacters = 1_500;

    /// <summary>The default for <see cref="MinNoteTokens"/>.</summary>
    public const int DefaultMinNoteTokens = 200;

    /// <summary>The default for <see cref="MaxNoteTokens"/>.</summary>
    public const int DefaultMaxNoteTokens = 400;

    /// <summary>The default for <see cref="CombinedNoteTokens"/>.</summary>
    public const int DefaultCombinedNoteTokens = 500;

    /// <summary>The default for <see cref="MaxNoteCharacters"/>.</summary>
    public const int DefaultMaxNoteCharacters = 3_000;

    /// <summary>The default for <see cref="NotesShareOfPrompt"/>.</summary>
    public const double DefaultNotesShareOfPrompt = 0.6;

    // Upper bounds, whatever is asked: reading many files at once only competes for the disk, and the engine has few slots.
    private const int MostConcurrentReads = 8;
    private const int MostConcurrentModelCalls = 4;

    /// <summary>The limits that apply when none are given.</summary>
    public static MultiFileLimits Default { get; } = new();

    /// <summary>
    /// Whether notes may be taken when the files do not fit one prompt. Without notes the files are always read directly, each to an
    /// equal share of one prompt, as one attached file always was.
    /// </summary>
    public bool TakeNotes { get; init; } = true;

    /// <summary>The most files one question reads; the files after them are left out and named.</summary>
    public int MaxFiles { get; init; } = DefaultMaxFiles;

    /// <summary>How many files are read (opened, extracted, cut into passages) at the same time.</summary>
    public int MaxConcurrentReads { get; init; } = DefaultMaxConcurrentReads;

    /// <summary>How many requests to the model run at the same time while notes are taken.</summary>
    public int MaxConcurrentModelCalls { get; init; } = DefaultMaxConcurrentModelCalls;

    /// <summary>
    /// The most parts notes are taken on for one question, each one request to the model: the files share them, a short file whole
    /// and the long ones the rest, so a question about many long files still ends in a bounded time.
    /// </summary>
    public int MaxMapCalls { get; init; } = DefaultMaxMapCalls;

    /// <summary>The most requests that combine notes into fewer, when there are too many to answer from at once.</summary>
    public int MaxCombineCalls { get; init; } = DefaultMaxCombineCalls;

    /// <summary>
    /// The most characters of the files' text held in memory once they are read, shared equally by the files: each file keeps no
    /// more of its passages than its share, and a part's text is let go as soon as its notes are taken.
    /// </summary>
    public int MaxHeldCharacters { get; init; } = DefaultMaxHeldCharacters;

    /// <summary>The longest part notes are taken on in one request; the model's window may make parts shorter.</summary>
    public int MaxPartCharacters { get; init; } = DefaultMaxPartCharacters;

    /// <summary>The shortest a part is made, however small the model's window.</summary>
    public int MinPartCharacters { get; init; } = DefaultMinPartCharacters;

    /// <summary>The fewest tokens the notes on one part may take.</summary>
    public int MinNoteTokens { get; init; } = DefaultMinNoteTokens;

    /// <summary>
    /// The most tokens the notes on one part may take. Fewer parts may each write more, up to this, as long as all the notes fit
    /// what the answer can read.
    /// </summary>
    public int MaxNoteTokens { get; init; } = DefaultMaxNoteTokens;

    /// <summary>The most tokens notes combined from several may take.</summary>
    public int CombinedNoteTokens { get; init; } = DefaultCombinedNoteTokens;

    /// <summary>The most characters of notes kept from one request, in case the model writes on past what it was asked.</summary>
    public int MaxNoteCharacters { get; init; } = DefaultMaxNoteCharacters;

    /// <summary>
    /// The share of the answer's prompt the notes may take; the instructions, the question and the earlier turns need the rest.
    /// </summary>
    public double NotesShareOfPrompt { get; init; } = DefaultNotesShareOfPrompt;

    /// <summary>The limits with every value in its range.</summary>
    public MultiFileLimits Resolve()
    {
        var minPart = MinPartCharacters > 0 ? MinPartCharacters : DefaultMinPartCharacters;
        var minNote = MinNoteTokens > 0 ? MinNoteTokens : DefaultMinNoteTokens;
        return new MultiFileLimits
        {
            TakeNotes = TakeNotes,
            MaxFiles = MaxFiles > 0 ? MaxFiles : DefaultMaxFiles,
            MaxConcurrentReads = Math.Min(MaxConcurrentReads > 0 ? MaxConcurrentReads : DefaultMaxConcurrentReads, MostConcurrentReads),
            MaxConcurrentModelCalls = Math.Min(
                MaxConcurrentModelCalls > 0 ? MaxConcurrentModelCalls : DefaultMaxConcurrentModelCalls, MostConcurrentModelCalls),
            MaxMapCalls = MaxMapCalls > 0 ? MaxMapCalls : DefaultMaxMapCalls,
            MaxCombineCalls = MaxCombineCalls > 0 ? MaxCombineCalls : DefaultMaxCombineCalls,
            MaxHeldCharacters = Math.Max(MaxHeldCharacters > 0 ? MaxHeldCharacters : DefaultMaxHeldCharacters, minPart),
            MaxPartCharacters = Math.Max(MaxPartCharacters > 0 ? MaxPartCharacters : DefaultMaxPartCharacters, minPart),
            MinPartCharacters = minPart,
            MinNoteTokens = minNote,
            MaxNoteTokens = Math.Max(MaxNoteTokens > 0 ? MaxNoteTokens : DefaultMaxNoteTokens, minNote),
            CombinedNoteTokens = CombinedNoteTokens > 0 ? CombinedNoteTokens : DefaultCombinedNoteTokens,
            MaxNoteCharacters = MaxNoteCharacters > 0 ? MaxNoteCharacters : DefaultMaxNoteCharacters,
            NotesShareOfPrompt = NotesShareOfPrompt is > 0 and <= 1 ? NotesShareOfPrompt : DefaultNotesShareOfPrompt,
        };
    }
}
