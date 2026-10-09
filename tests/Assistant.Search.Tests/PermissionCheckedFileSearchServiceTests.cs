using Assistant.Core.Contracts;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>The file search service behind the Files permission, in the service itself (PROJECT_SPEC §4.7, §4.9, step 119).</summary>
public sealed class PermissionCheckedFileSearchServiceTests
{
    [Fact]
    public async Task NoSearchReachesWindowsSearchWhileFilesIsNotAllowed()
    {
        var inner = new FakeFileSearch();
        var service = new PermissionCheckedFileSearchService(inner, new FakePermissions(filesAllowed: false));

        var plain = await Assert.ThrowsAsync<FileSearchException>(() => service.SearchAsync(new FileSearchQuery("biology")));
        var capable = await Assert.ThrowsAsync<FileSearchException>(() => service.SearchWithCapabilitiesAsync(new FileSearchQuery("biology")));

        Assert.Equal(FileSearchFailure.NotAllowed, plain.Failure);
        Assert.Equal(FileSearchFailure.NotAllowed, capable.Failure);
        Assert.Empty(inner.Queries);
    }

    [Fact]
    public async Task AllowedSearchesGoThroughAndKeepTheirAnswer()
    {
        var inner = new FakeFileSearch { Capability = ContentSearchCapability.NotRequested };
        var service = new PermissionCheckedFileSearchService(inner, new FakePermissions(filesAllowed: true));

        var items = await service.SearchAsync(new FileSearchQuery("biology"));
        var outcome = await service.SearchWithCapabilitiesAsync(new FileSearchQuery("chemistry"));

        Assert.Empty(items);
        Assert.Empty(outcome.Items);
        Assert.Equal(2, inner.Queries.Count);
    }
}
