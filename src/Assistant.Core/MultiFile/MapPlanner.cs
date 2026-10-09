using System.Globalization;
using System.Text;
using Assistant.Core.Documents;

namespace Assistant.Core.MultiFile;

/// <summary>The passages of one file chosen for a question, as the <see cref="MapPlanner"/> is given them.</summary>
/// <param name="FileIndex">Where the file is among the question's files, counted from 0.</param>
/// <param name="File">The file.</param>
/// <param name="Selection">Its passages that the question needs, in the order of the file.</param>
public sealed record FileSelection(int FileIndex, QuestionFile File, PassageSelection Selection);

/// <summary>One piece of a file that notes are taken on in one request to the model: passages that follow each other in it.</summary>
/// <param name="FileIndex">Where the file is among the question's files, counted from 0.</param>
/// <param name="File">The file.</param>
/// <param name="Number">Which of the file's pieces this is, counted from 1 in the order of the file.</param>
/// <param name="Count">How many pieces of the file notes are taken on.</param>
/// <param name="Passages">The piece's passages, laid out by <see cref="PassageSelection.ToText"/> with where each is.</param>
public sealed record MapPart(int FileIndex, QuestionFile File, int Number, int Count, PassageSelection Passages)
{
    /// <summary>The piece's name for the model and its notes: the file's name, and which piece when there are several.</summary>
    public string Label => Count == 1
        ? File.Name
        : string.Create(CultureInfo.InvariantCulture, $"{File.Name} (part {Number} of {Count})");

    // The passages are the file's own text (PROJECT_SPEC §3.2): ToString, and so a log, shows where and how many.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("FileIndex = ").Append(FileIndex.ToString(CultureInfo.InvariantCulture))
            .Append(", Number = ").Append(Number.ToString(CultureInfo.InvariantCulture))
            .Append(", Count = ").Append(Count.ToString(CultureInfo.InvariantCulture))
            .Append(", Passages = ").Append(Passages.Passages.Count.ToString(CultureInfo.InvariantCulture));
        return true;
    }
}

/// <summary>What notes are taken on for a question: the pieces, in the order of the files and of each file, and how much of each file they hold.</summary>
/// <param name="Parts">The pieces, one request to the model each.</param>
/// <param name="PassagesRead">For each file given, by its place in the list given, how many of its passages the pieces hold.</param>
/// <param name="PartsOf">For each file given, by its place in the list given, how many pieces it has.</param>
public sealed record MapPlan(IReadOnlyList<MapPart> Parts, IReadOnlyList<int> PassagesRead, IReadOnlyList<int> PartsOf);

/// <summary>
/// Decides what notes are taken on when files are too long to read at once (PROJECT_SPEC §5.5, several files). It is pure and
/// deterministic: the same selections and limits always give the same plan.
/// </summary>
/// <remarks>
/// Each file's chosen passages are gathered, in the order of the file, into pieces of at most the characters a request can hold. The
/// requests one question may make are shared by the files as water is: a file that needs fewer pieces than an equal share has all
/// of them, and the others share the rest equally (what is left over after an equal split goes to the first files that still want
/// more). A file that has more pieces than its share keeps those spread evenly over it, the first and the last among them, when the
/// question is about the whole file; the ones that match the question best when it has words to look for; and its first ones when
/// none of its passages holds them, which is never more than one piece. When the requests are fewer than the files, the first files
/// have one piece each and the others none.
/// </remarks>
public static class MapPlanner
{
    // The characters a passage's marker line takes in a piece's text ("[Passage 12: pages 7-9 (Budget > Q3)]"), counted generously.
    internal const int MarkerAllowance = 48;

    /// <summary>Plans the pieces of <paramref name="files"/> within <paramref name="partCharacters"/> each and <paramref name="maxParts"/> in all.</summary>
    /// <param name="files">The files' chosen passages, in the order of the question's files.</param>
    /// <param name="partCharacters">The most characters of passages one piece holds; a passage longer than that is a piece of its own.</param>
    /// <param name="maxParts">The most pieces in all.</param>
    public static MapPlan Plan(IReadOnlyList<FileSelection> files, int partCharacters, int maxParts)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentOutOfRangeException.ThrowIfLessThan(partCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParts, 1);

        var batches = files.Select(file => Pack(file.Selection, partCharacters)).ToArray();
        var wanted = files.Select((file, index) =>
            file.Selection.Reason == PassageSelectionReason.NoMatch ? Math.Min(batches[index].Count, 1) : batches[index].Count).ToArray();
        var given = Share(wanted, maxParts);

        var parts = new List<MapPart>();
        var passagesRead = new int[files.Count];
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var chosen = Choose(batches[index], given[index], file.Selection.Reason);
            for (var number = 0; number < chosen.Count; number++)
            {
                var batch = chosen[number];
                passagesRead[index] += batch.Count;
                parts.Add(new MapPart(
                    file.FileIndex,
                    file.File,
                    number + 1,
                    chosen.Count,
                    file.Selection with { Passages = batch }));
            }
        }

        return new MapPlan(parts, passagesRead, given);
    }

    /// <summary>
    /// The passages of <paramref name="selection"/> in runs of at most <paramref name="partCharacters"/>, in the order of the file.
    /// Passages that follow each other are laid out as one, their overlap once, so they cost their new text; one that starts a run of
    /// its own costs its marker line too; a passage longer than a piece is a piece of its own.
    /// </summary>
    public static List<List<SelectedPassage>> Pack(PassageSelection selection, int partCharacters)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var batches = new List<List<SelectedPassage>>();
        List<SelectedPassage>? current = null;
        var size = 0;
        foreach (var selected in selection.Passages)
        {
            var passage = selected.Passage;
            var follows = current is { Count: > 0 } && current[^1].Passage.Index == passage.Index - 1;
            var cost = follows
                ? passage.Text.Length - Math.Clamp(passage.OverlapLength, 0, passage.Text.Length)
                : passage.Text.Length + MarkerAllowance;
            if (current is null || (current.Count > 0 && size + cost > partCharacters))
            {
                current = [];
                batches.Add(current);
                size = 0;
                cost = passage.Text.Length + MarkerAllowance;
            }

            current.Add(selected);
            size += cost;
        }

        return batches;
    }

    /// <summary>
    /// Shares <paramref name="maxParts"/> requests among files that want <paramref name="wanted"/> each, as water fills: every file has
    /// what it wants up to one level, the highest that the requests reach, and what is left over goes, one each, to the first files
    /// that want more. When the requests are fewer than the files that want any, the first of those have one each.
    /// </summary>
    public static int[] Share(IReadOnlyList<int> wanted, int maxParts)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        var given = new int[wanted.Count];
        var asking = Enumerable.Range(0, wanted.Count).Where(index => wanted[index] > 0).ToArray();
        if (asking.Length == 0 || maxParts <= 0)
        {
            return given;
        }

        if (maxParts < asking.Length)
        {
            // Not even one each: the first files have one.
            foreach (var index in asking.Take(maxParts))
            {
                given[index] = 1;
            }

            return given;
        }

        // The highest level every file can be filled to.
        var level = 0;
        var most = asking.Max(index => wanted[index]);
        while (level < most && asking.Sum(index => Math.Min(wanted[index], level + 1)) <= maxParts)
        {
            level++;
        }

        var remaining = maxParts;
        foreach (var index in asking)
        {
            given[index] = Math.Min(wanted[index], level);
            remaining -= given[index];
        }

        foreach (var index in asking)
        {
            if (remaining > 0 && given[index] < wanted[index])
            {
                given[index]++;
                remaining--;
            }
        }

        return given;
    }

    /// <summary>
    /// Which <paramref name="count"/> of a file's pieces are kept when it may have only some, in the order of the file: spread from the
    /// first to the last for a question about the whole file, those whose passages match the question best for one with words to
    /// look for, and the first ones when none matched.
    /// </summary>
    public static List<List<SelectedPassage>> Choose(List<List<SelectedPassage>> batches, int count, PassageSelectionReason reason)
    {
        ArgumentNullException.ThrowIfNull(batches);
        if (count >= batches.Count)
        {
            return batches;
        }

        if (count <= 0)
        {
            return [];
        }

        IEnumerable<int> indexes = reason switch
        {
            // The pieces that hold the question's words most, each scored by its passages' scores together.
            PassageSelectionReason.Matched => Enumerable.Range(0, batches.Count)
                .OrderByDescending(index => batches[index].Sum(selected => selected.Score))
                .ThenBy(index => index)
                .Take(count),
            PassageSelectionReason.NoMatch => Enumerable.Range(0, count),
            _ => Spread(batches.Count, count),
        };
        return [.. indexes.Order().Select(index => batches[index])];
    }

    // Evenly spaced from the first to the last, each once.
    private static IEnumerable<int> Spread(int total, int count)
    {
        if (count == 1)
        {
            return [0];
        }

        return Enumerable.Range(0, count)
            .Select(step => (int)Math.Round(step * (total - 1) / (double)(count - 1), MidpointRounding.AwayFromZero))
            .Distinct();
    }
}
