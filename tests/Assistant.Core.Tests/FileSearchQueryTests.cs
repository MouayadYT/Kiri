using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>What a structured file-search query holds and says of itself, and that it gives away nothing of what it looks for.</summary>
public sealed class FileSearchQueryTests
{
    [Fact]
    public void ANewQueryHasNoCriterionAndTheOldDefaults()
    {
        var query = new FileSearchQuery();

        Assert.Null(query.Text);
        Assert.Null(query.Filename);
        Assert.Null(query.ContentTerm);
        Assert.Empty(query.Extensions);
        Assert.Null(query.Kind);
        Assert.True(query.Created.IsUnbounded);
        Assert.True(query.Modified.IsUnbounded);
        Assert.True(query.Size.IsUnbounded);
        Assert.Null(query.Folder);
        Assert.True(query.IncludeSubfolders);
        Assert.Equal(FileSearchOrder.Relevance, query.Order);
        Assert.Equal(5, query.MaxResultsPerType);
        Assert.False(query.MatchContents);
        Assert.Equal([SearchResultItemType.App, SearchResultItemType.File, SearchResultItemType.Folder], query.Types);
    }

    [Fact]
    public void TheOldWayOfAskingStillWorks()
    {
        var query = new FileSearchQuery("budget") { Types = [SearchResultItemType.File], MaxResultsPerType = 2, MatchContents = true };

        Assert.Equal("budget", query.Text);
        Assert.Equal(2, query.MaxResultsPerType);
        Assert.True(query.MatchContents);
        Assert.Equal([SearchResultItemType.File], query.Types);
    }

    [Fact]
    public void TheLastFiveScreenshotsIsAQueryNotAnInstruction()
    {
        var query = new FileSearchQuery
        {
            Filename = "Screenshot",
            Extensions = [".png"],
            Types = [SearchResultItemType.File],
            Order = FileSearchOrder.ModifiedDescending,
            MaxResultsPerType = 5,
        };

        Assert.Equal(FileSearchOrder.ModifiedDescending, query.Order);
        Assert.Equal(5, query.MaxResultsPerType);
        Assert.Equal("Screenshot", query.Filename);
        Assert.Equal([".png"], query.Extensions);
        Assert.Equal(query, query with { });
    }

    [Fact]
    public void ARangeHoldsItsEndsAndSaysWhetherItHasAny()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True(new DateRange().IsUnbounded);
        Assert.False(new DateRange(From: from).IsUnbounded);
        Assert.False(new DateRange(To: from).IsUnbounded);
        Assert.True(new SizeRange().IsUnbounded);
        Assert.False(new SizeRange(MaxBytes: 0).IsUnbounded);
        Assert.False(new SizeRange(MinBytes: 1).IsUnbounded);
    }

    [Fact]
    public void ToStringNamesTheCriteriaThatAreSetAndNeverTheirValues()
    {
        var query = new FileSearchQuery("merger with contoso")
        {
            Filename = "secret-plan",
            ContentTerm = "confidential clause",
            Extensions = [".docx"],
            Folder = @"C:\Users\Ada\Private Stuff",
            Modified = new DateRange(From: new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero)),
            Size = new SizeRange(MinBytes: 123456789),
            Kind = FileKind.Document,
            Order = FileSearchOrder.SizeDescending,
        };

        var text = query.ToString();

        foreach (var secret in new[] { "merger", "contoso", "secret-plan", "confidential", ".docx", "Ada", "Private", "2026", "123456789" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        Assert.Contains("Order = SizeDescending", text, StringComparison.Ordinal);
        Assert.Contains("Text, Filename, ContentTerm, Extensions, Kind, Modified, Size, Folder", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Created", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutcomeSaysHowManyItemsAndTheCapabilityAndNothingOfThem()
    {
        var outcome = new FileSearchOutcome(
            [new SearchResultItem(SearchResultItemType.File, "merger.docx", @"C:\Secret\merger.docx") { Snippet = "confidential" }],
            new ContentSearchCapability { Support = ContentSearchSupport.Partial, UnsupportedExtensions = [".md"] });

        var text = outcome.ToString();

        Assert.Contains("Items = 1", text, StringComparison.Ordinal);
        Assert.Contains("Partial", text, StringComparison.Ordinal);
        foreach (var secret in new[] { "merger", "Secret", "confidential", ".md" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheCapabilitiesSayWhetherTheyLimitTheAnswer()
    {
        Assert.Equal(ContentSearchSupport.NotRequested, ContentSearchCapability.NotRequested.Support);
        Assert.Equal(ContentSearchSupport.Available, ContentSearchCapability.Available.Support);
        Assert.False(ContentSearchCapability.NotRequested.IsLimited);
        Assert.False(ContentSearchCapability.Available.IsLimited);
        Assert.Equal(ContentSearchLimits.None, ContentSearchCapability.Available.Limits);
        Assert.Empty(ContentSearchCapability.Available.UnsupportedExtensions);

        var partial = new ContentSearchCapability { Support = ContentSearchSupport.Partial };
        var unavailable = new ContentSearchCapability { Support = ContentSearchSupport.Unavailable };
        Assert.True(partial.IsLimited);
        Assert.False(partial.IsUnavailable);
        Assert.True(unavailable.IsLimited);
        Assert.True(unavailable.IsUnavailable);
    }
}
