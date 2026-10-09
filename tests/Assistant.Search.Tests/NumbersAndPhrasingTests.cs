using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Search.Planning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>
/// Finding a file the way a person says it (PROJECT_SPEC §4.7): a number in a name is a word of the name (and "3" is "three"), a
/// kind said loosely ("doc") does not hide a file of another kind that holds every word, and a request that asks to find anywhere in
/// what was said is read as one.
/// </summary>
public sealed class NumbersAndPhrasingTests
{
    private static FileSearchPlanner CreatePlanner() =>
        new(PlannerDay.Clock, NullLogger.Instance, new FakePlanFolders(), DayOfWeek.Monday);

    private static PlannedFileSearch Plan(string request) => CreatePlanner().Plan(request);

    // -- The forms of a number. --

    [Theory]
    [InlineData("3", 3)]
    [InlineData("three", 3)]
    [InlineData("3rd", 3)]
    [InlineData("third", 3)]
    [InlineData("12", 12)]
    [InlineData("twelfth", 12)]
    [InlineData("11th", 11)]
    [InlineData("twenty", 20)]
    public void ANumberIsTheSameInEveryFormItIsWritten(string word, int value)
    {
        Assert.True(NumberForms.TryValue(word, out var read));
        Assert.Equal(value, read);
    }

    [Theory]
    [InlineData("milestone")]
    [InlineData("")]
    [InlineData("332")]
    [InlineData("21")]
    [InlineData("threes")]
    public void AnythingElseIsNotANumber(string word) => Assert.False(NumberForms.TryValue(word, out _));

    [Fact]
    public void TheOtherFormsOfANumberAreTheDigitTheWordAndTheOrdinals()
    {
        Assert.Equal(["3", "third", "3rd"], NumberForms.OtherForms("three"));
        Assert.Equal(["three", "third", "3rd"], NumberForms.OtherForms("3"));
        Assert.Empty(NumberForms.OtherForms("milestone"));
        Assert.True(NumberForms.Same("3", "three"));
        Assert.True(NumberForms.Same("second", "2"));
        Assert.False(NumberForms.Same("3", "four"));
    }

    [Fact]
    public void ANameWithTheNumberWrittenAnotherWayStillHoldsTheWord()
    {
        Assert.True(NameMatch.Fit("Milestone Three Guidelines and Rubric.mhtml", ["milestone", "3"]).IsFull);
        Assert.True(NameMatch.Fit("Milestone 3 Draft.docx", ["milestone", "three"]).IsFull);
        Assert.True(NameMatch.Fit("Chapter 2 notes.txt", ["chapter", "second"]).IsFull);

        // Another number, or digits inside a longer one, are not it: "HIS-332" is not "3".
        Assert.False(NameMatch.Fit("HIS-332_Milestone_Four_Rough_Draft.docx", ["milestone", "3"]).IsFull);
        Assert.False(NameMatch.Fit("Milestone Four.docx", ["milestone", "three"]).IsFull);
    }

    [Fact]
    public void ANumberIsLookedForInItsOtherFormsToo()
    {
        var queries = CandidateSearch.Queries(new FileSearchQuery("milestone three"), ["milestone", "three"]);
        var texts = queries.Select(query => query.Text).ToArray();

        Assert.Contains("milestone", texts);
        Assert.Contains("three", texts);
        Assert.Contains("3", texts);
        Assert.DoesNotContain("third", texts);
    }

    // -- A number in a name is not a count. --

    [Theory]
    [InlineData("find milestone three doc", new[] { "milestone", "three" }, null)]
    [InlineData("find milestoen 3 doc", new[] { "milestoen", "3" }, null)]
    [InlineData("find chapter 5 summary pdf", new[] { "chapter", "5", "summary" }, null)]
    [InlineData("find 3 docs", new string[0], 3)]
    [InlineData("find me 3 pdfs", new string[0], 3)]
    [InlineData("show me the last 5 pdfs", new string[0], 5)]
    [InlineData("find 2 pdf", new string[0], 2)]
    [InlineData("top 3 pdfs", new string[0], 3)]
    public void ANumberAfterAWordOfTheNameIsPartOfTheName_AndOnlyANumberBesideTheKindOrTheOrderIsACount(string request, string[] keywords, int? limit)
    {
        var plan = Plan(request);

        Assert.Equal(keywords, plan.Keywords);
        Assert.Equal(limit ?? RequestReader.DefaultLimit, plan.Query!.MaxResultsPerType);
    }

    [Theory]
    [InlineData("find milestone 3", "milestone 3")]
    [InlineData("milestone 3", "milestone 3")]
    [InlineData("find my last 5", "")]
    [InlineData("find the top 3", "top")]
    [InlineData("find budget 2026 pdf", "budget 2026 pdf")]
    public void WhatIsTypedInTheBarKeepsANumberThatIsPartOfAName(string typed, string expected)
    {
        var query = KeywordQuery.Live(typed);

        Assert.Equal(expected.Length == 0 ? null : expected, query?.Text);
    }

    // -- Said the way people say it. --

    [Fact]
    public void WordsThatOnlyCorrectOrInsistAreNotWordsOfTheName()
    {
        var plan = Plan("i said milestone three doc. FIND IT");

        Assert.Equal(["milestone", "three"], plan.Keywords);
        Assert.NotEmpty(plan.Query!.Extensions);
    }

    [Theory]
    [InlineData("i said milestone three doc. FIND IT", true)]
    [InlineData("find milsotne 3", true)]
    [InlineData("fidn the milestone", true)]
    [InlineData("milestone 3 doc", true)]
    [InlineData("budget pdf", true)]
    [InlineData("can you locate my resume", true)]
    [InlineData("find out why the sky is blue", false)]
    [InlineData("find a way to learn french", false)]
    [InlineData("how do I find a pdf", false)]
    [InlineData("what is a pdf", false)]
    [InlineData("find it", false)]
    [InlineData("hello there", false)]
    [InlineData("doc", false)]
    [InlineData("my doc", false)]
    [InlineData("write a doc about trade", false)]
    [InlineData("files", false)]
    [InlineData("", false)]
    public void ARequestThatMayBeAboutFilesWithoutBeingClearlyOneIsRecognisedForASearch(string request, bool likely) =>
        Assert.Equal(likely, FileRequestClassifier.IsLikelyFileRequest(request));

    // -- Another kind that holds every word. --

    private static SearchResultItem Item(string name) =>
        new(SearchResultItemType.File, name, @"C:\Users\Test\Downloads\" + name) { Extension = Path.GetExtension(name) };

    private static readonly SearchResultItem Four = Item("HIS-332_Milestone_Four_Rough_Draft.docx");
    private static readonly SearchResultItem ThreeGuidelines = Item("Milestone Three Guidelines and Rubric.mhtml");
    private static readonly SearchResultItem FourGuidelines = Item("Milestone Four Guidelines and Rubric.mhtml");

    // The documents only have "milestone" in them; the other kinds are found by each word alone.
    private static IReadOnlyList<SearchResultItem> Answer(FileSearchQuery query)
    {
        var documentsOnly = query.Extensions.Count > 0 || query.Kind is not null;
        return query.Text switch
        {
            "milestone" => documentsOnly ? [Four] : [Four, FourGuidelines, ThreeGuidelines],
            "three" or "3" => documentsOnly ? [] : [ThreeGuidelines],
            _ => [],
        };
    }

    [Fact]
    public async Task WhenNoFileOfTheKindHoldsEveryWord_AFileOfAnotherKindThatDoesIsShownAndSaidToBeOfAnotherKind()
    {
        var plan = new PlannedFileSearch(
            new FileSearchQuery("milestone three") { Extensions = [".docx", ".doc"], MaxResultsPerType = 10 }, FileSearchPlanSource.Read)
        {
            Keywords = ["milestone", "three"],
        };
        var search = new FakeFileSearch { Answer = Answer };
        var reviewer = new FakeReviewer();
        var service = new FileRequestService(new FakePlanner(plan: _ => plan), search, new FakePermissions(), NullLogger<FileRequestService>.Instance, reviewer);

        var result = await service.FindAsync("find milestone three doc");

        Assert.Equal(FileRequestStatus.Found, result.Status);
        Assert.True(result.OtherKind);
        Assert.False(result.Reviewed);
        Assert.Equal([ThreeGuidelines], result.Items);

        // The Four draft, which holds only "milestone", did not need the model's judgement to lose to a file with both words.
        Assert.Empty(reviewer.Asked);
    }

    [Fact]
    public async Task ANumberTheIndexFindsOnlyInsideALongerNumberIsNotAMatch()
    {
        // "LIKE '%3%'" finds "HIS-332", which has no 3 of its own; the exact search's answer is checked, and the file that has it wins.
        var plan = new PlannedFileSearch(
            new FileSearchQuery("milestone 3") { Extensions = [".docx", ".doc"], MaxResultsPerType = 10 }, FileSearchPlanSource.Read)
        {
            Keywords = ["milestone", "3"],
        };
        var search = new FakeFileSearch { Answer = query => query.Text == "milestone 3" ? [Four] : Answer(query) };
        var service = new FileRequestService(new FakePlanner(plan: _ => plan), search, new FakePermissions(), NullLogger<FileRequestService>.Instance);

        var result = await service.FindAsync("find milestone 3 doc");

        Assert.Equal([ThreeGuidelines], result.Items);
        Assert.True(result.OtherKind);

        // A name that does hold the number as a word of its own stays.
        var threeDraft = Item("Milestone 3 Draft.docx");
        var kept = new FakeFileSearch { Answer = query => query.Text == "milestone 3" ? [Four, threeDraft] : Answer(query) };
        var again = new FileRequestService(new FakePlanner(plan: _ => plan), kept, new FakePermissions(), NullLogger<FileRequestService>.Instance);
        var result2 = await again.FindAsync("find milestone 3 doc");
        Assert.Equal([threeDraft], result2.Items);
        Assert.False(result2.Reviewed);
        Assert.False(result2.OtherKind);
    }

    [Fact]
    public async Task WhenAFileOfTheKindHoldsEveryWordItIsShownAsBefore_AndNothingOfAnotherKindIsLookedFor()
    {
        var threeDraft = Item("Milestone 3 Draft.docx");
        var plan = new PlannedFileSearch(
            new FileSearchQuery("milestone three") { Extensions = [".docx"], MaxResultsPerType = 10 }, FileSearchPlanSource.Read)
        {
            Keywords = ["milestone", "three"],
        };
        var search = new FakeFileSearch
        {
            Answer = query => query.Text switch
            {
                "milestone" => [Four, threeDraft],
                "3" => [threeDraft],
                _ => [],
            },
        };
        var service = new FileRequestService(new FakePlanner(plan: _ => plan), search, new FakePermissions(), NullLogger<FileRequestService>.Instance);

        var result = await service.FindAsync("find milestone three docx");

        Assert.True(result.Reviewed);
        Assert.False(result.OtherKind);
        Assert.Equal([threeDraft], result.Items);
        Assert.All(search.Queries.Skip(1), query => Assert.Equal([".docx"], query.Extensions));
    }

    [Fact]
    public async Task WithNoKindToLoosen_TheSecondLookIsAsItWas()
    {
        var plan = new PlannedFileSearch(new FileSearchQuery("milestone nine") { MaxResultsPerType = 10 }, FileSearchPlanSource.Read)
        {
            Keywords = ["milestone", "nine"],
        };
        var search = new FakeFileSearch { Answer = Answer };
        var service = new FileRequestService(new FakePlanner(plan: _ => plan), search, new FakePermissions(), NullLogger<FileRequestService>.Instance);

        var result = await service.FindAsync("find milestone nine");

        Assert.False(result.OtherKind);
        Assert.True(result.Reviewed);
        Assert.Equal([Four, FourGuidelines, ThreeGuidelines], result.Items);
    }
}
