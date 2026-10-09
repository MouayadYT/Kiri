using Assistant.Core.Domain;
using Assistant.Search.Index;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>How a row of the index becomes a normalized result.</summary>
public sealed class SearchRowMapperTests
{
    private static SearchResultItem? Map(object?[] row, SearchResultItemType type = SearchResultItemType.File, ExcludedFolders? excluded = null) =>
        SearchRowMapper.Map(row, type, excluded ?? ExcludedFolders.None);

    [Fact]
    public void AFileKeepsItsPathNameExtensionSizeAndDates()
    {
        var row = Rows.File(@"C:\Docs\Budget 2026.XLSX", modified: new DateTime(2026, 9, 21, 18, 20, 51), size: 26340792UL);

        var item = Map(row)!;

        Assert.Equal(SearchResultItemType.File, item.Type);
        Assert.Equal("Budget 2026.XLSX", item.DisplayName);
        Assert.Equal(@"C:\Docs\Budget 2026.XLSX", item.Path);
        Assert.Equal(".xlsx", item.Extension);
        Assert.Equal(26340792L, item.SizeBytes);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 18, 20, 51, TimeSpan.Zero), item.ModifiedAt);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 8, 30, 0, TimeSpan.Zero), item.CreatedAt);
    }

    [Fact]
    public void ALongPathAndItsNameAreKeptWhole()
    {
        // A name from a download site: well over 200 characters, with what tells it apart at its very end.
        var name = "Changes in the Land_ Indians, Colonists, and the Ecology of -- William Cronon -- 1st ed_, New York, New York State, 1983 -- "
            + "Hill and Wang -- isbn13 9780809001583 -- e9ce34896a3c5f71b9699cde1b5b8e55 -- Anna\u2019s Archiv.pdf";
        var path = @"C:\Users\Test\Downloads\" + name;

        var item = Map(Rows.File(path))!;

        Assert.Equal(path, item.Path);
        Assert.Equal(name, item.DisplayName);
        Assert.Equal(".pdf", item.Extension);
    }

    [Fact]
    public void TheIndexsDatesAreUtcWhateverThisPcsZoneIs()
    {
        var item = Map(Rows.File(@"C:\a.txt", modified: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)))!;

        Assert.Equal(TimeSpan.Zero, item.ModifiedAt!.Value.Offset);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0), item.ModifiedAt.Value.DateTime);
    }

    [Fact]
    public void AFolderHasNoExtensionAndNoSize()
    {
        var item = Map(Rows.Folder(@"C:\Docs\Finance.old"), SearchResultItemType.Folder)!;

        Assert.Equal(SearchResultItemType.Folder, item.Type);
        Assert.Equal("Finance.old", item.DisplayName);
        Assert.Null(item.Extension);
        Assert.Null(item.SizeBytes);
    }

    [Theory]
    [InlineData("pdf", ".pdf")]
    [InlineData(".PDF", ".pdf")]
    [InlineData(" .Md ", ".md")]
    [InlineData("", null)]
    [InlineData(".", null)]
    public void TheExtensionIsLowerCaseWithItsDot(string column, string? expected)
    {
        var row = Rows.File(@"C:\Docs\readme", extension: column);
        row[SearchColumns.Extension] = column;

        Assert.Equal(expected, Map(row)!.Extension);
    }

    [Fact]
    public void AMissingExtensionIsReadFromTheName()
    {
        var row = Rows.File(@"C:\Docs\notes.Markdown");
        row[SearchColumns.Extension] = DBNull.Value;

        Assert.Equal(".markdown", Map(row)!.Extension);
    }

    [Theory]
    [InlineData(1234UL, 1234L)]
    [InlineData(1234L, 1234L)]
    [InlineData(1234, 1234L)]
    [InlineData(1234u, 1234L)]
    [InlineData(ulong.MaxValue, null)]
    [InlineData(-5L, null)]
    [InlineData("not a number", null)]
    [InlineData(null, null)]
    public void TheSizeIsAnyNumberTheProviderUses(object? size, long? expected)
    {
        var row = Rows.File(@"C:\a.bin");
        row[SearchColumns.Size] = size;

        Assert.Equal(expected, Map(row)!.SizeBytes);
    }

    [Theory]
    [InlineData(2L)]
    [InlineData(4L)]
    [InlineData(6L | 32L)]
    public void HiddenAndSystemItemsAreNotShown(long attributes)
    {
        Assert.Null(Map(Rows.File(@"C:\hidden.txt", attributes: attributes)));
    }

    [Fact]
    public void AnItemInAnExcludedFolderIsNotShown()
    {
        var excluded = ExcludedFolders.Create([@"C:\Private"]);

        Assert.Null(Map(Rows.File(@"C:\Private\taxes.pdf"), excluded: excluded));
        Assert.NotNull(Map(Rows.File(@"C:\Public\taxes.pdf"), excluded: excluded));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ARowWithNoPathIsNotShown(string? path)
    {
        var row = Rows.File(@"C:\a.txt");
        row[SearchColumns.Path] = path;

        Assert.Null(Map(row));
    }

    [Fact]
    public void ADriveRootIsNamedByItsPath()
    {
        var row = Rows.Folder(@"D:\");
        row[SearchColumns.Name] = "Data (D:)";

        Assert.Equal("Data (D:)", Map(row, SearchResultItemType.Folder)!.DisplayName);
    }

    [Fact]
    public void MissingValuesAreNullNotDbNull()
    {
        var row = Rows.File(@"C:\a.txt");
        row[SearchColumns.Modified] = DBNull.Value;
        row[SearchColumns.Created] = null;
        row[SearchColumns.Size] = DBNull.Value;

        var item = Map(row)!;

        Assert.Null(item.ModifiedAt);
        Assert.Null(item.CreatedAt);
        Assert.Null(item.SizeBytes);
        Assert.Null(item.Snippet);
    }

    [Fact]
    public void TheMetadataTheIndexHasIsKept()
    {
        var row = Rows.File(@"C:\Docs\plan.pdf");
        row[SearchColumns.Accessed] = new DateTime(2026, 5, 2, 9, 0, 0);
        row[SearchColumns.TypeText] = "Adobe Acrobat Document";
        row[SearchColumns.Kind] = new[] { "document", "text" };
        row[SearchColumns.MimeType] = "application/pdf";
        row[SearchColumns.Title] = "  Trip plan ";
        row[SearchColumns.Authors] = new[] { "Ada", " ", "Grace", "ada" };
        row[SearchColumns.Keywords] = "trip;planning;;";

        var metadata = Map(row)!.Metadata!;

        Assert.Equal("Adobe Acrobat Document", metadata.TypeDescription);
        Assert.Equal("document", metadata.Kind);
        Assert.Equal("application/pdf", metadata.MimeType);
        Assert.Equal("Trip plan", metadata.Title);
        Assert.Equal(["Ada", "Grace"], metadata.Authors);
        Assert.Equal(["trip", "planning"], metadata.Keywords);
        Assert.Equal(new DateTimeOffset(2026, 5, 2, 9, 0, 0, TimeSpan.Zero), metadata.AccessedAt);
    }

    [Fact]
    public void AnItemWithNoPropertiesHasNoMetadata()
    {
        var row = Rows.File(@"C:\a.txt");
        row[SearchColumns.Created] = null;

        // Only the modified date and size are set: nothing that counts as metadata.
        Assert.Null(Map(row)!.Metadata);
    }

    [Fact]
    public void ALongValueIsCutAndALongListIsShortened()
    {
        var row = Rows.File(@"C:\a.txt");
        row[SearchColumns.Title] = new string('t', 5000);
        row[SearchColumns.Keywords] = Enumerable.Range(0, 100).Select(number => $"word{number}").ToArray();

        var metadata = Map(row)!.Metadata!;

        Assert.Equal(200, metadata.Title!.Length);
        Assert.Equal(10, metadata.Keywords.Count);
    }

    [Fact]
    public void TheExcerptIsOnlyThereWhenTheRowHasIt()
    {
        var row = Rows.Row(@"C:\a.txt", ".txt", length: 16);
        row[SearchColumns.Summary] = "Quarterly   budget\r\nreview\t notes";

        Assert.Equal("Quarterly budget review notes", Map(row)!.Snippet);
        Assert.Null(Map(Rows.File(@"C:\a.txt"))!.Snippet);
    }

    [Fact]
    public void AnExcerptIsOneCleanLineOfLimitedLength()
    {
        Assert.Equal("a b", SearchRowMapper.Snippet("\0a\u0001 \n b\0"));
        Assert.Null(SearchRowMapper.Snippet("   \0 \r\n"));
        Assert.Null(SearchRowMapper.Snippet("... ---"));
        Assert.Null(SearchRowMapper.Snippet(null));

        var cut = SearchRowMapper.Snippet(new string('x', 1000))!;
        Assert.Equal(SearchRowMapper.MaxSnippetLength + 1, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExcerptIsNeverCutInsideASurrogatePair()
    {
        var text = new string('x', SearchRowMapper.MaxSnippetLength - 1) + "😀😀";

        var snippet = SearchRowMapper.Snippet(text)!;

        Assert.False(char.IsHighSurrogate(snippet[^2]));
        Assert.Equal(SearchRowMapper.MaxSnippetLength - 1, snippet.Length - 1);
    }

    [Fact]
    public void AResultsToStringHoldsNoPrivateContent()
    {
        var row = Rows.File(@"C:\Secret\plan.pdf");
        row[SearchColumns.Title] = "Merger";
        row[SearchColumns.Authors] = new[] { "Ada" };
        var item = Map(row)!;

        Assert.DoesNotContain("Secret", item.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Merger", item.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Merger", item.Metadata!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Ada", item.Metadata!.ToString(), StringComparison.Ordinal);
    }
}
