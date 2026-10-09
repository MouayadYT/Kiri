using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch;
using Assistant.Search.Files;
using Xunit;

namespace Assistant.Search.Tests;

public sealed class FilesQuickSearchProviderTests
{
    private const string Profile = "C:\\Users\\sam";
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static SearchResultItem File(string path, DateTimeOffset? modified = null) =>
        new(SearchResultItemType.File, Path.GetFileNameWithoutExtension(path), path)
        {
            Extension = Path.GetExtension(path).ToLowerInvariant(),
            ModifiedAt = modified,
        };

    private static SearchResultItem Folder(string path) => new(SearchResultItemType.Folder, Path.GetFileName(path), path);

    private static FilesQuickSearchProvider Provider(
        FakeFileSearch? search = null, FakePlanner? planner = null, FakePermissions? permissions = null) =>
        new(search ?? new FakeFileSearch(), planner ?? new FakePlanner(), permissions ?? new FakePermissions(), new ManualClock(Now), Profile);

    private static Task<IReadOnlyList<QuickSearchResult>> Search(
        FilesQuickSearchProvider provider, string query, int max = QuickSearchRequest.DefaultMaxResults, bool deliberate = false) =>
        provider.SearchAsync(new QuickSearchRequest(query) { MaxResults = max, IsDeliberate = deliberate }, CancellationToken.None);

    [Fact]
    public void TheProviderWaitsOutThePauseAndLooksOnlyAtTwoOrMoreLetters()
    {
        var provider = Provider();

        Assert.Equal("files", provider.Id);
        Assert.Equal(QuickSearchResultType.Files, provider.ResultType);
        Assert.Equal(TimeSpan.FromMilliseconds(150), provider.Debounce);
        Assert.Equal(60, provider.Priority);
        Assert.Equal(0, provider.MinimumQueryLength);
    }

    [Fact]
    public async Task TypedWordsAreLookedForInNamesWithAFewResultsPerKind()
    {
        var search = new FakeFileSearch
        {
            Items =
            [
                File("C:\\Users\\sam\\Documents\\Budget 2026.xlsx", Now.AddDays(-1)),
                Folder("C:\\Users\\sam\\Documents\\Budget"),
            ],
        };

        var results = await Search(Provider(search), "budget");

        var query = Assert.Single(search.Queries);
        Assert.Equal("budget", query.Text);
        Assert.Equal([SearchResultItemType.File, SearchResultItemType.Folder], query.Types);
        Assert.Equal(FilesQuickSearchProvider.MaxFiles, query.MaxResultsPerType);
        Assert.False(query.MatchContents);

        Assert.Equal(["Budget 2026", "Budget"], results.Select(result => result.Title));
        var file = results[0];
        Assert.Equal("file:C:\\Users\\sam\\Documents\\Budget 2026.xlsx", file.Id);
        Assert.Equal(QuickSearchResultType.Files, file.ResultType);
        Assert.Equal("~\\Documents", file.Subtitle);
        Assert.Equal(Now.AddDays(-1), file.When);
        Assert.Equal(QuickSearchIconKind.File, file.Icon.Kind);
        Assert.Equal(new QuickSearchAction(QuickSearchActionKind.OpenPath, "Open", "C:\\Users\\sam\\Documents\\Budget 2026.xlsx"), file.Primary);
        Assert.Equal(QuickSearchIconKind.Folder, results[1].Icon.Kind);
        Assert.Equal(1.0, file.Relevance);
        Assert.True(results[1].Relevance < file.Relevance);
    }

    [Fact]
    public async Task AFileHasAlternatesToShowItAndCopyItsPathAndAPictureOrADocumentCanBeAttachedFirst()
    {
        var search = new FakeFileSearch
        {
            Items =
            [
                File("C:\\Users\\sam\\Pictures\\cat.png"),
                File("C:\\Users\\sam\\Documents\\plan.pdf"),
                File("C:\\Users\\sam\\Downloads\\setup.exe"),
                Folder("C:\\Users\\sam\\Downloads\\stuff"),
            ],
        };

        var results = await Search(Provider(search), "stuff");

        static IEnumerable<QuickSearchActionKind> Kinds(QuickSearchResult result) => result.Alternates.Select(action => action.Kind);
        Assert.Equal([QuickSearchActionKind.AttachFile, QuickSearchActionKind.RevealPath, QuickSearchActionKind.CopyFile, QuickSearchActionKind.CopyPath], Kinds(results[0]));
        Assert.Equal([QuickSearchActionKind.AttachFile, QuickSearchActionKind.RevealPath, QuickSearchActionKind.CopyFile, QuickSearchActionKind.CopyPath], Kinds(results[1]));
        Assert.Equal([QuickSearchActionKind.RevealPath, QuickSearchActionKind.CopyFile, QuickSearchActionKind.CopyPath], Kinds(results[2]));
        Assert.Equal([QuickSearchActionKind.RevealPath, QuickSearchActionKind.CopyFile, QuickSearchActionKind.CopyPath], Kinds(results[3]));
        Assert.Equal(
            new QuickSearchAction(QuickSearchActionKind.AttachFile, "Attach to conversation", "C:\\Users\\sam\\Pictures\\cat.png"),
            results[0].Alternates[0]);

        // What a right-click on the row offers: where the file is, the file itself for pasting into a folder or a message, and its path as text.
        Assert.Equal(["Open file location", "Copy file", "Copy path"], results[2].Alternates.Select(action => action.Title));
        Assert.Equal(["Open file location", "Copy folder", "Copy path"], results[3].Alternates.Select(action => action.Title));
        Assert.All(results[2].Alternates, action => Assert.Equal("C:\\Users\\sam\\Downloads\\setup.exe", action.Target));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a.")]
    [InlineData(" . ")]
    [InlineData("!!")]
    public async Task LessThanTwoLettersOrDigitsIsNotLookedUp(string typed)
    {
        var search = new FakeFileSearch { Items = [File("C:\\a.txt")] };
        var permissions = new FakePermissions();

        Assert.Empty(await Search(Provider(search, permissions: permissions), typed));

        Assert.Empty(search.Queries);
        Assert.Equal(0, permissions.Asked);
    }

    [Fact]
    public async Task TwoLettersOrDigitsAreEnough()
    {
        var search = new FakeFileSearch { Items = [File("C:\\Users\\sam\\q3.txt")] };

        Assert.Single(await Search(Provider(search), "q3"));
    }

    [Fact]
    public async Task NothingIsLookedUpWhileTheFilesPermissionIsOff()
    {
        var search = new FakeFileSearch { Items = [File("C:\\Users\\sam\\budget.txt")] };
        var planner = new FakePlanner();
        var permissions = new FakePermissions(filesAllowed: false);
        var provider = Provider(search, planner, permissions);

        Assert.Empty(await Search(provider, "budget"));
        Assert.Empty(await Search(provider, ""));

        Assert.Empty(search.Queries);
        Assert.Empty(planner.Planned);
    }

    [Fact]
    public async Task AKnownShapeIsLookedUpByItsRuleWithoutPlanning()
    {
        var rule = new FileSearchQuery { Kind = FileKind.Picture, Order = FileSearchOrder.ModifiedDescending };
        var planner = new FakePlanner(quick: text =>
            text.Contains("screenshots", StringComparison.Ordinal) ? new PlannedFileSearch(rule, FileSearchPlanSource.Template) : null);
        var search = new FakeFileSearch { Items = [File("C:\\Users\\sam\\Pictures\\Screenshot 1.png")] };

        var results = await Search(Provider(search, planner), "the last 5 screenshots");

        Assert.Single(results);
        var query = Assert.Single(search.Queries);
        Assert.Equal(FileKind.Picture, query.Kind);
        Assert.Equal(FileSearchOrder.ModifiedDescending, query.Order);
        Assert.Empty(planner.Planned);
    }

    [Fact]
    public async Task OnlyTextThatClearlyAsksForFilesIsPlannedAsARequest()
    {
        var planned = new FileSearchQuery("biology") { Extensions = [".pdf"] };
        var planner = new FakePlanner(plan: _ => new PlannedFileSearch(planned, FileSearchPlanSource.Read));
        var search = new FakeFileSearch { Items = [File("C:\\Users\\sam\\Documents\\biology.pdf")] };
        var provider = Provider(search, planner);

        // Plain words are a name search: no planning.
        await Search(provider, "biology");
        Assert.Empty(planner.Planned);
        Assert.Equal("biology", search.Queries[0].Text);

        // A request is planned by the planner's rules.
        await Search(provider, "find the pdf about biology I edited last Tuesday");
        Assert.Equal(["find the pdf about biology I edited last Tuesday"], planner.Planned);
        Assert.Equal([".pdf"], search.Queries[1].Extensions);
    }

    [Fact]
    public async Task ASubmittedQueryIsPlannedWhateverItSaysAndFallsBackOnItsWordsWhenThePlanHasNoQuery()
    {
        var planner = new FakePlanner(plan: _ => new PlannedFileSearch(null, FileSearchPlanSource.Read));
        var search = new FakeFileSearch { Items = [File("C:\\Users\\sam\\notes.txt")] };
        var provider = Provider(search, planner);

        Assert.Single(await Search(provider, "notes", deliberate: true));

        Assert.Equal(["notes"], planner.Planned);
        Assert.Equal("notes", Assert.Single(search.Queries).Text);
    }

    [Fact]
    public async Task OnlyFiveFilesAndThreeFoldersAreListedWhileTyping()
    {
        var items = Enumerable.Range(0, 9).Select(i => File($"C:\\Users\\sam\\Documents\\f{i}.txt"))
            .Concat(Enumerable.Range(0, 9).Select(i => Folder($"C:\\Users\\sam\\Documents\\d{i}")))
            .ToArray();
        var search = new FakeFileSearch { Items = items };

        var results = await Search(Provider(search), "doc");

        Assert.Equal(5, results.Count(result => result.Icon.Kind == QuickSearchIconKind.File));
        Assert.Equal(3, results.Count(result => result.Icon.Kind == QuickSearchIconKind.Folder));
    }

    [Fact]
    public async Task AListNarrowedToFilesHasRoomForMore()
    {
        var items = Enumerable.Range(0, 30).Select(i => File($"C:\\Users\\sam\\Documents\\f{i}.txt"))
            .Concat(Enumerable.Range(0, 30).Select(i => Folder($"C:\\Users\\sam\\Documents\\d{i}")))
            .ToArray();
        var search = new FakeFileSearch { Items = items };

        var results = await Search(Provider(search), "doc", max: 20);

        Assert.Equal(20, results.Count(result => result.Icon.Kind == QuickSearchIconKind.File));
        Assert.Equal(10, results.Count(result => result.Icon.Kind == QuickSearchIconKind.Folder));
        Assert.Equal(20, Assert.Single(search.Queries).MaxResultsPerType);
    }

    [Fact]
    public async Task WithNothingTypedTheFilesThatChangedMostLatelyAreListed()
    {
        var planner = new FakePlanner();
        var search = new FakeFileSearch { Items = [File("C:\\Users\\sam\\Documents\\today.docx")] };

        var results = await Search(Provider(search, planner), "", max: 20);

        Assert.Single(results);
        var query = Assert.Single(search.Queries);
        Assert.Null(query.Text);
        Assert.Equal([SearchResultItemType.File], query.Types);
        Assert.Equal(FileSearchOrder.ModifiedDescending, query.Order);
        Assert.Equal(Now - FilesQuickSearchProvider.RecentWindow, query.Modified.From);
        Assert.Null(query.Modified.To);
        Assert.Empty(planner.Planned);
    }

    [Theory]
    [InlineData(FileSearchFailure.IndexUnavailable)]
    [InlineData(FileSearchFailure.TimedOut)]
    [InlineData(FileSearchFailure.QueryFailed)]
    public async Task ASearchThatCannotRunIsNoResultsAndNeverAnException(FileSearchFailure failure)
    {
        var search = new FakeFileSearch { Failure = new FileSearchException(failure) };

        Assert.Empty(await Search(Provider(search), "budget"));
    }

    [Fact]
    public async Task ACancelledSearchIsNotSwallowed()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider().SearchAsync(new QuickSearchRequest("budget"), cancel.Token));
    }

    [Fact]
    public async Task APathWithoutAFolderOrOutsideTheProfileIsWrittenAsItIs()
    {
        var search = new FakeFileSearch
        {
            Items = [File("D:\\Data\\log.txt"), File("C:\\Users\\samantha\\x.txt"), Folder("C:\\")],
        };

        var results = await Search(Provider(search), "log");

        Assert.Equal(["D:\\Data", "C:\\Users\\samantha", "C:\\"], results.Select(result => result.Subtitle));
    }
}
