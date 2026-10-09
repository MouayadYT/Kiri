using System.Text;
using Assistant.Core.Budgeting;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>What the token estimate says, and the two properties trimming relies on.</summary>
public sealed class HeuristicTokenEstimatorTests
{
    private readonly HeuristicTokenEstimator _estimator = new();

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("word", 1)]
    [InlineData("hello", 2)]
    [InlineData("understanding", 5)]
    [InlineData("abcdefgh", 2)]
    [InlineData("abcdefghi", 3)]
    [InlineData("abcdefghij", 3)]
    [InlineData("abcdefghijk", 4)]
    [InlineData("hello world", 4)]
    [InlineData("2026", 2)]
    [InlineData("12345", 3)]
    [InlineData("abc123", 3)]
    [InlineData("...", 3)]
    [InlineData("a-b", 3)]
    public void ARun_CostsByItsKind(string text, int expected) =>
        Assert.Equal(expected, _estimator.Estimate(text));

    [Theory]
    [InlineData("a b", 2)]
    [InlineData("a  b", 3)]
    [InlineData("a\nb", 3)]
    [InlineData("a\n\nb", 3)]
    [InlineData("a\r\n\r\nb", 3)]
    [InlineData("a\t\tb", 3)]
    public void WhiteSpace_IsFreeAsASingleSpace_AndOtherwiseCostsByLength(string text, int expected) =>
        Assert.Equal(expected, _estimator.Estimate(text));

    [Fact]
    public void ALongRunOfWhiteSpace_CostsAnEighthOfItsLength()
    {
        Assert.Equal(1 + 125 + 1, _estimator.Estimate("a" + new string('\n', 1000) + "b"));
        Assert.Equal(1 + 128, _estimator.Estimate("a" + new string(' ', 1024)));
    }

    [Theory]
    [InlineData("日本語", 3)]
    [InlineData("مرحبا", 5)]
    [InlineData("café", 2)]
    [InlineData("😀", 2)]
    public void ScriptsBeyondAscii_CostATokenForEveryCharacter(string text, int expected) =>
        Assert.Equal(expected, _estimator.Estimate(text));

    [Fact]
    public void Prose_IsEstimatedAboveWhatARealTokenizerGives_ButNotWildlyAbove()
    {
        // A real tokenizer needs about 10 tokens for this sentence.
        var estimate = _estimator.Estimate("The quick brown fox jumps over the lazy dog.");

        Assert.InRange(estimate, 10, 16);
    }

    [Fact]
    public void EncodedData_CostsATokenForEveryTwoCharacters_WhichIsWhatTokenizersNeedForIt()
    {
        var hash = new string('a', 64);

        Assert.Equal(2 + (64 - 8 + 1) / 2, _estimator.Estimate(hash));
        Assert.True(_estimator.Estimate(hash) >= 64 / 3);
    }

    [Fact]
    public void Code_IsEstimatedWithAtLeastATokenForEveryPunctuationMark()
    {
        var code = "if (x[i] > 0) { total += x[i]; }";

        Assert.True(_estimator.Estimate(code) >= code.Count(character => "()[]{}>+=;".Contains(character)));
    }

    [Fact]
    public void ARunCostsTheSameWhereverItStands_SoAnEstimateIsTheSumOfItsRuns()
    {
        Assert.Equal(
            _estimator.Estimate("hello world") + _estimator.Estimate("!") + _estimator.Estimate("2026"),
            _estimator.Estimate("hello world!2026"));
    }

    [Fact]
    public void TheEstimateNeverFalls_WhenTextIsAddedAtEitherEnd()
    {
        var random = new Random(38);
        const string alphabet = "abcXYZ019 \n\t.,;-é日";
        for (var round = 0; round < 200; round++)
        {
            var text = new string(Enumerable.Range(0, random.Next(0, 60)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var extra = alphabet[random.Next(alphabet.Length)];

            Assert.True(_estimator.Estimate(text + extra) >= _estimator.Estimate(text), $"after '{text}'");
            Assert.True(_estimator.Estimate(extra + text) >= _estimator.Estimate(text), $"before '{text}'");
        }
    }

    [Fact]
    public void EveryPrefixAndEverySuffix_CostsNoMoreThanTheWhole()
    {
        var text = "The plan: 12 steps.\n\nFirst, read (carefully) the notes;  then act. 日本語 done";
        var whole = _estimator.Estimate(text);

        for (var length = 0; length <= text.Length; length++)
        {
            Assert.True(_estimator.Estimate(text.AsSpan(0, length)) <= whole);
            Assert.True(_estimator.Estimate(text.AsSpan(length)) <= whole);
        }
    }

    [Fact]
    public void ALargeText_IsEstimatedInOnePassWithoutOverflow()
    {
        var text = new StringBuilder().Insert(0, "word ", 2_000_000).ToString();

        Assert.Equal(2_000_000, _estimator.Estimate(text.TrimEnd()));
    }
}
