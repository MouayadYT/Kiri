using Assistant.BrowserBridge;
using Xunit;

namespace Assistant.BrowserBridge.Tests;

/// <summary>What the executable's command line means (PROJECT_SPEC §4.5): a browser serving it, or the app asking for its registration.</summary>
public sealed class HostCommandTests
{
    private const string OtherExtension = "abcdefghijklmnopabcdefghijklmnop";

    [Fact]
    public void ABrowserStartsTheHostWithTheExtensionsOriginAndTheParentWindow_AndIsServed()
    {
        var command = HostCommand.Parse(["chrome-extension://cklcgaanpeplmbjmconamjjghmibhfok/", "--parent-window=1234"]);

        Assert.Equal(HostCommandKind.Serve, command.Kind);
        Assert.Null(command.PipeName);
    }

    [Fact]
    public void NoArgumentsServesToo()
    {
        Assert.Equal(HostCommandKind.Serve, HostCommand.Parse([]).Kind);
    }

    [Theory]
    [InlineData("register")]
    [InlineData("REGISTER")]
    [InlineData("Register")]
    public void RegisterIsMatchedInAnyCase(string name)
    {
        var command = HostCommand.Parse([name]);

        Assert.Equal(HostCommandKind.Register, command.Kind);
        Assert.Empty(command.ExtensionIds);
    }

    [Fact]
    public void RegisterTakesMoreExtensionIds_AndNothingElse()
    {
        var command = HostCommand.Parse(["register", "--extension-id", OtherExtension, "--extension-id", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"]);
        Assert.Equal(HostCommandKind.Register, command.Kind);
        Assert.Equal([OtherExtension, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"], command.ExtensionIds);

        foreach (var bad in new[]
                 {
                     new[] { "register", "--extension-id" },
                     new[] { "register", "--extension-id", "not-an-id" },
                     new[] { "register", "--other", OtherExtension },
                     new[] { "register", "extra" },
                 })
        {
            Assert.Equal(HostCommandKind.Unknown, HostCommand.Parse(bad).Kind);
        }
    }

    [Fact]
    public void UnregisterTakesNothing()
    {
        Assert.Equal(HostCommandKind.Unregister, HostCommand.Parse(["unregister"]).Kind);
        Assert.Equal(HostCommandKind.Unregister, HostCommand.Parse(["UNREGISTER"]).Kind);
        Assert.Equal(HostCommandKind.Unknown, HostCommand.Parse(["unregister", "now"]).Kind);
    }

    [Fact]
    public void ThePipeOptionNamesAnotherAppPipe_ButOnlyAPlainPipeName()
    {
        var command = HostCommand.Parse(["--pipe", "Assistant.Tests.One", "chrome-extension://x/"]);
        Assert.Equal(HostCommandKind.Serve, command.Kind);
        Assert.Equal("Assistant.Tests.One", command.PipeName);

        foreach (var bad in new[] { new[] { "--pipe" }, new[] { "--pipe", @"\\.\pipe\x" }, new[] { "--pipe", "a b" }, new[] { "--pipe", "" } })
        {
            Assert.Equal(HostCommandKind.Unknown, HostCommand.Parse(bad).Kind);
        }
    }
}
