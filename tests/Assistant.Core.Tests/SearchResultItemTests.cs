using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>What a search result holds, and that neither it nor a failed search gives away what was found.</summary>
public sealed class SearchResultItemTests
{
    [Fact]
    public void ANewResultHoldsOnlyItsTypeNameAndPath()
    {
        var item = new SearchResultItem(SearchResultItemType.File, "a.txt", @"C:\a.txt");

        Assert.Null(item.Extension);
        Assert.Null(item.SizeBytes);
        Assert.Null(item.ModifiedAt);
        Assert.Null(item.CreatedAt);
        Assert.Null(item.Metadata);
        Assert.Null(item.Snippet);
    }

    [Fact]
    public void ToStringHoldsNoNamePathTitleAuthorOrExcerpt()
    {
        var item = new SearchResultItem(SearchResultItemType.File, "merger.docx", @"C:\Secret\merger.docx")
        {
            Extension = ".docx",
            Snippet = "the confidential plan",
            Metadata = new SearchResultMetadata { Title = "Merger", Authors = ["Ada"], Keywords = ["contoso"] },
        };

        var text = item + " " + item.Metadata;

        foreach (var word in new[] { "merger", "Secret", "confidential", "Merger", "Ada", "contoso" })
        {
            Assert.DoesNotContain(word, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(FileSearchFailure.IndexUnavailable)]
    [InlineData(FileSearchFailure.TimedOut)]
    [InlineData(FileSearchFailure.QueryFailed)]
    public void AFailedSearchSaysWhyAndKeepsOnlyTheProvidersCode(FileSearchFailure failure)
    {
        var exception = new FileSearchException(failure, 0x1234);

        Assert.Equal(failure, exception.Failure);
        Assert.Equal(0x1234, exception.ProviderErrorCode);
        Assert.Contains("Windows Search", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        Assert.Null(new FileSearchException(failure).ProviderErrorCode);
    }
}
