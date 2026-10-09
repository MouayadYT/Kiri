using Assistant.Core.Ipc;
using Assistant.Core.ModelHosting;
using Xunit;

namespace Assistant.ModelHost.Tests;

public sealed class ModelHostArgumentsTests
{
    [Fact]
    public void CommandLine_RoundTrips()
    {
        var arguments = new ModelHostArguments(LocalPipe.CreateUniqueName(ModelHostProtocol.PipeNamePrefix), 4242);

        Assert.True(ModelHostArguments.TryParse(arguments.ToCommandLine(), out var parsed));
        Assert.Equal(arguments, parsed);
        Assert.Equal(["--pipe", arguments.PipeName, "--owner", "4242"], arguments.ToCommandLine());
    }

    [Fact]
    public void OptionsInEitherOrder_AreRead()
    {
        Assert.True(ModelHostArguments.TryParse(["--owner", "7", "--pipe", "Assistant.ModelHost.abc"], out var parsed));
        Assert.Equal(new ModelHostArguments("Assistant.ModelHost.abc", 7), parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--pipe|Assistant.ModelHost.abc")]
    [InlineData("--pipe|Assistant.ModelHost.abc|--owner|7|--verbose|1")]
    [InlineData("--pipe|Assistant.ModelHost.abc|--pipe|Assistant.ModelHost.def")]
    [InlineData("--pipe|Assistant.ModelHost.abc|--port|7")]
    [InlineData("--pipe|Assistant.ModelHost.abc|--owner|0")]
    [InlineData("--pipe|Assistant.ModelHost.abc|--owner|-7")]
    [InlineData("--pipe|Assistant.ModelHost.abc|--owner|seven")]
    [InlineData(@"--pipe|\\.\pipe\Assistant.ModelHost.abc|--owner|7")]
    [InlineData("--pipe|Assistant ModelHost|--owner|7")]
    [InlineData("--pipe||--owner|7")]
    public void AnythingElse_IsRefused(string commandLine)
    {
        Assert.False(ModelHostArguments.TryParse(Split(commandLine), out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void UniquePipeNames_AreValidAndDifferent()
    {
        var first = LocalPipe.CreateUniqueName(ModelHostProtocol.PipeNamePrefix);
        var second = LocalPipe.CreateUniqueName(ModelHostProtocol.PipeNamePrefix);

        Assert.StartsWith("Assistant.ModelHost.", first, StringComparison.Ordinal);
        Assert.True(LocalPipe.IsValidName(first));
        Assert.NotEqual(first, second);
        Assert.False(LocalPipe.IsValidName(new string('a', LocalPipe.MaxNameLength + 1)));
    }

    /// <summary>Reads arguments separated by "|", as theory data holds them: attributes cannot hold string arrays.</summary>
    internal static string[] Split(string commandLine) => commandLine.Length == 0 ? [] : commandLine.Split('|');
}
