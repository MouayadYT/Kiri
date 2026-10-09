using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Search.Planning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>
/// The natural-language search planner (PROJECT_SPEC §4.7): a request becomes a structured query by fixed rules that forgive
/// typing mistakes, so its days, kinds and order are always right. The day is Friday 2 October 2026 (see <see cref="PlannerDay"/>).
/// </summary>
public sealed class FileSearchPlannerTests
{
    private static FileSearchPlanner CreatePlanner(CapturingLogger? logger = null) =>
        new(PlannerDay.Clock, (Microsoft.Extensions.Logging.ILogger?)logger ?? NullLogger.Instance, new FakePlanFolders(), DayOfWeek.Monday);

    private static PlannedFileSearch Plan(string request) => CreatePlanner().Plan(request);

    private static DateRange Days(int month, int day, int toMonth, int toDay) =>
        new(PlannerDay.Midnight(2026, month, day), PlannerDay.Midnight(2026, toMonth, toDay).AddDays(1));

    private static DateRange Day(int month, int day) => Days(month, day, month, day);

    // -- Requests of a known shape are read by their template. --

    [Fact]
    public async Task TheLastFiveScreenshotsAreImageFilesNamedScreenshot_NewestModifiedFirst_LimitedToFive()
    {
        var plan = await CreatePlanner().PlanAsync("show me the last 5 screenshots I took");

        Assert.Equal(FileSearchPlanSource.Template, plan.Source);
        var query = plan.Query!;
        Assert.Equal("Screenshot", query.Filename);
        Assert.Null(query.Text);
        Assert.Equal(ImageFileTypes.Extensions, query.Extensions);
        Assert.Equal([SearchResultItemType.File], query.Types);
        Assert.Equal(FileSearchOrder.ModifiedDescending, query.Order);
        Assert.Equal(5, query.MaxResultsPerType);
        Assert.True(query.Modified.IsUnbounded);
        Assert.True(query.Created.IsUnbounded);
        Assert.True(plan.AsksForImages);
        Assert.Equal(("screenshot", "screenshots"), plan.KindName);
    }

    [Theory]
    [InlineData("Show me the last 5 screenshots I took", 5)]
    [InlineData("show me the last five screenshots i took?", 5)]
    [InlineData("can you show me my last 12 screenshots please", 12)]
    [InlineData("last 3 screenshots", 3)]
    [InlineData("the 7 newest screenshots", 7)]
    [InlineData("find the most recent screenshot", 1)]
    [InlineData("show me the last screenshot I took", 1)]
    [InlineData("show me my screenshots", RecentMediaTemplate.DefaultCount)]
    [InlineData("show me the last 100 screenshots", WindowsFileSearchService.MaxResultsPerType)]
    [InlineData("show me my last 5 screen shots", 5)]
    public void ScreenshotRequestsOfTheKnownShapeAreReadByTheirTemplate(string request, int limit)
    {
        var plan = CreatePlanner().PlanKnownShape(request);

        Assert.NotNull(plan);
        Assert.Equal("Screenshot", plan.Query!.Filename);
        Assert.Equal(limit, plan.Query.MaxResultsPerType);
        Assert.Equal(FileSearchOrder.ModifiedDescending, plan.Query.Order);
        Assert.Equal(ImageFileTypes.Extensions, plan.Query.Extensions);
    }

    [Fact]
    public void APictureRequestWithADayHasThatDayAsItsRange()
    {
        var planner = CreatePlanner();

        var yesterday = planner.PlanKnownShape("Find the image I took yesterday")!.Query!;
        Assert.Null(yesterday.Filename);
        Assert.Equal(ImageFileTypes.Extensions, yesterday.Extensions);
        Assert.Equal(Day(10, 1), yesterday.Modified);
        Assert.Equal(RecentMediaTemplate.DefaultCount, yesterday.MaxResultsPerType);

        Assert.Equal(Days(9, 21, 9, 27), planner.PlanKnownShape("show me my photos from last week")!.Query!.Modified);
        Assert.Equal(Days(9, 28, 10, 2), planner.PlanKnownShape("pictures from this week")!.Query!.Modified);
        Assert.Equal(Days(9, 30, 10, 2), planner.PlanKnownShape("show me 4 pictures from the past 3 days")!.Query!.Modified);
        Assert.Equal(Day(10, 2), planner.PlanKnownShape("screenshots I took today")!.Query!.Modified);
        Assert.Equal(Days(9, 1, 9, 30), planner.PlanKnownShape("images from last month")!.Query!.Modified);
    }

    [Fact]
    public void PicturesYouCreatedOrDownloadedAreOrderedAndRangedByCreationTime()
    {
        var query = CreatePlanner().PlanKnownShape("show me the pictures I downloaded yesterday")!.Query!;

        Assert.Equal(FileSearchOrder.CreatedDescending, query.Order);
        Assert.Equal(Day(10, 1), query.Created);
        Assert.True(query.Modified.IsUnbounded);
    }

    [Theory]
    [InlineData("show me the last 0 screenshots")]
    [InlineData("screenshots of the error dialog")]
    [InlineData("show me photos of the beach")]
    [InlineData("find the PDF about biology I edited last Tuesday")]
    [InlineData("how many screenshots do I have")]
    [InlineData("")]
    [InlineData("what is 9+10")]
    public void AnyOtherRequestHasNoKnownShape(string request)
    {
        Assert.Null(CreatePlanner().PlanKnownShape(request));
    }

    // -- Any other request is read word by word. --

    [Fact]
    public void ThePdfAboutBiologyEditedLastTuesdayIsAPdfWithTheWordInItsNameOrText_ChangedOnTuesday29September()
    {
        var plan = Plan("find the PDF about biology I edited last Tuesday.");

        Assert.Equal(FileSearchPlanSource.Read, plan.Source);
        var query = plan.Query!;
        Assert.Equal("biology", query.Text);
        Assert.True(query.MatchContents);
        Assert.Equal([".pdf"], query.Extensions);
        Assert.Equal(Day(9, 29), query.Modified);
        Assert.True(query.Created.IsUnbounded);
        Assert.Equal([SearchResultItemType.File], query.Types);
        Assert.Equal(["biology"], plan.Keywords);
        Assert.Equal(("PDF", "PDFs"), plan.KindName);
    }

    [Fact]
    public void TheLastDocIMadeIsTheNewestMadeDocument_AndNoDayIsMadeUp()
    {
        var query = Plan("find the last doc i made").Query!;

        Assert.Null(query.Text);
        Assert.Equal([".docx", ".doc", ".pdf", ".odt", ".rtf"], query.Extensions);
        Assert.Equal(FileSearchOrder.CreatedDescending, query.Order);
        Assert.Equal(1, query.MaxResultsPerType);
        Assert.True(query.Modified.IsUnbounded);
        Assert.True(query.Created.IsUnbounded);
    }

    [Theory]
    [InlineData("find the pdf on myu computer")]
    [InlineData("find the pdf on my computer")]
    [InlineData("show me pdfs anywhere on my pc")]
    [InlineData("find pdfs on the laptop")]
    public void OnMyComputerIsWhereASearchLooksAnyway_SoItIsNoPartOfTheName(string request)
    {
        var query = Plan(request).Query!;

        Assert.Null(query.Text);
        Assert.Equal([".pdf"], query.Extensions);
        Assert.Equal(FileSearchOrder.ModifiedDescending, query.Order);
        Assert.Equal(RequestReader.DefaultLimit, query.MaxResultsPerType);
    }

    [Theory]
    [InlineData("find the annas archive pdf", "annas archive")]
    [InlineData("fidn the annas aarhcive pdf", "annas aarhcive")]
    [InlineData("find annas arhciv pdff", "annas arhciv")]
    [InlineData("can you serach for the annas archive pdfs", "annas archive")]
    [InlineData("where is my Anna's Archive PDF?", "annas archive")]
    public void MistypedVerbsAndKindsAreReadAsMeant_AndTheRestIsTheName(string request, string words)
    {
        var plan = Plan(request);

        Assert.Equal(words, plan.Query!.Text);
        Assert.Equal([".pdf"], plan.Query.Extensions);
        Assert.Equal(FileSearchOrder.Relevance, plan.Query.Order);
        Assert.Equal(words.Split(' '), plan.Keywords);
    }

    [Fact]
    public void SpreadsheetsInMyDownloadsBiggerThanTenMegabytes()
    {
        var query = Plan("spreadsheets in my downloads bigger than 10 MB").Query!;

        Assert.Null(query.Text);
        Assert.Equal([".xlsx", ".xls", ".csv", ".ods"], query.Extensions);
        Assert.Equal(@"C:\Users\Test\Downloads", query.Folder);
        Assert.Equal(new SizeRange(10 * 1024 * 1024, null), query.Size);
        Assert.Equal(FileSearchOrder.ModifiedDescending, query.Order);
    }

    [Fact]
    public void DocumentsFromLastMonthThatMentionAPhraseLookInsideTheFiles()
    {
        var query = Plan("documents from last month that mention quarterly revenue").Query!;

        Assert.Equal("quarterly revenue", query.ContentTerm);
        Assert.Null(query.Text);
        Assert.Equal(Days(9, 1, 9, 30), query.Modified);
        Assert.Contains(".docx", query.Extensions);

        Assert.Equal("the Q3 plan", Plan("find the pdf containing \"the Q3 plan\"").Query!.ContentTerm);
    }

    [Fact]
    public void FoldersAreAskedForByName()
    {
        var query = Plan("the folder called Taxes").Query!;

        Assert.Equal("taxes", query.Text);
        Assert.Equal([SearchResultItemType.Folder], query.Types);
    }

    [Fact]
    public void ACountAndAnOrderAndAPlace()
    {
        var query = Plan("my 3 newest videos on the desktop").Query!;

        Assert.Contains(".mp4", query.Extensions);
        Assert.Equal(@"C:\Users\Test\Desktop", query.Folder);
        Assert.Equal(FileSearchOrder.ModifiedDescending, query.Order);
        Assert.Equal(3, query.MaxResultsPerType);

        Assert.Equal(FileSearchOrder.SizeDescending, Plan("find my biggest videos").Query!.Order);
        Assert.Equal(FileSearchOrder.ModifiedAscending, Plan("find my oldest spreadsheets").Query!.Order);
        Assert.Equal(FileSearchOrder.CreatedAscending, Plan("the oldest pdf I downloaded").Query!.Order);
    }

    [Theory]
    [InlineData("what files did I edit yesterday", 10, 1, 10, 1)]
    [InlineData("find the report from last week", 9, 21, 9, 27)]
    [InlineData("find last week's slides", 9, 21, 9, 27)]
    [InlineData("the pdf I opened on tuseday", 9, 29, 9, 29)]
    [InlineData("the notes from this monday", 9, 28, 9, 28)]
    [InlineData("the essay from september", 9, 1, 9, 30)]
    [InlineData("the essay from september 2026", 9, 1, 9, 30)]
    [InlineData("files from the past 2 weeks", 9, 19, 10, 2)]
    [InlineData("the budget I changed two weeks ago", 9, 14, 9, 20)]
    public void DaysAreWorkedOutInCode(string request, int month, int day, int toMonth, int toDay)
    {
        var query = Plan(request).Query!;

        Assert.Equal(Days(month, day, toMonth, toDay), query.Modified);
    }

    [Fact]
    public void DaysOfMakingAreRangedByCreationTime()
    {
        var query = Plan("find the report I downloaded 3 days ago").Query!;

        Assert.Equal(Day(9, 29), query.Created);
        Assert.True(query.Modified.IsUnbounded);
        Assert.Equal("report", query.Text);
    }

    [Theory]
    [InlineData("find the new england pdf", "new england")]
    [InlineData("find the photos of the sun", "sun")]
    [InlineData("find the may report", "may report")]
    [InlineData("find my budget 2026 spreadsheet", "budget 2026")]
    [InlineData("find the shoe catalog pdf", "shoe catalog")]
    [InlineData("find the zip code list", "zip code list")]
    public void WordsThatOnlyLookLikeDaysOrOrdersStayInTheName(string request, string words)
    {
        var query = Plan(request).Query!;

        Assert.Equal(words, query.Text);
        Assert.True(query.Modified.IsUnbounded);
        Assert.True(query.Created.IsUnbounded);
    }

    [Fact]
    public void AQuotedNameIsOnePhrase()
    {
        var plan = Plan("find \"annual report\" from last year");

        Assert.Equal("\"annual report\"", plan.Query!.Text);
        Assert.Equal(["annual report"], plan.Keywords);
        Assert.Equal(new DateRange(PlannerDay.Midnight(2025, 1, 1), PlannerDay.Midnight(2026, 1, 1)), plan.Query.Modified);
    }

    [Fact]
    public void TheWordDocumentAboutTaxesIsAWordFileWithTheTopicInItsNameOrText()
    {
        var query = Plan("find the word document about taxes").Query!;

        Assert.Equal([".docx", ".doc"], query.Extensions);
        Assert.Equal("taxes", query.Text);
        Assert.True(query.MatchContents);
    }

    [Fact]
    public void OnlyAnOrderLooksAtTheLastThirtyDays()
    {
        var query = Plan("show me my latest files").Query!;

        Assert.Equal(FileSearchOrder.ModifiedDescending, query.Order);
        Assert.Equal(Days(9, 3, 10, 2), query.Modified);
    }

    [Theory]
    [InlineData("find it")]
    [InlineData("find")]
    [InlineData("show me")]
    [InlineData("   ")]
    public void ARequestWithNothingToLookForHasNoQuery(string request)
    {
        var plan = Plan(request);

        Assert.Null(plan.Query);
        Assert.False(plan.HasQuery);
    }

    [Fact]
    public void TheRequestIsNeverLogged()
    {
        var logger = new CapturingLogger();

        CreatePlanner(logger).Plan("find SECRETWORD notes from yesterday");
        CreatePlanner(logger).Plan("show me the last 5 screenshots I took");

        Assert.Contains("5510", logger.AllText);
        Assert.DoesNotContain("SECRETWORD", logger.AllText, StringComparison.OrdinalIgnoreCase);
    }
}
