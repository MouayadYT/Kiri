using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Actions;
using Xunit;

namespace Assistant.Core.Tests;

public sealed class QuickActionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static ActionsQuickSearchProvider Provider(
        IQuickActionExecutor? executor = null, IPermissionPolicy? permissions = null, IQuickSearchUsage? usage = null,
        IQuickActionCatalog? catalog = null) =>
        new(catalog ?? QuickActionCatalog.Default, executor ?? new EveryActionRunner(), permissions ?? new AllowAll(), usage, new TestClock(Now));

    private static async Task<IReadOnlyList<QuickSearchResult>> Search(
        ActionsQuickSearchProvider provider, string query, int max = QuickSearchRequest.HardMaxResults) =>
        await provider.SearchAsync(new QuickSearchRequest(query) { MaxResults = max }, CancellationToken.None);

    private static IEnumerable<string> Ids(IEnumerable<QuickSearchResult> results) =>
        results.Select(result => result.Primary.Target);

    [Fact]
    public void TheCatalogDefinesEveryActionTheStepNamesAndWiresTheSafeOnes()
    {
        var catalog = QuickActionCatalog.Default;

        foreach (var id in new[]
        {
            QuickActionIds.OpenAssistantSettings, QuickActionIds.OpenWindowsSettings, QuickActionIds.TakeScreenshot,
            QuickActionIds.NewConversation, QuickActionIds.ShowHistory, QuickActionIds.Mute, QuickActionIds.Unmute,
            QuickActionIds.VolumeUp, QuickActionIds.VolumeDown, QuickActionIds.SetVolume, QuickActionIds.LockPc,
            QuickActionIds.OpenHome, QuickActionIds.OpenDesktop, QuickActionIds.OpenDocuments, QuickActionIds.OpenDownloads,
            QuickActionIds.OpenPictures, QuickActionIds.OpenMusic, QuickActionIds.OpenVideos,
        })
        {
            var definition = Assert.IsType<QuickActionDefinition>(catalog.Find(id));
            Assert.Equal(QuickActionAvailability.Available, definition.Availability);
            Assert.True(definition.IsSafeToRun);
            Assert.NotEqual(RiskLevel.Destructive, definition.Risk);
            Assert.False(string.IsNullOrWhiteSpace(definition.Title));
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
        }

        Assert.Equal(catalog.All.Count, catalog.All.Select(definition => definition.Id).Distinct().Count());
    }

    [Fact]
    public void WhatCanLoseTheUsersWorkIsDefinedAndRefusedAndNeverRunnable()
    {
        var catalog = QuickActionCatalog.Default;

        foreach (var id in new[] { QuickActionIds.ShutDown, QuickActionIds.Restart, QuickActionIds.SignOut, QuickActionIds.EmptyRecycleBin })
        {
            var definition = Assert.IsType<QuickActionDefinition>(catalog.Find(id));
            Assert.Equal(QuickActionAvailability.Refused, definition.Availability);
            Assert.Equal(RiskLevel.Destructive, definition.Risk);
            Assert.False(definition.IsSafeToRun);
            Assert.False(string.IsNullOrWhiteSpace(definition.Note));
        }

        var sleep = catalog.Find(QuickActionIds.Sleep)!;
        Assert.Equal(QuickActionAvailability.Planned, sleep.Availability);
        Assert.False(sleep.IsSafeToRun);
        Assert.All(catalog.All.Where(definition => definition.Risk == RiskLevel.Destructive),
            definition => Assert.Equal(QuickActionAvailability.Refused, definition.Availability));
    }

    [Fact]
    public void ACatalogCannotBeMadeWithADestructiveActionThatIsNotRefusedOrWithTwoOfOneId()
    {
        var destructive = new QuickActionDefinition("x", "X", "x", QuickActionCategory.Session, RiskLevel.Destructive, QuickActionAvailability.Available);
        var fine = new QuickActionDefinition("y", "Y", "y", QuickActionCategory.Session, RiskLevel.SideEffect, QuickActionAvailability.Available);

        Assert.Throws<ArgumentException>(() => new QuickActionCatalog([destructive]));
        Assert.Throws<ArgumentException>(() => new QuickActionCatalog([fine, fine]));
        Assert.Throws<ArgumentException>(() => new QuickActionCatalog([fine with { Id = " " }]));
        Assert.Null(new QuickActionCatalog([fine]).Find("nothing"));
        Assert.Same(fine, new QuickActionCatalog([fine]).Find("y"));
    }

    [Fact]
    public async Task TypedWordsFindTheActionsAndTheyRunByTheirId()
    {
        var provider = Provider();

        var mute = await Search(provider, "mute");
        Assert.Equal([QuickActionIds.Mute, QuickActionIds.Unmute], Ids(mute));
        var first = mute[0];
        Assert.Equal("Mute", first.Title);
        Assert.Equal("Turn the sound off", first.Subtitle);
        Assert.Equal("action:sound.mute", first.Id);
        Assert.Equal(QuickSearchResultType.Actions, first.ResultType);
        Assert.Equal(new QuickSearchAction(QuickSearchActionKind.RunAction, "Run", QuickActionIds.Mute), first.Primary);
        Assert.Equal(QuickSearchIconKind.Action, first.Icon.Kind);
        Assert.Empty(first.Alternates);

        Assert.Equal([QuickActionIds.LockPc], Ids(await Search(provider, "lock")));
        Assert.Equal(QuickActionIds.ShowHistory, Ids(await Search(provider, "history")).First());
        Assert.Contains(QuickActionIds.NewConversation, Ids(await Search(provider, "new chat")));
        Assert.Contains(QuickActionIds.OpenWindowsSettings, Ids(await Search(provider, "windows settings")));
        Assert.Contains(QuickActionIds.OpenAssistantSettings, Ids(await Search(provider, "settings")));
        Assert.Contains(QuickActionIds.TakeScreenshot, Ids(await Search(provider, "screenshot")));
        Assert.Empty(await Search(provider, "zzzz"));
    }

    [Fact]
    public async Task FoldersAreOpenedWithAFolderIcon()
    {
        var results = await Search(Provider(), "downloads");

        var downloads = Assert.Single(results);
        Assert.Equal(QuickActionIds.OpenDownloads, downloads.Primary.Target);
        Assert.Equal(QuickSearchIconKind.Folder, downloads.Icon.Kind);
    }

    [Fact]
    public async Task WhatIsPlannedOrRefusedIsNeverListedEvenWhenAnExecutorWouldRunAnything()
    {
        var provider = Provider(new EveryActionRunner());

        foreach (var query in new[] { "shut down", "shutdown", "restart", "sign out", "empty recycle bin", "recycle", "sleep" })
        {
            Assert.Empty(await Search(provider, query));
        }

        var all = Ids(await Search(provider, ""));
        Assert.DoesNotContain(QuickActionIds.ShutDown, all);
        Assert.DoesNotContain(QuickActionIds.Restart, all);
        Assert.DoesNotContain(QuickActionIds.SignOut, all);
        Assert.DoesNotContain(QuickActionIds.EmptyRecycleBin, all);
        Assert.DoesNotContain(QuickActionIds.Sleep, all);
    }

    [Fact]
    public async Task OnlyAnActionSomethingCanRunIsListed()
    {
        var provider = Provider(new SomeActionsRunner(QuickActionIds.Mute, QuickActionIds.LockPc));

        Assert.Equal(
            new[] { QuickActionIds.Mute, QuickActionIds.LockPc }.Order(StringComparer.Ordinal),
            Ids(await Search(provider, "")).Order(StringComparer.Ordinal));
        Assert.Empty(await Search(provider, "history"));
        Assert.Equal([QuickActionIds.Mute], Ids(await Search(provider, "mute")));
    }

    [Fact]
    public async Task AnActionWhosePermissionIsOffIsNotListed()
    {
        var off = new AllowAll { Denied = [PermissionCapability.ScreenCapture, PermissionCapability.ClipboardHistory] };
        var provider = Provider(permissions: off);

        Assert.Empty(await Search(provider, "screenshot"));
        Assert.Empty(await Search(provider, "clear clipboard history"));
        Assert.Contains(QuickActionIds.NewConversation, Ids(await Search(provider, "")));

        // Without a policy to ask, an action that needs a permission is not listed.
        var noPolicy = new ActionsQuickSearchProvider(QuickActionCatalog.Default, new EveryActionRunner());
        Assert.Empty(await Search(noPolicy, "screenshot"));
        Assert.Contains(QuickActionIds.ShowHistory, Ids(await Search(noPolicy, "history")));

        // With the permission on, it is listed.
        Assert.Contains(QuickActionIds.TakeScreenshot, Ids(await Search(Provider(), "screenshot")));
        Assert.Contains(QuickActionIds.ClearClipboardHistory, Ids(await Search(Provider(), "clipboard")));
    }

    [Theory]
    [InlineData("volume 30", 30)]
    [InlineData("Volume 30%", 30)]
    [InlineData("set volume to 45", 45)]
    [InlineData("set the volume to 0", 0)]
    [InlineData("vol 100", 100)]
    [InlineData("30 volume", 30)]
    [InlineData("volume at 7 percent", 7)]
    [InlineData("turn volume to 12", 12)]
    public void AVolumeWordWithANumberIsAVolumeLevel(string typed, int expected) =>
        Assert.Equal(expected, ActionsQuickSearchProvider.ParseVolume(typed));

    [Theory]
    [InlineData("volume")]
    [InlineData("volume up")]
    [InlineData("volume 150")]
    [InlineData("volume 30 40")]
    [InlineData("volume of a sphere 30")]
    [InlineData("30")]
    [InlineData("volume 3000")]
    [InlineData("volume volume 30")]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingElseIsAVolumeLevel(string typed) => Assert.Null(ActionsQuickSearchProvider.ParseVolume(typed));

    [Fact]
    public async Task ThePercentIsGivenToTheActionAndOnlyThenIsSetVolumeListed()
    {
        var provider = Provider();

        var set = await Search(provider, "volume 30");

        var level = Assert.Single(set, result => result.Primary.Target == QuickActionIds.SetVolume);
        Assert.Equal("Set volume to 30%", level.Title);
        Assert.Equal("30", level.Primary.Argument);
        Assert.Equal(QuickSearchActionKind.RunAction, level.Primary.Kind);
        Assert.Equal(level, set[0]);

        // Without a number there is nothing to set, so the action is not listed; its relatives still are.
        var plain = Ids(await Search(provider, "volume"));
        Assert.DoesNotContain(QuickActionIds.SetVolume, plain);
        Assert.Contains(QuickActionIds.VolumeUp, plain);
        Assert.Contains(QuickActionIds.VolumeDown, plain);
        Assert.DoesNotContain(QuickActionIds.SetVolume, Ids(await Search(provider, "")));
    }

    [Fact]
    public async Task WithNothingTypedEveryAvailableActionIsListedAndWhatIsRunIsFirst()
    {
        var usage = new QuickSearchUsage(new TestClock(Now));
        usage.RecordUse("action:" + QuickActionIds.LockPc);
        var provider = Provider(usage: usage);

        var all = await Search(provider, "");

        Assert.Equal(QuickActionIds.LockPc, all[0].Primary.Target);
        var expected = QuickActionCatalog.Default.All
            .Where(definition => definition.IsSafeToRun && definition.Parameter is null)
            .Select(definition => definition.Id)
            .Order(StringComparer.Ordinal);
        Assert.Equal(expected, Ids(all).Order(StringComparer.Ordinal));
        Assert.Equal(4, (await Search(provider, "", max: 4)).Count);
    }

    [Fact]
    public async Task AKeywordFindsAnActionBelowATitleThatMatches()
    {
        var provider = Provider();

        // "silence" is a keyword of Mute only.
        var results = await Search(provider, "silence");

        Assert.Equal(QuickActionIds.Mute, results[0].Primary.Target);
    }

    [Fact]
    public void ANullExecutorOrCatalogIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new ActionsQuickSearchProvider(null!, new EveryActionRunner()));
        Assert.Throws<ArgumentNullException>(() => new ActionsQuickSearchProvider(QuickActionCatalog.Default, null!));
    }

    private sealed class EveryActionRunner : IQuickActionExecutor
    {
        public bool CanRun(string actionId) => true;

        public Task<QuickActionOutcome> RunAsync(string actionId, string? argument, CancellationToken cancellationToken = default) =>
            Task.FromResult(QuickActionOutcome.Done);
    }

    private sealed class SomeActionsRunner(params string[] ids) : IQuickActionExecutor
    {
        public bool CanRun(string actionId) => ids.Contains(actionId);

        public Task<QuickActionOutcome> RunAsync(string actionId, string? argument, CancellationToken cancellationToken = default) =>
            Task.FromResult(QuickActionOutcome.Done);
    }

    private sealed class AllowAll : IPermissionPolicy
    {
        public HashSet<PermissionCapability> Denied { get; init; } = [];

        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PermissionDecision(
                capability, Denied.Contains(capability) ? PermissionDecisionReason.TurnedOff : PermissionDecisionReason.Granted));
    }
}
