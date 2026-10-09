using Assistant.Core.Documents;
using Assistant.Documents.Extraction;
using Microsoft.Extensions.Logging;

namespace Assistant.Documents.Text;

/// <summary>
/// Reads Markdown files (<c>.md</c>). The text is kept as written, Markdown markers and all, and cut into one
/// <see cref="DocumentLocationKind.Section"/> for each heading (<see cref="MarkdownSectionSplitter"/>), each with its heading
/// trail and the lines it is on. A file with no heading is one piece, like a text file.
/// </summary>
internal sealed class MarkdownDocumentReader : DocumentReaderBase
{
    public MarkdownDocumentReader(ILogger<MarkdownDocumentReader> logger)
        : base(logger)
    {
    }

    public override string Id => "markdown";

    public override string DisplayName => "Markdown document";

    public override IReadOnlyCollection<string> SupportedExtensions { get; } = [".md", ".markdown"];

    public override IReadOnlyCollection<string> SupportedMimeTypes { get; } = ["text/markdown", "text/x-markdown"];

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
        if (!MarkdownSectionSplitter.HasHeadings(text))
        {
            if (LineSpan.Of(text, 1) is { } lines)
            {
                collector.Add(DocumentLocation.WholeDocument(lines.First, lines.Last), text);
            }
        }
        else
        {
            var number = 0;
            foreach (var section in MarkdownSectionSplitter.Split(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (LineSpan.Of(section.Text, section.FirstLine) is not { } lines)
                {
                    continue;
                }

                number++;
                if (number > limits.MaxUnits)
                {
                    collector.Stop();
                    break;
                }

                collector.Add(DocumentLocation.ForSection(number, section.Heading, lines.First, lines.Last), section.Text);
                if (collector.Truncated)
                {
                    break;
                }
            }
        }

        return new ExtractedDocument(new DocumentMetadata(), collector.Segments, collector.Truncated || cut);
    }
}
