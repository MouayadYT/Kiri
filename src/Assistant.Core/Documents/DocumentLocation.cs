using System.Globalization;
using System.Text;

namespace Assistant.Core.Documents;

/// <summary>What kind of place in a document a piece of its text came from.</summary>
public enum DocumentLocationKind
{
    /// <summary>The document as a whole: a file with no pages, slides or headings to tell its parts apart.</summary>
    Document,

    /// <summary>A page of a PDF (or a form-feed page of a plain text file).</summary>
    Page,

    /// <summary>A slide of a presentation.</summary>
    Slide,

    /// <summary>The speaker notes of a slide.</summary>
    SlideNotes,

    /// <summary>The part of a document under one heading, headings and all, including what comes before its first heading.</summary>
    Section,
}

/// <summary>
/// Where in a document a piece of text came from, so that an answer can say so (PROJECT_SPEC §4.7): "page 12", "slide 4", "the
/// section Budget" and, for text files, the lines. Numbers count from 1.
/// </summary>
public sealed record DocumentLocation
{
    /// <summary>What kind of place this is.</summary>
    public DocumentLocationKind Kind { get; init; }

    /// <summary>The number of the page or slide, or of the section among the sections of the document; 0 for the whole document.</summary>
    public int Number { get; init; }

    /// <summary>
    /// The heading (for a section: its whole trail, <c>Install &gt; Windows</c>) or the title of the slide, when there is one. It
    /// is the document's own text.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>The first line of the file this text is on, for text and Markdown files.</summary>
    public int? FirstLine { get; init; }

    /// <summary>The last line of the file this text is on, for text and Markdown files.</summary>
    public int? LastLine { get; init; }

    /// <summary>The whole document.</summary>
    public static DocumentLocation WholeDocument(int? firstLine = null, int? lastLine = null) =>
        new() { Kind = DocumentLocationKind.Document, FirstLine = firstLine, LastLine = lastLine };

    /// <summary>A page, counted from 1.</summary>
    public static DocumentLocation ForPage(int number, int? firstLine = null, int? lastLine = null) =>
        new() { Kind = DocumentLocationKind.Page, Number = number, FirstLine = firstLine, LastLine = lastLine };

    /// <summary>A slide, counted from 1 in the order of the presentation, with its title when it has one.</summary>
    public static DocumentLocation ForSlide(int number, string? title = null) =>
        new() { Kind = DocumentLocationKind.Slide, Number = number, Label = title };

    /// <summary>The speaker notes of a slide.</summary>
    public static DocumentLocation ForSlideNotes(int number) =>
        new() { Kind = DocumentLocationKind.SlideNotes, Number = number };

    /// <summary>A section, counted from 1, with its heading trail when it has one.</summary>
    public static DocumentLocation ForSection(int number, string? heading = null, int? firstLine = null, int? lastLine = null) =>
        new() { Kind = DocumentLocationKind.Section, Number = number, Label = heading, FirstLine = firstLine, LastLine = lastLine };

    /// <summary>
    /// The place in words, without the document's own text: <c>page 3</c>, <c>slide 4 notes</c>, <c>section 2, lines 40-72</c>.
    /// </summary>
    public string Describe()
    {
        var place = Kind switch
        {
            DocumentLocationKind.Page => string.Create(CultureInfo.InvariantCulture, $"page {Number}"),
            DocumentLocationKind.Slide => string.Create(CultureInfo.InvariantCulture, $"slide {Number}"),
            DocumentLocationKind.SlideNotes => string.Create(CultureInfo.InvariantCulture, $"slide {Number} notes"),
            DocumentLocationKind.Section => string.Create(CultureInfo.InvariantCulture, $"section {Number}"),
            _ => "document",
        };

        if (FirstLine is not { } first)
        {
            return place;
        }

        var last = LastLine ?? first;
        return last > first
            ? string.Create(CultureInfo.InvariantCulture, $"{place}, lines {first}-{last}")
            : string.Create(CultureInfo.InvariantCulture, $"{place}, line {first}");
    }

    // The heading or title is the document's text (PROJECT_SPEC §3.2): ToString, and so a log, shows the place and not it.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Kind = ").Append(Kind).Append(", Number = ").Append(Number.ToString(CultureInfo.InvariantCulture));
        return true;
    }
}
