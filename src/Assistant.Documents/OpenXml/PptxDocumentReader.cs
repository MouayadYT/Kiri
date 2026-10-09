using System.Xml;
using Assistant.Core.Documents;
using Assistant.Documents.Extraction;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;

namespace Assistant.Documents.OpenXml;

/// <summary>
/// Reads PowerPoint presentations (<c>.pptx</c>) through the Open XML SDK, which only opens the package and finds its parts: the
/// text is read from the XML itself (<see cref="DrawingTextReader"/>), so nothing in the file is run, opened or followed (no
/// macro, no embedded object, no link), and a document type declaration in any part makes the file
/// <see cref="DocumentReadStatus.Corrupt"/>. Each slide is a <see cref="DocumentLocationKind.Slide"/>, numbered in the order of the
/// presentation (not the order of the files inside the package) and with its title when it has one, and the speaker notes of a slide
/// come right after it as <see cref="DocumentLocationKind.SlideNotes"/>. A slide with no text keeps its number and is left out.
/// A password-protected presentation is <see cref="DocumentReadStatus.Encrypted"/>; <c>.ppt</c> (the older binary format) and
/// macro-enabled <c>.pptm</c> are not read.
/// </summary>
internal sealed class PptxDocumentReader : DocumentReaderBase
{
    public PptxDocumentReader(ILogger<PptxDocumentReader> logger)
        : base(logger)
    {
    }

    public override string Id => "pptx";

    public override string DisplayName => "PowerPoint presentation";

    public override IReadOnlyCollection<string> SupportedExtensions { get; } = [".pptx"];

    public override IReadOnlyCollection<string> SupportedMimeTypes { get; } =
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"];

    protected override DocumentMetadata ReadMetadataCore(Stream stream, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        OpenXmlPackageGuard.Check(stream, limits, cancellationToken);
        using var document = Open(stream);
        var presentation = document.PresentationPart ?? throw new DocumentReadException(DocumentReadStatus.Corrupt);
        return Describe(document, ReadSlideIds(presentation, limits, cancellationToken).Count, limits, cancellationToken);
    }

    protected override ExtractedDocument ReadCore(Stream stream, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        OpenXmlPackageGuard.Check(stream, limits, cancellationToken);
        using var document = Open(stream);
        var presentation = document.PresentationPart ?? throw new DocumentReadException(DocumentReadStatus.Corrupt);
        var slideIds = ReadSlideIds(presentation, limits, cancellationToken);
        var metadata = Describe(document, slideIds.Count, limits, cancellationToken);

        var collector = new SegmentCollector(limits.MaxCharacters);
        for (var index = 0; index < slideIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = index + 1;
            if (number > limits.MaxUnits)
            {
                collector.Stop();
                break;
            }

            if (!presentation.TryGetPartById(slideIds[index], out var part) || part is not SlidePart slide)
            {
                continue;
            }

            DrawingText text;
            using (var xml = OpenPart(slide, limits))
            {
                text = DrawingTextReader.ReadSlide(xml, id => ReadDiagram(slide, id, limits, cancellationToken), limits.MaxCharacters, cancellationToken);
            }

            collector.Add(DocumentLocation.ForSlide(number, text.Title), text.Text);

            if (slide.NotesSlidePart is { } notesPart)
            {
                using var xml = OpenPart(notesPart, limits);
                collector.Add(DocumentLocation.ForSlideNotes(number), DrawingTextReader.ReadNotes(xml, limits.MaxCharacters, cancellationToken));
            }

            if (collector.Truncated)
            {
                break;
            }
        }

        return new ExtractedDocument(metadata, collector.Segments, collector.Truncated);
    }

    // What a presentation's main part may be: a presentation, a slide show or a template, with or without macros (a macro-enabled one
    // renamed to .pptx is read like any other: its macros are never touched).
    private static readonly HashSet<string> MainContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
        "application/vnd.openxmlformats-officedocument.presentationml.slideshow.main+xml",
        "application/vnd.openxmlformats-officedocument.presentationml.template.main+xml",
        "application/vnd.ms-powerpoint.presentation.macroEnabled.main+xml",
        "application/vnd.ms-powerpoint.slideshow.macroEnabled.main+xml",
        "application/vnd.ms-powerpoint.template.macroEnabled.main+xml",
    };

    // A package of another kind (a Word document under a .pptx name) opens without complaint and has no slides to a presentation
    // reader: it is not a presentation, and is said to be damaged, not empty.
    private static PresentationDocument Open(Stream stream)
    {
        var document = PresentationDocument.Open(stream, isEditable: false, new OpenSettings { AutoSave = false });
        if (document.PresentationPart is not { } main || !MainContentTypes.Contains(main.ContentType))
        {
            document.Dispose();
            throw new DocumentReadException(DocumentReadStatus.Corrupt);
        }

        return document;
    }

    private static XmlReader OpenPart(OpenXmlPart part, DocumentReadLimits limits) =>
        SafeXml.Create(part.GetStream(FileMode.Open, FileAccess.Read), limits.MaxExpandedBytes);

    private static DocumentMetadata Describe(PresentationDocument document, int slides, DocumentReadLimits limits, CancellationToken cancellationToken) =>
        PackageMetadata.Read(document, limits, cancellationToken) with { UnitKind = DocumentUnitKind.Slide, UnitCount = slides };

    // The slides in the order of the presentation: the relationship ids of the slide list in presentation.xml.
    private static List<string> ReadSlideIds(PresentationPart presentation, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        using var xml = OpenPart(presentation, limits);
        while (!xml.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeXml.CheckDepth(xml);

            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "sldId" && Ooxml.IsPresentation(xml.NamespaceURI))
            {
                var id = RelationshipId(xml);
                if (id is not null)
                {
                    ids.Add(id);
                }
            }

            xml.Read();
        }

        return ids;
    }

    private static string? RelationshipId(XmlReader xml)
    {
        if (!xml.MoveToFirstAttribute())
        {
            return null;
        }

        string? id = null;
        do
        {
            if (xml.LocalName == "id" && Ooxml.IsRelationships(xml.NamespaceURI))
            {
                id = xml.Value;
                break;
            }
        }
        while (xml.MoveToNextAttribute());

        xml.MoveToElement();
        return id;
    }

    private static string? ReadDiagram(SlidePart slide, string relationshipId, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        if (!slide.TryGetPartById(relationshipId, out var part) || part is not DiagramDataPart data)
        {
            return null;
        }

        using var xml = OpenPart(data, limits);
        return DrawingTextReader.ReadDiagram(xml, limits.MaxCharacters, cancellationToken);
    }
}
