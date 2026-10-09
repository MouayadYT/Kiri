using Assistant.Core.Documents;
using Assistant.Documents.Extraction;
using Microsoft.Extensions.Logging;

namespace Assistant.Documents.Text;

/// <summary>
/// Reads <c>.txt</c> files, and the plain text kept under other names (<c>.csv</c>, <c>.json</c>, <c>.log</c> and the like). A text file has no parts of its own, so its text is one piece with the lines it is on, except that
/// a form feed, which plain text uses to end a page, ends a <see cref="DocumentLocationKind.Page"/> here too. Text in any
/// encoding a byte order mark or the content itself shows (<see cref="TextFileDecoder"/>); a file that is binary data under a
/// text name is <see cref="DocumentReadStatus.Unsupported"/>.
/// </summary>
internal sealed class PlainTextDocumentReader : DocumentReaderBase
{
    public PlainTextDocumentReader(ILogger<PlainTextDocumentReader> logger)
        : base(logger)
    {
    }

    public override string Id => "text";

    public override string DisplayName => "Text file";

    // Plain text under other names: tables, data and logs a person may ask about as they are.
    public override IReadOnlyCollection<string> SupportedExtensions { get; } =
        [".txt", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".log", ".ini", ".toml"];

    public override IReadOnlyCollection<string> SupportedMimeTypes { get; } =
        ["text/plain", "text/csv", "text/tab-separated-values", "application/json", "text/xml", "application/xml", "text/yaml", "application/x-yaml"];

    protected override DocumentMetadata ReadMetadataCore(Stream stream, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        TextFileDecoder.Sniff(stream);
        return new DocumentMetadata();
    }

    protected override ExtractedDocument ReadCore(Stream stream, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        var raw = TextFileDecoder.Read(stream, limits.MaxCharacters, cancellationToken, out var cut);
        var text = NewLines.Normalize(raw);

        var collector = new SegmentCollector(limits.MaxCharacters);
        if (text.Contains('\f'))
        {
            var pageNumber = 0;
            var line = 1;
            foreach (var page in text.Split('\f'))
            {
                cancellationToken.ThrowIfCancellationRequested();
                pageNumber++;
                if (pageNumber > limits.MaxUnits)
                {
                    collector.Stop();
                    break;
                }

                if (LineSpan.Of(page, line) is { } lines)
                {
                    collector.Add(DocumentLocation.ForPage(pageNumber, lines.First, lines.Last), page);
                }

                line += LineSpan.CountBreaks(page);
                if (collector.Truncated)
                {
                    break;
                }
            }
        }
        else if (LineSpan.Of(text, 1) is { } lines)
        {
            collector.Add(DocumentLocation.WholeDocument(lines.First, lines.Last), text);
        }

        return new ExtractedDocument(new DocumentMetadata(), collector.Segments, collector.Truncated || cut);
    }
}
