using System.Text;
using Assistant.Core.Documents;
using Assistant.Documents.Extraction;
using Assistant.Documents.Text;
using Microsoft.Extensions.Logging;

namespace Assistant.Documents.Web;

/// <summary>
/// Reads saved web pages: <c>.html</c> and <c>.htm</c> files, and single-file archives a browser saves (<c>.mhtml</c>, <c>.mht</c>).
/// Only the text a person reads on the page is taken (<see cref="HtmlTextExtractor"/>), as Markdown-like text, and it is cut into
/// one <see cref="DocumentLocationKind.Section"/> for each heading, each with its heading trail; a page with no heading is one
/// piece. The page's own title is the document's title. A page is never rendered, run or fetched from, and an archive's pictures,
/// scripts and styles are never decoded (<see cref="MimeHtmlExtractor"/>). The lines of the page are not the lines of the text
/// made from it, so no lines are recorded.
/// </summary>
internal sealed class WebPageDocumentReader : DocumentReaderBase
{
    // Text of a page is about this much of the characters the page's markup takes: the markup is read up to this many characters.
    private const int MarkupFactor = 6;

    public WebPageDocumentReader(ILogger<WebPageDocumentReader> logger)
        : base(logger)
    {
    }

    public override string Id => "web";

    public override string DisplayName => "Web page";

    public override IReadOnlyCollection<string> SupportedExtensions { get; } = [".html", ".htm", ".mhtml", ".mht"];

    public override IReadOnlyCollection<string> SupportedMimeTypes { get; } = ["text/html", "application/xhtml+xml", "multipart/related", "message/rfc822"];

    protected override DocumentMetadata ReadMetadataCore(Stream stream, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        var (_, title, _) = ReadPage(stream, limits, cancellationToken, maxText: 64 * 1024);
        return new DocumentMetadata { Title = title };
    }

    protected override ExtractedDocument ReadCore(Stream stream, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        var (text, title, cut) = ReadPage(stream, limits, cancellationToken, limits.MaxCharacters);
        var collector = new SegmentCollector(limits.MaxCharacters);
        if (!MarkdownSectionSplitter.HasHeadings(text))
        {
            collector.Add(DocumentLocation.WholeDocument(), text);
        }
        else
        {
            var number = 0;
            foreach (var section in MarkdownSectionSplitter.Split(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(section.Text))
                {
                    continue;
                }

                number++;
                if (number > limits.MaxUnits)
                {
                    collector.Stop();
                    break;
                }

                collector.Add(DocumentLocation.ForSection(number, section.Heading), section.Text);
                if (collector.Truncated)
                {
                    break;
                }
            }
        }

        return new ExtractedDocument(new DocumentMetadata { Title = title }, collector.Segments, collector.Truncated || cut);
    }

    // The text of the page in the file, its title, and whether there was more than was read.
    private static (string Text, string? Title, bool Cut) ReadPage(
        Stream stream, DocumentReadLimits limits, CancellationToken cancellationToken, int maxText)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var head = new byte[Math.Min(4096, (int)Math.Min(stream.Length, int.MaxValue))];
        var read = stream.Read(head, 0, head.Length);
        stream.Seek(0, SeekOrigin.Begin);

        // A web archive is a MIME message; the page inside it is found, and its text made.
        if (LooksLikeArchive(head.AsSpan(0, read)))
        {
            if (stream.Length > int.MaxValue / 2)
            {
                throw new DocumentReadException(DocumentReadStatus.TooLarge);
            }

            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            var (parts, isHtml) = MimeHtmlExtractor.Find(Encoding.Latin1.GetString(bytes), cancellationToken);
            if (parts.Count == 0)
            {
                throw new DocumentReadException(DocumentReadStatus.Unsupported);
            }

            if (!isHtml)
            {
                return (parts[0].Length > maxText ? parts[0][..maxText] : parts[0], null, parts[0].Length > maxText);
            }

            return Join(parts, maxText, cancellationToken);
        }

        var markup = TextFileDecoder.Read(stream, (int)Math.Min((long)limits.MaxCharacters * MarkupFactor, 64L * 1024 * 1024), cancellationToken, out var markupCut);
        var (text, title, cut) = ToText(markup, maxText, cancellationToken);
        return (text, title, cut || markupCut);
    }

    // The text of each page of an archive (the page, then the frames in it), the title of the first, and each part's text once.
    private static (string Text, string? Title, bool Cut) Join(IReadOnlyList<string> pages, int maxText, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        string? title = null;
        var cut = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            var (part, pageTitle, partCut) = ToText(page, Math.Max(0, maxText - text.Length), cancellationToken);
            title ??= pageTitle;
            cut |= partCut;
            if (part.Length == 0 || !seen.Add(part))
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append('\n').Append('\n');
            }

            text.Append(part);
            if (text.Length >= maxText)
            {
                cut = true;
                break;
            }
        }

        return (text.ToString(), title, cut);
    }

    private static (string Text, string? Title, bool Cut) ToText(string html, int maxText, CancellationToken cancellationToken)
    {
        var text = HtmlTextExtractor.Extract(html, maxText, cancellationToken, out var title, out var cut);
        return (text, title, cut);
    }

    // A MIME message starts with headers (a line "Name: value"), and has a "MIME-Version" or a multipart "Content-Type" among them.
    private static bool LooksLikeArchive(ReadOnlySpan<byte> head)
    {
        var text = Encoding.Latin1.GetString(head);
        if (text.TrimStart().StartsWith('<'))
        {
            return false;
        }

        return text.Contains("MIME-Version:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Content-Type: multipart/", StringComparison.OrdinalIgnoreCase);
    }
}
