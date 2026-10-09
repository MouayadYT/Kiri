using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>
/// The second look inside a request to find files (PROJECT_SPEC §4.7): when no name holds the words exactly, names are gathered
/// by each word and by the plan's other limits, scored word by word, and those that hold only some words are judged by the model.
/// </summary>
public sealed class SecondLookTests
{
    private static SearchResultItem Pdf(string name) =>
        new(SearchResultItemType.File, name, @"C:\Users\Test\Downloads\" + name) { ModifiedAt = PlannerDay.Midnight(2026, 9, 21) };

    // Names as the download site cut them: every word in some form, or only "Anna's".
    private static readonly SearchResultItem Archiv = Pdf("Changes in the Land -- e9ce34896a -- Anna\u2019s Archiv.pdf");
    private static readonly SearchResultItem Ar = Pdf("Manitou and providence -- 8c78ce -- Anna\u2019s Ar.pdf");
    private static readonly SearchResultItem OnlyA = Pdf("Conquering the American Wilderness -- 3e11cd -- Anna\u2019s A.pdf");
    private static readonly SearchResultItem Savannah = Pdf("Northwind Presentaion Marcus & Savannah.pdf");
    private static readonly SearchResultItem Recent = Pdf("Projectile Motion.pdf");

    private static readonly FileSearchQuery AnnasArchive = new("annas archive") { Extensions = [".pdf"], MaxResultsPerType = 10 };

    private static PlannedFileSearch Plan(FileSearchQuery? query = null, params string[] words) =>
        new(query ?? AnnasArchive, FileSearchPlanSource.Read) { Keywords = words.Length > 0 ? words : ["annas", "archive"] };

    // The exact search finds nothing; "annas" finds the four names (and "anna" in Savannah's); the newest PDFs are other files.
    private static IReadOnlyList<SearchResultItem> Answer(FileSearchQuery query) => query.Text switch
    {
        "annas" => [Archiv, Ar, OnlyA, Savannah],
        "arch" => [Archiv],
        null => [Recent, Archiv],
        _ => [],
    };

    private static (FileRequestService Service, FakeFileSearch Search, FakeReviewer? Reviewer) Create(
        PlannedFileSearch? plan = null,
        Func<string, IReadOnlyList<SearchResultItem>, IReadOnlyList<SearchResultItem>?>? choose = null,
        bool withReviewer = true,
        ILogger<FileRequestService>? logger = null)
    {
        var search = new FakeFileSearch { Answer = Answer };
        var reviewer = withReviewer ? new FakeReviewer(choose) : null;
        var service = new FileRequestService(
            new FakePlanner(plan: _ => plan ?? Plan()), search, new FakePermissions(), logger ?? NullLogger<FileRequestService>.Instance, reviewer);
        return (service, search, reviewer);
    }

    [Fact]
    public async Task NamesThatHoldEveryWordComeFirst_AndTheModelJudgesOnlyThoseThatHoldSome()
    {
        var (service, search, reviewer) = Create(choose: (_, partial) => partial);

        var result = await service.FindAsync("find the annas archive pdf");

        Assert.Equal(FileRequestStatus.Found, result.Status);
        Assert.True(result.Reviewed);
        Assert.Equal([Archiv, Ar, OnlyA], result.Items);

        // The exact search, each word and the start of the long one, then the newest PDFs; the PDF limit is on every one.
        Assert.Equal(["annas archive", "annas", "archive", "arch", "(newest)"], search.Queries.Select(query => query.Text ?? "(newest)"));
        Assert.All(search.Queries, query => Assert.Equal([".pdf"], query.Extensions));

        // Only the name with just "Anna's" was put to the model; Savannah's never, as "anna" is only inside a word of it.
        var asked = Assert.Single(reviewer!.Asked);
        Assert.Equal("find the annas archive pdf", asked.Request);
        Assert.Equal([OnlyA], asked.Candidates);
    }

    [Fact]
    public async Task WhatTheModelLeavesOutIsLeftOut()
    {
        var (service, _, _) = Create(choose: (_, _) => []);

        var result = await service.FindAsync("find the annas archive pdf");

        Assert.Equal([Archiv, Ar], result.Items);
        Assert.True(result.Reviewed);
    }

    [Fact]
    public async Task WithoutTheModelsJudgementSomeWordsAreShownOnlyWhenNoNameHoldsEvery()
    {
        var (withFull, _, _) = Create(choose: (_, _) => null);
        Assert.Equal([Archiv, Ar], (await withFull.FindAsync("find the annas archive pdf")).Items);

        // "annas book": no name holds "book", so the names with "annas" are the closest there are.
        var (partialOnly, _, _) = Create(Plan(new FileSearchQuery("annas book") { Extensions = [".pdf"] }, "annas", "book"), withReviewer: false);
        var result = await partialOnly.FindAsync("find the annas book");
        Assert.Equal([Archiv, Ar, OnlyA], result.Items);
        Assert.True(result.Reviewed);
    }

    [Fact]
    public async Task WhenTheExactSearchFindsSomethingThereIsNoSecondLook()
    {
        var search = new FakeFileSearch { Items = [Archiv] };
        var reviewer = new FakeReviewer();
        var service = new FileRequestService(
            new FakePlanner(plan: _ => Plan()), search, new FakePermissions(), NullLogger<FileRequestService>.Instance, reviewer);

        var result = await service.FindAsync("find the annas archive pdf");

        Assert.Equal([Archiv], result.Items);
        Assert.False(result.Reviewed);
        Assert.Single(search.Queries);
        Assert.Empty(reviewer.Asked);
    }

    [Fact]
    public async Task APlanWithNoWordsHasNoSecondLook()
    {
        var byKind = new PlannedFileSearch(new FileSearchQuery { Extensions = [".pdf"] }, FileSearchPlanSource.Read);
        var search = new FakeFileSearch();
        var reviewer = new FakeReviewer();
        var service = new FileRequestService(
            new FakePlanner(plan: _ => byKind), search, new FakePermissions(), NullLogger<FileRequestService>.Instance, reviewer);

        var result = await service.FindAsync("the pdfs from yesterday");

        Assert.Equal(FileRequestStatus.NothingFound, result.Status);
        Assert.Single(search.Queries);
        Assert.Empty(reviewer.Asked);
    }

    [Fact]
    public async Task IfNothingHoldsAnyWordTheModelIsNotAsked_AndNothingIsFound()
    {
        var (service, _, reviewer) = Create(Plan(new FileSearchQuery("qqqs zzzz") { Extensions = [".pdf"] }, "qqqs", "zzzz"));

        var result = await service.FindAsync("find the qqqs zzzz pdf");

        Assert.Equal(FileRequestStatus.NothingFound, result.Status);
        Assert.False(result.Reviewed);
        Assert.Empty(reviewer!.Asked);
    }

    [Fact]
    public async Task AFileTheModelNamesThatWasNotACandidateIsNeverReturned()
    {
        var stranger = Pdf("Something else entirely.pdf");
        var (service, _, _) = Create(choose: (_, _) => [stranger]);

        var result = await service.FindAsync("find the annas archive pdf");

        Assert.DoesNotContain(stranger, result.Items);
    }

    [Fact]
    public async Task NoMoreThanThePlansLimitIsReturned_AndTheModelIsNotAskedWhenTheLimitIsMet()
    {
        var (service, _, reviewer) = Create(Plan(AnnasArchive with { MaxResultsPerType = 1 }));

        var result = await service.FindAsync("find the annas archive pdf");

        Assert.Equal([Archiv], result.Items);
        Assert.Empty(reviewer!.Asked);
    }

    [Fact]
    public async Task ASearchThatFailsForOneWordLeavesTheOthers()
    {
        var (service, search, _) = Create();
        search.FailFor = query => query.Text == "arch" ? new FileSearchException(FileSearchFailure.TimedOut) : null;

        var result = await service.FindAsync("find the annas archive pdf");

        Assert.Equal(FileRequestStatus.Found, result.Status);
        Assert.Contains(Archiv, result.Items);
    }

    [Fact]
    public async Task WhenTheFilesPermissionIsOffNothingIsSearchedOrShownToTheModel()
    {
        var search = new FakeFileSearch { Answer = Answer };
        var reviewer = new FakeReviewer();
        var service = new FileRequestService(
            new FakePlanner(plan: _ => Plan()), search, new FakePermissions(filesAllowed: false), NullLogger<FileRequestService>.Instance, reviewer);

        var result = await service.FindAsync("find the annas archive pdf");

        Assert.Equal(FileRequestStatus.FilesTurnedOff, result.Status);
        Assert.Empty(search.Queries);
        Assert.Empty(reviewer.Asked);
    }

    [Fact]
    public async Task CancellingStopsTheSecondLookToo()
    {
        var (service, _, _) = Create();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FindAsync("find the annas archive pdf", cancelled.Token));
    }

    [Fact]
    public async Task NothingOfTheRequestOrTheNamesIsLogged()
    {
        var logger = new CapturingLogger();
        var secret = Pdf("SECRETNAME secretword.pdf");
        var search = new FakeFileSearch { Answer = query => query.Text == "secretword" ? [secret] : [] };
        var service = new FileRequestService(
            new FakePlanner(plan: _ => Plan(new FileSearchQuery("secretword other"), "secretword", "other")),
            search,
            new FakePermissions(),
            new TypedLogger<FileRequestService>(logger),
            new FakeReviewer());

        await service.FindAsync("find SECRETREQUEST secretword");

        Assert.Contains("5513", logger.AllText);
        foreach (var hidden in new[] { "SECRETREQUEST", "SECRETNAME", "secretword" })
        {
            Assert.DoesNotContain(hidden, logger.AllText, StringComparison.OrdinalIgnoreCase);
        }
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
