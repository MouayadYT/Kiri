using Xunit;

namespace Assistant.ExplorerExtension.Tests;

/// <summary>The entry point's command line: what File Explorer and the app's settings start it with.</summary>
public sealed class ExtensionCommandTests
{
    [Fact]
    public void AskTakesTheFilesAsTheyAre()
    {
        var command = ExtensionCommand.Parse(["ask", @"C:\Users\Ana\My Documents\plan (1).docx", @"D:\b.png"]);
        Assert.Equal(ExtensionCommandKind.Ask, command.Kind);
        Assert.Equal([@"C:\Users\Ana\My Documents\plan (1).docx", @"D:\b.png"], command.Paths);

        Assert.Equal(ExtensionCommandKind.Ask, ExtensionCommand.Parse(["ASK", @"C:\a.txt", ""]).Kind);
        Assert.Single(ExtensionCommand.Parse(["ASK", @"C:\a.txt", ""]).Paths);
    }

    [Theory]
    [InlineData]
    [InlineData("ask")]
    [InlineData("ask", " ")]
    [InlineData("register", "extra")]
    [InlineData("install")]
    [InlineData(@"C:\a.txt")]
    public void AnythingElseIsNotACommand(params string[] args) =>
        Assert.Equal(ExtensionCommandKind.Unknown, ExtensionCommand.Parse(args).Kind);

    [Fact]
    public void RegisterAndUnregisterTakeNothingElse()
    {
        Assert.Equal(ExtensionCommandKind.Register, ExtensionCommand.Parse(["register"]).Kind);
        Assert.Equal(ExtensionCommandKind.Unregister, ExtensionCommand.Parse(["Unregister"]).Kind);
    }
}
