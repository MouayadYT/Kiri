using System.Net;
using System.Net.Sockets;
using System.Text;
using Assistant.Core.Documents;
using Assistant.Documents.OpenXml;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Assistant.Documents.Tests.DocxFixture;

namespace Assistant.Documents.Tests;

/// <summary>Reading Word documents: order, headings as sections, what is and is not text, and everything a hostile file could try.</summary>
public sealed class DocxDocumentReaderTests : IDisposable
{
    private const string LinkType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";

    private readonly TempFolder _folder = new();
    private readonly DocxDocumentReader _reader = new(NullLogger<DocxDocumentReader>.Instance);

    public void Dispose() => _folder.Dispose();

    private async Task<DocumentReadResult> ReadAsync(string body, DocxOptions? options = null, DocumentReadOptions? limits = null) =>
        await _reader.ReadAsync(_folder.WriteBytes("doc.docx", Create(body, options)), limits);

    private static string[] Texts(DocumentReadResult result) => result.Segments.Select(s => s.Text).ToArray();

    private static string[] Labels(DocumentReadResult result) => result.Segments.Select(s => s.Location.Label ?? "(none)").ToArray();

    [Fact]
    public void TheReaderSaysWhatItHandles()
    {
        Assert.Equal("docx", _reader.Id);
        Assert.Equal([".docx"], _reader.SupportedExtensions);
        Assert.Equal(["application/vnd.openxmlformats-officedocument.wordprocessingml.document"], _reader.SupportedMimeTypes);
    }

    [Fact]
    public async Task ParagraphsKeepTheirOrderAndBlankOnesAreLeftOut()
    {
        var result = await ReadAsync(P("First paragraph.") + P(string.Empty) + P("Second paragraph.") + "<w:p/>" + P("   ") + P("Third."));

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(DocumentLocationKind.Document, segment.Location.Kind);
        Assert.Equal("First paragraph.\n\nSecond paragraph.\n\nThird.", segment.Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task EachHeadingStartsASectionWithItsTrailInTheOrderOfTheDocument()
    {
        var result = await ReadAsync(
            P("Preamble.") + Heading(1, "Install") + P("Run setup.") + Heading(2, "Windows") + P("Use the installer.")
            + Heading(2, "Linux") + P("Use apt.") + Heading(1, "Usage") + P("Start it."),
            new DocxOptions { Styles = Styles() });

        Assert.Equal(["(none)", "Install", "Install > Windows", "Install > Linux", "Usage"], Labels(result));
        Assert.Equal(
            ["Preamble.", "Install\n\nRun setup.", "Windows\n\nUse the installer.", "Linux\n\nUse apt.", "Usage\n\nStart it."],
            Texts(result));
        Assert.Equal([1, 2, 3, 4, 5], result.Segments.Select(s => s.Location.Number).ToArray());
        Assert.All(result.Segments, s => Assert.Equal(DocumentLocationKind.Section, s.Location.Kind));
        Assert.StartsWith("[Section 1]\nPreamble.\n\n[Section 2: Install]\nInstall\n\nRun setup.", result.ToText());
    }

    [Fact]
    public async Task AStyleIsAHeadingByItsOutlineLevelItsEnglishNameOrTheStyleItIsBasedOn()
    {
        var result = await ReadAsync(
            P("Based on Heading 1", "Kop1") + P("body a") + P("Localised id", "berschrift1") + P("body b")
            + P("By outline in the style", "Outlined") + P("body c")
            + "<w:p><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr><w:r><w:t>By outline on the paragraph</w:t></w:r></w:p>" + P("body d"),
            new DocxOptions { Styles = Styles() });

        Assert.Equal(
            ["Based on Heading 1", "Localised id", "Localised id > By outline in the style", "By outline on the paragraph"],
            Labels(result));
    }

    [Fact]
    public async Task TheTitleStyleAndAParagraphMarkedAsBodyTextAreNotHeadings()
    {
        var result = await ReadAsync(
            P("A Title", "Title") + P("text")
            + "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/><w:outlineLvl w:val=\"9\"/></w:pPr><w:r><w:t>Looks like a heading, is body</w:t></w:r></w:p>",
            new DocxOptions { Styles = Styles() });

        var segment = Assert.Single(result.Segments);
        Assert.Equal(DocumentLocationKind.Document, segment.Location.Kind);
        Assert.Equal("A Title\n\ntext\n\nLooks like a heading, is body", segment.Text);
    }

    [Fact]
    public async Task ADocumentWithoutAStylesPartHasNoHeadingsByStyle()
    {
        var result = await ReadAsync(Heading(1, "Not known as a heading") + P("text"));

        Assert.Equal(DocumentLocationKind.Document, Assert.Single(result.Segments).Location.Kind);
    }

    [Fact]
    public async Task ATableIsOneRowToALineWithItsCellsBetweenBars()
    {
        var result = await ReadAsync(
            P("Before") + Table(["Name", "Qty"], ["Apple", "3"], [string.Empty, string.Empty], ["Pear", string.Empty]) + P("After"));

        Assert.Equal("Before\n\nName | Qty\nApple | 3\nPear\n\nAfter", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task ATableInACellIsPartOfThatCellsText()
    {
        var nested =
            "<w:tbl><w:tr><w:tc>" + P("outer") + "<w:tbl><w:tr><w:tc>" + P("in1") + "</w:tc><w:tc>" + P("in2") + "</w:tc></w:tr></w:tbl><w:p/></w:tc>"
            + "<w:tc>" + P("side") + "</w:tc></w:tr></w:tbl>";

        var result = await ReadAsync(nested);

        Assert.Equal("outer in1 | in2 | side", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task AHeadingInsideATableDoesNotStartASection()
    {
        var result = await ReadAsync(
            Heading(1, "Real") + "<w:tbl><w:tr><w:tc>" + Heading(1, "In a cell") + "</w:tc></w:tr></w:tbl>" + P("after"),
            new DocxOptions { Styles = Styles() });

        Assert.Equal(["Real"], Labels(result));
        Assert.Equal("Real\n\nIn a cell\n\nafter", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task TabsLineBreaksAndNonBreakingHyphensAreTextAndPropertiesAreNot()
    {
        var body =
            "<w:p><w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"720\"/></w:tabs></w:pPr><w:r><w:rPr><w:b/></w:rPr>"
            + "<w:t>a</w:t><w:tab/><w:t>b</w:t><w:br/><w:t>c</w:t><w:noBreakHyphen/><w:t>d</w:t></w:r></w:p>";

        var result = await ReadAsync(body);

        Assert.Equal("a\tb\nc-d", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task TrackedDeletionsAreLeftOutAndInsertionsAreKept()
    {
        var body =
            "<w:p><w:r><w:t xml:space=\"preserve\">Keep </w:t></w:r>"
            + "<w:del w:id=\"1\"><w:r><w:delText>removed</w:delText></w:r></w:del>"
            + "<w:ins w:id=\"2\"><w:r><w:t>added</w:t></w:r></w:ins>"
            + "<w:moveFrom w:id=\"3\"><w:r><w:t> moved away</w:t></w:r></w:moveFrom>"
            + "<w:moveTo w:id=\"4\"><w:r><w:t xml:space=\"preserve\"> moved in</w:t></w:r></w:moveTo></w:p>";

        var result = await ReadAsync(body);

        Assert.Equal("Keep added moved in", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task ATrackedChangeToAParagraphsStyleDoesNotMakeItAHeading()
    {
        var body =
            "<w:p><w:pPr><w:pPrChange w:id=\"1\"><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr></w:pPrChange></w:pPr><w:r><w:t>Plain now</w:t></w:r></w:p>";

        var result = await ReadAsync(body, new DocxOptions { Styles = Styles() });

        Assert.Equal(DocumentLocationKind.Document, Assert.Single(result.Segments).Location.Kind);
    }

    [Fact]
    public async Task AFieldShowsItsResultAndNeverItsCommand()
    {
        var body =
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> HYPERLINK \"http://secret.example/command\" </w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>shown text</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>"
            + "<w:p><w:fldSimple w:instr=\" INCLUDETEXT &quot;C:\\secret\\other.docx&quot; \"><w:r><w:t>cached result</w:t></w:r></w:fldSimple></w:p>";

        var result = await ReadAsync(body);

        var text = Assert.Single(result.Segments).Text;
        Assert.Equal("shown text\n\ncached result", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("HYPERLINK", text);
    }

    [Fact]
    public async Task ALinkShowsItsTextAndNeverItsAddress()
    {
        var body = "<w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>click here</w:t></w:r></w:hyperlink></w:p>";
        var options = new DocxOptions { ExtraRelationships = $"<Relationship Id=\"rIdLink\" Type=\"{LinkType}\" Target=\"http://secret.example/page\" TargetMode=\"External\"/>" };

        var result = await ReadAsync(body, options);

        Assert.Equal("click here", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task ATextBoxIsReadOnceAndAfterTheParagraphItIsAnchoredIn()
    {
        var body =
            "<w:p><w:r><w:t>Host text</w:t></w:r><w:r><mc:AlternateContent>"
            + "<mc:Choice Requires=\"wps\"><w:drawing><wp:inline><a:graphic><a:graphicData uri=\"x\"><wps:wsp><wps:txbx><w:txbxContent>"
            + "<w:p><w:r><w:t>Inside the box</w:t></w:r></w:p></w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></mc:Choice>"
            + "<mc:Fallback><w:pict><v:shape><v:textbox><w:txbxContent><w:p><w:r><w:t>Inside the box</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></mc:Fallback>"
            + "</mc:AlternateContent></w:r></w:p>" + P("Next paragraph");

        var result = await ReadAsync(body);

        Assert.Equal("Host text\n\nInside the box\n\nNext paragraph", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task AnEquationsTextIsKept()
    {
        var body = "<w:p><m:oMath><m:r><m:t>E=mc</m:t></m:r></m:oMath><w:r><w:t xml:space=\"preserve\"> is famous</w:t></w:r></w:p>";

        var result = await ReadAsync(body);

        Assert.Equal("E=mc is famous", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task FootnotesAndEndnotesFollowTheBodyEachAsASectionAndSeparatorsAreNotNotes()
    {
        var options = new DocxOptions
        {
            Footnotes = "<w:footnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:t>SEPARATOR</w:t></w:r></w:p></w:footnote>"
                + "<w:footnote w:id=\"1\">" + P("First footnote.") + "</w:footnote><w:footnote w:id=\"2\">" + P("Second footnote.") + "</w:footnote>",
            Endnotes = "<w:endnote w:type=\"continuationSeparator\" w:id=\"-1\"><w:p><w:r><w:t>SEPARATOR</w:t></w:r></w:p></w:endnote>"
                + "<w:endnote w:id=\"1\">" + P("An endnote.") + "</w:endnote>",
        };

        var result = await ReadAsync(P("The body."), options);

        Assert.Equal(["(none)", "Footnotes", "Endnotes"], Labels(result));
        Assert.Equal(["The body.", "First footnote.\n\nSecond footnote.", "An endnote."], Texts(result));
        Assert.DoesNotContain("SEPARATOR", result.ToText());
    }

    [Fact]
    public async Task HeadersFootersAndCommentsAreNotPartOfTheText()
    {
        var parts = new Dictionary<string, byte[]>
        {
            ["word/header1.xml"] = Encoding.UTF8.GetBytes(Xml + $"<w:hdr xmlns:w=\"{W}\">" + P("HEADER-TEXT") + "</w:hdr>"),
            ["word/comments.xml"] = Encoding.UTF8.GetBytes(Xml + $"<w:comments xmlns:w=\"{W}\"><w:comment w:id=\"0\">" + P("COMMENT-TEXT") + "</w:comment></w:comments>"),
        };
        var options = new DocxOptions
        {
            ExtraParts = parts,
            ExtraContentTypes =
                "<Override PartName=\"/word/header1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/>"
                + "<Override PartName=\"/word/comments.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml\"/>",
            ExtraRelationships =
                "<Relationship Id=\"rIdH\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/>"
                + "<Relationship Id=\"rIdC\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments\" Target=\"comments.xml\"/>",
        };

        var result = await ReadAsync(P("Body only."), options);

        Assert.Equal("Body only.", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task AMacroProjectAndAnEmbeddedObjectAreNeverReadOrRun()
    {
        var options = new DocxOptions
        {
            ExtraParts = new Dictionary<string, byte[]>
            {
                ["word/vbaProject.bin"] = Encoding.UTF8.GetBytes("MACRO-MARKER Sub AutoOpen() Shell \"calc\" End Sub"),
                ["word/embeddings/oleObject1.bin"] = Encoding.UTF8.GetBytes("OLE-MARKER"),
            },
            ExtraContentTypes =
                "<Override PartName=\"/word/vbaProject.bin\" ContentType=\"application/vnd.ms-office.vbaProject\"/>"
                + "<Override PartName=\"/word/embeddings/oleObject1.bin\" ContentType=\"application/vnd.openxmlformats-officedocument.oleObject\"/>",
            ExtraRelationships =
                "<Relationship Id=\"rIdVba\" Type=\"http://schemas.microsoft.com/office/2006/relationships/vbaProject\" Target=\"vbaProject.bin\"/>"
                + "<Relationship Id=\"rIdOle\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject\" Target=\"embeddings/oleObject1.bin\"/>"
                + "<Relationship Id=\"rIdOleLink\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject\" Target=\"file:///C:/secret.xlsx\" TargetMode=\"External\"/>",
        };

        var result = await ReadAsync(P("Plain text.") + "<w:p><w:r><w:object><v:shape/></w:object></w:r></w:p>", options);

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        var text = Assert.Single(result.Segments).Text;
        Assert.Equal("Plain text.", text);
    }

    [Fact]
    public async Task NoLinkOfAnyKindIsEverFetched()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/probe";

        var options = new DocxOptions
        {
            ExtraRelationships =
                $"<Relationship Id=\"rIdLink\" Type=\"{LinkType}\" Target=\"{address}\" TargetMode=\"External\"/>"
                + $"<Relationship Id=\"rIdImage\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"{address}.png\" TargetMode=\"External\"/>"
                + $"<Relationship Id=\"rIdChunk\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/aFChunk\" Target=\"{address}.docx\" TargetMode=\"External\"/>"
                + $"<Relationship Id=\"rIdTemplate\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate\" Target=\"{address}.dotm\" TargetMode=\"External\"/>"
                + $"<Relationship Id=\"rIdOle\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject\" Target=\"{address}.bin\" TargetMode=\"External\"/>",
        };
        var body =
            "<w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>link text</w:t></w:r></w:hyperlink></w:p>"
            + "<w:altChunk r:id=\"rIdChunk\"/>"
            + $"<w:p><w:fldSimple w:instr=\" INCLUDETEXT &quot;{address}.txt&quot; \"><w:r><w:t>included</w:t></w:r></w:fldSimple></w:p>";

        var read = await ReadAsync(body, options);
        var metadata = await _reader.ReadMetadataAsync(_folder.File("doc.docx"));
        await Task.Delay(300);

        Assert.Equal(DocumentReadStatus.Success, read.Status);
        Assert.Equal("link text\n\nincluded", Assert.Single(read.Segments).Text);
        Assert.Equal(DocumentReadStatus.Success, metadata.Status);
        Assert.False(listener.Pending(), "a reader contacted an address a document named");
    }

    [Fact]
    public async Task ADocumentTypeDeclarationIsRefusedSoNoEntityIsEverExpanded()
    {
        var secret = _folder.Write("secret.txt", "TOP-SECRET-CONTENT");
        var prolog = $"<!DOCTYPE w:document [<!ENTITY xxe SYSTEM \"file:///{secret.Replace('\\', '/')}\">]>";

        var result = await ReadAsync("<w:p><w:r><w:t>&xxe;</w:t></w:r></w:p>", new DocxOptions { Prolog = prolog });

        Assert.Equal(DocumentReadStatus.Corrupt, result.Status);
        Assert.Empty(result.Segments);
        Assert.DoesNotContain("TOP-SECRET", result.ToText());
    }

    [Fact]
    public async Task AnEntityBombIsRefusedAtOnce()
    {
        var prolog = "<!DOCTYPE w:document [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]>";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await ReadAsync("<w:p><w:r><w:t>&c;</w:t></w:r></w:p>", new DocxOptions { Prolog = prolog });

        Assert.Equal(DocumentReadStatus.Corrupt, result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TheMetadataHasTheTitleAuthorDatesAndPageCount()
    {
        var path = _folder.WriteBytes("meta.docx", Create(P("text"), new DocxOptions { Title = "Quarterly report", Author = "A. Writer", Pages = 7 }));

        var metadata = await _reader.ReadMetadataAsync(path);
        var read = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Success, metadata.Status);
        var value = metadata.Metadata!;
        Assert.Equal("docx", value.ReaderId);
        Assert.Equal(".docx", value.Extension);
        Assert.Equal(new FileInfo(path).Length, value.SizeBytes);
        Assert.Equal("Quarterly report", value.Title);
        Assert.Equal("A. Writer", value.Author);
        Assert.Equal(new DateTimeOffset(2024, 3, 5, 10, 20, 30, TimeSpan.Zero), value.CreatedAt);
        Assert.Equal(new DateTimeOffset(2025, 6, 7, 8, 9, 10, TimeSpan.Zero), value.ModifiedAt);
        Assert.Equal(DocumentUnitKind.Page, value.UnitKind);
        Assert.Equal(7, value.UnitCount);
        Assert.Equal(value, read.Metadata);
    }

    [Fact]
    public async Task ADocumentWithoutPropertiesHasNoneOfThem()
    {
        var path = _folder.WriteBytes("plain.docx", Create(P("text")));

        var metadata = await _reader.ReadMetadataAsync(path);

        Assert.Null(metadata.Metadata!.Title);
        Assert.Null(metadata.Metadata.Author);
        Assert.Equal(DocumentUnitKind.None, metadata.Metadata.UnitKind);
        Assert.Null(metadata.Metadata.UnitCount);
    }

    [Fact]
    public async Task ADocumentWithNoTextHasNoText()
    {
        var result = await ReadAsync(P(string.Empty) + "<w:p/>");

        Assert.Equal(DocumentReadStatus.NoText, result.Status);
        Assert.Empty(result.Segments);
        Assert.NotNull(result.Metadata);
    }

    [Fact]
    public async Task APasswordProtectedDocumentIsEncryptedAndALegacyDocIsUnsupported()
    {
        var encrypted = CompoundFile("EncryptedPackage");
        var legacy = CompoundFile("WordDocument");

        var protectedResult = await _reader.ReadAsync(_folder.WriteBytes("locked.docx", encrypted));
        var legacyResult = await _reader.ReadAsync(_folder.WriteBytes("old.docx", legacy));
        var metadata = await _reader.ReadMetadataAsync(_folder.WriteBytes("locked2.docx", encrypted));

        Assert.Equal(DocumentReadStatus.Encrypted, protectedResult.Status);
        Assert.Empty(protectedResult.Segments);
        Assert.Null(protectedResult.Metadata);
        Assert.Equal(DocumentReadStatus.Unsupported, legacyResult.Status);
        Assert.Equal(DocumentReadStatus.Encrypted, metadata.Status);
    }

    [Fact]
    public async Task TheNameOfTheEncryptedStreamIsFoundAcrossTheEdgeOfAReadBuffer()
    {
        // The name begins six bytes before the end of the first read, so it straddles the two reads.
        var name = Encoding.Unicode.GetBytes("EncryptedPackage");
        var firstRead = (1 << 20) + name.Length;
        var bytes = CompoundFile(null, padding: firstRead + 64);
        name.CopyTo(bytes, firstRead - 6);

        var result = await _reader.ReadAsync(_folder.WriteBytes("edge.docx", bytes));

        Assert.Equal(DocumentReadStatus.Encrypted, result.Status);
    }

    [Theory]
    [InlineData("not a zip at all")]
    [InlineData("")]
    public async Task AFileThatIsNotAPackageIsCorruptAndNeverAnException(string content)
    {
        var path = _folder.Write("fake.docx", content);

        Assert.Equal(DocumentReadStatus.Corrupt, (await _reader.ReadAsync(path)).Status);
        Assert.Equal(DocumentReadStatus.Corrupt, (await _reader.ReadMetadataAsync(path)).Status);
    }

    [Fact]
    public async Task APackageThatIsNotAWordDocumentIsCorrupt()
    {
        var emptyZip = new OfficePackage().ToBytes();
        var presentation = PptxFixture.Create([PptxFixture.Title("a slide")]);
        var otherZip = new OfficePackage().Add("hello.txt", "hello").ToBytes();

        Assert.Equal(DocumentReadStatus.Corrupt, (await _reader.ReadAsync(_folder.WriteBytes("empty.docx", emptyZip))).Status);
        Assert.Equal(DocumentReadStatus.Corrupt, (await _reader.ReadAsync(_folder.WriteBytes("other.docx", otherZip))).Status);
        Assert.Equal(DocumentReadStatus.Corrupt, (await _reader.ReadAsync(_folder.WriteBytes("slides.docx", presentation))).Status);
    }

    [Fact]
    public async Task ADocumentCutShortInTheMiddleIsCorruptNotAnException()
    {
        var bytes = Create(string.Concat(Enumerable.Range(1, 200).Select(i => P("Paragraph number " + i))));

        var result = await _reader.ReadAsync(_folder.WriteBytes("cut.docx", bytes[..(bytes.Length / 2)]));

        Assert.Equal(DocumentReadStatus.Corrupt, result.Status);
    }

    [Fact]
    public async Task AFileLargerThanTheLimitIsTooLargeAndIsNotOpened()
    {
        var path = _folder.WriteBytes("big.docx", Create(P("text")));
        var size = new FileInfo(path).Length;

        var over = await _reader.ReadAsync(path, new DocumentReadOptions { MaxFileBytes = size - 1 });
        var exact = await _reader.ReadAsync(path, new DocumentReadOptions { MaxFileBytes = size });

        Assert.Equal(DocumentReadStatus.TooLarge, over.Status);
        Assert.Equal(DocumentReadStatus.Success, exact.Status);
    }

    [Fact]
    public async Task APackageThatExpandsPastTheLimitIsTooLargeBeforeAnythingIsUnpacked()
    {
        var body = string.Concat(Enumerable.Repeat(P(new string('a', 1_000)), 3_000));
        var path = _folder.WriteBytes("bomb.docx", Create(body));
        Assert.True(new FileInfo(path).Length < 100_000, "the test document should be small on disk and large once unpacked");

        var refused = await _reader.ReadAsync(path, new DocumentReadOptions { MaxExpandedBytes = 1_000_000 });
        var metadata = await _reader.ReadMetadataAsync(path, new DocumentReadOptions { MaxExpandedBytes = 1_000_000 });
        var allowed = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.TooLarge, refused.Status);
        Assert.Equal(DocumentReadStatus.TooLarge, metadata.Status);
        Assert.Equal(DocumentReadStatus.Success, allowed.Status);
    }

    [Fact]
    public async Task APackageWithAnAbsurdNumberOfEntriesIsTooLarge()
    {
        var package = new OfficePackage();
        for (var i = 0; i <= 20_000; i++)
        {
            package.Add($"junk/{i}.txt", "x");
        }

        var result = await _reader.ReadAsync(_folder.WriteBytes("many.docx", package.ToBytes()));

        Assert.Equal(DocumentReadStatus.TooLarge, result.Status);
    }

    [Fact]
    public async Task PicturesAndOtherPartsThatAreNeverReadDoNotCountAgainstTheExpandedLimit()
    {
        var options = new DocxOptions { ExtraParts = new Dictionary<string, byte[]> { ["word/media/image1.png"] = new byte[5_000_000] } };

        var result = await ReadAsync(P("text"), options, new DocumentReadOptions { MaxExpandedBytes = 500_000 });

        Assert.Equal(DocumentReadStatus.Success, result.Status);
    }

    [Fact]
    public async Task TextPastTheCharacterLimitIsLeftOutAndSaysSo()
    {
        var body = string.Concat(Enumerable.Range(1, 500).Select(i => P("Paragraph number " + i + " with some words in it")));

        var result = await ReadAsync(body, limits: new DocumentReadOptions { MaxCharacters = 1_000 });

        Assert.True(result.Truncated);
        Assert.InRange(result.CharacterCount, 800, 1_000);
        Assert.StartsWith("Paragraph number 1 with", result.Segments[0].Text);
        Assert.DoesNotContain("Paragraph number 500", result.Segments[0].Text);
    }

    [Fact]
    public async Task TextThatJustFitsTheCharacterLimitIsNotTruncated()
    {
        var exact = await ReadAsync(P(new string('a', 500)), limits: new DocumentReadOptions { MaxCharacters = 500 });
        var under = await ReadAsync(P(new string('a', 500)), limits: new DocumentReadOptions { MaxCharacters = 499 });

        Assert.False(exact.Truncated);
        Assert.True(under.Truncated);
    }

    [Fact]
    public async Task MoreSectionsThanTheUnitLimitAreLeftOutAndSaySo()
    {
        var body = string.Concat(Enumerable.Range(1, 5).Select(i => Heading(1, "Heading " + i) + P("text " + i)));

        var result = await ReadAsync(body, new DocxOptions { Styles = Styles() }, new DocumentReadOptions { MaxUnits = 2 });

        Assert.Equal(["Heading 1", "Heading 2"], Labels(result));
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task ATruncatedReadOfAHugeDocumentStopsEarlyInsteadOfReadingItAll()
    {
        var body = string.Concat(Enumerable.Repeat(P("A paragraph of the very long document."), 45_000));
        var path = _folder.WriteBytes("huge.docx", Create(body));
        await _reader.ReadAsync(path);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cut = await _reader.ReadAsync(path, new DocumentReadOptions { MaxCharacters = 2_000 });
        var cutTime = watch.Elapsed;
        watch.Restart();
        var all = await _reader.ReadAsync(path);
        var allTime = watch.Elapsed;

        Assert.True(cut.Truncated);
        Assert.False(all.Truncated);
        Assert.True(cutTime < allTime, $"a cut read ({cutTime}) was not faster than a full read ({allTime})");
    }

    [Fact]
    public async Task CancellingBeforeTheReadStopsItWithACancellation()
    {
        var path = _folder.WriteBytes("a.docx", Create(P("text")));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadAsync(path, null, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadMetadataAsync(path, null, cancelled.Token));
    }

    [Fact]
    public void CancellingInTheMiddleOfAPartStopsItBetweenElements()
    {
        using var cancellation = new CancellationTokenSource();
        var xml = Document(string.Concat(Enumerable.Repeat(P("paragraph"), 200_000)));
        using var source = new CancelOnThirdRead(Encoding.UTF8.GetBytes(xml), cancellation);
        using var reader = SafeXml.Create(source, long.MaxValue);
        var body = new WordBodyReader(WordStyleMap.Empty, long.MaxValue, int.MaxValue, cancellation.Token);

        Assert.ThrowsAny<OperationCanceledException>(() => body.ReadBody(reader));
        Assert.True(source.Reads < 50, "the reader went on after it was cancelled");
    }

    [Fact]
    public async Task ADocumentNestedTooDeepIsCorruptAndNotACrash()
    {
        var open = string.Concat(Enumerable.Repeat("<w:sdt><w:sdtContent>", 20_000));
        var close = string.Concat(Enumerable.Repeat("</w:sdtContent></w:sdt>", 20_000));

        var result = await ReadAsync(open + P("deep") + close);

        Assert.Equal(DocumentReadStatus.Corrupt, result.Status);
    }

    [Fact]
    public async Task AContentControlAroundAParagraphIsTransparent()
    {
        var body = "<w:sdt><w:sdtPr><w:alias w:val=\"PROPERTY-TEXT\"/></w:sdtPr><w:sdtContent>" + P("inside the control") + "</w:sdtContent></w:sdt>";

        var result = await ReadAsync(body);

        Assert.Equal("inside the control", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task ADocumentAnotherProgramHasOpenIsStillRead()
    {
        var path = _folder.WriteBytes("open.docx", Create(P("being edited")));
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);

        var result = await _reader.ReadAsync(path);

        Assert.Equal("being edited", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task ReadingNeverChangesTheFile()
    {
        var path = _folder.WriteBytes("keep.docx", Create(P("keep me"), new DocxOptions { Title = "t" }));
        var before = await File.ReadAllBytesAsync(path);

        await _reader.ReadAsync(path);
        await _reader.ReadMetadataAsync(path);

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ADocumentInTheStrictFormOfOpenXmlIsReadToo()
    {
        var package = new OfficePackage()
            .Add(
                "[Content_Types].xml",
                Xml + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>")
            .Add(
                "_rels/.rels",
                Xml + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>")
            .Add(
                "word/document.xml",
                Xml + "<w:document xmlns:w=\"http://purl.oclc.org/ooxml/wordprocessingml/main\"><w:body>"
                + "<w:p><w:r><w:t>Strict text.</w:t></w:r></w:p></w:body></w:document>");

        var result = await _reader.ReadAsync(_folder.WriteBytes("strict.docx", package.ToBytes()));

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Equal("Strict text.", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task ADocumentMadeByTheOpenXmlSdkItselfIsRead()
    {
        var path = _folder.File("sdk.docx");
        using (var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var styles = main.AddNewPart<DocumentFormat.OpenXml.Packaging.StyleDefinitionsPart>();
            styles.Styles = new DocumentFormat.OpenXml.Wordprocessing.Styles(
                new DocumentFormat.OpenXml.Wordprocessing.Style(
                    new DocumentFormat.OpenXml.Wordprocessing.StyleName { Val = "heading 1" })
                { Type = DocumentFormat.OpenXml.Wordprocessing.StyleValues.Paragraph, StyleId = "Heading1" });
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                new DocumentFormat.OpenXml.Wordprocessing.Body(
                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                        new DocumentFormat.OpenXml.Wordprocessing.ParagraphProperties(
                            new DocumentFormat.OpenXml.Wordprocessing.ParagraphStyleId { Val = "Heading1" }),
                        new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Made by the SDK"))),
                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                        new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Its body text.")))));
            document.PackageProperties.Title = "SDK title";
        }

        var result = await _reader.ReadAsync(path);

        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Equal(["Made by the SDK"], Labels(result));
        Assert.Equal("Made by the SDK\n\nIts body text.", result.Segments[0].Text);
        Assert.Equal("SDK title", result.Metadata!.Title);
    }

    private static byte[] CompoundFile(string? streamName, int padding = 4096)
    {
        var bytes = new byte[Math.Max(padding, 1024)];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);
        if (streamName is not null)
        {
            Encoding.Unicode.GetBytes(streamName).CopyTo(bytes, 512);
        }

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
