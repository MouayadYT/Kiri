using System.Globalization;
using System.Text;

namespace Assistant.Core.MultiFile;

/// <summary>Notes the model took for a question on a piece of a file, or combined from several such notes.</summary>
/// <param name="FileIndexes">The files the notes are about, by their place among the question's files, in order.</param>
/// <param name="Label">The notes' name for the model: the file and which piece, or the files they were combined from.</param>
/// <param name="Text">The notes.</param>
/// <param name="Path">The file's full path when the notes are about one file; otherwise <see langword="null"/>.</param>
public sealed record FileNote(IReadOnlyList<int> FileIndexes, string Label, string Text, string? Path)
{
    /// <summary>What notes that the model left empty, or that found nothing for the question, say.</summary>
    public const string NothingRelevant = "Nothing relevant.";

    // What a note that says only that nothing bears on the question comes to, in lower case without punctuation or emphasis.
    private static readonly HashSet<string> NothingSayings =
    [
        "nothing relevant",
        "nothing relevant here",
        "nothing relevant found",
        "nothing relevant in this part",
        "nothing relevant to the question",
        "nothing",
        "none",
        "no relevant information",
    ];

    /// <summary>Whether the notes say only that nothing bears on the question.</summary>
    public bool IsNothing => IsNothingText(Text);

    /// <summary>
    /// Whether <paramref name="text"/> says only that nothing bears on the question (or nothing at all), however it is punctuated or
    /// emphasized; a note that says anything more is a note.
    /// </summary>
    public static bool IsNothingText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var words = string.Join(
            ' ',
            new string([.. text.Select(character => char.IsLetter(character) ? char.ToLowerInvariant(character) : ' ')])
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return NothingSayings.Contains(words);
    }

    /// <summary>
    /// The model's notes made tidy: one kind of line break, no blank lines in a row, no space at the ends, and no longer than
    /// <paramref name="maxCharacters"/>, cut at the end of a line when one is near (in case the model wrote on past what it was asked).
    /// Notes that were <paramref name="cutShort"/> by the model's length limit lose their last line, which the limit cut through,
    /// unless it is the only one.
    /// </summary>
    public static string Clean(string text, int maxCharacters, bool cutShort = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharacters, 1);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var builder = new StringBuilder();
        var blank = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                blank = builder.Length > 0;
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(blank ? "\n\n" : "\n");
            }

            builder.Append(line);
            blank = false;
        }

        var clean = builder.ToString().Trim();
        if (cutShort && clean.LastIndexOf('\n') is var last and > 0)
        {
            clean = clean[..last].TrimEnd();
        }

        if (clean.Length <= maxCharacters)
        {
            return clean;
        }

        var cut = clean.LastIndexOf('\n', maxCharacters - 1);
        if (cut < maxCharacters / 2)
        {
            cut = char.IsHighSurrogate(clean[maxCharacters - 1]) ? maxCharacters - 1 : maxCharacters;
        }

        return clean[..cut].TrimEnd();
    }

    // The notes are about the user's files (PROJECT_SPEC §3.2): ToString, and so a log, shows how many files and how long.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Files = ").Append(FileIndexes.Count.ToString(CultureInfo.InvariantCulture))
            .Append(", Length = ").Append(Text.Length.ToString(CultureInfo.InvariantCulture));
        return true;
    }
}

/// <summary>How notes on pieces are made fewer before the answer is put together from them (PROJECT_SPEC §5.5, several files).</summary>
public static class FileNotes
{
    /// <summary>
    /// The notes that say something, in order: a piece that found nothing for the question is left out when its file has notes that
    /// say something, and a file all of whose pieces found nothing keeps one note that says so, so the answer still knows it was read.
    /// </summary>
    /// <param name="notes">The notes on each piece, in the order of the files and of each file.</param>
    /// <param name="files">The question's files, to name a file whose pieces found nothing.</param>
    public static IReadOnlyList<FileNote> Collapse(IReadOnlyList<FileNote> notes, IReadOnlyList<QuestionFile> files)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(files);
        var saysSomething = notes.Where(note => !note.IsNothing).SelectMany(note => note.FileIndexes).ToHashSet();
        var nothingNoted = new HashSet<int>();
        var kept = new List<FileNote>(notes.Count);
        foreach (var note in notes)
        {
            if (!note.IsNothing)
            {
                kept.Add(note);
                continue;
            }

            // One "nothing" for a file whose pieces all found nothing, named by the file alone.
            foreach (var index in note.FileIndexes.Where(index => !saysSomething.Contains(index) && nothingNoted.Add(index)))
            {
                var file = index >= 0 && index < files.Count ? files[index] : null;
                kept.Add(new FileNote([index], file?.Name ?? note.Label, FileNote.NothingRelevant, file?.Path ?? note.Path));
            }
        }

        return kept;
    }

    /// <summary>
    /// The notes in groups that follow each other, each as many as fit <paramref name="capacity"/> tokens together by
    /// <paramref name="cost"/>; a note that alone costs more is a group of its own.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<FileNote>> Group(IReadOnlyList<FileNote> notes, Func<FileNote, int> cost, int capacity)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(cost);
        var groups = new List<IReadOnlyList<FileNote>>();
        var current = new List<FileNote>();
        var size = 0;
        foreach (var note in notes)
        {
            var noteCost = cost(note);
            if (current.Count > 0 && size + noteCost > capacity)
            {
                groups.Add(current);
                current = [];
                size = 0;
            }

            current.Add(note);
            size += noteCost;
        }

        if (current.Count > 0)
        {
            groups.Add(current);
        }

        return groups;
    }

    /// <summary>
    /// The name of notes combined from <paramref name="group"/>: the file's name when they are all about one file, else the files'
    /// names, the first few and how many more.
    /// </summary>
    public static string CombinedLabel(IReadOnlyList<FileNote> group, IReadOnlyList<QuestionFile> files)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(files);
        var names = group.SelectMany(note => note.FileIndexes).Distinct()
            .Select(index => index >= 0 && index < files.Count ? files[index].Name : null)
            .OfType<string>()
            .ToArray();
        if (names.Length == 0)
        {
            return group.Count > 0 ? group[0].Label : string.Empty;
        }

        const int Listed = 3;
        if (names.Length <= Listed)
        {
            return string.Join(", ", names);
        }

        var more = names.Length - Listed;
        return string.Create(CultureInfo.InvariantCulture, $"{string.Join(", ", names.Take(Listed))} and {more} more {(more == 1 ? "file" : "files")}");
    }
}
