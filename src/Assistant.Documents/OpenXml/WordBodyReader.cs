using System.Globalization;
using System.Text;
using System.Xml;
using Assistant.Core.Documents;
using Assistant.Documents.Extraction;

namespace Assistant.Documents.OpenXml;

/// <summary>
/// Reads the text of a Word document's parts (the body, then the footnotes and endnotes) in the order it is written, with a
/// forward-only XML reader, so a long document is never held as a tree and reading stops the moment the limit is reached.
/// Only what is text is taken: the runs' text (<c>w:t</c>), tabs and line breaks, and equations' text (<c>m:t</c>). Not taken,
/// and never followed or run: field codes (the <c>INCLUDETEXT</c> or <c>HYPERLINK</c> of a field, only its displayed result),
/// the targets of links, deleted text of tracked changes (inserted text is taken), comments, headers and footers, embedded
/// objects and pictures, macros, and the old copy of a text box that Word keeps for programs that cannot draw the new one
/// (<c>mc:Fallback</c>). The paragraphs keep their order; a table is one row to a line with its cells between bars; a text box's
/// paragraphs follow the paragraph it is anchored in; each paragraph in a heading style begins a section.
/// </summary>
internal sealed class WordBodyReader
{
    private readonly WordStyleMap _styles;
    private readonly long _maxCharacters;
    private readonly int _maxUnits;
    private readonly CancellationToken _cancellationToken;

    private readonly List<Section> _sections = [];
    private readonly HeadingTrail _trail = new();
    private Section _current = new(null);
    private long _characters;

    public WordBodyReader(WordStyleMap styles, long maxCharacters, int maxUnits, CancellationToken cancellationToken)
    {
        _styles = styles;
        _maxCharacters = maxCharacters;
        _maxUnits = maxUnits;
        _cancellationToken = cancellationToken;
    }

    /// <summary>True when reading stopped before the end of the document because of a limit.</summary>
    public bool StoppedEarly { get; private set; }

    /// <summary>True when some paragraph is a heading, so the text is in sections.</summary>
    public bool HasHeadings { get; private set; }

    /// <summary>Reads the body of the document (<c>word/document.xml</c>).</summary>
    public void ReadBody(XmlReader reader) => Read(reader, isNotes: false);

    /// <summary>
    /// Reads a notes part (footnotes or endnotes) as a section of its own, called <paramref name="title"/>, after what has been read.
    /// </summary>
    public void ReadNotes(XmlReader reader, string title)
    {
        if (StoppedEarly)
        {
            return;
        }

        HasHeadings = true;
        FinishSection();
        _current = new Section(title);
        Read(reader, isNotes: true);
    }

    /// <summary>Hands what was read to <paramref name="collector"/>: one piece for a document with no headings, else a piece for each section.</summary>
    public void WriteTo(SegmentCollector collector)
    {
        FinishSection();

        if (!HasHeadings)
        {
            collector.Add(
                DocumentLocation.WholeDocument(), string.Join("\n\n", _sections.SelectMany(s => s.Blocks)));
        }
        else
        {
            // Every section is offered even when the collector is full: the first one it cannot take is how it learns the text is cut.
            var number = 0;
            foreach (var section in _sections)
            {
                number++;
                collector.Add(DocumentLocation.ForSection(number, section.Heading), string.Join("\n\n", section.Blocks));
            }
        }

        if (StoppedEarly)
        {
            collector.Stop();
        }
    }

    private void Read(XmlReader reader, bool isNotes)
    {
        var paragraphs = new Stack<ParagraphState>();
        var tables = new Stack<TableState>();

        while (!StoppedEarly && !reader.EOF)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            SafeXml.CheckDepth(reader);

            if (reader.NodeType == XmlNodeType.Element)
            {
                if (HandleStart(reader, paragraphs, tables, isNotes))
                {
                    continue;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && Ooxml.IsWordprocessing(reader.NamespaceURI))
            {
                HandleEnd(reader.LocalName, paragraphs, tables);
            }

            reader.Read();
        }
    }

    /// <summary>Returns <see langword="true"/> when it has already moved the reader on.</summary>
    private bool HandleStart(XmlReader reader, Stack<ParagraphState> paragraphs, Stack<TableState> tables, bool isNotes)
    {
        var ns = reader.NamespaceURI;
        var name = reader.LocalName;

        if (Ooxml.IsMarkupCompatibility(ns))
        {
            // A choice and its fallback are the same content twice: the choice is the one a current Word draws.
            if (name == "Fallback")
            {
                reader.Skip();
                return true;
            }

            return false;
        }

        if (Ooxml.IsMath(ns))
        {
            if (name == "t" && paragraphs.Count > 0)
            {
                paragraphs.Peek().Text.Append(reader.ReadElementContentAsString());
                return true;
            }

            return false;
        }

        if (!Ooxml.IsWordprocessing(ns))
        {
            return false;
        }

        switch (name)
        {
            case "p":
                if (reader.IsEmptyElement)
                {
                    return false;
                }

                paragraphs.Push(new ParagraphState());
                return false;

            case "pPr":
                if (paragraphs.Count > 0)
                {
                    using (var properties = reader.ReadSubtree())
                    {
                        ReadParagraphProperties(properties, paragraphs.Peek());
                    }

                    reader.Read();
                    return true;
                }

                return false;

            case "t":
                if (paragraphs.Count > 0)
                {
                    paragraphs.Peek().Text.Append(reader.ReadElementContentAsString());
                    return true;
                }

                reader.Skip();
                return true;

            case "tab" or "ptab":
                if (paragraphs.Count > 0)
                {
                    paragraphs.Peek().Text.Append('\t');
                }

                return false;

            case "br" or "cr":
                if (paragraphs.Count > 0)
                {
                    paragraphs.Peek().Text.Append('\n');
                }

                return false;

            case "noBreakHyphen":
                if (paragraphs.Count > 0)
                {
                    paragraphs.Peek().Text.Append('-');
                }

                return false;

            case "tbl":
                if (!reader.IsEmptyElement)
                {
                    tables.Push(new TableState());
                }

                return false;

            case "tr":
                if (tables.Count > 0 && !reader.IsEmptyElement)
                {
                    tables.Peek().Row = [];
                }

                return false;

            case "tc":
                if (tables.Count > 0 && !reader.IsEmptyElement)
                {
                    tables.Peek().Cell = [];
                }

                return false;

            case "footnote" or "endnote":
                // The separators Word draws between the text and its notes are not notes.
                if (isNotes && reader.GetAttribute("type", ns) is "separator" or "continuationSeparator" or "continuationNotice")
                {
                    reader.Skip();
                    return true;
                }

                return false;

            // Properties, and what a field or a tracked change keeps that is not the text as it reads now.
            case "rPr" or "sectPr" or "tblPr" or "trPr" or "tcPr" or "tblGrid" or "sdtPr" or "sdtEndPr"
                or "del" or "moveFrom" or "instrText" or "delInstrText" or "delText"
                or "tblGridChange" or "numberingChange":
                reader.Skip();
                return true;

            default:
                if (name.EndsWith("PrChange", StringComparison.Ordinal))
                {
                    reader.Skip();
                    return true;
                }

                return false;
        }
    }

    private void HandleEnd(string name, Stack<ParagraphState> paragraphs, Stack<TableState> tables)
    {
        switch (name)
        {
            case "p" when paragraphs.Count > 0:
                EndParagraph(paragraphs, tables);
                break;

            case "tc" when tables.Count > 0 && tables.Peek().Cell is { } cell:
                var table = tables.Peek();
                var text = string.Join(' ', cell.Where(static c => !string.IsNullOrWhiteSpace(c)).Select(static c => c.Trim()));
                table.Row?.Add(text);
                table.Cell = null;
                break;

            case "tr" when tables.Count > 0 && tables.Peek().Row is { } row:
                var owner = tables.Peek();
                while (row.Count > 0 && row[^1].Length == 0)
                {
                    row.RemoveAt(row.Count - 1);
                }

                if (row.Count > 0)
                {
                    owner.Rows.Add(string.Join(" | ", row));
                }

                owner.Row = null;
                break;

            case "tbl" when tables.Count > 0:
                EndTable(tables);
                break;
        }
    }

    private void EndParagraph(Stack<ParagraphState> paragraphs, Stack<TableState> tables)
    {
        var paragraph = paragraphs.Pop();
        var text = paragraph.Text.ToString();

        if (paragraphs.Count > 0)
        {
            // Inside another paragraph: a text box. Its text comes out after the paragraph it is anchored in.
            if (!string.IsNullOrWhiteSpace(text))
            {
                paragraphs.Peek().Nested.Add(text);
            }

            return;
        }

        Emit(paragraph, text, tables);
        foreach (var nested in paragraph.Nested)
        {
            Emit(null, nested, tables);
        }
    }

    private void Emit(ParagraphState? paragraph, string text, Stack<TableState> tables)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (tables.Count > 0)
        {
            tables.Peek().Cell?.Add(text);
            return;
        }

        var level = paragraph is null ? 0 : paragraph.OutlineLevel is { } outline ? (outline < 9 ? outline + 1 : 0) : _styles.HeadingLevel(paragraph.StyleId);
        if (level > 0)
        {
            StartSection(_trail.Enter(level, text));
        }

        AddBlock(text);
    }

    private void EndTable(Stack<TableState> tables)
    {
        var table = tables.Pop();
        if (table.Rows.Count == 0)
        {
            return;
        }

        if (tables.Count > 0)
        {
            // A table in a cell of another: its rows are part of that cell's text.
            tables.Peek().Cell?.Add(string.Join("; ", table.Rows));
            return;
        }

        AddBlock(string.Join('\n', table.Rows));
    }

    private void StartSection(string? heading)
    {
        HasHeadings = true;
        FinishSection();

        if (_sections.Count >= _maxUnits)
        {
            StoppedEarly = true;
            return;
        }

        _current = new Section(heading);
    }

    private void FinishSection()
    {
        if (_current.Blocks.Count > 0)
        {
            _sections.Add(_current);
            _current = new Section(null);
        }
    }

    private void AddBlock(string text)
    {
        if (StoppedEarly)
        {
            return;
        }

        _current.Blocks.Add(text);
        _characters += text.Length;
        if (_characters > _maxCharacters)
        {
            StoppedEarly = true;
        }
    }

    private static void ReadParagraphProperties(XmlReader properties, ParagraphState paragraph)
    {
        properties.Read();
        while (!properties.EOF)
        {
            if (properties.NodeType == XmlNodeType.Element && Ooxml.IsWordprocessing(properties.NamespaceURI))
            {
                switch (properties.LocalName)
                {
                    case "pStyle":
                        paragraph.StyleId = properties.GetAttribute("val", properties.NamespaceURI);
                        break;
                    case "outlineLvl":
                        if (int.TryParse(properties.GetAttribute("val", properties.NamespaceURI), NumberStyles.None, CultureInfo.InvariantCulture, out var level))
                        {
                            paragraph.OutlineLevel = level;
                        }

                        break;
                    case "pPrChange" or "rPr" or "sectPr":
                        // The properties the paragraph had before a tracked change, and its mark's run properties: not its own.
                        properties.Skip();
                        continue;
                }
            }

            properties.Read();
        }
    }

    private sealed class Section(string? heading)
    {
        public string? Heading { get; } = heading;

        public List<string> Blocks { get; } = [];
    }

    private sealed class ParagraphState
    {
        public StringBuilder Text { get; } = new();

        public string? StyleId { get; set; }

        public int? OutlineLevel { get; set; }

        public List<string> Nested { get; } = [];
    }

    private sealed class TableState
    {
        public List<string> Rows { get; } = [];

        public List<string>? Row { get; set; }

        public List<string>? Cell { get; set; }
    }
}
