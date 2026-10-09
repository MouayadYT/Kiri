using System.Diagnostics;
using Assistant.Core.QuickSearch;
using Assistant.Windows.Shell;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// The applications Start does not list, found where PowerToys Run finds them (PROJECT_SPEC §4.1): the Desktop's shortcuts, game launchers' links and
/// programs, and how they are started and drawn. Everything is made up, in a folder of the test's own.
/// </summary>
public sealed class ProgramShortcutSourceTests : IDisposable
{
    private readonly string _desktop = Path.Combine(Path.GetTempPath(), "assistant-desktop-tests-" + Guid.NewGuid().ToString("N"));

    public ProgramShortcutSourceTests() => Directory.CreateDirectory(_desktop);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_desktop, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is harmless.
        }
    }

    private string Write(string name, string text = "x", string? folder = null)
    {
        var directory = folder is null ? _desktop : Path.Combine(_desktop, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static string Link(string url) => "[{000214A0-0000-0000-C000-000000000046}]\r\nProp3=19,0\r\n[InternetShortcut]\r\nIDList=\r\nIconIndex=0\r\nURL=" + url + "\r\nIconFile=C:\\Games\\game.exe\r\n";

    [Theory]
    [InlineData("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true", true)]
    [InlineData("steam://rungameid/730", true)]
    [InlineData("uplay://launch/6100/0", true)]
    [InlineData("ms-settings:display", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("http://example.com/", false)]
    [InlineData("HTTPS://EXAMPLE.COM/", false)]
    [InlineData("file:///C:/notes.txt", false)]
    [InlineData("mailto:someone@example.com", false)]
    [InlineData("C:\\Windows\\notepad.exe", false)]
    [InlineData("not a link", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ALinkOfItsOwnKindStartsAnApplicationAndAPageAFileOrMailDoesNot(string? url, bool application) =>
        Assert.Equal(application, InternetShortcut.IsApplicationLink(url));

    [Fact]
    public void AnInternetShortcutSaysWhereItPoints()
    {
        var game = Write("Fortnite.url", Link("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true"));
        var page = Write("Docs.url", Link("https://example.com/docs"));
        var other = Write("Other.url", "[Other]\r\nURL=steam://rungameid/1\r\n");
        var empty = Write("Empty.url", "");

        Assert.Equal("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true", InternetShortcut.ReadUrl(game));
        Assert.True(InternetShortcut.StartsAnApplication(game));
        Assert.False(InternetShortcut.StartsAnApplication(page));

        // Only the shortcut's own section counts, and a file that is not one, or is not there, is nothing.
        Assert.Null(InternetShortcut.ReadUrl(other));
        Assert.Null(InternetShortcut.ReadUrl(empty));
        Assert.Null(InternetShortcut.ReadUrl(Path.Combine(_desktop, "gone.url")));

        // So a game launcher's link is an application, where a link to a page stays a page.
        Assert.False(ApplicationLaunchId.IsDocument(game));
        Assert.True(ApplicationLaunchId.IsDocument(page));
    }

    [Fact]
    public async Task TheDesktopsGamesProgramsAndShortcutsAreListedAndPagesDocumentsAndUninstallersAreNot()
    {
        var game = Write("Fortnite.url", Link("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true"));
        var steam = Write("Counter-Strike 2.url", Link("steam://rungameid/730"), folder: "Games");
        var program = Write("Tool.exe");
        var clickOnce = Write("Company App.appref-ms");
        Write("Read me.url", Link("https://example.com/"));
        Write("Notes.txt");
        Write("Uninstall Tool.exe");
        Write("unins000.exe");
        Write("desktop.ini");
        Write("Deep.exe", folder: Path.Combine("One", "Two"));
        var hidden = Write("Hidden.exe");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        using var source = new ProgramShortcutSource([_desktop], watch: false);
        var applications = await source.GetApplicationsAsync(CancellationToken.None);

        // Each is listed by its own file, which is what starts it, under the name its file has.
        Assert.Equal(
            ["Company App", "Counter-Strike 2", "Fortnite", "Tool"],
            applications.Select(application => application.DisplayName).Order(StringComparer.Ordinal));
        Assert.Equal(game, applications.Single(application => application.DisplayName == "Fortnite").Id);
        Assert.Equal(steam, applications.Single(application => application.DisplayName == "Counter-Strike 2").Id);
        Assert.Equal(clickOnce, applications.Single(application => application.DisplayName == "Company App").Id);

        // A program has its file to show in File Explorer; a link has none.
        Assert.Equal(program, applications.Single(application => application.DisplayName == "Tool").ExecutablePath);
        Assert.Null(applications.Single(application => application.DisplayName == "Fortnite").ExecutablePath);
        Assert.All(applications, application => Assert.True(ApplicationLaunchId.IsStartableFile(application.Id)));
    }

    [Fact]
    public async Task AFolderThatIsNotThereListsNothingAndAReadingCanBeCancelled()
    {
        using var source = new ProgramShortcutSource([Path.Combine(_desktop, "nowhere")], watch: false);
        Assert.Empty(await source.GetApplicationsAsync(CancellationToken.None));

        Write("Tool.exe");
        using var real = new ProgramShortcutSource([_desktop], watch: false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => real.GetApplicationsAsync(cancelled.Token));
    }

    [Fact]
    public async Task TwoShortcutsOfOneNameAreListedOnceAsTheUsersOwnDesktopHasIt()
    {
        // The user's Desktop and everyone's can both hold a shortcut to the same game: the first folder's stands for both.
        var mine = Write("Fortnite.url", Link("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true"), folder: "Mine");
        Write("fortnite.url", Link("com.epicgames.launcher://apps/Fortnite?action=launch"), folder: "Everyone");
        using var source = new ProgramShortcutSource([Path.Combine(_desktop, "Mine"), Path.Combine(_desktop, "Everyone")], watch: false);

        var application = Assert.Single(await source.GetApplicationsAsync(CancellationToken.None));

        Assert.Equal(mine, application.Id);
    }

    [Fact]
    public async Task TheRealDesktopIsReadWithoutFailingAndEveryEntryStartsFromItsOwnFile()
    {
        // This PC's own Desktop: nothing is started, only what is there is read. Its names are not written anywhere.
        using var source = new ProgramShortcutSource(ProgramShortcutSource.DesktopFolders(), watch: false);
        var clock = Stopwatch.StartNew();

        var applications = await source.GetApplicationsAsync(CancellationToken.None);

        Assert.All(applications, application =>
        {
            Assert.False(string.IsNullOrWhiteSpace(application.DisplayName));
            Assert.True(ApplicationLaunchId.IsStartableFile(application.Id));
            Assert.DoesNotContain("uninstall", application.DisplayName, StringComparison.OrdinalIgnoreCase);
            if (application.ExecutablePath is { } program)
            {
                Assert.True(ApplicationLaunchId.IsProgram(program));
            }
        });
        Assert.Equal(
            applications.Count, applications.Select(application => application.DisplayName).Distinct(StringComparer.CurrentCultureIgnoreCase).Count());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "Reading the Desktop took far too long.");
    }

    [Fact]
    public void AnApplicationListedByItsOwnFileIsStartedByOpeningThatFile()
    {
        var game = Write("Fortnite.url", Link("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true"));
        var program = Write("Tool.exe");
        var started = new List<ProcessStartInfo>();
        var launcher = new ShellApplicationLauncher(info =>
        {
            started.Add(info);
            return true;
        });

        // The link is opened itself, as PowerToys Run opens it: the shell hands it to the launcher it belongs to.
        Assert.True(launcher.Launch(game));
        Assert.Equal(game, started[0].FileName);
        Assert.True(started[0].UseShellExecute);

        // A program is started in its own folder, as a shortcut to it would start it.
        Assert.True(launcher.Launch(program));
        Assert.Equal((program, _desktop), (started[1].FileName, started[1].WorkingDirectory));

        // Anything else is still an entry of the apps folder: a file that is not there, a document, an identity that is not a path.
        Assert.True(launcher.Launch(Path.Combine(_desktop, "gone.url")));
        Assert.StartsWith("shell:AppsFolder\\", started[2].FileName, StringComparison.Ordinal);
        Assert.True(launcher.Launch("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"));
        Assert.Equal("shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", started[3].FileName);
        Assert.False(ApplicationLaunchId.IsStartableFile(Write("Notes.txt")));
        Assert.False(ApplicationLaunchId.IsStartableFile("tool.exe"));
        Assert.False(ApplicationLaunchId.IsStartableFile(null));
    }

    [Fact]
    public async Task AShortcutFileHasAnIconOfItsOwn()
    {
        // A real program of Windows, listed by its file as the Desktop's are: the shell draws its icon from the file.
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(notepad))
        {
            return;
        }

        var icon = await new ShellApplicationIconSource().GetIconAsync(notepad, CancellationToken.None);

        Assert.NotNull(icon);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], icon![..4]);
    }
}
