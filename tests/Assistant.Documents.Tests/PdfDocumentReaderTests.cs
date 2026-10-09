using Assistant.Core.Documents;
using Assistant.Documents.Pdf;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Documents.Tests;

/// <summary>Reading PDFs: pages as places, reading order, limits, and the files that cannot be read.</summary>
public sealed class PdfDocumentReaderTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly PdfDocumentReader _reader = new(NullLogger<PdfDocumentReader>.Instance);

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void TheReaderSaysWhatItHandles()
    {
        Assert.Equal("pdf", _reader.Id);
        Assert.Equal([".pdf"], _reader.SupportedExtensions);
        Assert.Equal(["application/pdf"], _reader.SupportedMimeTypes);
    }

    [Fact]
    public async Task EachPageIsAPieceWithItsNumberInTheOrderOfTheDocument()
    {
        var path = _folder.WriteBytes("report.pdf", PdfBuilder.Create("Alpha on page one", "Beta on page two", "Gamma on page three"));

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Collection(
            result.Segments,
            one =>
            {
                Assert.Equal(DocumentLocation.ForPage(1), one.Location);
                Assert.Equal("Alpha on page one", one.Text);
            },
            two =>
            {
                Assert.Equal(DocumentLocation.ForPage(2), two.Location);
                Assert.Equal("Beta on page two", two.Text);
            },
            three =>
            {
                Assert.Equal(DocumentLocation.ForPage(3), three.Location);
                Assert.Equal("Gamma on page three", three.Text);
            });
        Assert.False(result.Truncated);
        Assert.Equal("[Page 1]\nAlpha on page one\n\n[Page 2]\nBeta on page two\n\n[Page 3]\nGamma on page three", result.ToText());
        Assert.Equal("page 2", result.Segments[1].Location.Describe());
    }

    [Fact]
    public async Task ALineIsALineAndWordsKeepTheirSpaces()
    {
        var path = _folder.WriteBytes("lines.pdf", PdfBuilder.Create("The quick brown fox\njumps over the lazy dog"));

        var result = await _reader.ReadAsync(path);

        Assert.Equal("The quick brown fox\njumps over the lazy dog", result.Segments[0].Text);
    }

    [Fact]
    public async Task APageWithNoTextKeepsItsNumberForTheNextOne()
    {
        var path = _folder.WriteBytes("gap.pdf", PdfBuilder.Create("first", string.Empty, "third"));

        var result = await _reader.ReadAsync(path);

        Assert.Equal([1, 3], result.Segments.Select(s => s.Location.Number).ToArray());
        Assert.Equal(3, result.Metadata!.UnitCount);
    }

    [Fact]
    public async Task TheMetadataHasTheTitleAuthorAndPageCount()
    {
        var path = _folder.WriteBytes("meta.pdf", PdfBuilder.Create([["one"], ["two"]], "A title", "An author"));

        var read = await _reader.ReadAsync(path);
        var metadata = await _reader.ReadMetadataAsync(path);

        Assert.Equal(DocumentReadStatus.Success, metadata.Status);
        Assert.Equal("pdf", metadata.Metadata!.ReaderId);
        Assert.Equal(".pdf", metadata.Metadata.Extension);
        Assert.Equal(new FileInfo(path).Length, metadata.Metadata.SizeBytes);
        Assert.Equal("A title", metadata.Metadata.Title);
        Assert.Equal("An author", metadata.Metadata.Author);
        Assert.Equal(DocumentUnitKind.Page, metadata.Metadata.UnitKind);
        Assert.Equal(2, metadata.Metadata.UnitCount);
        Assert.Equal(metadata.Metadata, read.Metadata);
    }

    [Fact]
    public async Task APdfWithNoTitleOrAuthorHasNoneAndNeverTheFileName()
    {
        var path = _folder.WriteBytes("untitled-report.pdf", PdfBuilder.Create("text"));

        var metadata = await _reader.ReadMetadataAsync(path);

        Assert.True(string.IsNullOrEmpty(metadata.Metadata!.Title));
        Assert.True(string.IsNullOrEmpty(metadata.Metadata.Author));
    }

    [Fact]
    public async Task APdfOfPagesWithNoTextAtAllHasNoText()
    {
        var path = _folder.WriteBytes("scan.pdf", PdfBuilder.Create([[], []]));

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.NoText, result.Status);
        Assert.Empty(result.Segments);
        Assert.Equal(2, result.Metadata!.UnitCount);
    }

    [Fact]
    public async Task APdfThatAsksForAPasswordIsEncryptedAndNothingIsReturned()
    {
        var path = _folder.WriteBytes("locked.pdf", PdfBuilder.CreateEncrypted());

        var read = await _reader.ReadAsync(path);
        var metadata = await _reader.ReadMetadataAsync(path);

        Assert.Equal(DocumentReadStatus.Encrypted, read.Status);
        Assert.Empty(read.Segments);
        Assert.Null(read.Metadata);
        Assert.Equal(DocumentReadStatus.Encrypted, metadata.Status);
    }

    [Theory]
    [InlineData("this is not a pdf at all")]
    [InlineData("%PDF-1.7\n1 0 obj\n<< /Broken")]
    [InlineData("")]
    public async Task AFileThatIsNotAPdfOrIsDamagedIsCorruptAndNeverAnException(string content)
    {
        var path = _folder.Write("fake.pdf", content);

        var read = await _reader.ReadAsync(path);
        var metadata = await _reader.ReadMetadataAsync(path);

        Assert.Equal(DocumentReadStatus.Corrupt, read.Status);
        Assert.Empty(read.Segments);
        Assert.Equal(DocumentReadStatus.Corrupt, metadata.Status);
    }

    [Fact]
    public async Task APdfCutShortInTheMiddleIsCorruptOrReadAsFarAsItGoesButNeverAnException()
    {
        var bytes = PdfBuilder.Create("alpha", "beta", "gamma", "delta");
        var path = _folder.WriteBytes("cut.pdf", bytes[..(bytes.Length / 2)]);

        var result = await _reader.ReadAsync(path);

        Assert.True(result.Status is DocumentReadStatus.Corrupt or DocumentReadStatus.Success or DocumentReadStatus.NoText);
    }

    [Fact]
    public async Task APageThatCannotBeParsedIsSkippedAndTheRestOfTheDocumentIsStillRead()
    {
        var path = _folder.WriteBytes("damaged-page.pdf", BrokenPdf.WithUnreadableSecondPage("The good first page"));

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(DocumentLocation.ForPage(1), segment.Location);
        Assert.Contains("good first page", segment.Text);
        Assert.True(result.Truncated, "a document with a page that could not be read is not all there");
        Assert.Equal(2, result.Metadata!.UnitCount);
    }

    [Fact]
    public async Task APdfWhoseEveryPageCannotBeParsedIsCorrupt()
    {
        var path = _folder.WriteBytes("all-damaged.pdf", BrokenPdf.WithUnreadableSecondPage("unused", firstPageReadable: false));

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Corrupt, result.Status);
        Assert.Empty(result.Segments);
    }

    [Fact]
    public async Task APdfLargerThanTheLimitIsTooLargeAndIsNotOpened()
    {
        var path = _folder.WriteBytes("big.pdf", PdfBuilder.Create("text"));
        var size = new FileInfo(path).Length;

        var over = await _reader.ReadAsync(path, new DocumentReadOptions { MaxFileBytes = size - 1 });
        var exact = await _reader.ReadAsync(path, new DocumentReadOptions { MaxFileBytes = size });

        Assert.Equal(DocumentReadStatus.TooLarge, over.Status);
        Assert.Null(over.Metadata);
        Assert.Equal(DocumentReadStatus.Success, exact.Status);
    }

    [Fact]
    public async Task PagesPastTheUnitLimitAreLeftOutAndSaySo()
    {
        var path = _folder.WriteBytes("pages.pdf", PdfBuilder.Create("one", "two", "three", "four"));

        var result = await _reader.ReadAsync(path, new DocumentReadOptions { MaxUnits = 2 });

        Assert.Equal([1, 2], result.Segments.Select(s => s.Location.Number).ToArray());
        Assert.True(result.Truncated);
        Assert.Equal(4, result.Metadata!.UnitCount);
    }

    [Fact]
    public async Task TextPastTheCharacterLimitIsLeftOutAndTheReadStops()
    {
        var page = string.Join("\n", Enumerable.Range(1, 30).Select(i => "line number " + i));
        var path = _folder.WriteBytes("long.pdf", PdfBuilder.Create(page, page, page));

        var result = await _reader.ReadAsync(path, new DocumentReadOptions { MaxCharacters = 600 });

        Assert.True(result.Truncated);
        Assert.InRange(result.CharacterCount, 400, 600);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(1, result.Segments[0].Location.Number);
    }

    [Fact]
    public async Task PagesThatJustFitTheCharacterLimitAreNotTruncated()
    {
        var path = _folder.WriteBytes("fits.pdf", PdfBuilder.Create("abcde"));

        var exact = await _reader.ReadAsync(path, new DocumentReadOptions { MaxCharacters = 5 });

        Assert.False(exact.Truncated);
        Assert.Equal("abcde", exact.Segments[0].Text);
    }

    [Fact]
    public async Task CancellingBeforeTheReadStopsItWithACancellation()
    {
        var path = _folder.WriteBytes("a.pdf", PdfBuilder.Create("text"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadAsync(path, null, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadMetadataAsync(path, null, cancelled.Token));
    }

    [Fact]
    public async Task CancellingInTheMiddleOfALongPdfStopsItBetweenPages()
    {
        var pages = Enumerable.Range(1, 1_500).Select(i => new[] { "page " + i + " with a little text on it", "and a second line for page " + i }).ToArray();
        var path = _folder.WriteBytes("long.pdf", PdfBuilder.Create(pages));
        using var cancellation = new CancellationTokenSource();

        var started = System.Diagnostics.Stopwatch.StartNew();
        var read = _reader.ReadAsync(path, null, cancellation.Token);
        await Task.Delay(30);
        Assert.False(read.IsCompleted, "the read finished before it could be cancelled: make the document longer");
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task APdfOpenInAnotherProgramIsStillRead()
    {
        var path = _folder.WriteBytes("open.pdf", PdfBuilder.Create("being viewed"));
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);

        var result = await _reader.ReadAsync(path);

        Assert.Equal("being viewed", result.Segments[0].Text);
    }

    [Fact]
    public async Task ReadingNeverChangesTheFile()
    {
        var path = _folder.WriteBytes("keep.pdf", PdfBuilder.Create("keep me"));
        var before = await File.ReadAllBytesAsync(path);

        await _reader.ReadAsync(path);
        await _reader.ReadMetadataAsync(path);

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task LigaturesAreWrittenOutAsLetters()
    {
        var fi = ((char)0xFB01).ToString();
        var path = _folder.WriteBytes("lig.pdf", PdfBuilder.Create($"the {fi}le"));

        var result = await _reader.ReadAsync(path);

        Assert.Contains("file", result.Segments[0].Text.Replace(" ", string.Empty));
    }
}
