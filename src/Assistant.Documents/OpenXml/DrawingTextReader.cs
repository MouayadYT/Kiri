using System.Text;
using System.Xml;
using Assistant.Documents.Extraction;

namespace Assistant.Documents.OpenXml;

/// <summary>What was read from a slide (or its notes): its title, when a shape is the title, and all its text.</summary>
internal sealed record DrawingText(string? Title, string Text);

/// <summary>
/// Reads the text of one slide part, or of one notes part, with a forward-only XML reader. The shapes' text comes in the order
/// the shapes are in the slide (a shape's paragraphs one to a line, the shapes apart by a blank line, a group's shapes in
/// place), a table one row to a line with its cells between bars, and the text of a SmartArt diagram where the diagram is. Not
/// taken, and never followed or run: the footer, date and slide number placeholders (the same on every slide), the cached
/// field values of a slide number or date, charts, pictures, embedded objects and their links, and the old copy of content
/// that <c>mc:Fallback</c> keeps for programs that cannot draw the new one. In the notes only the notes text itself is taken.
/// </summary>
internal sealed class DrawingTextReader
{
    private readonly bool _notes;
    private readonly Func<string, string?>? _diagramText;
    private readonly long _maxCharacters;
    private readonly CancellationToken _cancellationToken;

    private readonly List<string> _blocks = [];
    private string? _title;
    private long _characters;

    private DrawingTextReader(bool notes, Func<string, string?>? diagramText, long maxCharacters, CancellationToken cancellationToken)
    {
        _notes = notes;
        _diagramText = diagramText;
        _maxCharacters = maxCharacters;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Reads a slide. <paramref name="diagramText"/> gives the text of the SmartArt diagram a relationship id names (or
    /// <see langword="null"/>).
    /// </summary>
    public static DrawingText ReadSlide(XmlReader reader, Func<string, string?>? diagramText, long maxCharacters, CancellationToken cancellationToken)
    {
        var slide = new DrawingTextReader(notes: false, diagramText, maxCharacters, cancellationToken);
        slide.Read(reader);
        return new DrawingText(slide._title, string.Join("\n\n", slide._blocks));
    }

    /// <summary>Reads the speaker notes of a slide.</summary>
    public static string ReadNotes(XmlReader reader, long maxCharacters, CancellationToken cancellationToken)
    {
        var notes = new DrawingTextReader(notes: true, diagramText: null, maxCharacters, cancellationToken);
        notes.Read(reader);
        return string.Join("\n\n", notes._blocks);
    }

    /// <summary>Reads the text of a SmartArt diagram's data part: what is written in its nodes, one to a line.</summary>
    public static string ReadDiagram(XmlReader reader, long maxCharacters, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        long characters = 0;

        while (characters <= maxCharacters && !reader.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeXml.CheckDepth(reader);

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "pt" && Ooxml.IsDiagram(reader.NamespaceURI))
            {
                // A point is a node (the default), or one of the parts the layout is made of: only nodes hold what someone wrote.
                var type = reader.GetAttribute("type");
                var isNode = type is null or "node" or "asst";
                using (var point = reader.ReadSubtree())
                {
                    var text = isNode ? ReadParagraphs(point, cancellationToken) : string.Empty;
                    if (text.Length > 0)
                    {
                        lines.Add(text);
                        characters += text.Length;
                    }
                }

                reader.Read();
                continue;
            }

            reader.Read();
        }

        return string.Join("\n", lines);
    }

    // The paragraphs of a piece of DrawingML, one to a line.
    private static string ReadParagraphs(XmlReader reader, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        StringBuilder? paragraph = null;

        reader.Read();
        while (!reader.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element && Ooxml.IsDrawing(reader.NamespaceURI))
            {
                switch (reader.LocalName)
                {
                    case "p" when !reader.IsEmptyElement:
                        paragraph = new StringBuilder();
                        break;
                    case "t":
                        paragraph?.Append(reader.ReadElementContentAsString());
                        continue;
                    case "br":
                        paragraph?.Append('\n');
                        break;
                    case "rPr" or "pPr" or "endParaRPr" or "extLst":
                        reader.Skip();
                        continue;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p" && Ooxml.IsDrawing(reader.NamespaceURI))
            {
                var text = paragraph?.ToString() ?? string.Empty;
                paragraph = null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    lines.Add(text.Trim());
                }
            }

            reader.Read();
        }

        return string.Join("\n", lines);
    }

    private void Read(XmlReader reader)
    {
        var shapes = new Stack<ShapeState>();
        var tables = new Stack<TableState>();
        StringBuilder? paragraph = null;

        while (_characters <= _maxCharacters && !reader.EOF)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            SafeXml.CheckDepth(reader);

            if (reader.NodeType == XmlNodeType.Element)
            {
                var ns = reader.NamespaceURI;
                var name = reader.LocalName;

                if (Ooxml.IsMarkupCompatibility(ns))
                {
                    // A choice and its fallback are the same content twice: the choice is the one a current PowerPoint draws.
                    if (name == "Fallback")
                    {
                        reader.Skip();
                        continue;
                    }
                }
                else if (Ooxml.IsDrawing(ns))
                {
                    switch (name)
                    {
                        case "p" when !reader.IsEmptyElement:
                            paragraph = new StringBuilder();
                            break;

                        case "t":
                            paragraph?.Append(reader.ReadElementContentAsString());
                            continue;

                        case "br":
                            paragraph?.Append('\n');
                            break;

                        case "fld":
                            // The number of the slide and the date are not what is on the slide.
                            if (reader.GetAttribute("type") is { } type
                                && (type.StartsWith("slidenum", StringComparison.OrdinalIgnoreCase) || type.StartsWith("datetime", StringComparison.OrdinalIgnoreCase)))
                            {
                                reader.Skip();
                                continue;
                            }

                            break;

                        case "tbl":
                            if (!reader.IsEmptyElement)
                            {
                                tables.Push(new TableState());
                            }

                            break;

                        case "tr":
                            if (tables.Count > 0 && !reader.IsEmptyElement)
                            {
                                tables.Peek().Row = [];
                            }

                            break;

                        case "tc":
                            if (tables.Count > 0 && !reader.IsEmptyElement)
                            {
                                tables.Peek().Cell = [];
                            }

                            break;

                        case "pPr" or "rPr" or "endParaRPr" or "bodyPr" or "lstStyle" or "tblPr" or "tblGrid" or "tcPr" or "extLst":
                            reader.Skip();
                            continue;
                    }
                }
                else if (Ooxml.IsPresentation(ns))
                {
                    switch (name)
                    {
                        case "sp" when !reader.IsEmptyElement:
                            shapes.Push(new ShapeState());
                            break;

                        case "ph":
                            if (shapes.Count > 0)
                            {
                                shapes.Peek().Placeholder = reader.GetAttribute("type") ?? "obj";
                            }

                            break;

                        case "spPr" or "extLst" or "custDataLst" or "oleObj" or "timing" or "transition":
                            reader.Skip();
                            continue;
                    }
                }
                else if (Ooxml.IsDiagram(ns) && name == "relIds")
                {
                    foreach (var relationship in DiagramRelationships(reader))
                    {
                        if (_diagramText?.Invoke(relationship) is { Length: > 0 } diagram)
                        {
                            AddBlock(diagram);
                        }
                    }
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                var ns = reader.NamespaceURI;
                var name = reader.LocalName;

                if (Ooxml.IsDrawing(ns))
                {
                    switch (name)
                    {
                        case "p" when paragraph is not null:
                            EndParagraph(paragraph.ToString(), shapes, tables);
                            paragraph = null;
                            break;

                        case "tc" when tables.Count > 0 && tables.Peek().Cell is { } cell:
                            var table = tables.Peek();
                            table.Row?.Add(string.Join(' ', cell));
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
                            var finished = tables.Pop();
                            if (finished.Rows.Count > 0)
                            {
                                if (tables.Count > 0)
                                {
                                    tables.Peek().Cell?.Add(string.Join("; ", finished.Rows));
                                }
                                else
                                {
                                    AddBlock(string.Join('\n', finished.Rows));
                                }
                            }

                            break;
                    }
                }
                else if (Ooxml.IsPresentation(ns) && name == "sp" && shapes.Count > 0)
                {
                    EndShape(shapes.Pop());
                }
            }

            reader.Read();
        }
    }

    private void EndParagraph(string raw, Stack<ShapeState> shapes, Stack<TableState> tables)
    {
        var text = raw.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (tables.Count > 0 && tables.Peek().Cell is { } cell)
        {
            cell.Add(text.Replace('\n', ' '));
        }
        else if (shapes.Count > 0)
        {
            shapes.Peek().Paragraphs.Add(text);
        }
    }

    private void EndShape(ShapeState shape)
    {
        if (shape.Paragraphs.Count == 0 || !IsWanted(shape.Placeholder))
        {
            return;
        }

        var text = string.Join('\n', shape.Paragraphs);
        if (_title is null && shape.Placeholder is "title" or "ctrTitle" && !_notes)
        {
            _title = Title(shape.Paragraphs[0]);
        }

        AddBlock(text);
    }

    // On a slide everything but the footer, the date and the number, which are the same on every slide; in the notes only the notes.
    private bool IsWanted(string? placeholder) => _notes
        ? placeholder is null or "body" or "obj"
        : placeholder is not ("dt" or "ftr" or "sldNum" or "hdr");

    private static string? Title(string text)
    {
        var clean = TextCleaner.Clean(text).Replace('\n', ' ');
        if (clean.Length == 0)
        {
            return null;
        }

        return clean.Length <= HeadingTrail.MaxLabelLength ? clean : clean[..(HeadingTrail.MaxLabelLength - 1)].TrimEnd() + "…";
    }

    private void AddBlock(string text)
    {
        _blocks.Add(text);
        _characters += text.Length;
    }

    // <dgm:relIds r:dm="rId3" r:lo="rId4" .../>: the diagram's data is the one named by r:dm.
    private static IEnumerable<string> DiagramRelationships(XmlReader reader)
    {
        if (reader.MoveToFirstAttribute())
        {
            var found = new List<string>();
            do
            {
                if (reader.LocalName == "dm" && Ooxml.IsRelationships(reader.NamespaceURI) && reader.Value.Length > 0)
                {
                    found.Add(reader.Value);
                }
            }
            while (reader.MoveToNextAttribute());

            reader.MoveToElement();
            return found;
        }

        return [];
    }

    private sealed class ShapeState
    {
        public string? Placeholder { get; set; }

        public List<string> Paragraphs { get; } = [];
    }

    private sealed class TableState
    {
        public List<string> Rows { get; } = [];

        public List<string>? Row { get; set; }

        public List<string>? Cell { get; set; }
    }
}
