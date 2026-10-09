using System.Diagnostics;
using Assistant.Core.QuickSearch;
using Assistant.Search.Files;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>
/// The Files provider against this PC's real Windows Search index. They check what must hold whatever the index holds, and pass
/// without checking more on a PC where Windows Search is off or has nothing, because the outcome then says nothing about the code.
/// </summary>
public sealed class LiveQuickSearchTests
{
    private static FilesQuickSearchProvider Provider() => new(
        new WindowsFileSearchService(new FakeSettings(), NullLogger<WindowsFileSearchService>.Instance),
        new Planning.FileSearchPlanner(TimeProvider.System, NullLogger<Planning.FileSearchPlanner>.Instance),
        new FakePermissions());

    [Fact]
    public async Task TypedWordsAreAnsweredQuicklyWithAFewNamedFilesAndFolders()
    {
        var clock = Stopwatch.StartNew();

        var results = await Provider().SearchAsync(new QuickSearchRequest("test"), CancellationToken.None);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "The index must answer within its time limit.");
        Assert.True(results.Count(result => result.Icon.Kind == QuickSearchIconKind.File) <= FilesQuickSearchProvider.MaxFiles);
        Assert.True(results.Count(result => result.Icon.Kind == QuickSearchIconKind.Folder) <= FilesQuickSearchProvider.MaxFolders);
        foreach (var result in results)
        {
            Assert.Equal(QuickSearchResultType.Files, result.ResultType);
            Assert.Contains("test", result.Title, StringComparison.OrdinalIgnoreCase);
            Assert.True(Path.IsPathFullyQualified(result.Primary.Target));
            Assert.Equal(QuickSearchActionKind.OpenPath, result.Primary.Kind);
            Assert.Contains(result.Alternates, action => action.Kind == QuickSearchActionKind.RevealPath);
        }
    }

    [Fact]
    public async Task WithNothingTypedTheFilesThatChangedLatelyAreListedNewestFirst()
    {
        var results = await Provider().SearchAsync(new QuickSearchRequest("") { MaxResults = 20 }, CancellationToken.None);

        Assert.True(results.Count <= 20);
        Assert.All(results, result => Assert.Equal(QuickSearchIconKind.File, result.Icon.Kind));
        var times = results.Select(result => result.When).ToArray();
        Assert.All(times, time => Assert.NotNull(time));
        Assert.Equal(times.OrderByDescending(time => time), times);
        Assert.All(times, time => Assert.True(time >= DateTimeOffset.UtcNow - FilesQuickSearchProvider.RecentWindow - TimeSpan.FromMinutes(5)));
    }
}
