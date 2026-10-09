using Assistant.Core.Documents;
using Assistant.Documents.Extraction;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Util;

namespace Assistant.Documents.Pdf;

/// <summary>
/// Reads PDF files with PdfPig, which parses the file itself and runs nothing in it (no script, no launch action, no embedded
/// file is opened). Each page is one <see cref="DocumentLocationKind.Page"/>, numbered as the PDF numbers them from 1 (a page
/// with no text keeps its number and is left out), in the reading order the page's text is laid out in. A file that needs a
/// password is <see cref="DocumentReadStatus.Encrypted"/>; one with pages and no text at all (a scan) is
/// <see cref="DocumentReadStatus.NoText"/>, since the Assistant does not read pictures of text here.
/// </summary>
internal sealed class PdfDocumentReader : DocumentReaderBase
{
    private const int MaxMetadataLength = 200;

    public PdfDocumentReader(ILogger<PdfDocumentReader> logger)
        : base(logger)
    {
    }

    public override string Id => "pdf";

    public override string DisplayName => "PDF document";

    public override IReadOnlyCollection<string> SupportedExtensions { get; } = [".pdf"];

    public override IReadOnlyCollection<string> SupportedMimeTypes { get; } = ["application/pdf"];

    protected override DocumentMetadata ReadMetadataCore(Stream stream, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        using var document = Open(stream);
        return Describe(document);
    }

    protected override ExtractedDocument ReadCore(Stream stream, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        using var document = Open(stream);
        var metadata = Describe(document);
        var collector = new SegmentCollector(limits.MaxCharacters);

        var unreadable = 0;
        string? failure = null;
        for (var number = 1; number <= document.NumberOfPages; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (number > limits.MaxUnits)
            {
                collector.Stop();
                break;
            }

            string text;
            try
            {
                text = ExpandLigatures(ContentOrderTextExtractor.GetText(document.GetPage(number)));
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or DocumentReadException))
            {
                // One page of a real PDF can be beyond the parser (a malformed page size, a broken font): the pages that read are
                // still the document's text, and the result says it is not all of it.
                unreadable++;
                failure = ex.GetType().FullName;
                continue;
            }

            collector.Add(DocumentLocation.ForPage(number), text);
            if (collector.Truncated)
            {
                break;
            }
        }

        if (unreadable > 0)
        {
            DocumentsLog.UnitsSkipped(Logger, Id, unreadable, failure ?? string.Empty);
            if (collector.Segments.Count == 0)
            {
                throw new DocumentReadException(DocumentReadStatus.Corrupt);
            }
        }

        return new ExtractedDocument(metadata, collector.Segments, collector.Truncated || unreadable > 0);
    }

    private static PdfDocument Open(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var options = new ParsingOptions
        {
            UseLenientParsing = true,
            SkipMissingFonts = true,
            ClipPaths = false,
        };

        try
        {
            return PdfDocument.Open(stream, options);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new DocumentReadException(DocumentReadStatus.Encrypted);
        }
    }

    private static DocumentMetadata Describe(PdfDocument document)
    {
        var information = document.Information;
        return new DocumentMetadata
        {
            Title = Property(information.Title),
            Author = Property(information.Author),
            UnitKind = DocumentUnitKind.Page,
            UnitCount = document.NumberOfPages,
            CreatedAt = Date(information.CreationDate),
            ModifiedAt = Date(information.ModifiedDate),
        };
    }

    private static string? Property(string? value)
    {
        var clean = TextCleaner.Clean(value).Replace('\n', ' ');
        return clean.Length == 0 ? null : clean.Length <= MaxMetadataLength ? clean : clean[..MaxMetadataLength];
    }

    private static DateTimeOffset? Date(string? value) =>
        !string.IsNullOrWhiteSpace(value) && DateFormatHelper.TryParseDateTimeOffset(value, out var date) ? date : null;

    // Some PDFs carry a ligature as one character (the single-character "fi" or "fl"): a person reads "file", and a search for it
    // must find it. Written as numbers, since the characters themselves are not readable in the source.
    private static readonly (char Ligature, string Letters)[] Ligatures =
    [
        ((char)0xFB00, "ff"),
        ((char)0xFB01, "fi"),
        ((char)0xFB02, "fl"),
        ((char)0xFB03, "ffi"),
        ((char)0xFB04, "ffl"),
        ((char)0xFB05, "st"),
        ((char)0xFB06, "st"),
    ];

    private static string ExpandLigatures(string text)
    {
        if (text.AsSpan().IndexOfAnyInRange((char)0xFB00, (char)0xFB06) < 0)
        {
            return text;
        }

        foreach (var (ligature, letters) in Ligatures)
        {
            text = text.Replace(ligature.ToString(), letters, StringComparison.Ordinal);
        }

        return text;
    }
}
