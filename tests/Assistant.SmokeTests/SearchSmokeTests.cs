using System.IO;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.QuickSearch.Routing;
using Assistant.SmokeTests.Support;
using Assistant.UI.Messages;
using Assistant.UI.Search;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Assistant.SmokeTests;

/// <summary>
/// Checklists 6 and 11: Windows Search answers for a file by name, and the bar finds an application and a file as the user types and runs them. The index
/// and the Start menu are this PC's own, read for real; only the launchers that would open something are replaced by ones that write down what they were
/// asked to open.
/// </summary>
public sealed class SearchSmokeTests
{
    /// <summary>
    /// A file that is on this PC and in the Windows Search index: the project's own README by default (so the repository must be in a folder Windows indexes,
    /// as the user's profile is), or any other file named by <c>ASSISTANT_SMOKE_INDEXED_FILE</c>.
    /// </summary>
    private static string IndexedFile =>
        Environment.GetEnvironmentVariable("ASSISTANT_SMOKE_INDEXED_FILE") is { Length: > 0 } named ? named : Repo.Combine("README.md");

    private static string NotIndexed(string path) =>
        $"Windows Search did not return {Path.GetFileName(path)}. The Windows Search service must be running and must index the folder it is in " +
        "(Indexing Options); or name another indexed file in ASSISTANT_SMOKE_INDEXED_FILE.";

    [Fact]
    public Task WindowsSearchFindsAFileByName_FromThisPcsOwnIndex() => Smoke.RunAsync(async app =>
    {
        var file = IndexedFile;
        Assert.True(File.Exists(file), "The file the check looks for is not on this PC: " + file);
        var name = Path.GetFileNameWithoutExtension(file);

        var found = await app.Get<IFileSearchService>().SearchAsync(
            new FileSearchQuery(name) { Types = [SearchResultItemType.File], MaxResultsPerType = 25 }, Cancel());

        Assert.True(
            found.Any(item => string.Equals(item.Path, file, StringComparison.OrdinalIgnoreCase)),
            NotIndexed(file));
    });

    [Fact]
    public Task TheBarFindsAnApplicationAndAFileWhileTheUserTypes_AndRunsThemThroughTheLaunchers() => Smoke.RunAsync(async app =>
    {
        var applications = new RecordingLauncher();
        var files = new RecordingLauncher();
        var coordinator = app.Get<IQuickSearchCoordinator>();
        var ranker = app.Get<QuickSearchRanker>();

        // The Start menu is read in the background when the app starts, so the first looks may come before it is there.
        QuickSearchResult? notepad = null;
        await Wait.UntilAsync(
            () =>
            {
                notepad = Search(coordinator, ranker, "notepad").FirstOrDefault(
                    result => result.ResultType == QuickSearchResultType.Applications && result.Title.Contains("Notepad", StringComparison.OrdinalIgnoreCase));
                return notepad is not null;
            },
            "The bar did not find Notepad among the applications.", TimeSpan.FromSeconds(30));

        var file = IndexedFile;
        QuickSearchResult? spec = null;
        await Wait.UntilAsync(
            () =>
            {
                spec = Search(coordinator, ranker, Path.GetFileNameWithoutExtension(file)).FirstOrDefault(
                    result => result.ResultType == QuickSearchResultType.Files && result.Primary.Target.Equals(file, StringComparison.OrdinalIgnoreCase));
                return spec is not null;
            },
            NotIndexed(file), TimeSpan.FromSeconds(30));

        // Running what was chosen goes to the launchers, with the application's identity and the file's path.
        var runner = new QuickSearchActionRunner(
            applications, files, app.Get<ITextClipboard>(), app.Get<AttachRequests>(), app.Get<IQuickActionExecutor>());
        await runner.RunAsync(notepad!, notepad!.Primary);
        await runner.RunAsync(spec!, spec!.Primary);

        Assert.Equal([notepad.Primary.Target], applications.Launched);
        Assert.Equal([file], files.Launched, StringComparer.OrdinalIgnoreCase);
    });

    private static IReadOnlyList<QuickSearchResult> Search(IQuickSearchCoordinator coordinator, QuickSearchRanker ranker, string typed)
    {
        var outcome = coordinator.SearchAsync(new QuickSearchRequest(typed), null, Cancel()).GetAwaiter().GetResult();
        Assert.DoesNotContain(outcome.Providers, answer => answer.Status == QuickSearchProviderStatus.Failed);
        return [.. ranker.Rank(typed, outcome.Results, DateTimeOffset.Now).Ordered.Select(ranked => ranked.Result)];
    }

    private static CancellationToken Cancel() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private sealed class RecordingLauncher : IApplicationLauncher, IFileLauncher
    {
        public List<string> Launched { get; } = [];

        public bool Launch(string applicationId)
        {
            Launched.Add(applicationId);
            return true;
        }

        public bool Open(string path)
        {
            Launched.Add(path);
            return true;
        }

        public bool Reveal(string path) => true;
    }
}
