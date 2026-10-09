using System.Diagnostics;
using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch.Routing;
using Assistant.Core.Tools;
using Assistant.Tools.Calculator;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// The calculator (PROJECT_SPEC §4.1, §4.8): a parser over numbers, <c>+ - * / ^ %</c> and brackets, evaluated exactly, that knows
/// nothing else, so no sum can be code. It returns the structured result the conversation draws as a calculation card.
/// </summary>
public sealed class CalculatorTests
{
    private static CalculationOutput Work(string sum)
    {
        Assert.True(ArithmeticEvaluator.TryEvaluate(sum, out var output, out var problem), $"{sum}: {problem}");
        Assert.Null(problem);
        return output;
    }

    private static string Problem(string sum)
    {
        Assert.False(ArithmeticEvaluator.TryEvaluate(sum, out _, out var problem), sum);
        Assert.False(string.IsNullOrWhiteSpace(problem));
        return problem!;
    }

    // ---- Values ----

    [Theory]
    [InlineData("9+10", "19")]
    [InlineData("9 + 10", "19")]
    [InlineData("(3 + 4) * 5", "35")]
    [InlineData("2 + 3 * 4", "14")]
    [InlineData("(2 + 3) * 4", "20")]
    [InlineData("10 - 4 - 3", "3")]
    [InlineData("100 / 4 / 5", "5")]
    [InlineData("7 / 2", "3.5")]
    [InlineData("0.1 + 0.2", "0.3")]
    [InlineData("1.5 * 4", "6")]
    [InlineData(".5 + .25", "0.75")]
    [InlineData("10 % 3", "1")]
    [InlineData("10.5 % 3", "1.5")]
    [InlineData("-7 % 3", "-1")]
    [InlineData("2 ^ 10", "1024")]
    [InlineData("2 ^ 3 ^ 2", "512")]
    [InlineData("(2 ^ 3) ^ 2", "64")]
    [InlineData("2 ^ -1", "0.5")]
    [InlineData("10 ^ -2", "0.01")]
    [InlineData("0 ^ 0", "1")]
    [InlineData("9 ^ 0.5", "3")]
    [InlineData("-2 ^ 2", "-4")]
    [InlineData("(-2) ^ 2", "4")]
    [InlineData("-3 * 2", "-6")]
    [InlineData("2 * -3", "-6")]
    [InlineData("--3", "3")]
    [InlineData("+5 - -5", "10")]
    [InlineData("-(1 + 2)", "-3")]
    [InlineData("3 - (2 - 1)", "2")]
    [InlineData("((((1))))", "1")]
    [InlineData("1,000 + 2,500", "3500")]
    [InlineData("1,000,000 * 3", "3000000")]
    [InlineData("1,234.5 + 0.5", "1235")]
    [InlineData("3 x 4", "12")]
    [InlineData("3 X 4", "12")]
    [InlineData("3×4", "12")]
    [InlineData("12÷4", "3")]
    [InlineData("9−5", "4")]
    [InlineData("0.000001 * 0.000001", "0.000000000001")]
    [InlineData("1 / 3 * 3", "1")]
    [InlineData("0 * 5", "0")]
    [InlineData("0 - 0", "0")]
    [InlineData("007 + 1", "8")]
    [InlineData("2 ^ 64", "18446744073709551616")]
    [InlineData("79228162514264337593543950335 + 0", "79228162514264337593543950335")]
    public void SumsAreWorkedOutExactly(string sum, string value)
    {
        Assert.Equal(value, Work(sum).Result);
    }

    [Theory]
    [InlineData("9+10", "9 + 10")]
    [InlineData("  9   +   10 ", "9 + 10")]
    [InlineData("( 3+4 )*5", "(3 + 4) * 5")]
    [InlineData("-3*(2+1)", "-3 * (2 + 1)")]
    [InlineData("2*-3", "2 * -3")]
    [InlineData("1,000+2", "1000 + 2")]
    [InlineData(".5+1", "0.5 + 1")]
    [InlineData("3x4", "3 * 4")]
    [InlineData("12÷4", "12 / 4")]
    [InlineData("2^10", "2 ^ 10")]
    [InlineData("10%3", "10 % 3")]
    public void TheSumIsShownAsItIsRead(string written, string shown)
    {
        Assert.Equal(shown, Work(written).Expression);
    }

    [Fact]
    public void ARoundedValueSaysSo_AndAnExactOneDoesNot()
    {
        var third = Work("1 / 3");
        Assert.Equal("0.3333333333", third.Result);
        Assert.Equal("Rounded to 10 decimal places.", third.Secondary);

        var twoThirds = Work("2 / 3");
        Assert.Equal("0.6666666667", twoThirds.Result);

        Assert.Null(Work("1 / 4").Secondary);
        Assert.Equal("0.25", Work("1 / 4").Result);
        Assert.Null(Work("0.1 + 0.2").Secondary);
        Assert.Null(Work("2 ^ 10").Secondary);
    }

    [Fact]
    public void AValueTooSmallToShowAtTenPlaces_IsShownInFull_NotAsNothing()
    {
        var tiny = Work("1 / 10000000000000");

        Assert.Equal("0.0000000000001", tiny.Result);
        Assert.Null(tiny.Secondary);
    }

    [Fact]
    public void NegativeResultsAndNegativeZeroAreShownPlainly()
    {
        Assert.Equal("-0.5", Work("0 - 0.5").Result);
        Assert.Equal("0", Work("-0 * 5").Result);
        Assert.Equal("0", Work("0 / -3").Result);
    }

    // ---- What cannot be worked out ----

    [Theory]
    [InlineData("1 / 0", "Division by zero")]
    [InlineData("5 % 0", "Division by zero")]
    [InlineData("0 ^ -1", "Division by zero")]
    [InlineData("1 / (2 - 2)", "Division by zero")]
    [InlineData("79228162514264337593543950335 * 2", "too large")]
    [InlineData("10 ^ 40", "too large")]
    [InlineData("10 ^ 10000", "too large")]
    [InlineData("2 ^ 10001", "exponent is too large")]
    [InlineData("0.5 ^ 5000", "too small")]
    [InlineData("(-8) ^ 0.5", "no real value")]
    [InlineData("10 ^ 400.5", "too large")]
    public void ASumWithNoValueIsRefused_WithWordsThatSayWhy(string sum, string reason)
    {
        Assert.Contains(reason, Problem(sum), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("()")]
    [InlineData("1 +")]
    [InlineData("+")]
    [InlineData("* 3")]
    [InlineData("1 + * 2")]
    [InlineData("1 2")]
    [InlineData("2(3)")]
    [InlineData("(1 + 2)(3)")]
    [InlineData("(1 + 2) 3")]
    [InlineData("2 (3 + 4)")]
    [InlineData("(1 + 2")]
    [InlineData("1 + 2)")]
    [InlineData(")(")]
    [InlineData("1..2 + 1")]
    [InlineData("1. + 1")]
    [InlineData("1,2 + 1")]
    [InlineData("12,34 + 1")]
    [InlineData("1,000. + 1")]
    [InlineData("1.5,000 + 1")]
    [InlineData("5, + 1")]
    [InlineData("1e3 + 1")]
    [InlineData("two + 2")]
    [InlineData("pi * 2")]
    [InlineData("sqrt(4)")]
    [InlineData("1 + 2 = 3")]
    [InlineData("x * 2")]
    [InlineData("3 x")]
    [InlineData("3 * x")]
    [InlineData("1; 2")]
    [InlineData("1   2")]
    public void WhatIsNotASumIsRefused(string sum)
    {
        Problem(sum);
    }

    [Fact]
    public void ASumNestedTooDeeplyOrWrittenTooLong_IsRefused()
    {
        Assert.Contains("too deeply", Problem(new string('(', 40) + "1" + new string(')', 40)), StringComparison.Ordinal);
        Assert.Contains("too deeply", Problem(string.Concat(Enumerable.Repeat("-", 50)) + "1"), StringComparison.Ordinal);
        Assert.Contains("too long", Problem(string.Join(" + ", Enumerable.Repeat("1", 200))), StringComparison.Ordinal);
        Assert.Contains("at most 29 digits", Problem(new string('9', 30) + " + 1"), StringComparison.Ordinal);
        Assert.Contains("too large", Problem(new string('9', 29) + " + 1"), StringComparison.Ordinal);
    }

    [Fact]
    public void ASumAtTheLongestLengthIsWorkedOut()
    {
        var sum = string.Join("+", Enumerable.Repeat("1", 100)); // 199 characters.

        Assert.Equal("100", Work(sum).Result);
    }

    // ---- It is not code ----

    [Theory]
    [InlineData("System.Diagnostics.Process.Start(\"calc\")")]
    [InlineData("1 + $(Get-Date)")]
    [InlineData("1; DROP TABLE users")]
    [InlineData("`whoami`")]
    [InlineData("1 + {{7*7}}")]
    [InlineData("1 + eval('2')")]
    [InlineData("__import__('os').system('dir')")]
    [InlineData("1 + 1 // comment")]
    [InlineData("1 + 1 /* c */")]
    [InlineData("\"1\" + \"1\"")]
    [InlineData("[1] + [2]")]
    [InlineData("1 & 1")]
    [InlineData("1 | 1")]
    [InlineData("1 << 2")]
    [InlineData("!1")]
    [InlineData("1 > 0 ? 1 : 2")]
    [InlineData("a = 5")]
    [InlineData("1\n+\n2x")]
    public void NothingThatIsCodeIsEverRun(string text)
    {
        var worked = ArithmeticEvaluator.TryEvaluate(text, out var output, out var problem);

        // Only a sum has a value; anything else is refused, and nothing was started (the test would not be here to say so otherwise).
        Assert.False(worked, output.Result);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void ASumWithNewlinesAndTabsBetweenItsPartsIsReadAsSpace()
    {
        Assert.Equal("3", Work("1\n+\t2").Result);
    }

    [Fact]
    public void NoSumTakesLong()
    {
        var stopwatch = Stopwatch.StartNew();
        foreach (var sum in new[]
                 {
                     string.Join("^", Enumerable.Repeat("9", 60)),
                     "9 ^ 9 ^ 9 ^ 9",
                     "2 ^ 10000 ^ 10000",
                     "(((((((((((((((((((((((((((((((1)))))))))))))))))))))))))))))))",
                     "0.9999999999999999999999999999 ^ 10000",
                     string.Join(" * ", Enumerable.Repeat("99999999999999999999", 8)),
                 })
        {
            ArithmeticEvaluator.TryEvaluate(sum, out _, out _);
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"It took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void AThousandRandomStringsNeverThrowAndNeverMakeUpAValue()
    {
        var random = new Random(20261002);
        const string alphabet = "0123456789 +-*/^%().,xX×÷−eEa;'\"\\\n";
        for (var round = 0; round < 1000; round++)
        {
            var text = new string(Enumerable.Range(0, random.Next(1, 40)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());

            var worked = ArithmeticEvaluator.TryEvaluate(text, out var output, out var problem);

            Assert.Equal(worked, problem is null);
            if (worked)
            {
                Assert.False(string.IsNullOrEmpty(output.Result));
                Assert.False(string.IsNullOrEmpty(output.Expression));

                // Whatever has a value is a sum the router would read as one too, once it is spelled as the router does.
                Assert.DoesNotContain('e', output.Result);
            }
        }
    }

    [Fact]
    public void WhatTheRouterReadsAsASumIsWhatTheCalculatorWorksOut()
    {
        foreach (var typed in new[] { "9+10", "what is 9+10", "calculate (3+4)*5", "2^10", "100 / 8", "10 % 3", "3 x 4", "-3 * (2 + 1)", "1,000 + 1", "what's 7*6 =", "12 ÷ 4" })
        {
            Assert.True(ArithmeticExpression.TryRead(typed, out var read), typed);
            Assert.True(ArithmeticEvaluator.TryEvaluate(read, out var output, out var problem), $"{typed} -> {read}: {problem}");
            Assert.Equal(read, output.Expression);
        }
    }

    // ---- The tool ----

    private static readonly ToolCall Sum = new("c1", "calculate", """{"expression":"(3 + 4) * 5"}""");

    [Fact]
    public async Task TheToolReturnsTheStructuredResultTheCardIsDrawnFrom()
    {
        var executor = new ToolExecutor([CalculateTool.Create()]);

        var result = await executor.ExecuteAsync(Sum, new ToolContext(Guid.NewGuid()));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(("c1", "calculate"), (result.ToolCallId, result.ToolName));
        Assert.Equal("""{"expression":"(3 + 4) * 5","result":"35"}""", result.OutputJson);
        Assert.True(CalculationToolResults.TryRead(result.OutputJson, out var output));
        Assert.Equal(new CalculationOutput("(3 + 4) * 5", "35"), output);
    }

    [Fact]
    public async Task ARoundedResultCarriesItsNote()
    {
        var result = await new ToolExecutor([CalculateTool.Create()]).ExecuteAsync(new ToolCall("c1", "calculate", """{"expression":"2/3"}"""));

        Assert.True(CalculationToolResults.TryRead(result.OutputJson, out var output));
        Assert.Equal(("2 / 3", "0.6666666667", "Rounded to 10 decimal places."), (output.Expression, output.Result, output.Secondary));
    }

    [Theory]
    [InlineData("""{"expression":"1/0"}""", "Division by zero")]
    [InlineData("""{"expression":"two plus two"}""", "Only numbers")]
    public async Task ASumThatCannotBeWorkedOut_IsAFailedResultTheModelCanRead(string arguments, string reason)
    {
        var result = await new ToolExecutor([CalculateTool.Create()]).ExecuteAsync(new ToolCall("c1", "calculate", arguments));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out var message));
        Assert.Equal(ToolErrors.Failed, code);
        Assert.Contains(reason, message, StringComparison.Ordinal);
        Assert.False(CalculationToolResults.TryRead(result.OutputJson, out _));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"expression":5}""")]
    [InlineData("""{"expression":"1+1","precision":2}""")]
    [InlineData("""{"sum":"1+1"}""")]
    [InlineData("not json")]
    public async Task TheToolTakesOnlyTheSum_AsText(string arguments)
    {
        var result = await new ToolExecutor([CalculateTool.Create()]).ExecuteAsync(new ToolCall("c1", "calculate", arguments));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal(ToolErrors.InvalidArguments, Code(result));
        Assert.Contains("calculate(expression: text)", result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASumLongerThanTheLimitIsRefusedBeforeItIsRead()
    {
        var sum = new string('1', ArithmeticEvaluator.MaxLength + 1);

        var result = await new ToolExecutor([CalculateTool.Create()]).ExecuteAsync(
            new ToolCall("c1", "calculate", JsonSerializer.Serialize(new { expression = sum })));

        Assert.Contains("too long", result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolIsReadOnly_NeedsNoPermission_AndIsQuick()
    {
        var definition = CalculateTool.Definition;

        Assert.Equal("calculate", definition.Name);
        Assert.Equal(RiskLevel.ReadOnly, definition.RiskLevel);
        Assert.Null(definition.RequiredPermission);
        Assert.True(definition.EffectiveTimeout <= TimeSpan.FromSeconds(10));
        Assert.Equal("calculate(expression: text)", ToolUsage.Describe(definition));
    }

    [Fact]
    public async Task TheToolRunsWithNoConfirmation_BecauseItOnlyCalculates()
    {
        var confirmation = new FakeConfirmation(false);

        var result = await new ToolExecutor([CalculateTool.Create()], confirmation).ExecuteAsync(Sum);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(0, confirmation.Asked);
    }

    private static string Code(ToolResult result)
    {
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        return code;
    }
}
