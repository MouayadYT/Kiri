using Assistant.Core.Contracts;
using Assistant.Search.Planning;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>Reading mistyped words, and scoring a real file name against the words of a request.</summary>
public sealed class NameMatchTests
{
    // Names as a download site writes them: long, cut short at the end, with a curly apostrophe.
    private const string Archiv = "Changes in the Land_ Indians, Colonists, and the Ecology of -- William Cronon -- isbn13 9780809001583 -- e9ce34896a3c5f71b9699cde1b5b8e55 -- Anna’s Archiv.pdf";
    private const string Archi = "Colonial Ecology, Atlantic Economy -- isbn13 9780812251272 -- edd2e4bdea742acdc6bc2376d1191062 -- Anna’s Archi.pdf";
    private const string Ar = "Manitou and providence -- isbn13 9780195030259 -- 8c78ce6ae4a3618e8fc26c8c2403969b -- Anna’s Ar.pdf";
    private const string A = "Conquering the American Wilderness -- isbn13 9781558493667 -- 3e11cd90a0f12dc3e5c47524012b75d4 -- Anna’s A.pdf";
    private const string Savannah = "Northwind Presentaion Marcus & Savannah.pdf";

    [Theory]
    [InlineData("fidn", "find", true)]
    [InlineData("serach", "search", true)]
    [InlineData("pdff", "pdf", true)]
    [InlineData("pdfs", "pdf", true)]
    [InlineData("documnet", "document", true)]
    [InlineData("tuseday", "tuesday", true)]
    [InlineData("yesterdy", "yesterday", true)]
    [InlineData("dog", "doc", false)]
    [InlineData("rare", "rar", false)]
    [InlineData("pdfx", "pdf", false)]
    [InlineData("kind", "find", false)]
    [InlineData("sweet", "sheet", true)]
    public void AMistypedWordIsReadAsMeantWhenItIsLongEnoughToTell(string typed, string word, bool expected)
    {
        Assert.Equal(expected, Fuzzy.IsWord(typed, word));
    }

    [Theory]
    [InlineData("archive", "archiv", 1)]
    [InlineData("arhciv", "archiv", 1)]
    [InlineData("biolgy", "biology", 1)]
    [InlineData("abc", "abc", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("kitten", "sitting", 3)]
    public void TheDistanceCountsEditsAndSwaps(string a, string b, int expected)
    {
        Assert.Equal(expected, Fuzzy.Distance(a, b));
    }

    [Fact]
    public void TheDistanceStopsCountingPastItsLimit()
    {
        Assert.Equal(2, Fuzzy.Distance("abcdef", "uvwxyz", 1));
        Assert.Equal(1, Fuzzy.Distance("ab", "ba", 1));
    }

    [Fact]
    public void TheWordsOfANameLoseTheirApostrophesCaseAndExtension()
    {
        Assert.Equal(["annas", "archi"], NameMatch.Tokens("Anna’s Archi.pdf"));
        Assert.Equal(["annual", "report", "2026"], NameMatch.Tokens("AnnualReport_2026.xlsx"));
        Assert.Equal(["finance", "old", "folder"], NameMatch.Tokens("Finance.old folder"));
        Assert.Equal(["notes", "v2"], NameMatch.Tokens("notes v2"));
    }

    [Theory]
    [InlineData(Archiv)]
    [InlineData(Archi)]
    [InlineData(Ar)]
    public void ANameCutShortOrWithAnotherApostropheHoldsEveryWord(string name)
    {
        Assert.True(NameMatch.Fit(name, ["annas", "archive"]).IsFull);
        Assert.True(NameMatch.Fit(name, ["annas", "arhciv"]).IsFull);
        Assert.True(NameMatch.Fit(name, ["anna's", "archive"]).IsFull);
    }

    [Theory]
    [InlineData(Archiv)]
    [InlineData(Archi)]
    public void AMistypedWordStillFitsANameCutShort(string name)
    {
        Assert.True(NameMatch.Fit(name, ["annas", "aarhcive"]).IsFull);
    }

    [Fact]
    public void ANameCutPastTheWordHoldsOnlySome_AndAWordInsideAnotherWordIsNotFound()
    {
        var a = NameMatch.Fit(A, ["annas", "archive"]);
        Assert.False(a.IsFull);
        Assert.True(a.IsPartial);
        Assert.Equal(1, a.Matched);

        Assert.False(NameMatch.Fit(Savannah, ["annas", "archive"]).IsPartial);
    }

    [Theory]
    [InlineData("Biology notes.docx", "biolgy", 0.75)]
    [InlineData("Biology notes.docx", "bio", 0.9)]
    [InlineData("Biology notes.docx", "biology", 1)]
    [InlineData("Microbiology History Questions Study Guide - Studley AI.pdf", "microbiolgy", 0.75)]
    public void AWordFitsANameWordByHowCloseItIs(string name, string word, double expected)
    {
        Assert.Equal(expected, NameMatch.Fit(name, [word]).Score, 3);
    }

    [Fact]
    public void AFitCountsTheWordsAndIsBestWithEveryOneExact()
    {
        var exact = NameMatch.Fit("Pequots book.pdf", ["pequots", "book"]);
        var partial = NameMatch.Fit("The Pequots in southern New England.pdf", ["pequots", "book"]);

        Assert.Equal(new NameFit(2, 2, 1), exact);
        Assert.Equal(1, partial.Matched);
        Assert.Equal(0.5, partial.Score);
        Assert.Equal(new NameFit(0, 0, 0), NameMatch.Fit("anything.pdf", []));
    }

    [Fact]
    public void EachWordIsSearchedAloneAndLongOnesByTheirStart_AndTheOtherLimitsStay()
    {
        var original = new FileSearchQuery("annas arhciv") { Extensions = [".pdf"], MaxResultsPerType = 10 };

        var queries = CandidateSearch.Queries(original, ["annas", "arhciv"]);

        Assert.Equal(["annas", "arhciv", "arhc", "(none)"], queries.Select(query => query.Text ?? "(none)"));
        Assert.All(queries, query => Assert.Equal([".pdf"], query.Extensions));
        Assert.All(queries.Take(3), query => Assert.Equal(CandidateSearch.MaxPerWord, query.MaxResultsPerType));

        // The last is the newest files within the other limits, so a word mistyped at its start still meets its file.
        Assert.Equal(CandidateSearch.MaxNewest, queries[^1].MaxResultsPerType);
        Assert.Equal(FileSearchOrder.ModifiedDescending, queries[^1].Order);
    }

    [Fact]
    public void WithNoOtherLimitThereIsNoNewestSearch_AndAPhraseIsSearchedWhole()
    {
        var queries = CandidateSearch.Queries(new FileSearchQuery("\"trip plan\" budget"), ["trip plan", "budget"]);

        Assert.Equal(["\"trip plan\"", "budget", "budg"], queries.Select(query => query.Text ?? ""));
        Assert.Empty(CandidateSearch.Queries(new FileSearchQuery { Extensions = [".pdf"] }, []));
    }

    [Fact]
    public void AScreenshotSearchKeepsItsScreenshotName()
    {
        var original = new FileSearchQuery("error") { Filename = "Screenshot", Extensions = [".png"] };

        Assert.All(CandidateSearch.Queries(original, ["error"]), query => Assert.Equal("Screenshot", query.Filename));
    }
}
