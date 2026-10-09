using Assistant.Core.QuickSearch;
using Assistant.Search.Applications;
using Xunit;

namespace Assistant.Search.Tests;

public sealed class ApplicationCatalogTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static InstalledApplication App(string name) => new("id-" + name, name);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition was not met in time.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task TheFirstLookupWaitsForTheReadingAndLaterOnesAnswerFromMemory()
    {
        var source = new FakeApplicationSource { Applications = [App("Brave"), App("Mail")] };
        using var catalog = new ApplicationCatalog(source, new ManualClock(Start));
        Assert.False(catalog.IsLoaded);
        Assert.Empty(catalog.Current);

        var first = await catalog.GetApplicationsAsync(CancellationToken.None).WaitAsync(Patience);
        var second = await catalog.GetApplicationsAsync(CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(["Brave", "Mail"], first.Select(app => app.DisplayName));
        Assert.Same(first, second);
        Assert.True(catalog.IsLoaded);
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task LookupsThatCometogetherShareOneReading()
    {
        var gate = new TaskCompletionSource();
        var source = new FakeApplicationSource { Applications = [App("Brave")], Gate = gate };
        using var catalog = new ApplicationCatalog(source, new ManualClock(Start));

        var lookups = Enumerable.Range(0, 5).Select(_ => catalog.GetApplicationsAsync(CancellationToken.None)).ToArray();
        await WaitUntilAsync(() => source.Reads >= 1);
        gate.SetResult();
        var answers = await Task.WhenAll(lookups).WaitAsync(Patience);

        Assert.All(answers, answer => Assert.Single(answer));
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task WarmingUpReadsInTheBackgroundSoTheFirstSearchDoesNotWait()
    {
        var source = new FakeApplicationSource { Applications = [App("Brave")] };
        using var catalog = new ApplicationCatalog(source, new ManualClock(Start));

        catalog.WarmUp();
        catalog.WarmUp();
        await WaitUntilAsync(() => catalog.IsLoaded);

        Assert.Equal(1, source.Reads);
        Assert.Single(catalog.Current);
        Assert.Single(await catalog.GetApplicationsAsync(CancellationToken.None));
        Assert.Equal(1, source.Reads);

        // A catalog that has its list is not warmed up again.
        catalog.WarmUp();
        await Task.Delay(30);
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task AStaleListKeepsAnsweringWhileItIsReadAgainInTheBackground()
    {
        var clock = new ManualClock(Start);
        var source = new FakeApplicationSource { Applications = [App("Brave")] };
        using var catalog = new ApplicationCatalog(source, clock, maxAge: TimeSpan.FromMinutes(10));
        var changes = 0;
        catalog.Changed += (_, _) => Interlocked.Increment(ref changes);
        await catalog.GetApplicationsAsync(CancellationToken.None);
        Assert.Equal(1, changes);

        // Still fresh: nothing is read.
        clock.Advance(TimeSpan.FromMinutes(9));
        await catalog.GetApplicationsAsync(CancellationToken.None);
        Assert.Equal(1, source.Reads);

        // Grown old: the old list is the answer, at once, while a new one is read.
        var gate = new TaskCompletionSource();
        source.Gate = gate;
        source.Applications = [App("Brave"), App("Mail")];
        clock.Advance(TimeSpan.FromMinutes(2));
        var old = await catalog.GetApplicationsAsync(CancellationToken.None).WaitAsync(Patience);
        Assert.Single(old);
        await WaitUntilAsync(() => source.Reads == 2);
        Assert.Single(catalog.Current);

        gate.SetResult();
        await WaitUntilAsync(() => catalog.Current.Count == 2);
        Assert.Equal(2, changes);

        // A reading that finds the same applications changes nothing, and says nothing.
        source.Gate = null;
        clock.Advance(TimeSpan.FromMinutes(11));
        await catalog.GetApplicationsAsync(CancellationToken.None);
        await WaitUntilAsync(() => source.Reads == 3);
        await Task.Delay(30);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task SomethingInstalledOrRemovedMakesTheNextLookupReadAgain()
    {
        var source = new FakeApplicationSource { Applications = [App("Brave")] };
        using var catalog = new ApplicationCatalog(source, new ManualClock(Start));
        await catalog.GetApplicationsAsync(CancellationToken.None);

        // The PC says something changed; the next lookup still answers at once, and starts the reading.
        source.Applications = [App("Brave"), App("Notepad")];
        source.RaiseChanged();
        Assert.Single(await catalog.GetApplicationsAsync(CancellationToken.None));
        await WaitUntilAsync(() => catalog.Current.Count == 2);
        Assert.Equal(2, source.Reads);

        // It says so once: a lookup after that reads nothing.
        await catalog.GetApplicationsAsync(CancellationToken.None);
        await Task.Delay(30);
        Assert.Equal(2, source.Reads);

        source.Applications = [App("Brave")];
        catalog.Invalidate();
        await catalog.GetApplicationsAsync(CancellationToken.None);
        await WaitUntilAsync(() => catalog.Current.Count == 1);
    }

    [Fact]
    public async Task AReadingThatFailsKeepsWhatWasReadAndIsTriedAgainOnlyAfterAWhile()
    {
        var clock = new ManualClock(Start);
        var source = new FakeApplicationSource { Fails = true };
        using var catalog = new ApplicationCatalog(source, clock, retryAfter: TimeSpan.FromSeconds(30));

        // Nothing was ever read: the answer is nothing, and a second look straight after does not read again.
        Assert.Empty(await catalog.GetApplicationsAsync(CancellationToken.None).WaitAsync(Patience));
        Assert.Empty(await catalog.GetApplicationsAsync(CancellationToken.None).WaitAsync(Patience));
        Assert.Equal(1, source.Reads);
        Assert.False(catalog.IsLoaded);

        clock.Advance(TimeSpan.FromSeconds(31));
        source.Fails = false;
        source.Applications = [App("Brave")];
        Assert.Single(await catalog.GetApplicationsAsync(CancellationToken.None).WaitAsync(Patience));
        Assert.Equal(2, source.Reads);

        // A later reading that fails leaves the list as it was.
        source.Fails = true;
        catalog.Invalidate();
        await catalog.GetApplicationsAsync(CancellationToken.None);
        await WaitUntilAsync(() => source.Reads == 3);
        await Task.Delay(30);
        Assert.Single(catalog.Current);
        Assert.True(catalog.IsLoaded);
    }

    [Fact]
    public async Task ALookupThatIsCancelledWhileWaitingStopsWaitingButTheReadingCarriesOn()
    {
        var gate = new TaskCompletionSource();
        var source = new FakeApplicationSource { Applications = [App("Brave")], Gate = gate };
        using var catalog = new ApplicationCatalog(source, new ManualClock(Start));
        using var cancel = new CancellationTokenSource();

        var lookup = catalog.GetApplicationsAsync(cancel.Token);
        await WaitUntilAsync(() => source.Reads == 1);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);

        gate.SetResult();
        await WaitUntilAsync(() => catalog.IsLoaded);
        Assert.Single(catalog.Current);
        Assert.Equal(1, source.Reads);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.GetApplicationsAsync(cancel.Token));
    }

    [Fact]
    public async Task ADisposedCatalogStopsListeningToTheSource()
    {
        var source = new FakeApplicationSource { Applications = [App("Brave")] };
        var catalog = new ApplicationCatalog(source, new ManualClock(Start));
        await catalog.GetApplicationsAsync(CancellationToken.None);
        Assert.True(source.HasSubscribers);

        catalog.Dispose();
        catalog.Dispose();

        Assert.False(source.HasSubscribers);
    }

    [Fact]
    public void ANullSourceIsRefused() => Assert.Throws<ArgumentNullException>(() => new ApplicationCatalog(null!));
}
