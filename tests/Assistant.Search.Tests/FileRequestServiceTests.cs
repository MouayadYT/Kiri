using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Search.Planning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>
/// A request to find files from start to end: clearly a file search or not, the Files permission, the plan, the search and how it
/// ended, and what the bar looks up as the user types.
/// </summary>
public sealed class FileRequestServiceTests
{
    private static readonly SearchResultItem Shot1 = new(SearchResultItemType.File, "Screenshot 1.png", @"C:\Pictures\Screenshot 1.png");
    private static readonly SearchResultItem Shot2 = new(SearchResultItemType.File, "Screenshot 2.png", @"C:\Pictures\Screenshot 2.png");

    private static PlannedFileSearch Plan(FileSearchQuery? query, FileSearchPlanSource source = FileSearchPlanSource.Read) =>
        new(query, source);

    private static (FileRequestService Service, FakePlanner Planner, FakeFileSearch Search, FakePermissions Permissions) Create(
        PlannedFileSearch? plan = null, Func<string, PlannedFileSearch?>? quick = null, ILogger<FileRequestService>? logger = null)
    {
        var planner = new FakePlanner(quick, _ => plan ?? Plan(new FileSearchQuery("biology")));
        var search = new FakeFileSearch { Items = [Shot1, Shot2] };
        var permissions = new FakePermissions();
        return (new FileRequestService(planner, search, permissions, logger ?? NullLogger<FileRequestService>.Instance), planner, search, permissions);
    }

    // -- Is it clearly a request to find files? --

    [Theory]
    [InlineData("find the PDF about biology I edited last Tuesday")]
    [InlineData("Show me the last 5 screenshots I took")]
    [InlineData("show me the last 5 screenshots I took?")]
    [InlineData("Find the image I took yesterday")]
    [InlineData("where is my tax return pdf")]
    [InlineData("where did I save the budget spreadsheet")]
    [InlineData("can you find my documents from last week")]
    [InlineData("show me my photos from last week")]
    [InlineData("what files did I edit yesterday")]
    [InlineData("which screenshots are from today")]
    [InlineData("list my downloads")]
    [InlineData("find my files in the downloads folder")]
    [InlineData("please locate the presentation I made last month")]
    [InlineData("I need the latest videos I recorded")]
    [InlineData("search for budget.xlsx")]
    [InlineData("fidn the annas aarhcive pdf")]
    [InlineData("find annas arhciv pdff")]
    [InlineData("can you serach for my documnet about taxes")]
    [InlineData("find the last doc i made")]
    [InlineData("find the pdf on myu computer")]
    public void ARequestThatAsksToFindFilesIsClearlyAFileSearch(string request)
    {
        Assert.True(FileRequestClassifier.IsFileRequest(request), request);
    }

    [Theory]
    [InlineData("show me how to write a PDF parser")]
    [InlineData("find a picture of a sunset")]
    [InlineData("show me an image of the eiffel tower")]
    [InlineData("find the derivative of x squared")]
    [InlineData("find out what time it is")]
    [InlineData("find the bug in this file")]
    [InlineData("find a good file manager for windows")]
    [InlineData("where is the nearest pharmacy")]
    [InlineData("show me my schedule")]
    [InlineData("find my keys")]
    [InlineData("summarize my documents")]
    [InlineData("explain this pdf")]
    [InlineData("write a report about file systems")]
    [InlineData("what is 9+10")]
    [InlineData("find the zip code for boston")]
    [InlineData("find me a cheat sheet for git")]
    [InlineData("find a deck of cards game")]
    [InlineData("fine, thanks")]
    [InlineData("annas arhcive")]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("   ")]
    public void AQuestionOrATaskForTheModelIsNotAFileSearch(string request)
    {
        Assert.False(FileRequestClassifier.IsFileRequest(request), request);
    }

    [Theory]
    [InlineData("find that pdf", true)]
    [InlineData("find it", true)]
    [InlineData("show me it again", true)]
    [InlineData("where is it", true)]
    [InlineData("can you find those files", true)]
    [InlineData("fidn that pdf", true)]
    [InlineData("find the IT report", false)]
    [InlineData("find that pdf about biology", false)]
    [InlineData("what does it mean", false)]
    [InlineData("yes", false)]
    [InlineData("thanks, that works", false)]
    [InlineData("", false)]
    public void ARequestThatOnlyPointsBackIsAFollowUp_AndOneThatNamesAnythingNewIsNot(string request, bool expected)
    {
        var (service, _, _, _) = Create();

        Assert.Equal(expected, service.IsFollowUp(request));
    }

    [Fact]
    public void TheServiceCountsARuleShapedRequestWithoutAVerbAsAFileRequestToo()
    {
        var (service, _, _, _) = Create(quick: request => request == "last 5 screenshots" ? Plan(new FileSearchQuery(), FileSearchPlanSource.Template) : null);

        Assert.True(service.IsFileRequest("last 5 screenshots"));
        Assert.True(service.IsFileRequest("find the PDF about biology"));
        Assert.False(service.IsFileRequest("what is 9+10"));
    }

    // -- Finding files. --

    [Fact]
    public async Task AnAllowedRequestIsPlannedSearchedAndReturnedWithHowItWasFound()
    {
        var query = new FileSearchQuery { Filename = "Screenshot", Extensions = ImageFileTypes.Extensions, MaxResultsPerType = 5 };
        var (service, planner, search, permissions) = Create(Plan(query, FileSearchPlanSource.Template));
        search.Capability = ContentSearchCapability.Available;

        var result = await service.FindAsync("show me the last 5 screenshots I took");

        Assert.Equal(FileRequestStatus.Found, result.Status);
        Assert.Equal([Shot1, Shot2], result.Items);
        Assert.Same(query, result.Plan!.Query);
        Assert.True(result.AsksForImages);
        Assert.Equal(ContentSearchSupport.Available, result.ContentSearch.Support);
        Assert.Equal(["show me the last 5 screenshots I took"], planner.Planned);
        Assert.Same(query, Assert.Single(search.Queries));
        Assert.Equal(1, permissions.Asked);
    }

    [Fact]
    public async Task ARequestForDocumentsIsNotAGallery()
    {
        var (service, _, _, _) = Create(Plan(new FileSearchQuery { Filename = "biology", Extensions = [".pdf"] }));

        var result = await service.FindAsync("find the PDF about biology");

        Assert.Equal(FileRequestStatus.Found, result.Status);
        Assert.False(result.AsksForImages);
    }

    [Fact]
    public async Task WithTheFilesPermissionOffNothingIsPlannedOrSearched()
    {
        var (service, planner, search, permissions) = Create();
        permissions.FilesAllowed = false;

        var result = await service.FindAsync("find the PDF about biology");

        Assert.Equal(FileRequestStatus.FilesTurnedOff, result.Status);
        Assert.Empty(result.Items);
        Assert.Null(result.Plan);
        Assert.Empty(planner.Planned);
        Assert.Empty(search.Queries);
    }

    [Fact]
    public async Task ASearchThatFindsNothingOrHasNothingToSearchForSaysSo()
    {
        var (service, _, search, _) = Create();
        search.Items = [];
        Assert.Equal(FileRequestStatus.NothingFound, (await service.FindAsync("find the PDF about biology")).Status);
        Assert.Single(search.Queries);

        var (nothing, _, none, _) = Create(Plan(null, FileSearchPlanSource.Read));
        var result = await nothing.FindAsync("find my");
        Assert.Equal(FileRequestStatus.NothingToSearch, result.Status);
        Assert.Empty(none.Queries);
        Assert.NotNull(result.Plan);
    }

    [Theory]
    [InlineData(FileSearchFailure.IndexUnavailable, FileRequestStatus.SearchUnavailable)]
    [InlineData(FileSearchFailure.TimedOut, FileRequestStatus.SearchTimedOut)]
    [InlineData(FileSearchFailure.QueryFailed, FileRequestStatus.SearchFailed)]
    public async Task ASearchThatCannotRunIsAStatusAndNotAnException(FileSearchFailure failure, FileRequestStatus status)
    {
        var (service, _, search, _) = Create();
        search.Failure = new FileSearchException(failure);

        var result = await service.FindAsync("find the PDF about biology");

        Assert.Equal(status, result.Status);
        Assert.Empty(result.Items);
        Assert.NotNull(result.Plan);
    }

    [Fact]
    public async Task CancellingStopsTheRequest()
    {
        var (service, _, _, _) = Create();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FindAsync("find the PDF", cancelled.Token));
    }

    [Fact]
    public async Task OnlyCountsAndWhereThePlanCameFromAreLogged()
    {
        var logger = new CapturingLogger();
        var (service, _, search, _) = Create(Plan(new FileSearchQuery("SECRETQUERY")), logger: new TypedLogger<FileRequestService>(logger));
        search.Items = [new SearchResultItem(SearchResultItemType.File, "SECRETNAME.docx", @"C:\SECRETPATH\SECRETNAME.docx")];

        await service.FindAsync("find SECRETREQUEST");

        Assert.Contains("5512", logger.AllText);
        Assert.DoesNotContain("SECRET", logger.AllText, StringComparison.OrdinalIgnoreCase);
    }

    // -- What the bar looks up as the user types. --

    [Fact]
    public async Task TypedWordsAreLookedUpInFileAndFolderNames()
    {
        var (service, planner, search, _) = Create();

        var found = await service.LookUpAsync("budget");

        Assert.Equal([Shot1, Shot2], found);
        var query = Assert.Single(search.Queries);
        Assert.Equal("budget", query.Text);
        Assert.Equal([SearchResultItemType.File, SearchResultItemType.Folder], query.Types);
        Assert.Equal(KeywordQuery.MaxLiveResultsPerType, query.MaxResultsPerType);

        // The model is never asked while typing.
        Assert.Empty(planner.Planned);
    }

    [Fact]
    public async Task TypedWordsThatStartLikeARequestAreLookedUpByTheirKeywords()
    {
        var (service, _, search, _) = Create();

        await service.LookUpAsync("find my resume");

        Assert.Equal("resume", Assert.Single(search.Queries).Text);
    }

    [Fact]
    public async Task ARequestOfAKnownShapeIsLookedUpByItsRulesQueryAsSoonAsItIsTyped()
    {
        var rule = new FileSearchQuery { Filename = "Screenshot", Extensions = ImageFileTypes.Extensions, MaxResultsPerType = 5 };
        var (service, _, search, _) = Create(quick: _ => Plan(rule, FileSearchPlanSource.Template));

        await service.LookUpAsync("show me the last 5 screenshots I took");

        Assert.Same(rule, Assert.Single(search.Queries));
    }

    [Fact]
    public async Task TypingIsNeverInterruptedByAFailureAndNothingIsLookedUpWithoutPermissionOrWords()
    {
        var (service, _, search, permissions) = Create();

        // Too little to look up, or no letter at all: the permission is not even asked.
        Assert.Empty(await service.LookUpAsync("a"));
        Assert.Empty(await service.LookUpAsync("!?"));
        Assert.Empty(await service.LookUpAsync(""));
        Assert.Equal(0, permissions.Asked);
        Assert.Empty(search.Queries);

        permissions.FilesAllowed = false;
        Assert.Empty(await service.LookUpAsync("budget"));
        Assert.Empty(search.Queries);

        permissions.FilesAllowed = true;
        search.Failure = new FileSearchException(FileSearchFailure.IndexUnavailable);
        Assert.Empty(await service.LookUpAsync("budget"));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LookUpAsync("budget", cancelled.Token));
    }

    [Fact]
    public void TheKeywordsOfARequestAreWhatIsLeftWhenTheFillerIsTakenOut()
    {
        Assert.Equal("pdf biology", KeywordQuery.Keywords("find the PDF about biology I edited last Tuesday"));
        Assert.Equal("screenshots", KeywordQuery.Keywords("show me the last 5 screenshots I took"));
        Assert.Equal("budget 2026 final", KeywordQuery.Keywords("Budget 2026 final"));
        Assert.Null(KeywordQuery.Keywords("find my"));
        Assert.Null(KeywordQuery.Keywords(""));
        Assert.Equal(8, KeywordQuery.Keywords("one1 two2 three3 four4 five5 six6 seven7 eight8 nine9 ten10")!.Split(' ').Length);

        Assert.Null(KeywordQuery.Live("?!"));
        Assert.Equal("\"annual report\"", KeywordQuery.Live("\"annual report\"")!.Text);
    }

    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
