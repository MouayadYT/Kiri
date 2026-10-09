using System.Diagnostics;
using Assistant.Windows.Shell;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// Signing in as another account than the one the user's browser already has (a school account, say) in a private window: which browser is used, which switch it is given, and that
/// only a web address, as an argument of its own, is ever started. Nothing is started in these tests and the registry is a fake.
/// </summary>
public sealed class PrivateBrowserTests
{
    private const string Edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string Firefox = @"C:\Program Files\Mozilla Firefox\firefox.exe";

    private sealed class FakeRegistry : IBrowserRegistry
    {
        public string? ProgId { get; init; }

        public Dictionary<string, string> Commands { get; init; } = [];

        public Dictionary<string, string> AppPaths { get; init; } = [];

        public HashSet<string> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public string? DefaultBrowserProgId() => ProgId;

        public string? OpenCommandOf(string progId) => Commands.GetValueOrDefault(progId);

        public string? RegisteredPath(string programFileName) => AppPaths.GetValueOrDefault(programFileName);

        public bool FileExists(string path) => Files.Contains(path);
    }

    [Theory]
    [InlineData("msedge.exe", "--inprivate")]
    [InlineData("chrome.exe", "--incognito")]
    [InlineData("Brave.exe", "--incognito")]
    [InlineData("vivaldi.exe", "--incognito")]
    [InlineData("opera.exe", "--private")]
    [InlineData("firefox.exe", "-private-window")]
    public void EachKnownBrowserHasItsOwnPrivateSwitch(string program, string expected) =>
        Assert.Equal(expected, PrivateBrowserFinder.SwitchFor(program));

    [Theory]
    [InlineData("notepad.exe")]
    [InlineData("iexplore.exe")]
    [InlineData("cmd.exe")]
    [InlineData("")]
    public void AProgramThatIsNotAKnownBrowserGetsNoSwitchAtAll(string program) => Assert.Null(PrivateBrowserFinder.SwitchFor(program));

    [Theory]
    [InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --single-argument %1", "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe")]
    [InlineData("C:\\Tools\\browser.exe -url \"%1\"", "C:\\Tools\\browser.exe")]
    [InlineData("  \"C:\\a b\\x.exe\"", "C:\\a b\\x.exe")]
    public void TheProgramIsReadFromAnOpenCommand(string command, string expected) => Assert.Equal(expected, PrivateBrowserFinder.ProgramOf(command));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"unterminated")]
    [InlineData("rundll32 url.dll,FileProtocolHandler %1")]
    [InlineData("\"relative\\chrome.exe\" %1")]
    [InlineData("\"C:\\Windows\\script.cmd\" %1")]
    public void AnOpenCommandThatIsNotAProgramPathIsNotUsed(string? command) => Assert.Null(PrivateBrowserFinder.ProgramOf(command));

    [Fact]
    public void TheDefaultBrowserIsUsedWhenItsPrivateSwitchIsKnown()
    {
        var registry = new FakeRegistry
        {
            ProgId = "ChromeHTML",
            Commands = { ["ChromeHTML"] = $"\"{Chrome}\" --single-argument %1" },
            Files = { Chrome, Edge },
            AppPaths = { ["msedge.exe"] = Edge },
        };

        var found = PrivateBrowserFinder.Find(registry);

        Assert.Equal(new PrivateBrowser(Chrome, "--incognito"), found);
    }

    [Fact]
    public void EdgeIsUsedWhenTheDefaultBrowserIsOneWhoseSwitchIsNotKnown()
    {
        var registry = new FakeRegistry
        {
            ProgId = "SomeOtherHTML",
            Commands = { ["SomeOtherHTML"] = "\"C:\\Other\\other.exe\" %1" },
            Files = { @"C:\Other\other.exe", Edge, Chrome },
            AppPaths = { ["msedge.exe"] = Edge, ["chrome.exe"] = Chrome },
        };

        Assert.Equal(new PrivateBrowser(Edge, "--inprivate"), PrivateBrowserFinder.Find(registry));
    }

    [Fact]
    public void ChromeThenFirefoxAreTheFallbacksWhenEdgeIsNotThere_AndNoneIsFoundWhenNothingIs()
    {
        var withoutEdge = new FakeRegistry { Files = { Firefox, Chrome }, AppPaths = { ["msedge.exe"] = Edge, ["chrome.exe"] = Chrome, ["firefox.exe"] = Firefox } };
        Assert.Equal(new PrivateBrowser(Chrome, "--incognito"), PrivateBrowserFinder.Find(withoutEdge));

        var onlyFirefox = new FakeRegistry { Files = { Firefox }, AppPaths = { ["firefox.exe"] = $"\"{Firefox}\"" } };
        Assert.Equal(new PrivateBrowser(Firefox, "-private-window"), PrivateBrowserFinder.Find(onlyFirefox));

        Assert.Null(PrivateBrowserFinder.Find(new FakeRegistry()));
    }

    [Fact]
    public void ADefaultBrowserWhoseProgramFileIsGoneIsNotUsed()
    {
        var registry = new FakeRegistry { ProgId = "ChromeHTML", Commands = { ["ChromeHTML"] = $"\"{Chrome}\" %1" }, AppPaths = { ["msedge.exe"] = Edge }, Files = { Edge } };

        Assert.Equal(new PrivateBrowser(Edge, "--inprivate"), PrivateBrowserFinder.Find(registry));
    }

    [Fact]
    public void AWebAddressIsStartedInAPrivateWindowAsAnArgumentOfItsOwn_NotThroughTheShell()
    {
        var started = new List<ProcessStartInfo>();
        var launcher = new ShellUrlLauncher(info => { started.Add(info); return true; }, () => new PrivateBrowser(Edge, "--inprivate"));

        Assert.True(launcher.OpenPrivate(new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id=x&prompt=select_account")));

        var info = Assert.Single(started);
        Assert.Equal(Edge, info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(2, info.ArgumentList.Count);
        Assert.Equal("--inprivate", info.ArgumentList[0]);
        Assert.StartsWith("https://login.microsoftonline.com/", info.ArgumentList[1], StringComparison.Ordinal);
        Assert.Contains("client_id=x&prompt=select_account", info.ArgumentList[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    public void NothingButHttpOrHttpsIsEverStartedInAPrivateWindow(string address)
    {
        var started = new List<ProcessStartInfo>();
        var launcher = new ShellUrlLauncher(info => { started.Add(info); return true; }, () => new PrivateBrowser(Edge, "--inprivate"));

        Assert.False(launcher.OpenPrivate(new Uri(address)));
        Assert.Empty(started);
    }

    [Fact]
    public void WithNoBrowserThatCanDoItNothingIsStarted_AndAFailureToStartIsOnlyFalse()
    {
        var started = new List<ProcessStartInfo>();

        Assert.False(new ShellUrlLauncher(info => { started.Add(info); return true; }, () => null).OpenPrivate(new Uri("https://example.com/")));
        Assert.False(new ShellUrlLauncher(_ => throw new InvalidOperationException(), () => new PrivateBrowser(Edge, "--inprivate")).OpenPrivate(new Uri("https://example.com/")));
        Assert.Empty(started);
    }
}
