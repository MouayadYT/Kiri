using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Xunit;

namespace Assistant.Documents.Tests;

/// <summary>How the registry finds the reader for a file, and what it does for a file nobody reads.</summary>
public sealed class DocumentReaderRegistryTests
{
    private static readonly FakeReader Pdf = new("pdf", [".pdf"], ["application/pdf"]);
    private static readonly FakeReader Text = new("text", [".txt", ".text"], ["text/plain"]);

    private static DocumentReaderRegistry Create(params IDocumentReader[] readers) => new(readers);

    [Theory]
    [InlineData(@"C:\docs\report.pdf")]
    [InlineData(@"C:\docs\REPORT.PDF")]
    [InlineData(@"C:\docs\Report.Pdf")]
    [InlineData("/home/user/report.pdf")]
    [InlineData(@"C:\docs\archive.v2.final.pdf")]
    public void AFileIsFoundByItsExtensionInAnyCase(string path)
    {
        Assert.Same(Pdf, Create(Pdf, Text).FindReader(path));
    }

    [Theory]
    [InlineData(@"C:\docs\report.doc")]
    [InlineData(@"C:\docs\report.pdf.exe")]
    [InlineData(@"C:\docs\report")]
    [InlineData(@"C:\docs\report.")]
    [InlineData(@"C:\docs.pdf\report")]
    [InlineData("")]
    [InlineData("   ")]
    public void AFileWithAnotherOrNoExtensionHasNoReader(string path)
    {
        Assert.Null(Create(Pdf, Text).FindReader(path));
    }

    [Fact]
    public void AReaderIsFoundByEveryExtensionItDeclares()
    {
        var registry = Create(Pdf, Text);

        Assert.Same(Text, registry.FindReader(@"C:\a.txt"));
        Assert.Same(Text, registry.FindReader(@"C:\a.TEXT"));
    }

    [Fact]
    public void TheRegistryListsWhatItsReadersHandleLowerCaseAndInOrder()
    {
        var registry = Create(Pdf, Text);

        Assert.Equal([".pdf", ".text", ".txt"], registry.SupportedExtensions);
        Assert.Equal(["application/pdf", "text/plain"], registry.SupportedMimeTypes);
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("APPLICATION/PDF")]
    [InlineData("application/pdf; charset=binary")]
    [InlineData("  application/pdf  ")]
    public void AMimeTypeFindsItsReaderWithParametersAndCaseIgnored(string mimeType)
    {
        Assert.Same(Pdf, Create(Pdf, Text).FindReaderForMimeType(mimeType));
    }

    [Theory]
    [InlineData("application/msword")]
    [InlineData("")]
    [InlineData(" ; ")]
    public void AnUnknownMimeTypeHasNoReader(string mimeType)
    {
        Assert.Null(Create(Pdf, Text).FindReaderForMimeType(mimeType));
    }

    [Fact]
    public void ARegistryOfNothingSupportsNothing()
    {
        var registry = Create();

        Assert.Empty(registry.SupportedExtensions);
        Assert.Null(registry.FindReader(@"C:\a.pdf"));
    }

    [Fact]
    public void TwoReadersOfOneExtensionAreAMistakeAndFailAtOnce()
    {
        var other = new FakeReader("other", [".PDF"], []);

        Assert.Throws<ArgumentException>(() => Create(Pdf, other));
    }

    [Fact]
    public void TwoReadersOfOneMimeTypeAreAMistakeAndFailAtOnce()
    {
        var other = new FakeReader("other", [".xyz"], ["Application/PDF; v=1"]);

        Assert.Throws<ArgumentException>(() => Create(Pdf, other));
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData(@".a\b")]
    public void AnExtensionWithoutItsDotIsRejected(string extension)
    {
        Assert.Throws<ArgumentException>(() => Create(new FakeReader("bad", [extension], [])));
    }

    [Fact]
    public async Task AFileNobodyReadsGetsTheFallbackWhichReportsUnsupportedAndOpensNothing()
    {
        var registry = Create(Pdf, Text);
        var reader = registry.GetReader(@"C:\definitely\not\a\real\path\archive.zip");

        var read = await reader.ReadAsync(@"C:\definitely\not\a\real\path\archive.zip");
        var metadata = await reader.ReadMetadataAsync(@"C:\definitely\not\a\real\path\archive.zip");

        Assert.Equal(DocumentReadStatus.Unsupported, read.Status);
        Assert.Empty(read.Segments);
        Assert.Null(read.Metadata);
        Assert.Equal(DocumentReadStatus.Unsupported, metadata.Status);
        Assert.Null(metadata.Metadata);
    }

    [Fact]
    public void TheFallbackIsNeverNullAndNeverFoundByFindReader()
    {
        var registry = Create(Pdf);

        Assert.Null(registry.FindReader(@"C:\a.bin"));
        Assert.NotNull(registry.GetReader(@"C:\a.bin"));
        Assert.NotNull(registry.GetReader(""));
        Assert.Same(Pdf, registry.GetReader(@"C:\a.pdf"));
        Assert.Empty(registry.GetReader(@"C:\a.bin").SupportedExtensions);
        Assert.Empty(registry.GetReader(@"C:\a.bin").SupportedMimeTypes);
    }

    [Fact]
    public async Task TheFallbackStillStopsWhenCancelled()
    {
        var reader = Create().GetReader(@"C:\a.bin");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(@"C:\a.bin", null, cancelled.Token));
    }

    [Fact]
    public async Task AFileWhoseNameSaysOneThingAndWhoseContentIsBinaryIsNotParsedOnAGuess()
    {
        using var folder = new TempFolder();
        var path = folder.WriteBytes("photo.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46]);

        var registry = new DocumentReaderRegistry([new Text.PlainTextDocumentReader(Microsoft.Extensions.Logging.Abstractions.NullLogger<Text.PlainTextDocumentReader>.Instance)]);
        var result = await registry.GetReader(path).ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Unsupported, result.Status);
        Assert.Empty(result.Segments);
    }

    private sealed class FakeReader(string id, string[] extensions, string[] mimeTypes) : IDocumentReader
    {
        public string Id => id;

        public string DisplayName => id;

        public IReadOnlyCollection<string> SupportedExtensions => extensions;

        public IReadOnlyCollection<string> SupportedMimeTypes => mimeTypes;

        public Task<DocumentMetadataResult> ReadMetadataAsync(string filePath, DocumentReadOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DocumentReadResult> ReadAsync(string filePath, DocumentReadOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
