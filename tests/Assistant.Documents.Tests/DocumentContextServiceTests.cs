using System.Globalization;
using System.Text;
using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Documents.Context;
using Assistant.Documents.OpenXml;
using Assistant.Documents.Pdf;
using Assistant.Documents.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Documents.Tests;

/// <summary>
/// A question about one file (PROJECT_SPEC §4.7): the reader for its type reads it, it is cut into passages, and the passages the
/// question needs are laid out for the prompt with where each is. Whatever cannot be read is a status, and nothing private is logged.
/// </summary>
public sealed class DocumentContextServiceTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static DocumentReaderRegistry Registry() => new(
    [
        new PlainTextDocumentReader(NullLogger<PlainTextDocumentReader>.Instance),
        new MarkdownDocumentReader(NullLogger<MarkdownDocumentReader>.Instance),
        new PdfDocumentReader(NullLogger<PdfDocumentReader>.Instance),
        new DocxDocumentReader(NullLogger<DocxDocumentReader>.Instance),
        new PptxDocumentReader(NullLogger<PptxDocumentReader>.Instance),
        new Web.WebPageDocumentReader(NullLogger<Web.WebPageDocumentReader>.Instance),
    ]);

    private static DocumentContextService Service(ILogger<DocumentContextService>? logger = null) =>
        new(Registry(), new DocumentContextBuilder(), logger);

    // A long Markdown file: a section each about a different thing, each several passages long.
    private static string Handbook(out string[] headings)
    {
        headings = ["Arrival", "Kitchen", "Garden", "Workshop", "Library", "Cellar", "Attic", "Studio"];
        var text = new StringBuilder("Introduction to the house handbook.\n\n");
        for (var number = 0; number < headings.Length; number++)
        {
            text.Append("## ").Append(headings[number]).Append("\n\n");
            var room = headings[number].ToLowerInvariant();
            for (var paragraph = 0; paragraph < 4; paragraph++)
            {
                text.Append(string.Create(CultureInfo.InvariantCulture,
                    $"The {room} is arranged for the {room} routine number {paragraph}. Guests should treat the {room} with care and leave it as they found it, "));
                text.Append("closing every door and window behind them and switching off the lights, which are on a timer that the owner sets each week in the hall cupboard.\n\n");
            }
        }

        return text.ToString();
    }

    [Fact]
    public async Task AShortTextFileIsGivenWholeUnderALineThatSaysWhereItIs()
    {
        var path = _folder.Write("notes.txt", "First line.\nSecond line.\nThird line.");

        var result = await Service().GetContextAsync(path, "anything at all");

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Equal("[Passage 1: document, lines 1-3]\nFirst line.\nSecond line.\nThird line.", result.Text);
        Assert.Equal(PassageSelectionReason.WholeDocument, result.Selection.Reason);
        Assert.True(result.Selection.IsComplete);
        Assert.False(result.Truncated);
        Assert.Equal(".txt", result.Metadata!.Extension);
        Assert.Empty(DocumentContextNotices.For("notes.txt", result));
    }

    [Fact]
    public async Task ALongMarkdownFileIsGivenAsThePassagesTheQuestionPointsTo()
    {
        var path = _folder.Write("handbook.md", Handbook(out _));

        var result = await Service().GetContextAsync(path, "How should guests treat the workshop?", new DocumentContextOptions
        {
            Selection = new PassageSelectionOptions { MaxCharacters = 2_500, MaxPassages = 4 },
        });

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Equal(PassageSelectionReason.Matched, result.Selection.Reason);
        Assert.False(result.Selection.IsComplete);
        Assert.InRange(result.Selection.Passages.Count, 1, 4);
        Assert.Matches(@"^\[Passage 1: section \d+, lines \d+-\d+ \(Workshop\)\]\n## Workshop", result.Text);
        Assert.Contains("workshop routine number", result.Text, StringComparison.Ordinal);

        // The other sections have the words "guests" and "treat" too, but only a little of the question's weight.
        Assert.DoesNotContain("kitchen routine", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("attic routine", result.Text, StringComparison.Ordinal);
        Assert.True(result.Text.Length <= 2_500 + (4 * 80), $"{result.Text.Length} characters");

        // The user is told that only part of the file was read.
        var notice = Assert.Single(DocumentContextNotices.For("handbook.md", result));
        Assert.StartsWith("Only the ", notice, StringComparison.Ordinal);
        Assert.Contains("“handbook.md”", notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQuestionThatNamesAPlaceGetsThatPlaceFromAPdf()
    {
        var pages = Enumerable.Range(1, 12).Select(page => new[]
        {
            $"This is the text of page number {page} of the report.",
            $"It discusses subject{page} in a good deal of detail and nothing else.",
        }).ToArray();
        var path = _folder.WriteBytes("report.pdf", PdfBuilder.Create(pages, "Annual report"));

        var result = await Service().GetContextAsync(path, "What does page 7 say?", new DocumentContextOptions
        {
            Selection = new PassageSelectionOptions { MaxCharacters = 200, MaxPassages = 1 },
        });

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        // Pages this short are read together, three to a passage, each marked where it starts.
        Assert.StartsWith("[Passage 1: pages 7-9]\nThis is the text of page number 7", result.Text, StringComparison.Ordinal);
        Assert.Contains("subject7", result.Text, StringComparison.Ordinal);
        Assert.Contains("[Page 8]\nThis is the text of page number 8", result.Text, StringComparison.Ordinal);
        Assert.Equal("Annual report", result.Metadata!.Title);
    }

    [Fact]
    public async Task AWordDocumentAndAPresentationAreGivenWithTheirSectionsAndSlides()
    {
        var body = new StringBuilder();
        for (var section = 1; section <= 6; section++)
        {
            body.Append(DocxFixture.Heading(1, $"Chapter {section}"));
            for (var paragraph = 0; paragraph < 6; paragraph++)
            {
                body.Append(DocxFixture.P($"Chapter {section} paragraph {paragraph} explains the topic{section} at length, with examples and some remarks for the reader to think over."));
            }
        }

        var docx = _folder.WriteBytes("guide.docx", DocxFixture.Create(body.ToString(), new DocxOptions { Styles = DocxFixture.Styles() }));
        var slides = Enumerable.Range(1, 12)
            .Select(slide => PptxFixture.Title($"Slide title {slide}") + PptxFixture.Body($"Bullet about matter{slide} that is discussed on this slide only, with a few more words so that it has some weight."))
            .ToArray();
        var pptx = _folder.WriteBytes("talk.pptx", PptxFixture.Create(slides));
        var options = new DocumentContextOptions { Selection = new PassageSelectionOptions { MaxCharacters = 1_500, MaxPassages = 2 } };

        var word = await Service().GetContextAsync(docx, "what is said about topic4?", options);
        var talk = await Service().GetContextAsync(pptx, "tell me about matter9", options);

        Assert.Equal(DocumentReadStatus.Success, word.Status);
        Assert.Contains("(Chapter 4)]", word.Text, StringComparison.Ordinal);
        Assert.Contains("topic4", word.Text, StringComparison.Ordinal);
        Assert.Equal(DocumentReadStatus.Success, talk.Status);

        // Slides this short are read two together, the second marked where it starts.
        Assert.Contains("[Passage 1: slides 9-10 (Slide title 9)]", talk.Text, StringComparison.Ordinal);
        Assert.Contains("matter9", talk.Text, StringComparison.Ordinal);
        Assert.Contains("[Slide 10: Slide title 10]", talk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThatCannotBeReadIsAStatusAndNeverAnException()
    {
        var empty = _folder.Write("empty.txt", "   \n  ");
        var binary = _folder.WriteBytes("program.exe", [0x4D, 0x5A, 0x90, 0x00]);
        var damaged = _folder.WriteBytes("damaged.docx", Encoding.UTF8.GetBytes("this is not a zip package"));
        var locked = _folder.WriteBytes("locked.pdf", PdfBuilder.CreateEncrypted());
        var service = Service();

        Assert.Equal(DocumentReadStatus.NoText, (await service.GetContextAsync(empty, "question")).Status);
        Assert.Equal(DocumentReadStatus.Unsupported, (await service.GetContextAsync(binary, "question")).Status);
        Assert.Equal(DocumentReadStatus.Corrupt, (await service.GetContextAsync(damaged, "question")).Status);
        Assert.Equal(DocumentReadStatus.Encrypted, (await service.GetContextAsync(locked, "question")).Status);
        Assert.Equal(DocumentReadStatus.NotFound, (await service.GetContextAsync(_folder.File("gone.pdf"), "question")).Status);

        var failed = await service.GetContextAsync(binary, "question");
        Assert.Equal(string.Empty, failed.Text);
        Assert.Equal(PassageSelection.Empty, failed.Selection);
    }

    [Fact]
    public async Task TheLimitsOfReadingAreHonoredAndSaidToHaveCutTheFile()
    {
        var path = _folder.Write("long.txt", string.Join("\n\n", Enumerable.Range(0, 200).Select(n => $"Paragraph {n} says something about the subject at hand, which goes on for a while.")));

        var cut = await Service().GetContextAsync(path, "subject", new DocumentContextOptions
        {
            Read = new DocumentReadOptions { MaxCharacters = 3_000 },
        });
        var tooBig = await Service().GetContextAsync(path, "subject", new DocumentContextOptions
        {
            Read = new DocumentReadOptions { MaxFileBytes = 100 },
        });

        Assert.Equal(DocumentReadStatus.Success, cut.Status);
        Assert.True(cut.Truncated);
        Assert.Equal(DocumentReadStatus.TooLarge, tooBig.Status);
        var notices = DocumentContextNotices.For("long.txt", cut);
        Assert.Contains(notices, notice => notice.Contains("very long", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFileIsNotChangedByAskingAboutIt()
    {
        var path = _folder.Write("keep.txt", "Some words in a file.");
        var before = File.GetLastWriteTimeUtc(path);

        await Service().GetContextAsync(path, "words");

        Assert.Equal("Some words in a file.", await File.ReadAllTextAsync(path));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task CancellingStopsTheWork()
    {
        var path = _folder.Write("notes.txt", "Some text.");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().GetContextAsync(path, "text", null, cancelled.Token));
    }

    [Fact]
    public async Task TheArgumentsAreChecked()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().GetContextAsync(" ", "question"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Service().GetContextAsync(_folder.File("a.txt"), null!));
        Assert.Throws<ArgumentNullException>(() => new DocumentContextService(null!, new DocumentContextBuilder()));
        Assert.Throws<ArgumentNullException>(() => new DocumentContextService(Registry(), null!));
    }

    [Fact]
    public async Task NothingOfTheFileOrTheQuestionIsLogged()
    {
        var logger = new CapturingLogger<DocumentContextService>();
        var service = Service(logger);
        var path = _folder.Write("secret-name-4417.txt", string.Join("\n\n", Enumerable.Range(0, 120).Select(n => $"SECRET-BODY-4417 paragraph {n} with words to fill a passage or two, as a long file has.")));

        await service.GetContextAsync(path, "what about SECRET-QUESTION-4417?", new DocumentContextOptions
        {
            Selection = new PassageSelectionOptions { MaxCharacters = 2_000 },
        });
        await service.GetContextAsync(_folder.File("secret-missing-4417.pdf"), "SECRET-QUESTION-4417");

        var text = string.Join('\n', logger.Entries);
        Assert.Contains("Document context from text", text, StringComparison.Ordinal);
        Assert.Contains("ended NotFound", text, StringComparison.Ordinal);
        Assert.DoesNotContain("4417", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_folder.Path, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFileTypesAViewCanTellAreTheOnesTheReadersRead()
    {
        Assert.Equal(Registry().SupportedExtensions.Order(StringComparer.Ordinal), DocumentFileTypes.Extensions.Order(StringComparer.Ordinal));
        Assert.All(DocumentFileTypes.Extensions, extension =>
        {
            Assert.True(DocumentFileTypes.IsDocumentExtension(extension));
            Assert.True(DocumentFileTypes.IsDocumentExtension(extension.ToUpperInvariant()));
            Assert.True(DocumentFileTypes.IsDocumentExtension(extension[1..]));
            Assert.True(DocumentFileTypes.IsDocument(@"C:\Files\report" + extension));
        });
        Assert.False(DocumentFileTypes.IsDocumentExtension(".xlsx"));
        Assert.False(DocumentFileTypes.IsDocumentExtension(".png"));
        Assert.False(DocumentFileTypes.IsDocumentExtension(""));
        Assert.False(DocumentFileTypes.IsDocument(null));
        Assert.False(DocumentFileTypes.IsDocument(@"C:\Files\noextension"));
    }
}
