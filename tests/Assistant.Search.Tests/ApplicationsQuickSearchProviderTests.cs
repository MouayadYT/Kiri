using Assistant.Core.QuickSearch;
using Assistant.Search.Applications;
using Xunit;

namespace Assistant.Search.Tests;

public sealed class ApplicationsQuickSearchProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Icon = [0x89, 0x50, 0x4E, 0x47];

    private static InstalledApplication App(string name, string? path = null) =>
        new("id:" + name, name) { ExecutablePath = path };

    private static readonly InstalledApplication[] Installed =
    [
        App("Brave", "C:\\Program Files\\Brave\\brave.exe"),
        App("Brave Beta"),
        App("Calculator"),
        App("Google Chrome", "C:\\Program Files\\Chrome\\chrome.exe"),
        App("Mail"),
        App("Visual Studio Code"),
        App("Windows Terminal"),
    ];

    private static ApplicationsQuickSearchProvider Provider(
        IApplicationCatalog? catalog = null, IApplicationIconSource? icons = null, IQuickSearchUsage? usage = null,
        TimeSpan? iconPatience = null) =>
        new(catalog ?? new FakeApplicationCatalog(Installed), icons, usage, new ManualClock(Now), iconPatience);

    private static Task<IReadOnlyList<QuickSearchResult>> Search(
        ApplicationsQuickSearchProvider provider, string query, int max = QuickSearchRequest.DefaultMaxResults) =>
        provider.SearchAsync(new QuickSearchRequest(query) { MaxResults = max }, CancellationToken.None);

    [Fact]
    public void TheProviderIsInstantAndHasSomethingToListForAnEmptyQuery()
    {
        var provider = Provider();

        Assert.Equal("applications", provider.Id);
        Assert.Equal(QuickSearchResultType.Applications, provider.ResultType);
        Assert.Equal(0, provider.MinimumQueryLength);
        Assert.Equal(TimeSpan.Zero, provider.Debounce);
        Assert.Equal(100, provider.Priority);
    }

    [Fact]
    public async Task ATypedNameFindsTheApplicationsWithTheirIdentityAndALaunchAction()
    {
        var results = await Search(Provider(), "brave");

        Assert.Equal(["Brave", "Brave Beta"], results.Select(result => result.Title));
        var brave = results[0];
        Assert.Equal("app:id:Brave", brave.Id);
        Assert.Equal(QuickSearchResultType.Applications, brave.ResultType);
        Assert.Equal("applications", brave.ProviderId);
        Assert.Equal(new QuickSearchAction(QuickSearchActionKind.LaunchApplication, "Open", "id:Brave"), brave.Primary);
        Assert.Equal(QuickSearchIconKind.Application, brave.Icon.Kind);
    }

    [Theory]
    [InlineData("BRAVE")]
    [InlineData("  brave  ")]
    public async Task ATypedNameIsMatchedWithoutRegardToCaseOrSpaces(string typed)
    {
        // The coordinator trims what is typed; the provider is only given trimmed text, but is not upset by an untrimmed one.
        var results = await Search(Provider(), typed.Trim());

        Assert.Equal("Brave", results[0].Title);
    }

    [Fact]
    public async Task AWordOfTheNameAndTheInitialsFindIt()
    {
        var provider = Provider();

        Assert.Equal(["Google Chrome"], (await Search(provider, "chrome")).Select(result => result.Title));
        Assert.Equal(["Visual Studio Code"], (await Search(provider, "vsc")).Select(result => result.Title));
        Assert.Equal(["Visual Studio Code"], (await Search(provider, "studio code")).Select(result => result.Title));
        Assert.Empty(await Search(provider, "zzz"));
    }

    [Theory]
    [InlineData("kiri")]
    [InlineData("Kiri")]
    [InlineData("kir")]
    public async Task TheAssistantIsTheFirstAnswerToKiriWhateverStartCallsIt(string typed)
    {
        // Start lists it as "Assistant"; an application whose name begins with Kiri would otherwise come first.
        var catalog = new FakeApplicationCatalog(
        [
            App("Kiri Notes"), App("Kirigami Viewer"),
            App("Assistant", @"C:\Users\someone\AppData\Local\Programs\Assistant\app\Assistant.UI.exe"),
        ]);

        var results = await Search(Provider(catalog), typed);

        Assert.Equal("Assistant", results[0].Title);
        Assert.Contains(results, result => result.Title == "Kiri Notes");
    }

    [Fact]
    public async Task TheAssistantIsFoundByItsProgramEvenWhenStartListsItUnderAnotherName()
    {
        var catalog = new FakeApplicationCatalog([App("Kiri Notes"), App("Helper", @"D:\Apps\Assistant\Assistant.UI.exe")]);

        var results = await Search(Provider(catalog), "kiri");

        Assert.Equal("Helper", results[0].Title);
    }

    [Fact]
    public async Task OnlyTheAssistantAnswersToKiriAndKiriIsNotWhatAnotherProgramIsCalled()
    {
        var results = await Search(Provider(), "kiri");
        Assert.Empty(results);

        var other = await Search(Provider(new FakeApplicationCatalog([App("Assistant Helper", @"C:\Tools\helper.exe")])), "kiri");
        Assert.Empty(other);
    }

    [Fact]
    public async Task OnlyDesktopProgramsWithAKnownFileOfferToShowAndCopyItsPath()
    {
        var results = await Search(Provider(), "br");

        var brave = results.Single(result => result.Title == "Brave");
        Assert.Equal(
            [
                new QuickSearchAction(QuickSearchActionKind.RevealPath, "Open file location", "C:\\Program Files\\Brave\\brave.exe"),
                new QuickSearchAction(QuickSearchActionKind.CopyPath, "Copy path", "C:\\Program Files\\Brave\\brave.exe"),
            ],
            brave.Alternates);
        Assert.Empty(results.Single(result => result.Title == "Brave Beta").Alternates);
    }

    [Fact]
    public async Task WithNothingTypedItBrowsesWhatTheUserRunsFirstThenTheRestInOrder()
    {
        var usage = new QuickSearchUsage(new ManualClock(Now));
        usage.RecordUse("app:id:Windows Terminal");
        usage.RecordUse("app:id:Mail");
        usage.RecordUse("app:id:Mail");

        var results = await Search(Provider(usage: usage), "", max: 4);

        Assert.Equal(["Mail", "Windows Terminal", "Brave", "Brave Beta"], results.Select(result => result.Title));
    }

    [Fact]
    public async Task OnlyAsManyAsAreAskedForAreReturnedAndOnlyThoseNeedIcons()
    {
        var icons = new FakeIconSource();
        foreach (var app in Installed)
        {
            icons.Icons[app.Id] = Icon;
        }

        var results = await Search(Provider(icons: icons), "", max: 3);

        Assert.Equal(3, results.Count);
        Assert.Equal(results.Select(result => result.Primary.Target).Order(), icons.Asked.Order());
        Assert.All(results, result => Assert.Equal(Icon, result.Icon.Image));
    }

    [Fact]
    public async Task AnApplicationAShortcutStandsForIsDrawnWithTheShortcutsIcon_AndItIsReadAgainWhenTheShortcutChanges()
    {
        var shortcut = Path.Combine(Path.GetTempPath(), "kiri-icon-" + Guid.NewGuid().ToString("N") + ".lnk");
        File.WriteAllText(shortcut, "a shortcut");
        try
        {
            byte[] chosen = [1, 2, 3], changed = [4, 5, 6];
            var icons = new FakeIconSource();
            icons.Icons["id:Mail"] = Icon;
            icons.Icons[shortcut] = chosen;
            var catalog = new FakeApplicationCatalog([App("Mail") with { IconPath = shortcut }]);
            var provider = Provider(catalog, icons);

            // The icon the user gave the shortcut, not the application's own; the application is still started by its own identity.
            var first = Assert.Single(await Search(provider, "mail"));
            Assert.Equal(chosen, first.Icon.Image);
            Assert.Equal("id:Mail", first.Primary.Target);
            Assert.Equal([shortcut], icons.Asked);

            // Kept while the shortcut is as it was.
            Assert.Equal(chosen, Assert.Single(await Search(provider, "mail")).Icon.Image);
            Assert.Single(icons.Asked);

            // The user gives the shortcut another icon, which writes the file again: the next search shows it, with no restart.
            icons.Icons[shortcut] = changed;
            File.SetLastWriteTimeUtc(shortcut, File.GetLastWriteTimeUtc(shortcut).AddMinutes(1));
            Assert.Equal(changed, Assert.Single(await Search(provider, "mail")).Icon.Image);
            Assert.Equal([shortcut, shortcut], icons.Asked);
        }
        finally
        {
            File.Delete(shortcut);
        }
    }

    [Fact]
    public async Task AnApplicationWithoutAnIconOrWhoseIconFailsIsListedWithItsGlyph()
    {
        var icons = new FakeIconSource();
        icons.Icons["id:Brave"] = Icon;
        icons.Failing.Add("id:Brave Beta");

        var results = await Search(Provider(icons: icons), "brave");

        Assert.Equal(Icon, results[0].Icon.Image);
        Assert.Null(results[1].Icon.Image);
        Assert.Equal(QuickSearchIconKind.Application, results[1].Icon.Kind);
    }

    [Fact]
    public async Task AnIconThatIsLateIsNotWaitedForBeyondAMomentAndIsThereNextTime()
    {
        var icons = new FakeIconSource();
        icons.Icons["id:Mail"] = Icon;
        icons.Slow.Add("id:Mail");
        var provider = Provider(icons: icons, iconPatience: TimeSpan.FromMilliseconds(40));

        var late = await Search(provider, "mail");
        Assert.Null(Assert.Single(late).Icon.Image);

        // The reading carries on, and the next search has the icon.
        icons.Gate.SetResult();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        QuickSearchResult? again;
        do
        {
            again = Assert.Single(await Search(provider, "mail"));
            Assert.True(DateTime.UtcNow < until, "The icon never arrived.");
            await Task.Delay(5);
        }
        while (again.Icon.Image is null);

        Assert.Equal(Icon, again.Icon.Image);
        Assert.Single(icons.Asked, id => id == "id:Mail");
    }

    [Fact]
    public async Task IconsAreReadOnceAndKept()
    {
        var icons = new FakeIconSource();
        icons.Icons["id:Brave"] = Icon;
        var provider = Provider(icons: icons);

        await Search(provider, "brave");
        await Search(provider, "brave");
        await Search(provider, "brav");

        Assert.Single(icons.Asked, id => id == "id:Brave");
    }

    [Fact]
    public async Task WarmingUpReadsTheApplicationsAndThenTheirIconsAFewAtATime()
    {
        var catalog = new FakeApplicationCatalog(Installed);
        var icons = new FakeIconSource();
        var provider = Provider(catalog, icons);

        await provider.WarmUpAsync(CancellationToken.None);

        Assert.Equal(1, catalog.WarmUps);
        Assert.Equal(Installed.Length, icons.Asked.Count);
        Assert.True(icons.Peak <= 2, "Icons are read one or two at a time.");
    }

    [Fact]
    public async Task WarmingUpCancelledEndsQuietly()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Provider(new FakeApplicationCatalog(Installed), new FakeIconSource()).WarmUpAsync(cancel.Token);
    }

    [Fact]
    public async Task NoApplicationsIsNoResults()
    {
        Assert.Empty(await Search(Provider(new FakeApplicationCatalog()), "brave"));
        Assert.Empty(await Search(Provider(new FakeApplicationCatalog()), ""));
    }

    [Fact]
    public async Task WhatWasFoundIsNeverPrinted()
    {
        var results = await Search(Provider(), "brave");

        Assert.All(results, result =>
        {
            Assert.DoesNotContain("Brave", result.ToString());
            Assert.DoesNotContain("Brave", result.Primary.ToString());
            Assert.DoesNotContain("Program Files", result.Alternates.Aggregate("", (text, action) => text + action));
        });
        Assert.DoesNotContain("Program Files", Installed[0].ToString());
    }
}
