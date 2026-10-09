using System.Globalization;
using System.Text;
using Assistant.Core.Documents;
using Assistant.Core.Domain;

namespace Assistant.Core.MultiFile;

/// <summary>How the files of a question were given to the model.</summary>
public enum MultiFileStrategy
{
    /// <summary>
    /// What the question needs of the files fits one prompt, so their passages go to the model with the question, as for one
    /// attached file (<see cref="ContextItemType.File"/>). Also what happens when no file could be read.
    /// </summary>
    Direct,

    /// <summary>
    /// It does not fit: each file's passages were cut into parts, the model took notes on each part on its own (map), notes were
    /// combined when there were too many (reduce), and the answer is put together from the notes
    /// (<see cref="ContextItemType.FileNotes"/>).
    /// </summary>
    MapReduce,
}

/// <summary>What became of one file of a question.</summary>
/// <param name="File">The file.</param>
/// <param name="Status">How reading it ended; only <see cref="DocumentReadStatus.Success"/> gave the model anything.</param>
public sealed record FileOutcome(QuestionFile File, DocumentReadStatus Status)
{
    /// <summary>The number of passages (parts, for the user) the file has.</summary>
    public int TotalPassages { get; init; }

    /// <summary>The number of its passages the model read, directly or in the parts it took notes on.</summary>
    public int PassagesRead { get; init; }

    /// <summary>Why the passages read are the ones that were (the question's words, spread over the file, its start).</summary>
    public PassageSelectionReason Reason { get; init; }

    /// <summary>Whether the file has text that was never read at all: a reader's limit was reached, or a page could not be parsed.</summary>
    public bool Truncated { get; init; }

    /// <summary>How many parts notes were taken on; zero when its passages went to the model directly.</summary>
    public int Parts { get; init; }

    /// <summary>Whether the file gave the model anything.</summary>
    public bool IsRead => Status == DocumentReadStatus.Success;

    /// <summary>Whether all of the file's passages were read.</summary>
    public bool IsComplete => IsRead && PassagesRead >= TotalPassages && !Truncated;

    // The file is the user's own (PROJECT_SPEC §3.2): ToString, and so a log, shows how it ended and counts.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Status = ").Append(Status)
            .Append(", PassagesRead = ").Append(PassagesRead.ToString(CultureInfo.InvariantCulture))
            .Append(", TotalPassages = ").Append(TotalPassages.ToString(CultureInfo.InvariantCulture))
            .Append(", Parts = ").Append(Parts.ToString(CultureInfo.InvariantCulture));
        return true;
    }
}

/// <summary>
/// What a question about several files (or one long one) came to (PROJECT_SPEC §5.5, several files): the context to ask the
/// model with, what became of each file, and what to tell the user about how they were read.
/// </summary>
public sealed record MultiFileResult
{
    /// <summary>How the files were given to the model.</summary>
    public MultiFileStrategy Strategy { get; init; }

    /// <summary>
    /// The context to ask with, in the order of the files: their passages (<see cref="ContextItemType.File"/>) or the notes on them
    /// (<see cref="ContextItemType.FileNotes"/>), each from what the user selected. Empty when no file could be read.
    /// </summary>
    public IReadOnlyList<ContextItem> Items { get; init; } = [];

    /// <summary>What became of each file that was looked at, in the order given.</summary>
    public IReadOnlyList<FileOutcome> Files { get; init; } = [];

    /// <summary>The files after the most a question reads, which were not looked at.</summary>
    public IReadOnlyList<QuestionFile> LeftOut { get; init; } = [];

    /// <summary>
    /// What to tell the user about how the files were used, in the order to say it (PROJECT_SPEC §4.4: no silent truncation): which
    /// were left out over the limit, that they were read in pieces and the answer put together from notes, and which were not read
    /// whole. Only file names appear in it. The files that could not be read at all are for the caller to name, by
    /// <see cref="FileOutcome.Status"/>.
    /// </summary>
    public IReadOnlyList<string> Notices { get; init; } = [];

    /// <summary>How many requests the model was asked before the answer: notes taken and notes combined.</summary>
    public int ModelCalls { get; init; }

    /// <summary>Whether any file was read.</summary>
    public bool HasContext => Items.Count > 0;

    // The context is the files' own text (PROJECT_SPEC §3.2): ToString, and so a log, shows how and how much.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Strategy = ").Append(Strategy)
            .Append(", Items = ").Append(Items.Count.ToString(CultureInfo.InvariantCulture))
            .Append(", Files = ").Append(Files.Count.ToString(CultureInfo.InvariantCulture))
            .Append(", LeftOut = ").Append(LeftOut.Count.ToString(CultureInfo.InvariantCulture))
            .Append(", ModelCalls = ").Append(ModelCalls.ToString(CultureInfo.InvariantCulture));
        return true;
    }
}
