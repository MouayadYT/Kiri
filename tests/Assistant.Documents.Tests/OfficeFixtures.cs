using System.IO.Compression;
using System.Security;
using System.Text;

namespace Assistant.Documents.Tests;

/// <summary>A zip package built from parts the test writes out by hand, so a test controls every byte of what a reader meets.</summary>
internal sealed class OfficePackage
{
    private readonly List<(string Name, byte[] Bytes)> _entries = [];

    public OfficePackage Add(string name, string xml) => Add(name, Encoding.UTF8.GetBytes(xml));

    public OfficePackage Add(string name, byte[] bytes)
    {
        _entries.RemoveAll(e => e.Name == name);
        _entries.Add((name, bytes));
        return this;
    }

    public byte[] ToBytes()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in _entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }

        return output.ToArray();
    }
}

/// <summary>What goes in a Word document beside its body.</summary>
internal sealed record DocxOptions
{
    public string? Styles { get; init; }

    public string? Footnotes { get; init; }

    public string? Endnotes { get; init; }

    public string? Title { get; init; }

    public string? Author { get; init; }

    public int? Pages { get; init; }

    /// <summary>Relationship elements added to <c>word/_rels/document.xml.rels</c>.</summary>
    public string ExtraRelationships { get; init; } = string.Empty;

    /// <summary>Parts added as they are, by name.</summary>
    public IReadOnlyDictionary<string, byte[]> ExtraParts { get; init; } = new Dictionary<string, byte[]>();

    public string ExtraContentTypes { get; init; } = string.Empty;

    /// <summary>Text put between the XML declaration and the document element of <c>word/document.xml</c> (a DOCTYPE, for a test of one).</summary>
    public string Prolog { get; init; } = string.Empty;
}

internal static class DocxFixture
{
    public const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string Xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    private const string Namespaces =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" "
        + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" "
        + "xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\" "
        + "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" "
        + "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" "
        + "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" "
        + "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" "
        + "xmlns:v=\"urn:schemas-microsoft-com:vml\"";

    /// <summary>The styles of the tests: Heading 1 to 3, one based on Heading 1, one with a name that is not English, and Title.</summary>
    public static string Styles() =>
        Xml + "<w:styles xmlns:w=\"" + W + "\">"
        + "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>"
        + "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/></w:style>"
        + "<w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/><w:basedOn w:val=\"Normal\"/></w:style>"
        + "<w:style w:type=\"paragraph\" w:styleId=\"Heading3\"><w:name w:val=\"Heading 3\"/><w:basedOn w:val=\"Normal\"/></w:style>"
        + "<w:style w:type=\"paragraph\" w:styleId=\"Kop1\"><w:name w:val=\"Kop 1 custom\"/><w:basedOn w:val=\"Heading1\"/></w:style>"
        + "<w:style w:type=\"paragraph\" w:styleId=\"berschrift1\"><w:name w:val=\"heading 1\"/></w:style>"
        + "<w:style w:type=\"paragraph\" w:styleId=\"Outlined\"><w:name w:val=\"Outlined body\"/><w:pPr><w:outlineLvl w:val=\"1\"/></w:pPr></w:style>"
        + "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/></w:style>"
        + "</w:styles>";

    public static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;

    /// <summary>A paragraph of one run, in a style when one is named.</summary>
    public static string P(string text, string? style = null) =>
        "<w:p>" + (style is null ? string.Empty : $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>")
        + $"<w:r><w:t xml:space=\"preserve\">{Escape(text)}</w:t></w:r></w:p>";

    public static string Heading(int level, string text) => P(text, "Heading" + level);

    /// <summary>A table with a row for each entry, its cells being the entries of the row.</summary>
    public static string Table(params string[][] rows) =>
        "<w:tbl><w:tblPr/><w:tblGrid/>"
        + string.Concat(rows.Select(row => "<w:tr>" + string.Concat(row.Select(cell => "<w:tc><w:tcPr/>" + (cell.Length == 0 ? "<w:p/>" : P(cell)) + "</w:tc>")) + "</w:tr>"))
        + "</w:tbl>";

    public static string Document(string body, string prolog = "") =>
        Xml + prolog + $"<w:document {Namespaces}><w:body>{body}<w:sectPr/></w:body></w:document>";

    public static byte[] Create(string body, DocxOptions? options = null)
    {
        options ??= new DocxOptions();
        var package = new OfficePackage();

        package.Add(
            "[Content_Types].xml",
            Xml + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
            + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Default Extension=\"bin\" ContentType=\"application/octet-stream\"/>"
            + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>"
            + "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>"
            + "<Override PartName=\"/word/footnotes.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml\"/>"
            + "<Override PartName=\"/word/endnotes.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.endnotes+xml\"/>"
            + "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>"
            + "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>"
            + options.ExtraContentTypes
            + "</Types>");

        package.Add(
            "_rels/.rels",
            Xml + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>"
            + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>"
            + "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>"
            + "</Relationships>");

        var relationships = new StringBuilder(Xml + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        if (options.Styles is not null)
        {
            relationships.Append("<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            package.Add("word/styles.xml", options.Styles);
        }

        if (options.Footnotes is not null)
        {
            relationships.Append("<Relationship Id=\"rIdFootnotes\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footnotes\" Target=\"footnotes.xml\"/>");
            package.Add("word/footnotes.xml", Xml + $"<w:footnotes {Namespaces}>{options.Footnotes}</w:footnotes>");
        }

        if (options.Endnotes is not null)
        {
            relationships.Append("<Relationship Id=\"rIdEndnotes\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/endnotes\" Target=\"endnotes.xml\"/>");
            package.Add("word/endnotes.xml", Xml + $"<w:endnotes {Namespaces}>{options.Endnotes}</w:endnotes>");
        }

        relationships.Append(options.ExtraRelationships).Append("</Relationships>");
        package.Add("word/_rels/document.xml.rels", relationships.ToString());
        package.Add("word/document.xml", Document(body, options.Prolog));

        package.Add(
            "docProps/core.xml",
            Xml + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" "
            + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" "
            + "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">"
            + (options.Title is null ? string.Empty : $"<dc:title>{Escape(options.Title)}</dc:title>")
            + (options.Author is null ? string.Empty : $"<dc:creator>{Escape(options.Author)}</dc:creator>")
            + "<dcterms:created xsi:type=\"dcterms:W3CDTF\">2024-03-05T10:20:30Z</dcterms:created>"
            + "<dcterms:modified xsi:type=\"dcterms:W3CDTF\">2025-06-07T08:09:10Z</dcterms:modified>"
            + "</cp:coreProperties>");

        package.Add(
            "docProps/app.xml",
            Xml + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\">"
            + (options.Pages is { } pages ? $"<Pages>{pages}</Pages>" : string.Empty)
            + "</Properties>");

        foreach (var (name, bytes) in options.ExtraParts)
        {
            package.Add(name, bytes);
        }

        return package.ToBytes();
    }
}

/// <summary>What goes in a presentation beside its slides.</summary>
internal sealed record PptxOptions
{
    public string? Title { get; init; }

    public string? Author { get; init; }

    /// <summary>The order the slides are listed in the presentation, as numbers of the slide files (1 is <c>slide1.xml</c>); null is file order.</summary>
    public int[]? ListOrder { get; init; }

    /// <summary>Speaker notes by slide file number: the XML of the notes slide's shapes.</summary>
    public IReadOnlyDictionary<int, string> Notes { get; init; } = new Dictionary<int, string>();

    /// <summary>SmartArt data parts by slide file number: the XML of the point list.</summary>
    public IReadOnlyDictionary<int, string> Diagrams { get; init; } = new Dictionary<int, string>();

    /// <summary>Relationship elements added to every slide's relationships.</summary>
    public string ExtraSlideRelationships { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, byte[]> ExtraParts { get; init; } = new Dictionary<string, byte[]>();

    public string ExtraContentTypes { get; init; } = string.Empty;

    /// <summary>Text put between the XML declaration and the document element of each slide.</summary>
    public string Prolog { get; init; } = string.Empty;
}

internal static class PptxFixture
{
    public const string Xml = DocxFixture.Xml;

    private const string Namespaces =
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" "
        + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" "
        + "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" "
        + "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" "
        + "xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"";

    private static string Paragraph(string text) => $"<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>{DocxFixture.Escape(text)}</a:t></a:r></a:p>";

    /// <summary>A shape, in a placeholder when one is named, whose paragraphs are <paramref name="paragraphs"/>.</summary>
    public static string Shape(string? placeholder, params string[] paragraphs) =>
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"S\"/><p:cNvSpPr/><p:nvPr>"
        + (placeholder is null ? string.Empty : $"<p:ph type=\"{placeholder}\"/>")
        + "</p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>"
        + string.Concat(paragraphs.Select(Paragraph))
        + "</p:txBody></p:sp>";

    public static string Title(string text) => Shape("title", text);

    public static string Body(params string[] paragraphs) => Shape("body", paragraphs);

    public static string Group(params string[] shapes) =>
        "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"9\" name=\"G\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" + string.Concat(shapes) + "</p:grpSp>";

    public static string Table(params string[][] rows) =>
        "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"T\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm/>"
        + "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl><a:tblPr/><a:tblGrid/>"
        + string.Concat(rows.Select(row => "<a:tr h=\"0\">" + string.Concat(row.Select(cell =>
            "<a:tc><a:txBody><a:bodyPr/><a:lstStyle/>" + (cell.Length == 0 ? "<a:p/>" : Paragraph(cell)) + "</a:txBody><a:tcPr/></a:tc>")) + "</a:tr>"))
        + "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";

    public static string DiagramFrame(string relationshipId) =>
        "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"5\" name=\"D\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm/>"
        + "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\">"
        + $"<dgm:relIds r:dm=\"{relationshipId}\" r:lo=\"rIdNone\" r:qs=\"rIdNone\" r:cs=\"rIdNone\"/></a:graphicData></a:graphic></p:graphicFrame>";

    public static string Slide(string shapes, string prolog = "", bool hidden = false) =>
        Xml + prolog + $"<p:sld {Namespaces}{(hidden ? " show=\"0\"" : string.Empty)}><p:cSld><p:spTree>"
        + "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>"
        + shapes + "</p:spTree></p:cSld></p:sld>";

    public static string NotesSlide(string shapes) =>
        Xml + $"<p:notes {Namespaces}><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{shapes}</p:spTree></p:cSld></p:notes>";

    public static string DiagramData(params string[] nodeTexts) =>
        Xml + $"<dgm:dataModel {Namespaces}><dgm:ptLst>"
        + "<dgm:pt modelId=\"{00000000-0000-0000-0000-000000000001}\" type=\"doc\"><dgm:prSet/><dgm:spPr/><dgm:t><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang=\"en-US\"/></a:p></dgm:t></dgm:pt>"
        + string.Concat(nodeTexts.Select((text, i) =>
            $"<dgm:pt modelId=\"{{00000000-0000-0000-0000-00000000010{i}}}\"><dgm:prSet/><dgm:spPr/><dgm:t><a:bodyPr/><a:lstStyle/>{Paragraph(text)}</dgm:t></dgm:pt>"))
        + "<dgm:pt modelId=\"{00000000-0000-0000-0000-000000000999}\" type=\"pres\"><dgm:prSet/><dgm:spPr/><dgm:t><a:bodyPr/><a:lstStyle/>"
        + Paragraph("LAYOUT-ONLY-TEXT") + "</dgm:t></dgm:pt>"
        + "</dgm:ptLst><dgm:cxnLst/></dgm:dataModel>";

    /// <summary>A presentation with a slide for each entry of <paramref name="slides"/>: the XML of its shapes.</summary>
    public static byte[] Create(string[] slides, PptxOptions? options = null)
    {
        options ??= new PptxOptions();
        var package = new OfficePackage();

        var contentTypes = new StringBuilder(
            Xml + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
            + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Default Extension=\"bin\" ContentType=\"application/octet-stream\"/>"
            + "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>"
            + "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>");

        package.Add(
            "_rels/.rels",
            Xml + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/>"
            + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>"
            + "</Relationships>");

        var presentationRelationships = new StringBuilder(Xml + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var i = 1; i <= slides.Length; i++)
        {
            presentationRelationships.Append(
                $"<Relationship Id=\"rIdSlide{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide{i}.xml\"/>");
            contentTypes.Append($"<Override PartName=\"/ppt/slides/slide{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
            package.Add($"ppt/slides/slide{i}.xml", slides[i - 1].StartsWith("<?xml", StringComparison.Ordinal) ? slides[i - 1] : Slide(slides[i - 1], options.Prolog));

            var slideRelationships = new StringBuilder(Xml + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            if (options.Notes.TryGetValue(i, out var notes))
            {
                slideRelationships.Append(
                    $"<Relationship Id=\"rIdNotes\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide\" Target=\"../notesSlides/notesSlide{i}.xml\"/>");
                contentTypes.Append($"<Override PartName=\"/ppt/notesSlides/notesSlide{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml\"/>");
                package.Add($"ppt/notesSlides/notesSlide{i}.xml", NotesSlide(notes));
            }

            if (options.Diagrams.TryGetValue(i, out var diagram))
            {
                slideRelationships.Append(
                    $"<Relationship Id=\"rIdDiagram\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/diagramData\" Target=\"../diagrams/data{i}.xml\"/>");
                contentTypes.Append($"<Override PartName=\"/ppt/diagrams/data{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawingml.diagramData+xml\"/>");
                package.Add($"ppt/diagrams/data{i}.xml", diagram);
            }

            slideRelationships.Append(options.ExtraSlideRelationships).Append("</Relationships>");
            package.Add($"ppt/slides/_rels/slide{i}.xml.rels", slideRelationships.ToString());
        }

        presentationRelationships.Append("</Relationships>");
        package.Add("ppt/_rels/presentation.xml.rels", presentationRelationships.ToString());

        var order = options.ListOrder ?? Enumerable.Range(1, slides.Length).ToArray();
        package.Add(
            "ppt/presentation.xml",
            Xml + $"<p:presentation {Namespaces}><p:sldIdLst>"
            + string.Concat(order.Select((n, i) => $"<p:sldId id=\"{256 + i}\" r:id=\"rIdSlide{n}\"/>"))
            + "</p:sldIdLst></p:presentation>");

        package.Add(
            "docProps/core.xml",
            Xml + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" "
            + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" "
            + "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">"
            + (options.Title is null ? string.Empty : $"<dc:title>{DocxFixture.Escape(options.Title)}</dc:title>")
            + (options.Author is null ? string.Empty : $"<dc:creator>{DocxFixture.Escape(options.Author)}</dc:creator>")
            + "</cp:coreProperties>");

        contentTypes.Append(options.ExtraContentTypes).Append("</Types>");
        package.Add("[Content_Types].xml", contentTypes.ToString());

        foreach (var (name, bytes) in options.ExtraParts)
        {
            package.Add(name, bytes);
        }

        return package.ToBytes();
    }
}
