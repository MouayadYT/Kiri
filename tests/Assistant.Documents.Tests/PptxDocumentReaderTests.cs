using System.Net;
using System.Net.Sockets;
using System.Text;
using Assistant.Core.Documents;
using Assistant.Documents.OpenXml;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Assistant.Documents.Tests.PptxFixture;

namespace Assistant.Documents.Tests;

/// <summary>Reading presentations: slides in the order of the presentation with their numbers, notes, and everything a hostile file could try.</summary>
public sealed class PptxDocumentReaderTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly PptxDocumentReader _reader = new(NullLogger<PptxDocumentReader>.Instance);

    public void Dispose() => _folder.Dispose();

    private async Task<DocumentReadResult> ReadAsync(string[] slides, PptxOptions? options = null, DocumentReadOptions? limits = null) =>
        await _reader.ReadAsync(_folder.WriteBytes("deck.pptx", Create(slides, options)), limits);

    private static string[] Texts(DocumentReadResult result) => result.Segments.Select(s => s.Text).ToArray();

    [Fact]
    public void TheReaderSaysWhatItHandles()
    {
        Assert.Equal("pptx", _reader.Id);
        Assert.Equal([".pptx"], _reader.SupportedExtensions);
        Assert.Equal(["application/vnd.openxmlformats-officedocument.presentationml.presentation"], _reader.SupportedMimeTypes);
    }

    [Fact]
    public async Task EachSlideIsAPieceWithItsNumberAndTitle()
    {
        var result = await ReadAsync(
            [Title("Welcome") + Body("Hello there", "Second bullet"), Title("Plan") + Body("Step one"), Shape(null, "A text box with no title")]);

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Collection(
            result.Segments,
            one =>
            {
                Assert.Equal(DocumentLocation.ForSlide(1, "Welcome"), one.Location);
                Assert.Equal("Welcome\n\nHello there\nSecond bullet", one.Text);
            },
            two =>
            {
                Assert.Equal(DocumentLocation.ForSlide(2, "Plan"), two.Location);
                Assert.Equal("Plan\n\nStep one", two.Text);
            },
            three =>
            {
                Assert.Equal(DocumentLocation.ForSlide(3), three.Location);
                Assert.Equal("A text box with no title", three.Text);
            });
        Assert.Equal("[Slide 1: Welcome]\nWelcome\n\nHello there\nSecond bullet\n\n[Slide 2: Plan]\nPlan\n\nStep one\n\n[Slide 3]\nA text box with no title", result.ToText());
        Assert.Equal("slide 2", result.Segments[1].Location.Describe());
    }

    [Fact]
    public async Task SlidesComeInTheOrderOfThePresentationNotTheOrderOfTheFiles()
    {
        var result = await ReadAsync(
            [Title("File one"), Title("File two"), Title("File three")],
            new PptxOptions { ListOrder = [3, 1, 2] });

        Assert.Equal(["File three", "File one", "File two"], result.Segments.Select(s => s.Location.Label!).ToArray());
        Assert.Equal([1, 2, 3], result.Segments.Select(s => s.Location.Number).ToArray());
    }

    [Fact]
    public async Task ShapesComeInPlaceAndGroupsAreReadWhereTheyAre()
    {
        var result = await ReadAsync([Title("T") + Shape(null, "first shape") + Group(Shape(null, "in the group"), Shape(null, "also in it")) + Shape(null, "last shape")]);

        Assert.Equal("T\n\nfirst shape\n\nin the group\n\nalso in it\n\nlast shape", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task TheFooterDateAndSlideNumberAreLeftOutAndSoAreTheirFields()
    {
        var footer = Shape("ftr", "FOOTER-TEXT") + Shape("dt", "DATE-TEXT") + Shape("sldNum", "NUMBER-TEXT");
        var field = "<p:sp><p:nvSpPr><p:cNvPr id=\"7\" name=\"F\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>"
            + "<a:p><a:r><a:t>Page </a:t></a:r><a:fld id=\"{1}\" type=\"slidenum\"><a:rPr/><a:t>FIELD-NUMBER</a:t></a:fld>"
            + "<a:fld id=\"{2}\" type=\"datetime1\"><a:t>FIELD-DATE</a:t></a:fld><a:fld id=\"{3}\" type=\"other\"><a:t>kept</a:t></a:fld></a:p></p:txBody></p:sp>";

        var result = await ReadAsync([Title("Real") + footer + field]);

        Assert.Equal("Real\n\nPage kept", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task SpeakerNotesFollowTheirSlideAsTheirOwnPieceAndOnlyTheNotesAreTaken()
    {
        var notes = Shape("sldImg", "SLIDE-IMAGE-TEXT") + Shape("body", "Say this first.", "Then this.") + Shape("sldNum", "7");
        var options = new PptxOptions { Notes = new Dictionary<int, string> { [2] = notes } };

        var result = await ReadAsync([Title("One"), Title("Two")], options);

        Assert.Collection(
            result.Segments,
            s => Assert.Equal(DocumentLocation.ForSlide(1, "One"), s.Location),
            s => Assert.Equal(DocumentLocation.ForSlide(2, "Two"), s.Location),
            s =>
            {
                Assert.Equal(DocumentLocation.ForSlideNotes(2), s.Location);
                Assert.Equal("Say this first.\nThen this.", s.Text);
            });
        Assert.Contains("[Slide 2 notes]\nSay this first.", result.ToText());
        Assert.Equal("slide 2 notes", result.Segments[2].Location.Describe());
    }

    [Fact]
    public async Task ATableIsOneRowToALineAndNestedShapesAreKept()
    {
        var result = await ReadAsync([Title("Numbers") + Table(["Name", "Qty"], ["Apple", "3"], [string.Empty, string.Empty], ["Pear", string.Empty])]);

        Assert.Equal("Numbers\n\nName | Qty\nApple | 3\nPear", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task TheTextOfASmartArtDiagramIsReadWhereTheDiagramIsAndItsLayoutPartsAreNot()
    {
        var options = new PptxOptions { Diagrams = new Dictionary<int, string> { [1] = DiagramData("Node A", "Node B") } };

        var result = await ReadAsync([Title("Process") + DiagramFrame("rIdDiagram") + Shape(null, "after the diagram")], options);

        Assert.Equal("Process\n\nNode A\nNode B\n\nafter the diagram", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task AContentChoiceIsReadOnceAndItsFallbackNever()
    {
        var alternate = "<mc:AlternateContent><mc:Choice Requires=\"p14\">" + Shape(null, "Current content") + "</mc:Choice><mc:Fallback>" + Shape(null, "Old copy") + "</mc:Fallback></mc:AlternateContent>";

        var result = await ReadAsync([alternate]);

        Assert.Equal("Current content", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task ALineBreakInAParagraphIsALineBreak()
    {
        var shape = "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"S\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>"
            + "<a:p><a:r><a:t>top</a:t></a:r><a:br><a:rPr/></a:br><a:r><a:t>bottom</a:t></a:r></a:p></p:txBody></p:sp>";

        var result = await ReadAsync([shape]);

        Assert.Equal("top\nbottom", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task ASlideWithNoTextKeepsItsNumberForTheNextOneAndAHiddenSlideIsStillRead()
    {
        var blank = Slide(string.Empty);
        var hidden = Slide(Title("Hidden one"), hidden: true);

        var result = await ReadAsync([Title("First"), blank, hidden, Title("Fourth")]);

        Assert.Equal([1, 3, 4], result.Segments.Select(s => s.Location.Number).ToArray());
        Assert.Equal(4, result.Metadata!.UnitCount);
    }

    [Fact]
    public async Task ThePresentationsOwnPropertiesAreTheMetadata()
    {
        var path = _folder.WriteBytes("meta.pptx", Create([Title("a"), Title("b"), Title("c")], new PptxOptions { Title = "Launch plan", Author = "P. Resenter" }));

        var metadata = await _reader.ReadMetadataAsync(path);
        var read = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Success, metadata.Status);
        var value = metadata.Metadata!;
        Assert.Equal("pptx", value.ReaderId);
        Assert.Equal(".pptx", value.Extension);
        Assert.Equal(new FileInfo(path).Length, value.SizeBytes);
        Assert.Equal("Launch plan", value.Title);
        Assert.Equal("P. Resenter", value.Author);
        Assert.Equal(DocumentUnitKind.Slide, value.UnitKind);
        Assert.Equal(3, value.UnitCount);
        Assert.Equal(value, read.Metadata);
    }

    [Fact]
    public async Task APresentationOfPicturesHasNoText()
    {
        var result = await ReadAsync([Slide(string.Empty), Slide(string.Empty)]);

        Assert.Equal(DocumentReadStatus.NoText, result.Status);
        Assert.Empty(result.Segments);
        Assert.Equal(2, result.Metadata!.UnitCount);
    }

    [Fact]
    public async Task AnEmbeddedObjectAndAMacroAreNeverReadOrRun()
    {
        var oleShape = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"8\" name=\"O\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm/>"
            + "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/presentationml/2006/ole\"><p:oleObj r:id=\"rIdOle\" progId=\"Excel.Sheet.12\"><p:embed/></p:oleObj></a:graphicData></a:graphic></p:graphicFrame>";
        var options = new PptxOptions
        {
            ExtraParts = new Dictionary<string, byte[]>
            {
                ["ppt/vbaProject.bin"] = Encoding.UTF8.GetBytes("MACRO-MARKER"),
                ["ppt/embeddings/oleObject1.bin"] = Encoding.UTF8.GetBytes("OLE-MARKER"),
            },
            ExtraContentTypes =
                "<Override PartName=\"/ppt/vbaProject.bin\" ContentType=\"application/vnd.ms-office.vbaProject\"/>"
                + "<Override PartName=\"/ppt/embeddings/oleObject1.bin\" ContentType=\"application/vnd.openxmlformats-officedocument.oleObject\"/>",
            ExtraSlideRelationships =
                "<Relationship Id=\"rIdOle\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject\" Target=\"../embeddings/oleObject1.bin\"/>"
                + "<Relationship Id=\"rIdVba\" Type=\"http://schemas.microsoft.com/office/2006/relationships/vbaProject\" Target=\"../vbaProject.bin\"/>",
        };

        var result = await ReadAsync([Title("Plain slide") + oleShape], options);

        Assert.Equal("Plain slide", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task NoLinkOfAnyKindIsEverFetched()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/probe";
        var options = new PptxOptions
        {
            ExtraSlideRelationships =
                $"<Relationship Id=\"rIdLink\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"{address}\" TargetMode=\"External\"/>"
                + $"<Relationship Id=\"rIdMedia\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/video\" Target=\"{address}.mp4\" TargetMode=\"External\"/>"
                + $"<Relationship Id=\"rIdOle\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject\" Target=\"{address}.xlsx\" TargetMode=\"External\"/>",
        };
        var link = "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"L\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>"
            + "<a:p><a:r><a:rPr><a:hlinkClick r:id=\"rIdLink\"/></a:rPr><a:t>a link</a:t></a:r></a:p></p:txBody></p:sp>";

        var read = await ReadAsync([Title("Links") + link], options);
        var metadata = await _reader.ReadMetadataAsync(_folder.File("deck.pptx"));
        await Task.Delay(300);

        Assert.Equal("Links\n\na link", Assert.Single(read.Segments).Text);
        Assert.Equal(DocumentReadStatus.Success, metadata.Status);
        Assert.False(listener.Pending(), "a reader contacted an address a presentation named");
    }

    [Fact]
    public async Task ADocumentTypeDeclarationIsRefusedSoNoEntityIsEverExpanded()
    {
        var secret = _folder.Write("secret.txt", "TOP-SECRET-CONTENT");
        var prolog = $"<!DOCTYPE p:sld [<!ENTITY xxe SYSTEM \"file:///{secret.Replace('\\', '/')}\">]>";
        var slide = Slide("<p:sp><p:txBody><a:p><a:r><a:t>&xxe;</a:t></a:r></a:p></p:txBody></p:sp>", prolog);

        var result = await ReadAsync([slide]);

        Assert.Equal(DocumentReadStatus.Corrupt, result.Status);
        Assert.Empty(result.Segments);
    }

    [Fact]
    public async Task APasswordProtectedPresentationIsEncryptedAndALegacyPptIsUnsupported()
    {
        var encrypted = CompoundFile("EncryptedPackage");
        var legacy = CompoundFile("PowerPoint Document");

        Assert.Equal(DocumentReadStatus.Encrypted, (await _reader.ReadAsync(_folder.WriteBytes("locked.pptx", encrypted))).Status);
        Assert.Equal(DocumentReadStatus.Unsupported, (await _reader.ReadAsync(_folder.WriteBytes("old.pptx", legacy))).Status);
        Assert.Equal(DocumentReadStatus.Encrypted, (await _reader.ReadMetadataAsync(_folder.WriteBytes("locked2.pptx", encrypted))).Status);
    }

    [Fact]
    public async Task AFileThatIsNotAPackageOrNotAPresentationIsCorrupt()
    {
        var document = DocxFixture.Create(DocxFixture.P("a word document"));

        Assert.Equal(DocumentReadStatus.Corrupt, (await _reader.ReadAsync(_folder.Write("fake.pptx", "not a zip"))).Status);
        Assert.Equal(DocumentReadStatus.Corrupt, (await _reader.ReadAsync(_folder.WriteBytes("word.pptx", document))).Status);
        Assert.Equal(DocumentReadStatus.Corrupt, (await _reader.ReadMetadataAsync(_folder.WriteBytes("word2.pptx", document))).Status);
        Assert.Equal(DocumentReadStatus.Corrupt, (await _reader.ReadAsync(_folder.WriteBytes("empty.pptx", new OfficePackage().ToBytes()))).Status);
    }

    [Fact]
    public async Task AFileLargerThanTheLimitIsTooLargeAndAPackageThatExpandsPastTheLimitIsToo()
    {
        var path = _folder.WriteBytes("big.pptx", Create([Title("x")]));
        var size = new FileInfo(path).Length;
        var bomb = _folder.WriteBytes("bomb.pptx", Create([Slide(Shape(null, new string('a', 3_000_000)))]));

        Assert.Equal(DocumentReadStatus.TooLarge, (await _reader.ReadAsync(path, new DocumentReadOptions { MaxFileBytes = size - 1 })).Status);
        Assert.Equal(DocumentReadStatus.Success, (await _reader.ReadAsync(path, new DocumentReadOptions { MaxFileBytes = size })).Status);
        Assert.Equal(DocumentReadStatus.TooLarge, (await _reader.ReadAsync(bomb, new DocumentReadOptions { MaxExpandedBytes = 1_000_000 })).Status);
    }

    [Fact]
    public async Task SlidesPastTheUnitLimitAreLeftOutAndSaySo()
    {
        var result = await ReadAsync([Title("1"), Title("2"), Title("3"), Title("4")], limits: new DocumentReadOptions { MaxUnits = 2 });

        Assert.Equal([1, 2], result.Segments.Select(s => s.Location.Number).ToArray());
        Assert.True(result.Truncated);
        Assert.Equal(4, result.Metadata!.UnitCount);
    }

    [Fact]
    public async Task TextPastTheCharacterLimitIsLeftOutAndTheReadStops()
    {
        var slides = Enumerable.Range(1, 50).Select(i => Title("Slide title " + i) + Body("Some body text for the slide " + i)).ToArray();

        var result = await ReadAsync(slides, limits: new DocumentReadOptions { MaxCharacters = 300 });

        Assert.True(result.Truncated);
        Assert.InRange(result.CharacterCount, 200, 300);
        Assert.True(result.Segments.Count < 50);
    }

    [Fact]
    public async Task SlidesThatJustFitTheCharacterLimitAreNotTruncated()
    {
        var exact = await ReadAsync([Shape(null, new string('a', 100))], limits: new DocumentReadOptions { MaxCharacters = 100 });
        var under = await ReadAsync([Shape(null, new string('a', 100))], limits: new DocumentReadOptions { MaxCharacters = 99 });

        Assert.False(exact.Truncated);
        Assert.True(under.Truncated);
    }

    [Fact]
    public async Task CancellingBeforeTheReadStopsItWithACancellation()
    {
        var path = _folder.WriteBytes("a.pptx", Create([Title("x")]));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadAsync(path, null, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadMetadataAsync(path, null, cancelled.Token));
    }

    [Fact]
    public void CancellingInTheMiddleOfASlideStopsItBetweenElements()
    {
        using var cancellation = new CancellationTokenSource();
        var xml = Slide(string.Concat(Enumerable.Repeat(Shape(null, "paragraph"), 100_000)));
        using var source = new CancelOnThirdRead(Encoding.UTF8.GetBytes(xml), cancellation);
        using var reader = SafeXml.Create(source, long.MaxValue);

        Assert.ThrowsAny<OperationCanceledException>(() => DrawingTextReader.ReadSlide(reader, null, long.MaxValue, cancellation.Token));
        Assert.True(source.Reads < 50, "the reader went on after it was cancelled");
    }

    [Fact]
    public async Task ASlideNestedTooDeepIsCorruptAndNotACrash()
    {
        var groups = string.Concat(Enumerable.Repeat("<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>", 20_000))
            + Shape(null, "deep") + string.Concat(Enumerable.Repeat("</p:grpSp>", 20_000));

        var result = await ReadAsync([groups]);

        Assert.Equal(DocumentReadStatus.Corrupt, result.Status);
    }

    [Fact]
    public async Task APresentationAnotherProgramHasOpenIsStillRead()
    {
        var path = _folder.WriteBytes("open.pptx", Create([Title("being edited")]));
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);

        var result = await _reader.ReadAsync(path);

        Assert.Equal("being edited", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task ReadingNeverChangesTheFile()
    {
        var path = _folder.WriteBytes("keep.pptx", Create([Title("keep me")]));
        var before = await File.ReadAllBytesAsync(path);

        await _reader.ReadAsync(path);
        await _reader.ReadMetadataAsync(path);

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task APresentationMadeByTheOpenXmlSdkItselfIsRead()
    {
        var path = _folder.File("sdk.pptx");
        using (var document = DocumentFormat.OpenXml.Packaging.PresentationDocument.Create(path, DocumentFormat.OpenXml.PresentationDocumentType.Presentation))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new DocumentFormat.OpenXml.Presentation.Presentation(new DocumentFormat.OpenXml.Presentation.SlideIdList());

            var slidePart = presentationPart.AddNewPart<DocumentFormat.OpenXml.Packaging.SlidePart>();
            slidePart.Slide = new DocumentFormat.OpenXml.Presentation.Slide(
                new DocumentFormat.OpenXml.Presentation.CommonSlideData(
                    new DocumentFormat.OpenXml.Presentation.ShapeTree(
                        new DocumentFormat.OpenXml.Presentation.Shape(
                            new DocumentFormat.OpenXml.Presentation.NonVisualShapeProperties(
                                new DocumentFormat.OpenXml.Presentation.NonVisualDrawingProperties { Id = 2, Name = "Title" },
                                new DocumentFormat.OpenXml.Presentation.NonVisualShapeDrawingProperties(),
                                new DocumentFormat.OpenXml.Presentation.ApplicationNonVisualDrawingProperties(
                                    new DocumentFormat.OpenXml.Presentation.PlaceholderShape { Type = DocumentFormat.OpenXml.Presentation.PlaceholderValues.Title })),
                            new DocumentFormat.OpenXml.Presentation.ShapeProperties(),
                            new DocumentFormat.OpenXml.Presentation.TextBody(
                                new DocumentFormat.OpenXml.Drawing.BodyProperties(),
                                new DocumentFormat.OpenXml.Drawing.Paragraph(
                                    new DocumentFormat.OpenXml.Drawing.Run(new DocumentFormat.OpenXml.Drawing.Text("Made by the SDK"))))))));

            presentationPart.Presentation.SlideIdList!.Append(
                new DocumentFormat.OpenXml.Presentation.SlideId { Id = 256, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
        }

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(DocumentLocation.ForSlide(1, "Made by the SDK"), segment.Location);
        Assert.Equal("Made by the SDK", segment.Text);
    }

    private static byte[] CompoundFile(string streamName)
    {
        var bytes = new byte[4096];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);
        Encoding.Unicode.GetBytes(streamName).CopyTo(bytes, 512);
        return bytes;
    }

    private sealed class CancelOnThirdRead(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public int Reads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            if (Reads == 3)
            {
                cancellation.Cancel();
            }

            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            if (Reads == 3)
            {
                cancellation.Cancel();
            }

            return base.Read(buffer);
        }
    }
}
