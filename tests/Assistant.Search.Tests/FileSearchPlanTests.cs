using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Search.Index;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>
/// A structured query read into what the index is asked: what is cleaned, what is normalized and, above all, that a criterion
/// that is set and cannot be met never widens the search.
/// </summary>
public sealed class FileSearchPlanTests
{
    private static FileSearchPlan? Plan(FileSearchQuery query, int max = 50) => FileSearchPlan.Create(query, max);

    private static FileSearchPlan Planned(FileSearchQuery query) =>
        Plan(query) ?? throw new InvalidOperationException("The query has no plan.");

    // ---- What makes a query ------------------------------------------------------------------------------------------

    [Fact]
    public void AQueryWithNoCriterionHasNoPlanRatherThanAskingForTheWholeIndex()
    {
        Assert.Null(Plan(new FileSearchQuery()));
        Assert.Null(Plan(new FileSearchQuery { Order = FileSearchOrder.ModifiedDescending, MaxResultsPerType = 5 }));
        Assert.Null(Plan(new FileSearchQuery { Types = [SearchResultItemType.File] }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t ")]
    public void ABlankStringIsNoCriterionAtAll(string? blank)
    {
        Assert.Null(Plan(new FileSearchQuery(blank) { Filename = blank, ContentTerm = blank, Folder = blank }));

        // ...and beside another criterion it is simply absent.
        var plan = Planned(new FileSearchQuery(blank) { Filename = blank, ContentTerm = blank, Folder = blank, Extensions = [".pdf"] });
        Assert.Empty(plan.Terms);
        Assert.Empty(plan.FilenameTerms);
        Assert.Null(plan.ContentPhrase);
        Assert.Null(plan.Scope);
    }

    [Theory]
    [InlineData("? - !")]
    [InlineData("\0\a")]
    [InlineData("\"\"")]
    public void WordsThatHaveNoLetterOrDigitCannotBeMetSoTheWholeQueryFindsNothing(string unusable)
    {
        Assert.Null(Plan(new FileSearchQuery(unusable) { Extensions = [".pdf"] }));
        Assert.Null(Plan(new FileSearchQuery { Filename = unusable, Extensions = [".pdf"] }));
        Assert.Null(Plan(new FileSearchQuery { ContentTerm = unusable, Extensions = [".pdf"] }));
    }

    [Fact]
    public void ATextIsReadIntoWordsAndPhrasesWithoutControlCharacters()
    {
        var plan = Planned(new FileSearchQuery("bud\0get \"trip\u0007 plan\" ?"));

        Assert.Equal(["budget", "trip plan"], plan.Terms.Select(term => term.Text));
        Assert.Equal([false, true], plan.Terms.Select(term => term.IsPhrase));
    }

    [Fact]
    public void MatchContentsMeansNothingWithoutWords()
    {
        Assert.False(Planned(new FileSearchQuery { MatchContents = true, Extensions = [".pdf"] }).MatchContents);
        Assert.True(Planned(new FileSearchQuery("budget") { MatchContents = true }).MatchContents);
    }

    // ---- Words, phrases, extensions ----------------------------------------------------------------------------------

    [Fact]
    public void AFilenameIsReadLikeTheTextAndMustHoldItsWords()
    {
        var plan = Planned(new FileSearchQuery { Filename = "screenshot \"2026 09\"" });

        Assert.Equal(["screenshot", "2026 09"], plan.FilenameTerms.Select(term => term.Text));
        Assert.Empty(plan.Terms);
        Assert.Equal(plan.FilenameTerms, plan.RelevanceTerms);
    }

    [Fact]
    public void AContentTermIsOnePhraseInOneLineWithoutTheQuoteAndTheStar()
    {
        Assert.Equal("quarterly budget review", Planned(new FileSearchQuery { ContentTerm = " \"quarterly\t budget*\"\r\nreview " }).ContentPhrase);
        Assert.Equal("ab c", Planned(new FileSearchQuery { ContentTerm = "a\"b c" }).ContentPhrase);
        Assert.Equal("C:\\Users", Planned(new FileSearchQuery { ContentTerm = "C:\\Users" }).ContentPhrase);
    }

    [Fact]
    public void AContentTermIsCutToItsLimitWithoutSplittingACharacter()
    {
        var text = new string('a', FileSearchQuery.MaxContentTermLength - 1) + "😀😀";

        var phrase = Planned(new FileSearchQuery { ContentTerm = text }).ContentPhrase!;

        Assert.Equal(FileSearchQuery.MaxContentTermLength - 1, phrase.Length);
        Assert.Equal(new string('a', FileSearchQuery.MaxContentTermLength - 1), phrase);
        Assert.Equal(FileSearchQuery.MaxContentTermLength, Planned(new FileSearchQuery { ContentTerm = new string('b', 1000) }).ContentPhrase!.Length);
    }

    [Theory]
    [InlineData(".pdf", ".pdf")]
    [InlineData("pdf", ".pdf")]
    [InlineData("PDF", ".pdf")]
    [InlineData("*.Pdf", ".pdf")]
    [InlineData("  .DocX  ", ".docx")]
    [InlineData("c++", ".c++")]
    [InlineData("7z", ".7z")]
    public void AnExtensionIsAlwaysLowerCaseWithItsDot(string given, string normalized)
    {
        Assert.Equal(normalized, FileSearchPlan.NormalizeExtension(given));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("*")]
    [InlineData("*.*")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a.b")]
    [InlineData("tar.gz")]
    [InlineData("a*b")]
    [InlineData("a?")]
    [InlineData("a'b")]
    [InlineData("a\"b")]
    [InlineData("a\0")]
    [InlineData("a;b")]
    [InlineData("averyveryveryverylongextension")]
    public void AnythingThatCannotBeAnExtensionIsNoExtension(string? given)
    {
        Assert.Null(FileSearchPlan.NormalizeExtension(given));
    }

    [Fact]
    public void ExtensionsAreDeduplicatedInOrderAndLimited()
    {
        var plan = Planned(new FileSearchQuery { Extensions = ["pdf", ".PDF", "docx", "not ok", "*.txt"] });
        Assert.Equal([".pdf", ".docx", ".txt"], plan.Extensions);

        var many = Enumerable.Range(0, 60).Select(number => "e" + number).ToArray();
        Assert.Equal(FileSearchQuery.MaxExtensions, Planned(new FileSearchQuery { Extensions = many }).Extensions.Count);
    }

    [Fact]
    public void AListOfExtensionsThatAreAllUnusableFindsNothing()
    {
        Assert.Null(Plan(new FileSearchQuery("budget") { Extensions = ["a b", "*", ""] }));
    }

    [Fact]
    public void AKindThatIsNotAKindFindsNothing()
    {
        Assert.Null(Plan(new FileSearchQuery("budget") { Kind = (FileKind)99 }));
        Assert.Equal(FileKind.Picture, Planned(new FileSearchQuery { Kind = FileKind.Picture }).Kind);
    }

    // ---- Dates and sizes ---------------------------------------------------------------------------------------------

    [Fact]
    public void DatesAreUtcToTheWholeSecond()
    {
        var plan = Planned(new FileSearchQuery
        {
            Modified = new DateRange(
                new DateTimeOffset(2026, 1, 15, 12, 30, 30, 999, TimeSpan.FromHours(2)),
                new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.FromHours(-5))),
        });

        Assert.Equal(new DateTime(2026, 1, 15, 10, 30, 30, DateTimeKind.Utc), plan.Modified.From);
        Assert.Equal(new DateTime(2026, 2, 1, 5, 0, 0, DateTimeKind.Utc), plan.Modified.To);
        Assert.True(plan.Created.IsUnbounded);
    }

    [Fact]
    public void ARangeThatEndsAtOrBeforeItsStartFindsNothing()
    {
        var at = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(Plan(new FileSearchQuery("budget") { Modified = new DateRange(at, at) }));
        Assert.Null(Plan(new FileSearchQuery("budget") { Created = new DateRange(at, at.AddDays(-1)) }));

        // The index knows no fractions of a second, so a range inside one second holds nothing either.
        Assert.Null(Plan(new FileSearchQuery("budget") { Modified = new DateRange(at.AddMilliseconds(100), at.AddMilliseconds(900)) }));
        Assert.NotNull(Plan(new FileSearchQuery("budget") { Modified = new DateRange(at, at.AddSeconds(1)) }));
    }

    [Fact]
    public void AnUnboundedRangeIsNotACriterion()
    {
        Assert.Null(Plan(new FileSearchQuery { Modified = new DateRange(), Created = default, Size = default }));
        Assert.True(new DateRange().IsUnbounded);
        Assert.True(new SizeRange().IsUnbounded);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(-5L, null, true)]
    [InlineData(0L, null, true)]
    [InlineData(1L, null, true)]
    [InlineData(null, 0L, true)]
    [InlineData(10L, 10L, true)]
    [InlineData(10L, 20L, true)]
    [InlineData(20L, 10L, false)]
    [InlineData(null, -1L, false)]
    [InlineData(5L, -1L, false)]
    public void ASizeRangeThatCannotHoldAFileFindsNothing(long? min, long? max, bool holdsFiles)
    {
        // An extension is given as well, so that the size is the only thing that can make the query unmeetable.
        var plan = Plan(new FileSearchQuery { Size = new SizeRange(min, max), Extensions = ["txt"] });

        Assert.Equal(holdsFiles, plan is not null);
    }

    [Theory]
    [InlineData(-5L, null)]
    [InlineData(0L, null)]
    [InlineData(1L, 1L)]
    [InlineData(1024L, 1024L)]
    public void AMinimumOfNothingOrLessIsNoMinimum(long min, long? expected)
    {
        var plan = Planned(new FileSearchQuery { Size = new SizeRange(min, 5000), Extensions = ["txt"] });

        Assert.Equal(expected, plan.MinBytes);
        Assert.Equal(5000, plan.MaxBytes);
    }

    // ---- Folders -----------------------------------------------------------------------------------------------------

    [Fact]
    public void AFolderIsAFullPathWithoutItsTrailingSeparatorAndItsUrlUsesForwardSlashes()
    {
        var scope = Planned(new FileSearchQuery { Folder = @"  C:\Users\Ada\My Docs\.\Old\..\ " }).Scope!;

        Assert.Equal(@"C:\Users\Ada\My Docs", scope.Path);
        Assert.Equal("file:C:/Users/Ada/My Docs", scope.Url);
        Assert.True(scope.IncludeSubfolders);
        Assert.False(Planned(new FileSearchQuery { Folder = @"C:\Docs", IncludeSubfolders = false }).Scope!.IncludeSubfolders);
    }

    [Fact]
    public void ADrivesRootKeepsItsSeparator()
    {
        Assert.Equal(@"C:\", Planned(new FileSearchQuery { Folder = @"C:\" }).Scope!.Path);
        Assert.Equal("file:C:/", Planned(new FileSearchQuery { Folder = @"C:\" }).Scope!.Url);
    }

    [Fact]
    public void ANetworkFolderIsAFileUrlWithTwoSlashes()
    {
        Assert.Equal("file://server/share/docs", Planned(new FileSearchQuery { Folder = @"\\server\share\docs\" }).Scope!.Url);
    }

    [Theory]
    [InlineData(@"Documents")]
    [InlineData(@"..\Documents")]
    [InlineData(@"\Users\Ada")]
    [InlineData(@"C:Users")]
    [InlineData("C:\\Docs\0")]
    [InlineData("C:\\Do\ncs")]
    public void AFolderThatIsNotAFullPathFindsNothingForItCouldMeanAnyFolder(string folder)
    {
        Assert.Null(Plan(new FileSearchQuery { Folder = folder, Extensions = [".txt"] }));
    }

    // ---- Kinds of item, limit, order --------------------------------------------------------------------------------

    [Fact]
    public void AppsAreDroppedAndEachKindIsAskedForOnceInTheOrderGiven()
    {
        var plan = Planned(new FileSearchQuery("budget")
        {
            Types = [SearchResultItemType.Folder, SearchResultItemType.App, SearchResultItemType.File, SearchResultItemType.Folder],
        });

        Assert.Equal([SearchResultItemType.Folder, SearchResultItemType.File], plan.Types);
        Assert.Null(Plan(new FileSearchQuery("calc") { Types = [SearchResultItemType.App] }));
        Assert.Null(Plan(new FileSearchQuery("calc") { Types = [] }));
    }

    [Fact]
    public void ACriterionOnlyAFileCanMeetLeavesOutFoldersEvenWhenTheyAreAskedFor()
    {
        FileSearchQuery[] fileOnly =
        [
            new() { Extensions = [".png"] },
            new() { Kind = FileKind.Picture },
            new() { Size = new SizeRange(MinBytes: 1) },
            new() { Size = new SizeRange(MaxBytes: 0) },
            new() { ContentTerm = "invoice" },
        ];

        foreach (var query in fileOnly)
        {
            Assert.Equal([SearchResultItemType.File], Planned(query).Types);
            Assert.Null(Plan(query with { Types = [SearchResultItemType.Folder] }));
        }

        // Words, dates and a folder can be met by a folder.
        Assert.Contains(SearchResultItemType.Folder, Planned(new FileSearchQuery("budget") { Filename = "x", Folder = @"C:\Docs" }).Types);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(1, 1)]
    [InlineData(50, 50)]
    [InlineData(51, 50)]
    [InlineData(int.MaxValue, 50)]
    public void TheLimitIsWhatWasAskedButNeverMoreThanTheMaximum(int asked, int limit)
    {
        Assert.Equal(limit, Planned(new FileSearchQuery("budget") { MaxResultsPerType = asked }).Limit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NoRoomForResultsHasNoPlan(int asked)
    {
        Assert.Null(Plan(new FileSearchQuery("budget") { MaxResultsPerType = asked }));
        Assert.Null(Plan(new FileSearchQuery("budget"), max: 0));
    }

    [Fact]
    public void AnOrderThatIsNotAnOrderIsRelevance()
    {
        Assert.Equal(FileSearchOrder.Relevance, Planned(new FileSearchQuery("budget") { Order = (FileSearchOrder)42 }).Order);
        Assert.Equal(FileSearchOrder.SizeAscending, Planned(new FileSearchQuery("budget") { Order = FileSearchOrder.SizeAscending }).Order);
    }

    // ---- Content criterion -------------------------------------------------------------------------------------------

    [Fact]
    public void AQueryAsksForContentsWhenItHasAContentTermOrMatchesContentsOfWords()
    {
        Assert.True(FileSearchPlan.AsksForContents(new FileSearchQuery { ContentTerm = "x" }));
        Assert.True(FileSearchPlan.AsksForContents(new FileSearchQuery("x") { MatchContents = true }));
        Assert.False(FileSearchPlan.AsksForContents(new FileSearchQuery("x")));
        Assert.False(FileSearchPlan.AsksForContents(new FileSearchQuery { MatchContents = true }));
        Assert.False(FileSearchPlan.AsksForContents(new FileSearchQuery { ContentTerm = "  " }));
        Assert.True(Planned(new FileSearchQuery { ContentTerm = "x" }).ContentRequested);
        Assert.False(Planned(new FileSearchQuery("x")).ContentRequested);
    }
}
