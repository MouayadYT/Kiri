using System.Globalization;
using System.Text;

namespace Assistant.Core.Documents;

/// <summary>Why the passages of a <see cref="PassageSelection"/> are the ones that were chosen.</summary>
public enum PassageSelectionReason
{
    /// <summary>The whole document fits the limits, so every passage was taken, in order, whatever was asked.</summary>
    WholeDocument,

    /// <summary>Passages that hold the words of the question were taken, the best first until the limits were reached.</summary>
    Matched,

    /// <summary>The question has words worth looking for and no passage holds any of them, so the start of the document was taken.</summary>
    NoMatch,

    /// <summary>
    /// The question has no words worth looking for ("what is this about?"), so it is about the whole document: passages spread
    /// evenly over it were taken, the first and the last among them.
    /// </summary>
    NoQueryTerms,
}

/// <summary>A passage that was selected and how well it matched.</summary>
/// <param name="Passage">The passage.</param>
/// <param name="Score">
/// How well the passage matches the question: zero when it holds none of its words, and more the more of them it holds, the
/// rarer they are in the document and the closer they stand together. Scores are comparable within one selection only.
/// </param>
public sealed record SelectedPassage(DocumentPassage Passage, double Score);

/// <summary>
/// The passages of a document chosen for a question (PROJECT_SPEC §4.7): what goes into the prompt in place of the document.
/// </summary>
public sealed record PassageSelection
{
    /// <summary>A selection of nothing, from a document with no passages.</summary>
    public static PassageSelection Empty { get; } = new();

    /// <summary>The selected passages in the order of the document, not of their scores.</summary>
    public IReadOnlyList<SelectedPassage> Passages { get; init; } = [];

    /// <summary>The number of passages the document has, selected or not.</summary>
    public int TotalPassages { get; init; }

    /// <summary>Why these were chosen.</summary>
    public PassageSelectionReason Reason { get; init; }

    /// <summary>How many distinct words of the question were looked for; zero when none was worth looking for.</summary>
    public int QueryTermCount { get; init; }

    /// <summary>Whether every passage of the document was selected.</summary>
    public bool IsComplete => Passages.Count == TotalPassages;

    /// <summary>
    /// The number of characters of the selected passages' text as <see cref="ToText"/> lays it out, where passages that follow
    /// each other are joined without repeating their overlap.
    /// </summary>
    public int CharacterCount
    {
        get
        {
            var count = 0;
            foreach (var group in Group())
            {
                count += group.Text.Length;
            }

            return count;
        }
    }

    /// <summary>
    /// The passages as one string for a prompt: each under a line that numbers it and says where in the document it is
    /// (<c>[Passage 2: page 7]</c>), a blank line between them. Passages that follow each other in the document, which overlap,
    /// are one passage with the overlap once, so nothing is read twice; the line then says where the run starts and ends
    /// (<c>pages 7-8</c>).
    /// </summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        var number = 0;
        foreach (var group in Group())
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            number++;
            builder.Append("[Passage ").Append(number.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(group.Header)
                .Append("]\n").Append(group.Text);
        }

        return builder.ToString();
    }

    // What one passage, or a run of passages that follow each other, is laid out as.
    private readonly record struct Laid(string Header, string Text);

    // Consecutive passages of one part of the document, each starting with the end of the one before it, become one piece.
    private List<Laid> Group()
    {
        var groups = new List<Laid>();
        var index = 0;
        while (index < Passages.Count)
        {
            var first = Passages[index].Passage;
            var text = new StringBuilder(first.Text);
            var last = first;
            while (index + 1 < Passages.Count
                   && Passages[index + 1].Passage is { } next
                   && next.Index == last.Index + 1
                   && Overlaps(last, next))
            {
                text.Append(next.Text, next.OverlapLength, next.Text.Length - next.OverlapLength);
                last = next;
                index++;
            }

            var header = ReferenceEquals(last, first) ? first : first with { EndLocation = last.EndLocation };
            groups.Add(new Laid(header.Header, text.ToString()));
            index++;
        }

        return groups;
    }

    // The next passage repeats the end of this one, exactly: merging them loses and repeats nothing.
    private static bool Overlaps(DocumentPassage previous, DocumentPassage next) =>
        next.OverlapLength > 0
        && next.OverlapLength < next.Text.Length
        && previous.Text.Length >= next.OverlapLength
        && string.CompareOrdinal(previous.Text, previous.Text.Length - next.OverlapLength, next.Text, 0, next.OverlapLength) == 0;

    // The passages are the document's own text (PROJECT_SPEC §3.2): ToString, and so a log, shows how many and how long.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Passages = ").Append(Passages.Count.ToString(CultureInfo.InvariantCulture))
            .Append(", TotalPassages = ").Append(TotalPassages.ToString(CultureInfo.InvariantCulture))
            .Append(", Reason = ").Append(Reason)
            .Append(", QueryTermCount = ").Append(QueryTermCount.ToString(CultureInfo.InvariantCulture));
        return true;
    }
}
