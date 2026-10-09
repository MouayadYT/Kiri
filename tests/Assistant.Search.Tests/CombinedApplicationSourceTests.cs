using Assistant.Core.QuickSearch;
using Assistant.Search.Applications;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>What Start lists and what PowerToys Run would also find, read as one list (PROJECT_SPEC §4.1).</summary>
public sealed class CombinedApplicationSourceTests
{
    private static InstalledApplication App(string id, string name, string? program = null) => new(id, name) { ExecutablePath = program };

    [Fact]
    public async Task TheListsAreOneListInNameOrderWithEachApplicationOnce()
    {
        var start = new FakeApplicationSource
        {
            Applications =
            [
                App("Valve.Steam", "Steam", @"C:\Program Files (x86)\Steam\steam.exe"),
                App("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Calculator"),
                App("{6D809377-6AF0-444B-8957-A3773F02200E}\\Tool\\tool.exe", "Tool", @"C:\Program Files\Tool\tool.exe"),

                // Start may list two entries under one name; both stay.
                App("help-1", "Help"), App("help-2", "Help"),
            ],
        };
        var desktop = new FakeApplicationSource
        {
            Applications =
            [
                // Only on the Desktop: a game a launcher installed.
                App(@"C:\Users\someone\Desktop\Fortnite.url", "Fortnite"),

                // Also in Start: by its name, by its program file under another name, and by the very same identity.
                App(@"C:\Users\Public\Desktop\Steam.lnk", "steam", @"C:\Program Files (x86)\Steam\steam.exe"),
                App(@"C:\Users\someone\Desktop\My tool.lnk", "My tool", @"c:\program files\tool\TOOL.EXE"),
                App("MICROSOFT.WINDOWSCALCULATOR_8WEKYB3D8BBWE!APP", "Calc"),
            ],
        };
        var source = new CombinedApplicationSource(start, desktop);

        var applications = await source.GetApplicationsAsync(CancellationToken.None);

        Assert.Equal(["Calculator", "Fortnite", "Help", "Help", "Steam", "Tool"], applications.Select(application => application.DisplayName));

        // What Start gave stands for both: its identity is the one that is started.
        Assert.Equal("Valve.Steam", applications.Single(application => application.DisplayName == "Steam").Id);
        Assert.Equal(@"C:\Users\someone\Desktop\Fortnite.url", applications.Single(application => application.DisplayName == "Fortnite").Id);

        // But it is drawn with the icon of the shortcut on the Desktop, which is where a person gives an application an icon of their own: by its name,
        // and by its program file. What is only in Start, only on the Desktop, or is not a shortcut, has its own icon.
        Assert.Equal(@"C:\Users\Public\Desktop\Steam.lnk", applications.Single(application => application.DisplayName == "Steam").IconPath);
        Assert.Equal(@"C:\Users\someone\Desktop\My tool.lnk", applications.Single(application => application.DisplayName == "Tool").IconPath);
        Assert.Null(applications.Single(application => application.DisplayName == "Calculator").IconPath);
        Assert.Null(applications.Single(application => application.DisplayName == "Fortnite").IconPath);
        Assert.All(applications.Where(application => application.DisplayName == "Help"), application => Assert.Null(application.IconPath));
    }

    [Fact]
    public async Task ASecondSourceThatCannotBeReadAddsNothingAndTheFirstFailingFailsTheReading()
    {
        var start = new FakeApplicationSource { Applications = [App("a", "Alpha")] };
        var desktop = new FakeApplicationSource { Applications = [App("b", "Beta")], Fails = true };
        var source = new CombinedApplicationSource(start, desktop);

        Assert.Equal(["Alpha"], (await source.GetApplicationsAsync(CancellationToken.None)).Select(application => application.DisplayName));

        // Without Start's list there is no list: the reading fails, so that the catalog keeps what it had and tries again.
        (start.Fails, desktop.Fails) = (true, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetApplicationsAsync(CancellationToken.None));

        // Cancelling is never taken for a source that could not be read.
        (start.Fails, desktop.Gate) = (false, new TaskCompletionSource());
        using var cancelled = new CancellationTokenSource();
        var reading = source.GetApplicationsAsync(cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }

    [Fact]
    public void AChangeInAnySourceIsAChangeOfTheList()
    {
        var (start, desktop) = (new FakeApplicationSource(), new FakeApplicationSource());
        var source = new CombinedApplicationSource(start, desktop);
        var changes = 0;
        source.Changed += (_, _) => changes++;

        start.RaiseChanged();
        desktop.RaiseChanged();

        Assert.Equal(2, changes);
        Assert.Throws<ArgumentException>(() => new CombinedApplicationSource());
    }

    [Fact]
    public async Task TheCatalogReadsBothThroughIt()
    {
        var start = new FakeApplicationSource { Applications = [App("word", "Microsoft Word")] };
        var desktop = new FakeApplicationSource { Applications = [App(@"C:\Users\someone\Desktop\Fortnite.url", "Fortnite")] };
        using var catalog = new ApplicationCatalog(new CombinedApplicationSource(start, desktop));

        var applications = await catalog.GetApplicationsAsync(CancellationToken.None);

        Assert.Equal(["Fortnite", "Microsoft Word"], applications.Select(application => application.DisplayName));

        // Something new on the Desktop makes the catalog read again, as something new in Start does.
        desktop.Applications = [.. desktop.Applications, App(@"C:\Users\someone\Desktop\Rocket League.url", "Rocket League")];
        desktop.RaiseChanged();
        await catalog.GetApplicationsAsync(CancellationToken.None);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (catalog.Current.Count != 3)
        {
            Assert.True(DateTime.UtcNow < until, "The catalog did not read the Desktop's change.");
            await Task.Delay(5);
        }

        Assert.Contains(catalog.Current, application => application.DisplayName == "Rocket League");
    }
}
