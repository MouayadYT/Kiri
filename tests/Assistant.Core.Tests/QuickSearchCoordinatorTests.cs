using Assistant.Core.QuickSearch;
using Xunit;

namespace Assistant.Core.Tests;

public sealed class QuickSearchCoordinatorTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static QuickSearchResult Result(string id, string title = "x") => QuickResults.Make(id, title);

    private static Task<IReadOnlyList<QuickSearchResult>> Found(params QuickSearchResult[] results) =>
        Task.FromResult<IReadOnlyList<QuickSearchResult>>(results);

    [Fact]
    public async Task ProvidersRunAtTheSameTime()
    {
        // Each provider waits until the other has been asked: if they ran one after the other, neither would ever finish.
        var started = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<QuickSearchRequest, CancellationToken, Task<IReadOnlyList<QuickSearchResult>>> answer = async (_, _) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                both.SetResult();
            }

            await both.Task;
            return [];
        };
        var coordinator = new QuickSearchCoordinator(
            [new ScriptedProvider("a") { Answer = answer }, new ScriptedProvider("b") { Answer = answer }]);

        var outcome = await coordinator.SearchAsync(new QuickSearchRequest("brave")).WaitAsync(Patience);

        Assert.Equal(2, started);
        Assert.All(outcome.Providers, provider => Assert.Equal(QuickSearchProviderStatus.Completed, provider.Status));
    }

    [Fact]
    public async Task OutcomesComeInTheOrderOfTheProvidersAndProgressInTheOrderTheyEnd()
    {
        var slow = new TaskCompletionSource<IReadOnlyList<QuickSearchResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new ScriptedProvider("slow", QuickSearchResultType.Files) { Answer = (_, _) => slow.Task };
        var second = ScriptedProvider.Returning("fast", QuickSearchResultType.Applications, 50, Result("fast:1"));
        var coordinator = new QuickSearchCoordinator([first, second]);
        var reported = new List<string>();
        var fastReported = new TaskCompletionSource();
        var progress = new SynchronousProgress(outcome =>
        {
            lock (reported)
            {
                reported.Add(outcome.ProviderId);
            }

            if (outcome.ProviderId == "fast")
            {
                fastReported.SetResult();
            }
        });

        var search = coordinator.SearchAsync(new QuickSearchRequest("a"), progress);

        // The fast provider is reported while the slow one is still working: that is what makes the results instant.
        await fastReported.Task.WaitAsync(Patience);
        Assert.False(search.IsCompleted);
        slow.SetResult([Result("slow:1")]);
        var outcome = await search.WaitAsync(Patience);

        Assert.Equal(["fast", "slow"], reported);
        Assert.Equal(["slow", "fast"], outcome.Providers.Select(provider => provider.ProviderId));
        Assert.Equal(["slow:1", "fast:1"], outcome.Results.Select(result => result.Id));
    }

    [Fact]
    public async Task AProviderThatDoesNotWantTheQueryIsNotAsked()
    {
        var files = new ScriptedProvider("files", QuickSearchResultType.Files, minimumLength: 2);
        var apps = new ScriptedProvider("apps", QuickSearchResultType.Applications);
        var coordinator = new QuickSearchCoordinator([files, apps]);

        var outcome = await coordinator.SearchAsync(new QuickSearchRequest("  b  "));

        Assert.Equal(QuickSearchProviderStatus.QueryTooShort, outcome.Providers[0].Status);
        Assert.Empty(files.Requests);
        Assert.Equal(QuickSearchProviderStatus.Completed, outcome.Providers[1].Status);

        // The query reaches a provider trimmed.
        Assert.Equal("b", Assert.Single(apps.Requests).Query);

        // An empty query browses: only a provider with nothing required is asked.
        var browsing = await coordinator.SearchAsync(new QuickSearchRequest(""));
        Assert.Equal(QuickSearchProviderStatus.QueryTooShort, browsing.Providers[0].Status);
        Assert.Equal(QuickSearchProviderStatus.Completed, browsing.Providers[1].Status);
    }

    [Fact]
    public async Task OnlyTheWantedTypesAreAsked()
    {
        var files = new ScriptedProvider("files", QuickSearchResultType.Files);
        var apps = new ScriptedProvider("apps", QuickSearchResultType.Applications);
        var coordinator = new QuickSearchCoordinator([apps, files]);

        var outcome = await coordinator.SearchAsync(
            new QuickSearchRequest("a") { Types = new HashSet<QuickSearchResultType> { QuickSearchResultType.Files } });

        Assert.Equal(QuickSearchProviderStatus.NotWanted, outcome.Providers[0].Status);
        Assert.Empty(apps.Requests);
        Assert.Single(files.Requests);
    }

    [Fact]
    public async Task ATypedQueryWaitsOutTheDebounceAndANewerQueryNeverAsksTheProvider()
    {
        var time = new ManualTime();
        var provider = new ScriptedProvider("files", QuickSearchResultType.Files, debounce: TimeSpan.FromMilliseconds(150));
        var coordinator = new QuickSearchCoordinator([provider], time);

        // A query that is typed over before the pause is out is never put to the provider.
        using (var typedOver = new CancellationTokenSource())
        {
            var stale = coordinator.SearchAsync(new QuickSearchRequest("re"), null, typedOver.Token);
            await WaitUntilAsync(() => time.Pending > 0);
            time.Advance(TimeSpan.FromMilliseconds(100));
            Assert.Empty(provider.Requests);
            await typedOver.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Empty(provider.Requests);
        }

        // The query that is left alone is asked once the pause is out.
        var current = coordinator.SearchAsync(new QuickSearchRequest("rep"));
        await WaitUntilAsync(() => time.Pending > 0);
        time.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Empty(provider.Requests);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await current.WaitAsync(Patience);
        Assert.Equal("rep", Assert.Single(provider.Requests).Query);
    }

    [Fact]
    public async Task ASubmittedQueryIsNeverDelayed()
    {
        var time = new ManualTime();
        var provider = new ScriptedProvider("files", QuickSearchResultType.Files, debounce: TimeSpan.FromMinutes(1));
        var coordinator = new QuickSearchCoordinator([provider], time);

        await coordinator.SearchAsync(new QuickSearchRequest("report") { IsDeliberate = true }).WaitAsync(Patience);

        Assert.True(Assert.Single(provider.Requests).IsDeliberate);
    }

    [Fact]
    public async Task AnEmptyQueryThatOnlyBrowsesIsNeverDelayed()
    {
        var time = new ManualTime();
        var provider = new ScriptedProvider("files", QuickSearchResultType.Files, debounce: TimeSpan.FromMinutes(1));
        var coordinator = new QuickSearchCoordinator([provider], time);

        await coordinator.SearchAsync(new QuickSearchRequest("  ")).WaitAsync(Patience);

        Assert.Equal("", Assert.Single(provider.Requests).Query);
    }

    [Fact]
    public async Task AFailingProviderIsAnEmptyAnswerAndNeverStopsTheOthers()
    {
        var logger = new CapturingLogger<QuickSearchCoordinator>();
        var failing = new ScriptedProvider("files", QuickSearchResultType.Files)
        {
            Answer = (request, _) => throw new InvalidOperationException("could not read C:\\secret\\" + request.Query),
        };
        var working = ScriptedProvider.Returning("apps", QuickSearchResultType.Applications, 50, Result("app:1"));
        var coordinator = new QuickSearchCoordinator([failing, working], logger: logger);

        var outcome = await coordinator.SearchAsync(new QuickSearchRequest("tax return"));

        Assert.Equal(QuickSearchProviderStatus.Failed, outcome.Providers[0].Status);
        Assert.Empty(outcome.Providers[0].Results);
        Assert.Equal(QuickSearchProviderStatus.Completed, outcome.Providers[1].Status);
        Assert.Equal("app:1", Assert.Single(outcome.Results).Id);

        // The log says which provider and what kind of failure, and nothing the user typed or the exception said.
        var line = Assert.Single(logger.Lines);
        Assert.Contains("files", line);
        Assert.Contains(nameof(InvalidOperationException), line);
        Assert.DoesNotContain("tax return", line);
        Assert.DoesNotContain("secret", line);
    }

    [Fact]
    public async Task AProviderThatTakesTooLongIsGivenUpOn()
    {
        var time = new ManualTime();
        var stuck = new ScriptedProvider("files", QuickSearchResultType.Files)
        {
            Answer = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return [];
            },
        };
        var working = ScriptedProvider.Returning("apps", QuickSearchResultType.Applications, 50, Result("app:1"));
        var coordinator = new QuickSearchCoordinator([stuck, working], time, TimeSpan.FromSeconds(2));

        // The provider's time limit starts running before it is asked, so once it has been asked the limit is there to run out.
        var search = coordinator.SearchAsync(new QuickSearchRequest("report"));
        await WaitUntilAsync(() => stuck.Requests.Count > 0);
        time.Advance(TimeSpan.FromSeconds(2));
        var outcome = await search.WaitAsync(Patience);

        Assert.Equal(QuickSearchProviderStatus.TimedOut, outcome.Providers[0].Status);
        Assert.Equal(QuickSearchProviderStatus.Completed, outcome.Providers[1].Status);
        Assert.Equal("app:1", Assert.Single(outcome.Results).Id);
    }

    [Fact]
    public async Task ACancelledSearchThrowsAndReportsNothingMore()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<QuickSearchResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource();
        var waiting = new ScriptedProvider("files", QuickSearchResultType.Files)
        {
            Answer = (_, token) =>
            {
                asked.SetResult();
                token.Register(() => gate.TrySetCanceled(token));
                return gate.Task;
            },
        };
        var coordinator = new QuickSearchCoordinator([waiting]);
        var reports = 0;
        using var cancel = new CancellationTokenSource();

        var search = coordinator.SearchAsync(
            new QuickSearchRequest("report"), new SynchronousProgress(_ => Interlocked.Increment(ref reports)), cancel.Token);
        await asked.Task.WaitAsync(Patience);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        Assert.Equal(0, reports);

        // A search that is cancelled before it starts never asks anything.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.SearchAsync(new QuickSearchRequest("a"), null, cancel.Token));
        Assert.Single(waiting.Requests);
    }

    [Fact]
    public async Task WhatAProviderReturnsIsTrimmedAndMarkedWithItsIdentity()
    {
        var provider = new ScriptedProvider("files", QuickSearchResultType.Files)
        {
            // It claims to be another provider, and returns more than it was asked for.
            Answer = (_, _) => Found(
                QuickResults.Make("1", "one", QuickSearchResultType.Applications, "applications"),
                QuickResults.Make("2", "two", QuickSearchResultType.Applications, "applications"),
                QuickResults.Make("3", "three", QuickSearchResultType.Applications, "applications")),
        };
        var coordinator = new QuickSearchCoordinator([provider]);

        var outcome = await coordinator.SearchAsync(new QuickSearchRequest("o") { MaxResults = 2 });

        Assert.Equal(["1", "2"], outcome.Results.Select(result => result.Id));
        Assert.All(outcome.Results, result =>
        {
            Assert.Equal("files", result.ProviderId);
            Assert.Equal(QuickSearchResultType.Files, result.ResultType);
        });
        Assert.Equal(2, Assert.Single(provider.Requests).MaxResults);
    }

    [Fact]
    public async Task HowManyAreAskedForIsKeptWithinLimits()
    {
        var provider = new ScriptedProvider("apps");
        var coordinator = new QuickSearchCoordinator([provider]);

        await coordinator.SearchAsync(new QuickSearchRequest("a") { MaxResults = 0 });
        await coordinator.SearchAsync(new QuickSearchRequest("a") { MaxResults = 5000 });

        Assert.Equal([1, QuickSearchRequest.HardMaxResults], provider.Requests.Select(request => request.MaxResults));
    }

    [Fact]
    public void ProvidersMustHaveDistinctNonEmptyIds()
    {
        Assert.Throws<ArgumentException>(() => new QuickSearchCoordinator([new ScriptedProvider("a"), new ScriptedProvider("a")]));
        Assert.Throws<ArgumentException>(() => new QuickSearchCoordinator([new ScriptedProvider(" ")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuickSearchCoordinator([], null, TimeSpan.Zero));
    }

    [Fact]
    public void NothingPrivateIsPrinted()
    {
        var result = new QuickSearchResult(
            "file:C:\\tax\\return.pdf", QuickSearchResultType.Files, "files", "return.pdf",
            new QuickSearchAction(QuickSearchActionKind.OpenPath, "Open", "C:\\tax\\return.pdf"))
        {
            Subtitle = "~\\tax",
            Icon = new QuickSearchIcon(QuickSearchIconKind.File, [1, 2, 3]),
        };
        var request = new QuickSearchRequest("tax return");
        var outcome = new QuickSearchProviderOutcome("files", QuickSearchResultType.Files, QuickSearchProviderStatus.Completed, [result], TimeSpan.Zero);

        foreach (var text in new[] { result.ToString(), result.Primary.ToString(), result.Icon.ToString(), request.ToString(), outcome.ToString() })
        {
            Assert.DoesNotContain("tax", text);
            Assert.DoesNotContain("return", text);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition was not met in time.");
            await Task.Delay(5);
        }
    }

    // Reports on the thread that reports, where Progress<T> would post to a context and so be late.
    private sealed class SynchronousProgress(Action<QuickSearchProviderOutcome> report) : IProgress<QuickSearchProviderOutcome>
    {
        public void Report(QuickSearchProviderOutcome value) => report(value);
    }
}
