using Assistant.Search.Planning;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>The model's pick of files: only numbers of the files it was shown, each once, and nothing else.</summary>
public sealed class ReviewReplyParserTests
{
    [Theory]
    [InlineData("""{"matches":[1]}""", new[] { 1 })]
    [InlineData("""{"matches":[3,1,2]}""", new[] { 1, 2, 3 })]
    [InlineData("""{ "matches" : [ 5 , 4 ] }""", new[] { 4, 5 })]
    [InlineData("""{"matches":[]}""", new int[0])]
    [InlineData("""{"matches":null}""", new int[0])]
    [InlineData("""{"matches":[1,2,3,4,5,6,7,8,9,10]}""", new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    public void AValidPickIsTheNumbersInOrder(string json, int[] expected)
    {
        Assert.Equal(expected, ReviewReplyParser.Parse(json, 12));
    }

    [Theory]
    [InlineData("""{"matches":[0]}""")]
    [InlineData("""{"matches":[-1]}""")]
    [InlineData("""{"matches":[13]}""")]
    [InlineData("""{"matches":[1,1]}""")]
    [InlineData("""{"matches":[1.5]}""")]
    [InlineData("""{"matches":[1e0]}""")]
    [InlineData("""{"matches":["1"]}""")]
    [InlineData("""{"matches":[null]}""")]
    [InlineData("""{"matches":[[1]]}""")]
    [InlineData("""{"matches":[1,2,3,4,5,6,7,8,9,10,11]}""")]
    [InlineData("""{"matches":"1"}""")]
    [InlineData("""{"matches":1}""")]
    [InlineData("""{"matches":{"a":1}}""")]
    [InlineData("""{"matches":[1],"why":"it fits"}""")]
    [InlineData("""{"matches":[1],"matches":[2]}""")]
    [InlineData("""{"paths":["C:\\secret.txt"]}""")]
    [InlineData("""{"files":[1]}""")]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("\"matches\"")]
    [InlineData("""{"matches":[1],}""")]
    [InlineData("""{"matches":[1]""")]
    [InlineData("not json")]
    public void AnythingElseIsInvalidAndPicksNothing(string json)
    {
        Assert.Null(ReviewReplyParser.Parse(json, 12));
    }

    [Fact]
    public void ANumberIsOnlyValidUpToTheNumberOfFilesShown()
    {
        Assert.Equal([3], ReviewReplyParser.Parse("""{"matches":[3]}""", 3));
        Assert.Null(ReviewReplyParser.Parse("""{"matches":[4]}""", 3));
        Assert.Null(ReviewReplyParser.Parse("""{"matches":[1]}""", 0));
    }
}
