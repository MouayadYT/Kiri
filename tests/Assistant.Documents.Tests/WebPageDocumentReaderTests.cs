using System.Text;
using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Documents.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Documents.Tests;

/// <summary>Reading saved web pages (<c>.html</c>) and single-file web archives (<c>.mhtml</c>): their text, and nothing else.</summary>
public sealed class WebPageDocumentReaderTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly WebPageDocumentReader _reader = new(NullLogger<WebPageDocumentReader>.Instance);

    public void Dispose() => _folder.Dispose();

    private static string Page(string body, string title = "A saved page") =>
        $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>{title}</title><style>body {{ color: red; }}</style>"
        + $"<script>var secret = 'never read';</script></head><body>{body}</body></html>";

    [Fact]
    public void TheReaderSaysWhatItHandles()
    {
        Assert.Equal("web", _reader.Id);
        Assert.Equal([".html", ".htm", ".mhtml", ".mht"], _reader.SupportedExtensions);
        Assert.Contains("text/html", _reader.SupportedMimeTypes);
        Assert.All(_reader.SupportedExtensions, extension => Assert.True(DocumentFileTypes.IsDocumentExtension(extension)));
    }

    [Fact]
    public async Task APageIsReadAsTheTextAPersonSeesOnIt_WithItsHeadingsAsSections()
    {
        var path = _folder.Write("page.html", Page(
            "<h1>Colonial trade</h1><p>Plymouth &amp; the Wampanoag <b>traded</b> furs.</p>"
            + "<h2>Goods</h2><ul><li>Beaver pelts</li><li>Corn</li></ul>"
            + "<h2>Tables</h2><table><tr><th>Item</th><th>Value</th></tr><tr><td>Pelt</td><td>3</td></tr></table>"
            + "<script>alert('x')</script><!-- a comment -->"));

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Equal("A saved page", result.Metadata!.Title);
        Assert.Equal(3, result.Segments.Count);
        Assert.All(result.Segments, segment => Assert.Equal(DocumentLocationKind.Section, segment.Location.Kind));
        Assert.Equal(["Colonial trade", "Colonial trade > Goods", "Colonial trade > Tables"], result.Segments.Select(segment => segment.Location.Label));
        Assert.Equal("# Colonial trade\n\nPlymouth & the Wampanoag traded furs.", result.Segments[0].Text);
        Assert.Equal("## Goods\n\n- Beaver pelts\n- Corn", result.Segments[1].Text);
        Assert.Contains("Item | Value", result.Segments[2].Text, StringComparison.Ordinal);
        Assert.Contains("Pelt | 3", result.Segments[2].Text, StringComparison.Ordinal);

        // Nothing of the scripts, the styles or the comments is text.
        var all = string.Join("\n", result.Segments.Select(segment => segment.Text));
        Assert.DoesNotContain("secret", all, StringComparison.Ordinal);
        Assert.DoesNotContain("color: red", all, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", all, StringComparison.Ordinal);
        Assert.DoesNotContain("comment", all, StringComparison.Ordinal);
        Assert.All(result.Segments, segment => Assert.Null(segment.Location.FirstLine));
    }

    [Fact]
    public async Task APageWithNoHeadingIsOnePiece_AndALessThanThatIsNotATagIsKept()
    {
        var path = _folder.Write("plain.htm", "<html><body><p>If a < b then b > a.</p><p>Second<br>line.</p></body></html>");

        var result = await _reader.ReadAsync(path);

        var segment = Assert.Single(result.Segments);
        Assert.Equal(DocumentLocationKind.Document, segment.Location.Kind);
        Assert.Equal("If a < b then b > a.\n\nSecond\nline.", segment.Text);
    }

    [Fact]
    public async Task APageThatIsOnlyScriptsHasNoText()
    {
        var path = _folder.Write("empty.html", "<html><head><title>x</title></head><body><script>run()</script></body></html>");

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.NoText, result.Status);
    }

    [Fact]
    public async Task AQuoteInsideATagDoesNotEndItEarly_AndAnUnclosedScriptSwallowsTheRest()
    {
        var path = _folder.Write("odd.html", "<p title=\"a > b\">Visible</p><script>hidden text never closed");

        var result = await _reader.ReadAsync(path);

        Assert.Equal("Visible", Assert.Single(result.Segments).Text);
    }

    private const string Archive = """
        From: <Saved by Blink>
        Snapshot-Content-Location: https://example.test/milestone
        Subject: =?utf-8?Q?Milestone_Three?=
        MIME-Version: 1.0
        Content-Type: multipart/related;
        	type="text/html";
        	boundary="----MultipartBoundary--abc123----"

        ------MultipartBoundary--abc123----
        Content-Type: text/html
        Content-ID: <frame-1@mhtml.blink>
        Content-Transfer-Encoding: quoted-printable
        Content-Location: https://example.test/milestone

        <!DOCTYPE html><html><head><title>Milestone Three Guidelines</title></head><body>
        <h1 class=3D"title">Milestone Three</h1><p>Students must analyze the colonists=E2=80=99 =
        use of primary sources, due in week 6.</p><h2>Rubric</h2><p>Thesis: 25 points.</p>
        </body></html>

        ------MultipartBoundary--abc123----
        Content-Type: image/png
        Content-Transfer-Encoding: base64
        Content-Location: https://example.test/logo.png

        iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==

        ------MultipartBoundary--abc123------
        """;

    [Fact]
    public async Task AWebArchiveGivesThePageInsideIt_WithItsEncodingUndone_AndNothingOfItsPictures()
    {
        var path = _folder.Write("Milestone Three.mhtml", Archive.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Equal("Milestone Three Guidelines", result.Metadata!.Title);
        Assert.Equal(".mhtml", result.Metadata!.Extension);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal("Milestone Three", result.Segments[0].Location.Label);
        Assert.Equal("# Milestone Three\n\nStudents must analyze the colonists’ use of primary sources, due in week 6.", result.Segments[0].Text);
        Assert.Equal("## Rubric\n\nThesis: 25 points.", result.Segments[1].Text);
        Assert.DoesNotContain("iVBOR", string.Join("", result.Segments.Select(segment => segment.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArchiveWithTheSameLinesEndingInLineFeedsOnlyIsReadToo()
    {
        var path = _folder.Write("lf.mht", Archive.Replace("\r\n", "\n", StringComparison.Ordinal));

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Equal(2, result.Segments.Count);
    }

    [Fact]
    public async Task TheFramesOfAnArchivedPageAreReadAfterIt_EachTextOnce()
    {
        var archive = string.Join(
            "\r\n",
            "MIME-Version: 1.0", "Content-Type: multipart/related; boundary=\"b\"", "",
            "--b", "Content-Type: text/html", "", "<h1>Course page</h1><p>Menu</p><iframe src=\"cid:frame\"></iframe>",
            "--b", "Content-Type: text/html", "", "<h1>Assignment</h1><p>Write four pages on trade.</p>",
            "--b", "Content-Type: text/html", "", "<h1>Assignment</h1><p>Write four pages on trade.</p>",
            "--b--", "");
        var path = _folder.Write("frames.mhtml", archive);

        var result = await _reader.ReadAsync(path);

        Assert.Equal(["Course page", "Assignment"], result.Segments.Select(segment => segment.Location.Label));
        Assert.Contains("Write four pages on trade.", result.Segments[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArchivesPageInBase64AndAnotherCharacterSetIsDecodedAsThatSet()
    {
        var html = "<html><body><p>Café menu</p></body></html>";
        // The test makes its own Windows-1252 bytes, so it asks for the code pages itself and does not rely on a reader having done so.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var base64 = Convert.ToBase64String(Encoding.GetEncoding(1252).GetBytes(html), Base64FormattingOptions.InsertLineBreaks);
        var archive = "MIME-Version: 1.0\r\nContent-Type: multipart/alternative; boundary=\"b1\"\r\n\r\n--b1\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n\r\nPlain version is second choice\r\n--b1\r\n"
            + "Content-Type: text/html; charset=windows-1252\r\nContent-Transfer-Encoding: base64\r\n\r\n" + base64 + "\r\n--b1--\r\n";
        var path = _folder.Write("alt.mhtml", archive);

        var result = await _reader.ReadAsync(path);

        Assert.Equal("Café menu", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task AnArchiveWithNestedPartsFindsThePageThroughThem()
    {
        var archive = "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"outer\"\r\n\r\n--outer\r\n"
            + "Content-Type: multipart/alternative; boundary=\"inner\"\r\n\r\n--inner\r\nContent-Type: text/html\r\n\r\n"
            + "<p>Deep page</p>\r\n--inner--\r\n--outer\r\nContent-Type: application/pdf\r\nContent-Transfer-Encoding: base64\r\n\r\nJVBERi0=\r\n--outer--\r\n";
        var path = _folder.Write("nested.mhtml", archive);

        var result = await _reader.ReadAsync(path);

        Assert.Equal("Deep page", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task AnArchiveWithNoPageInItIsNotSupported()
    {
        var archive = "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"b\"\r\n\r\n--b\r\nContent-Type: image/png\r\n"
            + "Content-Transfer-Encoding: base64\r\n\r\niVBORw0KGgo=\r\n--b--\r\n";
        var path = _folder.Write("pictures.mhtml", archive);

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Unsupported, result.Status);
        Assert.Empty(result.Segments);
    }

    [Fact]
    public async Task APlainHtmlPageSavedWithAnArchiveExtensionIsStillRead()
    {
        var path = _folder.Write("not-an-archive.mhtml", Page("<p>Just a page</p>"));

        var result = await _reader.ReadAsync(path);

        Assert.Equal("Just a page", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task BinaryDataUnderAPageNameIsNotSupported()
    {
        var path = _folder.WriteBytes("broken.html", [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 1, 2, 3, 0, 0, 0xFF]);

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Unsupported, result.Status);
    }

    [Fact]
    public async Task ALongPageIsCutAtTheCharacterLimit_AndSaysSo()
    {
        var path = _folder.Write("long.html", Page(string.Concat(Enumerable.Range(1, 400).Select(n => $"<p>Paragraph number {n} says something.</p>"))));

        var result = await _reader.ReadAsync(path, new DocumentReadOptions { MaxCharacters = 2_000 });

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.True(result.Truncated);
        Assert.InRange(result.CharacterCount, 1, 2_000);
    }

    [Fact]
    public async Task ReadingCanBeCancelled()
    {
        var path = _folder.Write("page.html", Page("<p>Text</p>"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadAsync(path, cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task ThePagesMetadataIsItsTitle_AndAMissingFileIsNotFound()
    {
        var path = _folder.Write("page.html", Page("<p>Text</p>", title: "Colonial &amp; Native trade"));

        var metadata = await _reader.ReadMetadataAsync(path);
        var missing = await _reader.ReadAsync(_folder.File("nowhere.html"));

        Assert.Equal("Colonial & Native trade", metadata.Metadata!.Title);
        Assert.Equal(DocumentReadStatus.NotFound, missing.Status);
    }
}
