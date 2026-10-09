using System.Globalization;
using System.Text;

namespace Assistant.Core.Documents;

/// <summary>
/// One passage of a document: a stretch of its text cut to a size a prompt can hold, and where in the document it is
/// (PROJECT_SPEC §4.7). A document is cut into passages that follow one another, each starting a little before the one
/// before ends, so a sentence that falls on a cut is whole in at least one of them.
/// </summary>
public sealed record DocumentPassage
{
    private DocumentLocation? _endLocation;

    /// <summary>Where the passage is among the passages of its document, counted from 0 in the order of the document.</summary>
    public int Index { get; init; }

    /// <summary>The passage's text, normalized and never empty.</summary>
    public string Text { get; init; } = "";

    /// <summary>Where the passage starts: its page, slide or section, and for text files the lines it is on.</summary>
    public DocumentLocation Location { get; init; } = DocumentLocation.WholeDocument();

    /// <summary>
    /// Where the passage ends. It is <see cref="Location"/> for a passage within one part of the document, and the last part
    /// for a passage that gathers several short ones (a few slides, each marked in the text where it starts).
    /// </summary>
    public DocumentLocation EndLocation
    {
        get => _endLocation ?? Location;
        init => _endLocation = value;
    }

    /// <summary>
    /// How many characters at the start of <see cref="Text"/> are also the end of the passage before it (the overlap); zero
    /// for the first passage of a part of the document, which does not repeat another.
    /// </summary>
    public int OverlapLength { get; init; }

    /// <summary>The number of characters of <see cref="Text"/>.</summary>
    public int CharacterCount => Text.Length;

    /// <summary>
    /// The place in words, without the document's own text: <c>page 3</c>, <c>slides 4-7</c>, <c>section 2, lines 40-72</c>.
    /// </summary>
    public string Describe()
    {
        var first = Location;
        var last = EndLocation;
        if (first == last)
        {
            return first.Describe();
        }

        if (first.Kind == last.Kind && first.Kind is DocumentLocationKind.Page or DocumentLocationKind.Slide or DocumentLocationKind.Section)
        {
            var noun = first.Kind switch
            {
                DocumentLocationKind.Page => "pages",
                DocumentLocationKind.Slide => "slides",
                _ => "sections",
            };
            var place = string.Create(CultureInfo.InvariantCulture, $"{noun} {first.Number}-{last.Number}");
            return first.FirstLine is { } firstLine && last.LastLine is { } lastLine
                ? string.Create(CultureInfo.InvariantCulture, $"{place}, lines {firstLine}-{lastLine}")
                : place;
        }

        return $"{first.Describe()} to {last.Describe()}";
    }

    /// <summary>
    /// <see cref="Describe"/> with the heading or title of the passage's first part after it when it has one
    /// (<c>section 3 (Install &gt; Windows)</c>), the line that marks the passage where it is laid out in a prompt.
    /// </summary>
    public string Header => Location.Label is { Length: > 0 } label ? $"{Describe()} ({label})" : Describe();

    // The text is the document's own (PROJECT_SPEC §3.2): ToString, and so a log, shows where the passage is and how long it is.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Index = ").Append(Index.ToString(CultureInfo.InvariantCulture))
            .Append(", Location = ").Append(Location)
            .Append(", Length = ").Append(Text.Length.ToString(CultureInfo.InvariantCulture));
        return true;
    }
}
