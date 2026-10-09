using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Documents.OpenXml;
using Assistant.Documents.Pdf;
using Assistant.Documents.Text;
using Xunit;

namespace Assistant.Documents.Tests;

/// <summary>A document's text, title, name and place are private (PROJECT_SPEC §3.2, §3.3): not in a log, not in a ToString.</summary>
public sealed class DocumentPrivacyTests : IDisposable
{
    private const string SecretText = "PRIVATE-BODY-7731";
    private const string SecretTitle = "PRIVATE-TITLE-7731";
    private const string SecretName = "private-name-7731";

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task NoReaderLogsAPathANameATitleOrAnyText()
    {
        var text = new CapturingLogger<PlainTextDocumentReader>();
        var markdown = new CapturingLogger<MarkdownDocumentReader>();
        var pdf = new CapturingLogger<PdfDocumentReader>();
        var docx = new CapturingLogger<DocxDocumentReader>();
        var pptx = new CapturingLogger<PptxDocumentReader>();

        var files = new (IDocumentReader Reader, string Path)[]
        {
            (new PlainTextDocumentReader(text), _folder.Write(SecretName + ".txt", SecretText)),
            (new MarkdownDocumentReader(markdown), _folder.Write(SecretName + ".md", "# " + SecretTitle + "\n" + SecretText)),
            (new PdfDocumentReader(pdf), _folder.WriteBytes(SecretName + ".pdf", PdfBuilder.Create([[SecretText]], SecretTitle, SecretTitle))),
            (new DocxDocumentReader(docx), _folder.WriteBytes(SecretName + ".docx", DocxFixture.Create(DocxFixture.P(SecretText), new DocxOptions { Title = SecretTitle, Author = SecretTitle }))),
            (new PptxDocumentReader(pptx), _folder.WriteBytes(SecretName + ".pptx", PptxFixture.Create([PptxFixture.Title(SecretTitle) + PptxFixture.Body(SecretText)], new PptxOptions { Title = SecretTitle }))),
        };

        foreach (var (reader, path) in files)
        {
            Assert.Equal(DocumentReadStatus.Success, (await reader.ReadAsync(path)).Status);
            Assert.Equal(DocumentReadStatus.Success, (await reader.ReadMetadataAsync(path)).Status);

            // The same readers on files that are not what their names say, and on files that are not there.
            var broken = _folder.Write(SecretName + "-broken" + Path.GetExtension(path), SecretText + SecretText);
            await reader.ReadAsync(broken);
            await reader.ReadAsync(_folder.File(SecretName + "-missing" + Path.GetExtension(path)));
        }

        var entries = text.Entries.Concat(markdown.Entries).Concat(pdf.Entries).Concat(docx.Entries).Concat(pptx.Entries).ToArray();
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            Assert.DoesNotContain("7731", entry);
            Assert.DoesNotContain(_folder.Path, entry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("assistant-documents-tests", entry, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheRegistryLogsOnlyTheExtensionOfAFileItCannotRead()
    {
        var logger = new CapturingLogger<DocumentReaderRegistry>();
        var registry = new DocumentReaderRegistry([], logger);

        registry.GetReader(_folder.File(SecretName + ".zzqq"));
        registry.GetReader(_folder.File(SecretName + ".this-is-not-an-extension-just-a-long-name"));

        Assert.Equal(2, logger.Entries.Count);
        Assert.Contains(".zzqq", logger.Entries[0]);
        Assert.All(logger.Entries, entry => Assert.DoesNotContain("7731", entry));
        Assert.All(logger.Entries, entry => Assert.DoesNotContain(_folder.Path, entry, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("(none)", logger.Entries[1]);
    }

    [Fact]
    public async Task ToStringOfAResultAShowsWhatIsReadButNeverItsContent()
    {
        var path = _folder.WriteBytes("doc.docx", DocxFixture.Create(DocxFixture.Heading(1, SecretTitle) + DocxFixture.P(SecretText), new DocxOptions { Styles = DocxFixture.Styles(), Title = SecretTitle, Author = SecretTitle }));
        var reader = new DocxDocumentReader(new CapturingLogger<DocxDocumentReader>());

        var result = await reader.ReadAsync(path);

        Assert.DoesNotContain("7731", result.ToString());
        Assert.DoesNotContain("7731", result.Metadata!.ToString());
        Assert.All(result.Segments, segment => Assert.DoesNotContain("7731", segment.ToString()));
        Assert.All(result.Segments, segment => Assert.DoesNotContain("7731", segment.Location.ToString()));
        Assert.Contains(SecretText, result.ToText());
    }
}
