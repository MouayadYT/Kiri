using System.Text;
using Assistant.Core.Documents;
using Assistant.Documents.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Documents.Tests;

/// <summary>Reading <c>.txt</c> files: encodings, line and page places, limits, and what is not text.</summary>
public sealed class PlainTextDocumentReaderTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly PlainTextDocumentReader _reader = new(NullLogger<PlainTextDocumentReader>.Instance);

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void TheReaderSaysWhatItHandles()
    {
        Assert.Equal("text", _reader.Id);
        Assert.Equal(
            [".txt", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".log", ".ini", ".toml"], _reader.SupportedExtensions);
        Assert.Contains("text/plain", _reader.SupportedMimeTypes);
        Assert.Contains("text/csv", _reader.SupportedMimeTypes);
    }

    [Fact]
    public async Task ATextFileIsOnePieceWithTheLinesItIsOn()
    {
        var path = _folder.Write("notes.txt", "\n\nfirst line\nsecond line\n\nfourth line\n\n");

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(DocumentLocationKind.Document, segment.Location.Kind);
        Assert.Equal(3, segment.Location.FirstLine);
        Assert.Equal(6, segment.Location.LastLine);
        Assert.Equal("first line\nsecond line\n\nfourth line", segment.Text);
        Assert.False(result.Truncated);
        Assert.Equal("document, lines 3-6", segment.Location.Describe());
    }

    [Fact]
    public async Task TheMetadataSaysWhoReadItAndHowBig()
    {
        var path = _folder.Write("notes.TXT", "hello");

        var result = await _reader.ReadAsync(path);

        Assert.Equal("text", result.Metadata!.ReaderId);
        Assert.Equal(".txt", result.Metadata.Extension);
        Assert.Equal(5, result.Metadata.SizeBytes);
        Assert.Null(result.Metadata.Title);
        Assert.Equal(DocumentUnitKind.None, result.Metadata.UnitKind);
        Assert.Null(result.Metadata.UnitCount);
    }

    [Fact]
    public async Task WindowsAndOldMacLineBreaksGiveTheSameTextAndLineNumbers()
    {
        var windows = await _reader.ReadAsync(_folder.Write("a.txt", "one\r\ntwo\r\nthree"));
        var oldMac = await _reader.ReadAsync(_folder.Write("b.txt", "one\rtwo\rthree"));

        Assert.Equal(ResultAssertions.Flatten(windows), ResultAssertions.Flatten(oldMac));
        Assert.Equal(3, windows.Segments[0].Location.LastLine);
        Assert.Equal(3, oldMac.Segments[0].Location.LastLine);
    }

    [Fact]
    public async Task AFormFeedEndsAPageAndPagesKeepTheirNumbersAndLines()
    {
        var path = _folder.Write("report.txt", "page one\nline two\f\fpage three\n\fpage four");

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Collection(
            result.Segments,
            one =>
            {
                Assert.Equal(DocumentLocation.ForPage(1, 1, 2), one.Location);
                Assert.Equal("page one\nline two", one.Text);
            },
            three =>
            {
                Assert.Equal(DocumentLocationKind.Page, three.Location.Kind);
                Assert.Equal(3, three.Location.Number);
                Assert.Equal(2, three.Location.FirstLine);
                Assert.Equal("page three", three.Text);
            },
            four =>
            {
                Assert.Equal(4, four.Location.Number);
                Assert.Equal(3, four.Location.FirstLine);
                Assert.Equal("page four", four.Text);
            });
    }

    [Fact]
    public async Task TheTextOfAllPiecesWithTheirMarkersMakesOneStringForAPrompt()
    {
        var path = _folder.Write("report.txt", "alpha\fbeta");

        var result = await _reader.ReadAsync(path);

        Assert.Equal("[Page 1]\nalpha\n\n[Page 2]\nbeta", result.ToText());
    }

    [Fact]
    public async Task AnEmptyFileHasNoText()
    {
        var empty = await _reader.ReadAsync(_folder.WriteBytes("empty.txt", []));
        var blank = await _reader.ReadAsync(_folder.Write("blank.txt", " \r\n\t\r\n "));

        Assert.Equal(DocumentReadStatus.NoText, empty.Status);
        Assert.Equal(DocumentReadStatus.NoText, blank.Status);
        Assert.Empty(empty.Segments);
        Assert.NotNull(empty.Metadata);
    }

    [Fact]
    public async Task Utf8WithAndWithoutAByteOrderMarkIsRead()
    {
        var text = "Grüße, 日本語, 🚀";
        var plain = await _reader.ReadAsync(_folder.WriteBytes("a.txt", new UTF8Encoding(false).GetBytes(text)));
        var marked = await _reader.ReadAsync(_folder.WriteBytes("b.txt", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray()));

        Assert.Equal(text, plain.Segments[0].Text);
        Assert.Equal(text, marked.Segments[0].Text);
    }

    [Fact]
    public async Task Utf16AndUtf32WithAByteOrderMarkAreRead()
    {
        var text = "Grüße 日本語 🚀";
        var little = await _reader.ReadAsync(_folder.WriteBytes("le.txt", Preamble(new UnicodeEncoding(false, true), text)));
        var big = await _reader.ReadAsync(_folder.WriteBytes("be.txt", Preamble(new UnicodeEncoding(true, true), text)));
        var utf32 = await _reader.ReadAsync(_folder.WriteBytes("u32.txt", Preamble(new UTF32Encoding(false, true), text)));

        Assert.Equal(text, little.Segments[0].Text);
        Assert.Equal(text, big.Segments[0].Text);
        Assert.Equal(text, utf32.Segments[0].Text);
    }

    [Fact]
    public async Task AFileOfOlderWindowsTextIsReadAsWindows1252()
    {
        var bytes = new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x20, 0x93, 0x71, 0x75, 0x6F, 0x74, 0x65, 0x94 };

        var result = await _reader.ReadAsync(_folder.WriteBytes("old.txt", bytes));

        Assert.Equal("café “quote”", result.Segments[0].Text);
    }

    [Fact]
    public async Task BinaryDataUnderATextNameIsUnsupportedAndNothingOfItIsReturned()
    {
        var withZeroBytes = await _reader.ReadAsync(_folder.WriteBytes("a.txt", [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00]));
        var controlCharacters = await _reader.ReadAsync(_folder.WriteBytes("b.txt", Enumerable.Range(1, 7).Select(i => (byte)i).Concat(Enumerable.Repeat((byte)0x01, 500)).ToArray()));
        var metadata = await _reader.ReadMetadataAsync(_folder.WriteBytes("c.txt", [0x00, 0x01, 0x02, 0x03]));

        Assert.Equal(DocumentReadStatus.Unsupported, withZeroBytes.Status);
        Assert.Empty(withZeroBytes.Segments);
        Assert.Equal(DocumentReadStatus.Unsupported, controlCharacters.Status);
        Assert.Equal(DocumentReadStatus.Unsupported, metadata.Status);
    }

    [Fact]
    public async Task ALogWithColourCodesIsStillText()
    {
        var result = await _reader.ReadAsync(_folder.Write("log.txt", "\u001b[31merror\u001b[0m: disk full\n"));

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Equal("[31merror[0m: disk full", result.Segments[0].Text);
    }

    [Fact]
    public async Task AMissingFileOrADirectoryOrARelativePathIsNotFound()
    {
        Assert.Equal(DocumentReadStatus.NotFound, (await _reader.ReadAsync(_folder.File("nope.txt"))).Status);
        Assert.Equal(DocumentReadStatus.NotFound, (await _reader.ReadAsync(_folder.Path)).Status);
        Assert.Equal(DocumentReadStatus.NotFound, (await _reader.ReadAsync("notes.txt")).Status);
        Assert.Equal(DocumentReadStatus.NotFound, (await _reader.ReadAsync("")).Status);
        Assert.Equal(DocumentReadStatus.NotFound, (await _reader.ReadAsync(_folder.File("bad\0name.txt"))).Status);
        Assert.Equal(DocumentReadStatus.NotFound, (await _reader.ReadMetadataAsync(_folder.File("nope.txt"))).Status);
    }

    [Fact]
    public async Task AFileLargerThanTheLimitIsTooLargeAndIsNotRead()
    {
        var path = _folder.Write("big.txt", new string('a', 5_000));

        var over = await _reader.ReadAsync(path, new DocumentReadOptions { MaxFileBytes = 4_999 });
        var exact = await _reader.ReadAsync(path, new DocumentReadOptions { MaxFileBytes = 5_000 });
        var metadata = await _reader.ReadMetadataAsync(path, new DocumentReadOptions { MaxFileBytes = 100 });

        Assert.Equal(DocumentReadStatus.TooLarge, over.Status);
        Assert.Empty(over.Segments);
        Assert.Null(over.Metadata);
        Assert.Equal(DocumentReadStatus.Success, exact.Status);
        Assert.Equal(DocumentReadStatus.TooLarge, metadata.Status);
    }

    [Fact]
    public async Task ALimitOfZeroOrLessIsTheDefaultAndNeverNoLimit()
    {
        var path = _folder.Write("a.txt", "hello");

        var result = await _reader.ReadAsync(path, new DocumentReadOptions { MaxFileBytes = 0, MaxCharacters = -1, MaxUnits = 0, MaxExpandedBytes = -5 });

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Equal("hello", result.Segments[0].Text);
    }

    [Fact]
    public async Task TextPastTheCharacterLimitIsLeftOutAndSaysSo()
    {
        var path = _folder.Write("long.txt", string.Join(" ", Enumerable.Range(1, 2_000).Select(i => "word" + i)));

        var result = await _reader.ReadAsync(path, new DocumentReadOptions { MaxCharacters = 500 });

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.True(result.Truncated);
        Assert.InRange(result.CharacterCount, 400, 500);
        Assert.StartsWith("word1 word2 word3", result.Segments[0].Text);
        Assert.DoesNotContain("word2000", result.Segments[0].Text);
    }

    [Fact]
    public async Task TextThatJustFitsTheCharacterLimitIsNotTruncated()
    {
        var path = _folder.Write("fits.txt", new string('a', 500));

        var exact = await _reader.ReadAsync(path, new DocumentReadOptions { MaxCharacters = 500 });
        var under = await _reader.ReadAsync(path, new DocumentReadOptions { MaxCharacters = 499 });

        Assert.False(exact.Truncated);
        Assert.Equal(500, exact.CharacterCount);
        Assert.True(under.Truncated);
    }

    [Fact]
    public async Task MorePagesThanTheUnitLimitAreLeftOutAndSaySo()
    {
        var path = _folder.Write("pages.txt", "a\fb\fc\fd");

        var result = await _reader.ReadAsync(path, new DocumentReadOptions { MaxUnits = 2 });

        Assert.Equal(2, result.Segments.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task AFileAnotherProgramHasOpenForWritingIsStillRead()
    {
        var path = _folder.Write("open.txt", "being edited");
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Equal("being edited", result.Segments[0].Text);
    }

    [Fact]
    public async Task AFileOpenedExclusivelyByAnotherProgramIsUnreadableNotAnException()
    {
        var path = _folder.Write("locked.txt", "secret");
        await using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Unreadable, result.Status);
        Assert.Empty(result.Segments);
    }

    [Fact]
    public async Task ReadingNeverChangesTheFile()
    {
        var path = _folder.Write("keep.txt", "keep me\r\nas I am");
        var before = await File.ReadAllBytesAsync(path);
        var modified = File.GetLastWriteTimeUtc(path);

        await _reader.ReadAsync(path);
        await _reader.ReadMetadataAsync(path);

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task CancellingBeforeTheReadStopsItWithACancellation()
    {
        var path = _folder.Write("a.txt", "hello");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadAsync(path, null, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadMetadataAsync(path, null, cancelled.Token));
    }

    [Fact]
    public void CancellingInTheMiddleOfAReadStopsItAtTheNextChunk()
    {
        using var cancellation = new CancellationTokenSource();
        var bytes = Encoding.UTF8.GetBytes(new string('x', 500_000));
        using var stream = new CancelOnSecondRead(bytes, cancellation);

        Assert.ThrowsAny<OperationCanceledException>(() => TextFileDecoder.Read(stream, 10_000_000, cancellation.Token, out _));
        Assert.True(stream.Reads < 40, "the decoder went on reading after it was cancelled");
    }

    [Fact]
    public async Task ALongFileIsNotReadPastTheCharacterLimit()
    {
        var path = _folder.Write("huge.txt", new string('x', 3_000_000));

        var result = await _reader.ReadAsync(path, new DocumentReadOptions { MaxCharacters = 1_000 });

        Assert.Equal(1_000, result.CharacterCount);
        Assert.True(result.Truncated);
    }

    private static byte[] Preamble(Encoding encoding, string text) => encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();

    private sealed class CancelOnSecondRead(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public int Reads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            if (Reads == 2)
            {
                cancellation.Cancel();
            }

            return base.Read(buffer, offset, count);
        }
    }
}
