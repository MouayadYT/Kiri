using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Search.Index;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>The search service over a fake index: what it asks, what it keeps and how it fails.</summary>
public sealed class WindowsFileSearchServiceTests
{
    private const string Secret = "merger-with-contoso";

    private readonly FakeIndex _index = new();
    private readonly CapturingLogger _logger = new();

    private WindowsFileSearchService Service(params string[] excluded) => new(new FakeSettings(excluded), _index, _logger);

    private WindowsFileSearchService ServiceWith(IContentTypeCatalog types, params string[] excluded) =>
        new(new FakeSettings(excluded), _index, _logger, types);

    private static bool AsksForFolders(string sql) => sql.Contains("System.IsFolder = true", StringComparison.Ordinal);

    private static bool IsLocationProbe(string sql) => sql.StartsWith("SELECT TOP 1 System.ItemUrl", StringComparison.Ordinal);

    [Fact]
    public async Task AFileSearchReturnsNormalizedResults()
    {
        _index.Answer = _ => [Rows.File(@"C:\Docs\Budget 2026.xlsx", size: 2048UL)];

        var results = await Service().SearchAsync(new FileSearchQuery("budget") { Types = [SearchResultItemType.File] });

        var item = Assert.Single(results);
        Assert.Equal(SearchResultItemType.File, item.Type);
        Assert.Equal("Budget 2026.xlsx", item.DisplayName);
        Assert.Equal(".xlsx", item.Extension);
        Assert.Equal(2048L, item.SizeBytes);
        Assert.Single(_index.Queries);
    }

    [Fact]
    public async Task FilesAndFoldersAreAskedForSeparatelyAndComeBackInTheOrderRequested()
    {
        _index.Answer = sql => AsksForFolders(sql) ? [Rows.Folder(@"C:\Docs\Budget")] : [Rows.File(@"C:\Docs\Budget.xlsx")];

        var results = await Service().SearchAsync(new FileSearchQuery("budget")
        {
            Types = [SearchResultItemType.Folder, SearchResultItemType.File],
        });

        Assert.Equal([SearchResultItemType.Folder, SearchResultItemType.File], results.Select(item => item.Type));
        Assert.Equal(2, _index.Queries.Count);
    }

    [Fact]
    public async Task AppsAreNotSoughtInTheFileIndex()
    {
        var onlyApps = await Service().SearchAsync(new FileSearchQuery("calc") { Types = [SearchResultItemType.App] });
        Assert.Empty(onlyApps);
        Assert.Empty(_index.Queries);

        _index.Answer = _ => [Rows.File(@"C:\calc.txt")];
        var withApps = await Service().SearchAsync(new FileSearchQuery("calc"));
        Assert.Equal(2, _index.Queries.Count);
        Assert.All(withApps, item => Assert.NotEqual(SearchResultItemType.App, item.Type));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("? - !")]
    public async Task AQueryWithNoWordAsksNothing(string text)
    {
        Assert.Empty(await Service().SearchAsync(new FileSearchQuery(text)));
        Assert.Empty(_index.Queries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task NoRoomForResultsAsksNothing(int max)
    {
        Assert.Empty(await Service().SearchAsync(new FileSearchQuery("budget") { MaxResultsPerType = max }));
        Assert.Empty(_index.Queries);
    }

    [Fact]
    public async Task ControlCharactersInTheQueryNeverReachTheIndex()
    {
        await Service().SearchAsync(new FileSearchQuery("bud\0get\u0007 x") { Types = [SearchResultItemType.File] });

        var sql = Assert.Single(_index.Queries);
        Assert.Contains("LIKE '%budget%'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(sql, character => char.IsControl(character));
    }

    [Fact]
    public async Task AtMostTheRequestedNumberOfEachKindIsReturned()
    {
        _index.Answer = _ => [.. Enumerable.Range(0, 30).Select(number => Rows.File($@"C:\Docs\budget{number}.txt"))];

        var results = await Service().SearchAsync(new FileSearchQuery("budget")
        {
            Types = [SearchResultItemType.File],
            MaxResultsPerType = 3,
        });

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task NoMoreThanTheLimitIsEverReturnedWhateverIsAsked()
    {
        _index.Answer = _ => [.. Enumerable.Range(0, 300).Select(number => Rows.File($@"C:\Docs\budget{number}.txt"))];

        var results = await Service().SearchAsync(new FileSearchQuery("budget")
        {
            Types = [SearchResultItemType.File],
            MaxResultsPerType = int.MaxValue,
        });

        Assert.Equal(WindowsFileSearchService.MaxResultsPerType, results.Count);
    }

    [Fact]
    public async Task ABetterNameComesBeforeANewerWorseOne()
    {
        _index.Answer = _ =>
        [
            Rows.File(@"C:\Docs\old budget draft.docx"),
            Rows.File(@"C:\Docs\rebudgeting.docx"),
            Rows.File(@"C:\Docs\budget planning.docx"),
            Rows.File(@"C:\Docs\budget.docx"),
        ];

        var results = await Service().SearchAsync(new FileSearchQuery("budget") { Types = [SearchResultItemType.File] });

        Assert.Equal(
            ["budget.docx", "budget planning.docx", "old budget draft.docx", "rebudgeting.docx"],
            results.Select(item => item.DisplayName));
    }

    [Fact]
    public async Task HiddenSystemAndExcludedItemsAreLeftOutAndTheRestStillFillTheLimit()
    {
        _index.Answer = _ =>
        [
            Rows.File(@"C:\Private\budget1.txt"),
            Rows.File(@"C:\Docs\hidden budget.txt", attributes: 2),
            Rows.File(@"C:\Docs\system budget.txt", attributes: 4),
            Rows.File(@"C:\Docs\budget2.txt"),
            Rows.File(@"C:\Docs\budget3.txt"),
        ];

        var results = await Service(@"C:\Private").SearchAsync(new FileSearchQuery("budget")
        {
            Types = [SearchResultItemType.File],
            MaxResultsPerType = 2,
        });

        Assert.Equal([@"C:\Docs\budget2.txt", @"C:\Docs\budget3.txt"], results.Select(item => item.Path));
        Assert.Contains("NOT LIKE 'file:C:/Private'", Assert.Single(_index.Queries), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnItemTheIndexReturnsTwiceIsShownOnce()
    {
        _index.Answer = _ => [Rows.File(@"C:\Docs\budget.txt"), Rows.File(@"c:\docs\BUDGET.txt")];

        var results = await Service().SearchAsync(new FileSearchQuery("budget") { Types = [SearchResultItemType.File] });

        Assert.Single(results);
    }

    [Fact]
    public async Task MatchingContentsAsksForFullTextAndKeepsTheExcerpt()
    {
        var row = Rows.Row(@"C:\Docs\notes.txt", ".txt", length: 16);
        row[SearchColumns.Summary] = "the merger budget";
        _index.Answer = _ => [row];

        var results = await Service().SearchAsync(new FileSearchQuery("merger")
        {
            Types = [SearchResultItemType.File],
            MatchContents = true,
        });

        Assert.Contains("CONTAINS(", Assert.Single(_index.Queries), StringComparison.Ordinal);
        var item = Assert.Single(results);
        Assert.Equal("the merger budget", item.Snippet);
    }

    [Theory]
    [InlineData(FileSearchFailure.IndexUnavailable)]
    [InlineData(FileSearchFailure.TimedOut)]
    [InlineData(FileSearchFailure.QueryFailed)]
    public async Task AFailureOfTheIndexReachesTheCallerAsItWasAndIsLoggedWithoutTheQuery(FileSearchFailure failure)
    {
        _index.Failure = new FileSearchException(failure, unchecked((int)0x80041605));

        var thrown = await Assert.ThrowsAsync<FileSearchException>(
            () => Service().SearchAsync(new FileSearchQuery(Secret)));

        Assert.Equal(failure, thrown.Failure);
        Assert.Equal(unchecked((int)0x80041605), thrown.ProviderErrorCode);
        Assert.Contains(failure.ToString(), _logger.AllText, StringComparison.Ordinal);
        Assert.Contains("80041605", _logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, _logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingPrivateIsLoggedForASearchThatWorks()
    {
        _index.Answer = _ => [Rows.File($@"C:\Docs\{Secret}.docx")];

        await Service().SearchAsync(new FileSearchQuery(Secret));

        Assert.NotEmpty(_logger.AllText);
        Assert.DoesNotContain(Secret, _logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Docs", _logger.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledSearchThrowsCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service().SearchAsync(new FileSearchQuery("budget"), cancelled.Token));
        Assert.Empty(_index.Queries);
    }

    // ---- Step 52: the structured query --------------------------------------------------------------------------------

    [Fact]
    public async Task TheLastFiveScreenshotsIsOneFileQueryInModifiedOrderKeptAsTheIndexGaveIt()
    {
        // The default kinds include folders and apps; a name, an extension and an order make it a query for files.
        _index.Answer = _ =>
        [
            .. Enumerable.Range(0, 12).Select(number =>
                Rows.File($@"C:\Users\Ada\Pictures\Screenshot {12 - number}.png", modified: new DateTime(2026, 9, 30, 12, 0, 0).AddMinutes(-number))),
        ];

        var results = await Service().SearchAsync(new FileSearchQuery
        {
            Filename = "Screenshot",
            Extensions = [".png"],
            Order = FileSearchOrder.ModifiedDescending,
            MaxResultsPerType = 5,
        });

        var sql = Assert.Single(_index.Queries);
        Assert.Contains("System.IsFolder = false", sql, StringComparison.Ordinal);
        Assert.Contains("System.FileName LIKE '%Screenshot%'", sql, StringComparison.Ordinal);
        Assert.Contains("System.FileExtension = '.png'", sql, StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY System.DateModified DESC, System.ItemUrl ASC", sql, StringComparison.Ordinal);
        Assert.Equal(
            ["Screenshot 12.png", "Screenshot 11.png", "Screenshot 10.png", "Screenshot 9.png", "Screenshot 8.png"],
            results.Select(item => item.DisplayName));
        Assert.All(results, item => Assert.Equal(SearchResultItemType.File, item.Type));
    }

    [Fact]
    public async Task ThatQueryGivesTheSameListEveryTimeForTheSameIndex()
    {
        _index.Answer = _ => [.. Enumerable.Range(0, 30).Select(number => Rows.File($@"C:\Pictures\Screenshot {number}.png"))];
        var query = new FileSearchQuery
        {
            Filename = "Screenshot",
            Extensions = ["png"],
            Order = FileSearchOrder.ModifiedDescending,
            MaxResultsPerType = 5,
        };

        var first = await Service().SearchAsync(query);
        var second = await Service().SearchAsync(query with { Types = [SearchResultItemType.File] });

        Assert.Equal(first.Select(item => item.Path), second.Select(item => item.Path));
        Assert.Equal(_index.Queries[0], _index.Queries[1]);
    }

    [Fact]
    public async Task AnExplicitOrderIsTheIndexsOwnWhileRelevanceJudgesTheName()
    {
        _index.Answer = _ => [Rows.File(@"C:\Docs\rebudgeting.docx"), Rows.File(@"C:\Docs\budget.docx")];
        var query = new FileSearchQuery("budget") { Types = [SearchResultItemType.File] };

        var byRelevance = await Service().SearchAsync(query);
        var byDate = await Service().SearchAsync(query with { Order = FileSearchOrder.ModifiedAscending });

        Assert.Equal(["budget.docx", "rebudgeting.docx"], byRelevance.Select(item => item.DisplayName));
        Assert.Equal(["rebudgeting.docx", "budget.docx"], byDate.Select(item => item.DisplayName));
    }

    [Fact]
    public async Task AFilenameOnlyQueryIsOrderedByHowWellTheNameFitsItsWords()
    {
        _index.Answer = _ => [Rows.File(@"C:\Docs\old budget draft.docx"), Rows.File(@"C:\Docs\budget.docx")];

        var results = await Service().SearchAsync(new FileSearchQuery { Filename = "budget", Types = [SearchResultItemType.File] });

        Assert.Equal(["budget.docx", "old budget draft.docx"], results.Select(item => item.DisplayName));
    }

    [Fact]
    public async Task AQueryOfCriteriaWithoutWordsIsAskedAndAnswered()
    {
        _index.Answer = _ => [Rows.File(@"C:\Docs\a.pdf"), Rows.File(@"C:\Docs\b.pdf")];

        var results = await Service().SearchAsync(new FileSearchQuery
        {
            Extensions = [".pdf"],
            Modified = new DateRange(From: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)),
            Size = new SizeRange(MinBytes: 1000),
        });

        Assert.Equal(2, results.Count);
        var sql = Assert.Single(_index.Queries);
        Assert.Contains("System.DateModified >= '2026-09-01 00:00:00'", sql, StringComparison.Ordinal);
        Assert.Contains("System.Size >= 1000", sql, StringComparison.Ordinal);
    }

    public static TheoryData<FileSearchQuery> QueriesNothingCanMeet() => new()
    {
        new FileSearchQuery(),
        new FileSearchQuery { Order = FileSearchOrder.ModifiedDescending, MaxResultsPerType = 5 },
        new FileSearchQuery("budget") { Modified = new DateRange(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)) },
        new FileSearchQuery("budget") { Folder = @"Documents" },
        new FileSearchQuery("budget") { Size = new SizeRange(10, 5) },
        new FileSearchQuery("budget") { Extensions = ["a b", "*"] },
        new FileSearchQuery("budget") { Filename = "? !" },
        new FileSearchQuery("budget") { ContentTerm = "((( ---" },
        new FileSearchQuery("budget") { Kind = (FileKind)77 },
        new FileSearchQuery { Extensions = [".png"], Types = [SearchResultItemType.Folder] },
    };

    [Theory]
    [MemberData(nameof(QueriesNothingCanMeet))]
    public async Task AQueryNothingCanMeetAsksNothingAndFindsNothing(FileSearchQuery query)
    {
        _index.Answer = _ => [Rows.File(@"C:\Docs\budget.txt")];

        Assert.Empty(await Service().SearchAsync(query));
        Assert.Empty(_index.Queries);
    }

    [Fact]
    public async Task AFolderIsSearchedInItsScopeAndBesideAnExcludedChildStillLeavesItOut()
    {
        await Service(@"C:\Docs\Private").SearchAsync(new FileSearchQuery("budget") { Folder = @"C:\Docs", Types = [SearchResultItemType.File] });

        var sql = Assert.Single(_index.Queries);
        Assert.Contains("SCOPE='file:C:/Docs' AND", sql, StringComparison.Ordinal);
        Assert.Contains("System.ItemUrl NOT LIKE 'file:C:/Docs/Private/%'", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Docs\Private")]
    [InlineData(@"C:\Docs\Private\Deep")]
    [InlineData(@"c:\docs\PRIVATE\")]
    public async Task AFolderTheUserHasExcludedOrThatIsInsideOneIsNotSearchedAtAll(string folder)
    {
        _index.Answer = _ => [Rows.File(folder + @"\budget.txt")];

        var results = await Service(@"C:\Docs\Private").SearchAsync(new FileSearchQuery("budget") { Folder = folder });

        Assert.Empty(results);
        Assert.Empty(_index.Queries);
    }

    [Fact]
    public async Task ANonRecursiveFolderSearchIsForItsDirectChildrenOnly()
    {
        await Service().SearchAsync(new FileSearchQuery("budget") { Folder = @"C:\Docs", IncludeSubfolders = false });

        Assert.All(_index.Queries, sql => Assert.Contains("DIRECTORY='file:C:/Docs' AND", sql, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASearchByFilenameAlonePutsNothingOfItsWordsInTheContentPart()
    {
        await Service().SearchAsync(new FileSearchQuery { Filename = "budget", Types = [SearchResultItemType.File] });

        var sql = Assert.Single(_index.Queries);
        Assert.DoesNotContain("CONTAINS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoSummary", sql, StringComparison.Ordinal);
    }

    // ---- Step 53: content-aware search --------------------------------------------------------------------------------

    private static object?[] RowWithSummary(string path, string summary)
    {
        var row = Rows.Row(path, Path.GetExtension(path), size: 1234UL, length: 16);
        row[SearchColumns.Summary] = summary;
        return row;
    }

    [Fact]
    public async Task AContentTermFindsFilesAndTheirPropertiesAndNeverAnExcerptOfTheirText()
    {
        // The real provider does not even select the excerpt column here; a row that has one is dropped of it all the same.
        _index.Answer = _ => [RowWithSummary(@"C:\Docs\notes.txt", "private words from inside the file")];

        var results = await Service().SearchAsync(new FileSearchQuery { ContentTerm = "merger plan", Types = [SearchResultItemType.File] });

        var item = Assert.Single(results);
        Assert.Equal(@"C:\Docs\notes.txt", item.Path);
        Assert.Equal(".txt", item.Extension);
        Assert.Equal(1234L, item.SizeBytes);
        Assert.NotNull(item.ModifiedAt);
        Assert.Null(item.Snippet);
        var sql = Assert.Single(_index.Queries);
        Assert.Contains("CONTAINS(System.Search.Contents, '\"merger plan\"')", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoSummary", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOlderMatchContentsFlagStillKeepsTheIndexsExcerpt()
    {
        _index.Answer = _ => [RowWithSummary(@"C:\Docs\notes.txt", "the merger budget")];

        var results = await Service().SearchAsync(new FileSearchQuery("merger") { MatchContents = true, Types = [SearchResultItemType.File] });

        Assert.Equal("the merger budget", Assert.Single(results).Snippet);
    }

    [Fact]
    public async Task AContentQueryFindsFilesAndNotFoldersAndAsksOnce()
    {
        await Service().SearchAsync(new FileSearchQuery { ContentTerm = "invoice" });

        var sql = Assert.Single(_index.Queries);
        Assert.Contains("System.IsFolder = false", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASearchWithoutAContentCriterionSaysContentIsNotRequestedAndAsksNothingMore()
    {
        _index.Answer = _ => [Rows.File(@"C:\Docs\budget.txt")];

        var outcome = await Service().SearchWithCapabilitiesAsync(new FileSearchQuery("budget") { Folder = @"C:\Docs" });

        Assert.Equal(ContentSearchSupport.NotRequested, outcome.ContentSearch.Support);
        Assert.False(outcome.ContentSearch.IsLimited);
        Assert.All(_index.Queries, sql => Assert.False(IsLocationProbe(sql)));
        Assert.NotEmpty(outcome.Items);
    }

    [Fact]
    public async Task AContentQueryAnywhereInTheIndexIsAvailableWithoutAProbe()
    {
        _index.Answer = _ => [Rows.File(@"C:\Docs\a.txt")];

        var outcome = await Service().SearchWithCapabilitiesAsync(new FileSearchQuery { ContentTerm = "invoice" });

        Assert.Equal(ContentSearchSupport.Available, outcome.ContentSearch.Support);
        Assert.Equal(ContentSearchLimits.None, outcome.ContentSearch.Limits);
        Assert.Empty(outcome.ContentSearch.UnsupportedExtensions);
        Assert.Single(_index.Queries);
        Assert.Single(outcome.Items);
    }

    [Fact]
    public async Task AContentQueryInAFolderTheIndexHoldsNothingUnderIsFlaggedNotIndexedAndStillAnswers()
    {
        var outcome = await Service().SearchWithCapabilitiesAsync(
            new FileSearchQuery { ContentTerm = "invoice", Folder = @"E:\Share" });

        Assert.Equal(ContentSearchSupport.Unavailable, outcome.ContentSearch.Support);
        Assert.Equal(ContentSearchLimits.LocationNotIndexed, outcome.ContentSearch.Limits);
        Assert.True(outcome.ContentSearch.IsUnavailable);
        Assert.Empty(outcome.Items);
        Assert.Equal(2, _index.Queries.Count);
        Assert.Contains("SELECT TOP 1 System.ItemUrl FROM SystemIndex WHERE SCOPE='file:E:/Share'", _index.Queries[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContentQueryInAFolderTheIndexHoldsItemsUnderIsAvailable()
    {
        _index.Answer = sql => IsLocationProbe(sql) ? [new object?[] { "file:C:/Docs/a.txt" }] : [Rows.File(@"C:\Docs\a.txt")];

        var outcome = await Service().SearchWithCapabilitiesAsync(
            new FileSearchQuery { ContentTerm = "invoice", Folder = @"C:\Docs" });

        Assert.Equal(ContentSearchSupport.Available, outcome.ContentSearch.Support);
        Assert.Single(outcome.Items);
    }

    [Fact]
    public async Task TheFlagNeverChangesWhatIsFound()
    {
        _index.Answer = _ => [Rows.File(@"C:\Docs\notes.md")];
        var types = new FakeContentTypes((".md", ContentTypeSupport.NoContentFilter));
        var query = new FileSearchQuery { ContentTerm = "invoice", Extensions = [".md"] };

        var outcome = await ServiceWith(types).SearchWithCapabilitiesAsync(query);
        var plain = await ServiceWith(types).SearchAsync(query);

        Assert.True(outcome.ContentSearch.IsUnavailable);
        Assert.Equal(plain.Select(item => item.Path), outcome.Items.Select(item => item.Path));
        Assert.Single(plain);
    }

    [Fact]
    public async Task AFileTypeWithNoFilterIsFlaggedAndTheOthersAreNot()
    {
        var types = new FakeContentTypes(
            (".md", ContentTypeSupport.NoContentFilter),
            (".pdf", ContentTypeSupport.HasContentFilter),
            (".png", ContentTypeSupport.NoTextInMedia));

        var some = await ServiceWith(types).SearchWithCapabilitiesAsync(
            new FileSearchQuery { ContentTerm = "invoice", Extensions = ["md", ".pdf"] });
        Assert.Equal(ContentSearchSupport.Partial, some.ContentSearch.Support);
        Assert.Equal(ContentSearchLimits.FileTypeNotContentIndexed, some.ContentSearch.Limits);
        Assert.Equal([".md"], some.ContentSearch.UnsupportedExtensions);
        Assert.True(some.ContentSearch.IsLimited);
        Assert.False(some.ContentSearch.IsUnavailable);

        var all = await ServiceWith(types).SearchWithCapabilitiesAsync(
            new FileSearchQuery { ContentTerm = "invoice", Extensions = [".md", ".png"] });
        Assert.Equal(ContentSearchSupport.Unavailable, all.ContentSearch.Support);
        Assert.Equal([".md", ".png"], all.ContentSearch.UnsupportedExtensions);

        var none = await ServiceWith(types).SearchWithCapabilitiesAsync(
            new FileSearchQuery { ContentTerm = "invoice", Extensions = [".pdf"] });
        Assert.Equal(ContentSearchSupport.Available, none.ContentSearch.Support);
    }

    [Theory]
    [InlineData(FileKind.Picture)]
    [InlineData(FileKind.Video)]
    [InlineData(FileKind.Music)]
    public async Task ScreenshotsPicturesVideosAndSoundsHaveNoTextForAContentSearchToFind(FileKind kind)
    {
        var outcome = await Service().SearchWithCapabilitiesAsync(new FileSearchQuery { ContentTerm = "invoice", Kind = kind });

        Assert.Equal(ContentSearchSupport.Unavailable, outcome.ContentSearch.Support);
        Assert.Equal(ContentSearchLimits.FileTypeNotContentIndexed, outcome.ContentSearch.Limits);
    }

    [Fact]
    public async Task ADocumentKindIsNotLimitedAndATypeWindowsCannotBeAskedAboutIsNotCalledUnavailable()
    {
        var unknown = new FakeContentTypes((".weird", ContentTypeSupport.Unknown));

        var document = await Service().SearchWithCapabilitiesAsync(new FileSearchQuery { ContentTerm = "invoice", Kind = FileKind.Document });
        var weird = await ServiceWith(unknown).SearchWithCapabilitiesAsync(new FileSearchQuery { ContentTerm = "invoice", Extensions = [".weird"] });

        Assert.Equal(ContentSearchSupport.Available, document.ContentSearch.Support);
        Assert.Equal(ContentSearchSupport.Available, weird.ContentSearch.Support);
        Assert.Contains(".weird", unknown.Asked);
    }

    [Fact]
    public async Task ALocationAndATypeThatBothLimitAreBothReported()
    {
        var types = new FakeContentTypes((".md", ContentTypeSupport.NoContentFilter), (".pdf", ContentTypeSupport.HasContentFilter));

        var outcome = await ServiceWith(types).SearchWithCapabilitiesAsync(
            new FileSearchQuery { ContentTerm = "invoice", Folder = @"E:\Share", Extensions = [".md", ".pdf"] });

        Assert.Equal(ContentSearchSupport.Unavailable, outcome.ContentSearch.Support);
        Assert.Equal(ContentSearchLimits.LocationNotIndexed | ContentSearchLimits.FileTypeNotContentIndexed, outcome.ContentSearch.Limits);
        Assert.Equal([".md"], outcome.ContentSearch.UnsupportedExtensions);
    }

    [Fact]
    public async Task AMatchContentsSearchInAFolderIsFlaggedToo()
    {
        var outcome = await Service().SearchWithCapabilitiesAsync(
            new FileSearchQuery("invoice") { MatchContents = true, Folder = @"E:\Share" });

        Assert.Equal(ContentSearchSupport.Unavailable, outcome.ContentSearch.Support);
    }

    [Fact]
    public async Task APlainSearchNeverRunsTheProbeOrTheTypeLookup()
    {
        var types = new FakeContentTypes((".md", ContentTypeSupport.NoContentFilter));

        await ServiceWith(types).SearchAsync(new FileSearchQuery { ContentTerm = "invoice", Folder = @"C:\Docs", Extensions = [".md"] });

        Assert.All(_index.Queries, sql => Assert.False(IsLocationProbe(sql)));
        Assert.Empty(types.Asked);
    }

    [Fact]
    public async Task AQueryThatIsNotPutToTheIndexSaysNothingOfItsLimits()
    {
        var content = await Service().SearchWithCapabilitiesAsync(new FileSearchQuery { ContentTerm = "invoice", Folder = "relative" });
        var plain = await Service().SearchWithCapabilitiesAsync(new FileSearchQuery());
        var excluded = await Service(@"C:\Docs").SearchWithCapabilitiesAsync(new FileSearchQuery { ContentTerm = "invoice", Folder = @"C:\Docs" });

        Assert.Empty(content.Items);
        Assert.Equal(ContentSearchSupport.Available, content.ContentSearch.Support);
        Assert.Equal(ContentSearchSupport.NotRequested, plain.ContentSearch.Support);
        Assert.Equal(ContentSearchSupport.Available, excluded.ContentSearch.Support);
        Assert.Empty(_index.Queries);
    }

    [Fact]
    public async Task AFailureOfTheProbeReachesTheCallerLikeAnyOther()
    {
        var calls = 0;
        var flaky = new FakeIndex { Answer = _ => calls++ == 0 ? [] : throw new FileSearchException(FileSearchFailure.TimedOut) };
        var service = new WindowsFileSearchService(new FakeSettings(), flaky, _logger);

        var thrown = await Assert.ThrowsAsync<FileSearchException>(() =>
            service.SearchWithCapabilitiesAsync(new FileSearchQuery { ContentTerm = "invoice", Folder = @"C:\Docs" }));

        Assert.Equal(FileSearchFailure.TimedOut, thrown.Failure);
    }

    [Fact]
    public async Task NoContentTermFolderOrFilenameIsEverLogged()
    {
        var types = new FakeContentTypes((".md", ContentTypeSupport.NoContentFilter));

        await ServiceWith(types).SearchWithCapabilitiesAsync(new FileSearchQuery
        {
            ContentTerm = "merger-with-contoso",
            Filename = "secret-plan",
            Folder = @"E:\Confidential\contoso",
            Extensions = [".md"],
        });

        Assert.Contains("Unavailable", _logger.AllText, StringComparison.Ordinal);
        Assert.Contains("LocationNotIndexed", _logger.AllText, StringComparison.Ordinal);
        foreach (var secret in new[] { "merger", "contoso", "secret-plan", "Confidential", ".md" })
        {
            Assert.DoesNotContain(secret, _logger.AllText, StringComparison.Ordinal);
        }
    }
}
