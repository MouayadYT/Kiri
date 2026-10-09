using Assistant.Core.Contracts;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>The engine options a model profile or hardware preset may set, and how layers of them combine.</summary>
public sealed class EngineArgumentsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("--threads|6")]
    [InlineData("--threads|6|--threads-batch|12|--batch-size|512|--ubatch-size|256")]
    [InlineData("--flash-attn|on|--cache-type-k|q8_0|--cache-type-v|q8_0")]
    [InlineData("--no-kv-offload|--jinja")]
    [InlineData("--reasoning-budget|-1")]
    [InlineData("--reasoning-budget|0")]
    public void AllowedArguments_AreValid(string arguments) =>
        Assert.Null(EngineArguments.Validate(Split(arguments)));

    [Fact]
    public void NoArguments_AreValid()
    {
        Assert.True(EngineArguments.IsValid(null));
        Assert.True(EngineArguments.IsValid([]));
    }

    // Everything the host sets itself, and everything that would open the engine to the network, a file or a log.
    [Theory]
    [InlineData("--host|0.0.0.0")]
    [InlineData("--port|8080")]
    [InlineData("--model|C:\\x.gguf")]
    [InlineData("--mmproj|C:\\x.gguf")]
    [InlineData("--ctx-size|99999")]
    [InlineData("--parallel|8")]
    [InlineData("--device|none")]
    [InlineData("--offline")]
    [InlineData("--no-slots")]
    [InlineData("--log-verbosity|5")]
    [InlineData("--log-file|C:\\prompts.txt")]
    [InlineData("--api-key|secret")]
    [InlineData("--lora|C:\\x.gguf")]
    [InlineData("--chat-template-file|C:\\x.jinja")]
    [InlineData("--path|C:\\")]
    [InlineData("--slot-save-path|C:\\")]
    [InlineData("--hf-repo|someone/model")]
    public void OptionsTheHostOwns_OrThatOpenTheEngineUp_AreRefused(string arguments) =>
        Assert.NotNull(EngineArguments.Validate(Split(arguments)));

    [Theory]
    [InlineData("-t|6")]
    [InlineData("-fa|on")]
    [InlineData("--threads=6")]
    [InlineData("--THREADS|6")]
    [InlineData("6")]
    [InlineData("--threads")]
    [InlineData("--threads|six")]
    [InlineData("--threads|0")]
    [InlineData("--threads|1025")]
    [InlineData("--threads| 6")]
    [InlineData("--threads|6.5")]
    [InlineData("--threads|--batch-size")]
    [InlineData("--flash-attn|maybe")]
    [InlineData("--flash-attn")]
    [InlineData("--cache-type-k|q2_k")]
    [InlineData("--reasoning-budget|-2")]
    [InlineData("--no-kv-offload|true")]
    public void Misspelt_ShortOrOutOfRangeArguments_AreRefused(string arguments) =>
        Assert.NotNull(EngineArguments.Validate(Split(arguments)));

    [Theory]
    [InlineData("--threads|4|--threads|8")]
    [InlineData("--jinja|--no-jinja")]
    public void SettingAnOptionTwice_IsRefused(string arguments) =>
        Assert.NotNull(EngineArguments.Validate(Split(arguments)));

    [Fact]
    public void ANullToken_IsRefused() =>
        Assert.NotNull(EngineArguments.Validate(new string[] { null! }));

    [Fact]
    public void TheProblem_NeverRepeatsTheArgument()
    {
        var problem = EngineArguments.Validate(["--log-file", @"C:\Users\PRIVATE-NAME-4d1f\prompts.txt"]);

        Assert.NotNull(problem);
        Assert.DoesNotContain("PRIVATE", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("log-file", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAllowedFlag_IsLongFormAndLowerCase() =>
        Assert.All(EngineArguments.AllowedFlags, flag => Assert.Matches("^--[a-z][a-z-]*$", flag));

    [Fact]
    public void Merge_LetsALaterLayerReplaceAnOption_KeepingItsPlace()
    {
        var merged = EngineArguments.Merge(
            ["--batch-size", "512", "--ubatch-size", "256", "--no-kv-offload"],
            null,
            ["--threads", "6", "--batch-size", "1024"]);

        Assert.Equal(
            ["--batch-size", "1024", "--ubatch-size", "256", "--no-kv-offload", "--threads", "6"], merged);
    }

    [Fact]
    public void Merge_ReplacesOneSwitchWithItsOpposite()
    {
        Assert.Equal(["--no-jinja"], EngineArguments.Merge(["--jinja"], ["--no-jinja"]));
    }

    [Fact]
    public void Merge_OfNothing_IsEmpty()
    {
        Assert.Empty(EngineArguments.Merge());
        Assert.Empty(EngineArguments.Merge(null, []));
    }

    [Fact]
    public void Merge_OfAnInvalidLayer_Throws() =>
        Assert.Throws<ArgumentException>(() => EngineArguments.Merge(["--threads", "2"], ["--host", "0.0.0.0"]));

    private static string[] Split(string arguments) =>
        arguments.Length == 0 ? [] : arguments.Split('|');
}
