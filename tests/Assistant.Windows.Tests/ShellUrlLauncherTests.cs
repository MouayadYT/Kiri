using System.ComponentModel;
using System.Diagnostics;
using Assistant.Windows.Shell;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>Opening a page from the image search results: only a web address goes to the shell, and nothing is started in the tests.</summary>
public sealed class ShellUrlLauncherTests
{
    private readonly List<ProcessStartInfo> _started = [];

    private ShellUrlLauncher Launcher(bool succeeds = true, Exception? failure = null) => new(info =>
    {
        _started.Add(info);
        return failure is null ? succeeds : throw failure;
    });

    [Theory]
    [InlineData("https://example.com/page?q=1")]
    [InlineData("http://example.com/")]
    public void AWebAddressIsHandedToTheShellToOpenInTheBrowser(string address)
    {
        Assert.True(Launcher().Open(new Uri(address)));

        var start = Assert.Single(_started);
        Assert.Equal(new Uri(address).AbsoluteUri, start.FileName);
        Assert.True(start.UseShellExecute);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ftp://example.com/file")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    public void NothingButHttpOrHttpsIsEverStarted(string address)
    {
        Assert.False(Launcher().Open(new Uri(address)));

        Assert.Empty(_started);
    }

    [Fact]
    public void ARelativeAddressIsRefused()
    {
        var launcher = Launcher();

        Assert.False(launcher.Open(new Uri("/just/a/path", UriKind.Relative)));
        Assert.Empty(_started);
    }

    [Fact]
    public void AnAddressWithSpacesOrQuotesIsEscapedBeforeTheShellSeesIt()
    {
        Assert.True(Launcher().Open(new Uri("https://example.com/a b/\"c\"")));

        var file = Assert.Single(_started).FileName;
        Assert.DoesNotContain(" ", file, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", file, StringComparison.Ordinal);
    }

    [Fact]
    public void AShellThatCannotOpenItIsAFailure_NotAnException()
    {
        var address = new Uri("https://example.com/");

        Assert.False(Launcher(succeeds: false).Open(address));
        Assert.False(Launcher(failure: new Win32Exception(1155)).Open(address));
        Assert.False(Launcher(failure: new InvalidOperationException()).Open(address));
    }

    [Fact]
    public void NoAddressIsAnError() =>
        Assert.Throws<ArgumentNullException>(() => Launcher().Open(null!));
}
