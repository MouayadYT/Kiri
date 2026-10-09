using System.Globalization;
using Assistant.Core.Budgeting;
using Assistant.Core.Documents;

namespace Assistant.Core.MultiFile;

/// <summary>
/// The words that tell the user how the files of a question were read when they were too long to read at once (PROJECT_SPEC §4.4: no
/// silent truncation): that they were read in pieces and the answer is put together from notes, and which files were not read whole.
/// Only file names appear in them, never any of their text.
/// </summary>
public static class MultiFileNotices
{
    // How many names a sentence lists before it counts the rest.
    private const int NamesListed = 3;

    /// <summary>What to tell the user, in the order to say it, about files whose notes the answer is put together from.</summary>
    /// <param name="files">What became of each file; the ones that were not read are left to the caller to name.</param>
    public static IReadOnlyList<string> ForNotes(IReadOnlyList<FileOutcome> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var read = files.Where(file => file.IsRead).ToArray();
        if (read.Length == 0)
        {
            return [];
        }

        var notices = new List<string>(read.Length + 1)
        {
            read.Length == 1
                ? $"{Name(read[0].File)} is too long to read at once, so it was read in pieces, and this answer is put together from notes on each piece."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"These {read.Length} files are too long to read at once, so they were read in pieces, and this answer is put together from notes on each piece."),
        };

        foreach (var file in read)
        {
            var name = Name(file.File);
            if (file.Truncated)
            {
                notices.Add(DocumentContextNotices.Truncated(name));
            }

            if (file.PassagesRead < file.TotalPassages)
            {
                notices.Add(DocumentContextNotices.Partial(name, file.Reason, file.PassagesRead, file.TotalPassages));
            }
        }

        return notices;
    }

    /// <summary>Tells the user which files were left out because a question reads at most <paramref name="most"/>.</summary>
    /// <param name="leftOut">The files left out, in the order given.</param>
    /// <param name="most">The most files a question reads.</param>
    public static string LeftOut(IReadOnlyList<QuestionFile> leftOut, int most)
    {
        ArgumentNullException.ThrowIfNull(leftOut);
        var names = leftOut.Select(Name).ToArray();
        var list = names.Length switch
        {
            1 => names[0],
            <= NamesListed => $"{string.Join(", ", names.Take(names.Length - 1))} and {names[^1]}",
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"{string.Join(", ", names.Take(NamesListed))} and {names.Length - NamesListed} other {(names.Length - NamesListed == 1 ? "file" : "files")}"),
        };
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Only {most} files can be read for one question, so {list} {(names.Length == 1 ? "was" : "were")} left out.");
    }

    private static string Name(QuestionFile file) => ContextBudgetNotices.Name(file.Name, "A file");
}
