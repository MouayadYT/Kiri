using System.Globalization;
using System.Text;

namespace Assistant.Core.Documents;

/// <summary>
/// One piece of a document's text and the place it came from: a page, a slide, the notes of a slide, a section or the whole
/// document. The pieces of a document come in the order of the document and hold every part of its text once.
/// </summary>
/// <param name="Location">Where the text is.</param>
/// <param name="Text">The text, with its paragraphs separated by line breaks. Never empty.</param>
public sealed record DocumentSegment(DocumentLocation Location, string Text)
{
    /// <summary>The number of characters of <see cref="Text"/>.</summary>
    public int CharacterCount => Text.Length;

    /// <summary>
    /// The line that marks the segment where the document's text is laid out as one string (<c>[Page 3]</c>, <c>[Slide 4: Budget]</c>,
    /// <c>[Section 2: Install &gt; Windows]</c>), or <see langword="null"/> for the whole document, which needs none.
    /// </summary>
    public string? Header
    {
        get
        {
            var number = Location.Number.ToString(CultureInfo.InvariantCulture);
            return Location.Kind switch
            {
                DocumentLocationKind.Page => $"[Page {number}]",
                DocumentLocationKind.Slide => string.IsNullOrEmpty(Location.Label) ? $"[Slide {number}]" : $"[Slide {number}: {Location.Label}]",
                DocumentLocationKind.SlideNotes => $"[Slide {number} notes]",
                DocumentLocationKind.Section => string.IsNullOrEmpty(Location.Label) ? $"[Section {number}]" : $"[Section {number}: {Location.Label}]",
                _ => null,
            };
        }
    }

    // The text is the document's own (PROJECT_SPEC §3.2): ToString, and so a log, shows where it is and how long it is.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Location = ").Append(Location).Append(", Length = ").Append(Text.Length.ToString(CultureInfo.InvariantCulture));
        return true;
    }
}
