using Assistant.Core.Startup;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>How the Assistant was started (PROJECT_SPEC §4.9, step 121): only a start with Windows keeps the bar hidden.</summary>
public sealed class LaunchOptionsTests
{
    [Fact]
    public void AStartWithNoArgumentsShowsTheBarAsAlways()
    {
        Assert.False(LaunchOptions.Parse(null).StartHidden);
        Assert.False(LaunchOptions.Parse([]).StartHidden);
    }

    [Theory]
    [InlineData("--background")]
    [InlineData("--BACKGROUND")]
    public void TheBackgroundSwitchKeepsTheBarHidden(string argument)
    {
        Assert.True(LaunchOptions.Parse([argument]).StartHidden);
        Assert.True(LaunchOptions.Parse([@"C:\Docs\plan.docx", argument]).StartHidden);
    }

    [Fact]
    public void AnythingElseOnTheCommandLineIsNotTheSwitch()
    {
        Assert.False(LaunchOptions.Parse([@"C:\Docs\plan.docx"]).StartHidden);
        Assert.False(LaunchOptions.Parse(["--background-task", "background", "-background", "/background"]).StartHidden);
    }

    [Fact]
    public void TheSignInEntryUsesTheSameSwitchTheAppReads()
    {
        Assert.Equal("--background", LaunchOptions.BackgroundArgument);
    }
}
