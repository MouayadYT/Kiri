using System.Globalization;
using System.Xml;
using Assistant.Core.Documents;
using Assistant.Documents.Extraction;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;

namespace Assistant.Documents.OpenXml;

/// <summary>
/// Reads Word documents (<c>.docx</c>) through the Open XML SDK, which only opens the package and finds its parts: the text is
/// read from the XML itself (<see cref="WordBodyReader"/>), so nothing in the file is run, opened or followed (no macro, no
/// embedded object, no link, no field's command), and a document type declaration in any part makes the file
/// <see cref="DocumentReadStatus.Corrupt"/> rather than expanding an entity. A Word document has no fixed pages, so its text is cut
/// into <see cref="DocumentLocationKind.Section"/>s by its headings (paragraphs in a heading style or with an outline level), in
/// the order of the document, followed by its footnotes and endnotes; a document with no heading is one piece. A password-protected
/// document is <see cref="DocumentReadStatus.Encrypted"/>; <c>.doc</c> (the older binary format) and macro-enabled <c>.docm</c>
/// are not read.
/// </summary>
internal sealed class DocxDocumentReader : DocumentReaderBase
{
    private const string NotesTitleFootnotes = "Footnotes";
    private const string NotesTitleEndnotes = "Endnotes";

    public DocxDocumentReader(ILogger<DocxDocumentReader> logger)
        : base(logger)
    {
    }

    public override string Id => "docx";

    public override string DisplayName => "Word document";

    public override IReadOnlyCollection<string> SupportedExtensions { get; } = [".docx"];

    public override IReadOnlyCollection<string> SupportedMimeTypes { get; } =
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"];

    protected override DocumentMetadata ReadMetadataCore(Stream stream, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        OpenXmlPackageGuard.Check(stream, limits, cancellationToken);
        using var document = Open(stream);
        return Describe(document, limits, cancellationToken);
    }

    protected override ExtractedDocument ReadCore(Stream stream, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        OpenXmlPackageGuard.Check(stream, limits, cancellationToken);
        using var document = Open(stream);
        var metadata = Describe(document, limits, cancellationToken);
        var main = document.MainDocumentPart ?? throw new DocumentReadException(DocumentReadStatus.Corrupt);

        var styles = WordStyleMap.Empty;
        if (main.StyleDefinitionsPart is { } stylesPart)
        {
            using var xml = OpenPart(stylesPart, limits);
            styles = WordStyleMap.Read(xml, cancellationToken);
        }

        var reader = new WordBodyReader(styles, limits.MaxCharacters, limits.MaxUnits, cancellationToken);
        using (var xml = OpenPart(main, limits))
        {
            reader.ReadBody(xml);
        }

        if (main.FootnotesPart is { } footnotes)
        {
            using var xml = OpenPart(footnotes, limits);
            reader.ReadNotes(xml, NotesTitleFootnotes);
        }

        if (main.EndnotesPart is { } endnotes)
        {
            using var xml = OpenPart(endnotes, limits);
            reader.ReadNotes(xml, NotesTitleEndnotes);
        }

        var collector = new SegmentCollector(limits.MaxCharacters);
        reader.WriteTo(collector);
        return new ExtractedDocument(metadata, collector.Segments, collector.Truncated);
    }

    // What a Word document's main part may be: a document or a template, with or without macros (a macro-enabled one renamed to
    // .docx is read like any other: its macros are never touched).
    private static readonly HashSet<string> MainContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml",
        "application/vnd.ms-word.document.macroEnabled.main+xml",
        "application/vnd.ms-word.template.macroEnabledTemplate.main+xml",
    };

    // A package of another kind (a presentation under a .docx name) opens without complaint and has no text to a Word reader: it is
    // not a Word document, and is said to be damaged, not empty.
    private static WordprocessingDocument Open(Stream stream)
    {
        var document = WordprocessingDocument.Open(stream, isEditable: false, new OpenSettings { AutoSave = false });
        if (document.MainDocumentPart is not { } main || !MainContentTypes.Contains(main.ContentType))
        {
            document.Dispose();
            throw new DocumentReadException(DocumentReadStatus.Corrupt);
        }

        return document;
    }

    private static XmlReader OpenPart(OpenXmlPart part, DocumentReadLimits limits) =>
        SafeXml.Create(part.GetStream(FileMode.Open, FileAccess.Read), limits.MaxExpandedBytes);

    private static DocumentMetadata Describe(WordprocessingDocument document, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        var properties = PackageMetadata.Read(document, limits, cancellationToken);
        var pages = ReadPageCount(document, limits, cancellationToken);
        return properties with
        {
            UnitKind = pages is null ? DocumentUnitKind.None : DocumentUnitKind.Page,
            UnitCount = pages,
        };
    }

    // The page count Word saved with the document (docProps/app.xml): a reader cannot lay the pages out to check it.
    private static int? ReadPageCount(WordprocessingDocument document, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        if (document.ExtendedFilePropertiesPart is not { } part)
        {
            return null;
        }

        using var xml = OpenPart(part, limits);
        while (!xml.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeXml.CheckDepth(xml);
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "Pages" && Ooxml.IsExtendedProperties(xml.NamespaceURI))
            {
                var text = xml.ReadElementContentAsString();
                return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pages) && pages > 0 ? pages : null;
            }

            xml.Read();
        }

        return null;
    }
}
