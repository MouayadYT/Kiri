using System.Runtime.CompilerServices;
using Assistant.Core.Activity;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Xunit;

namespace Assistant.UI.Tests;

public sealed class ActivityTrackerTests
{
    [Fact]
    public void AnOperationShowsUntilItIsDisposedWithItsKindsOwnWords()
    {
        var tracker = new ActivityTracker();
        var changes = 0;
        tracker.Changed += (_, _) => changes++;
        Assert.Null(tracker.Current);

        var scope = tracker.Begin(ActivityKind.WebSearch);
        Assert.Equal("Looking into it", tracker.Current!.Text);
        Assert.Equal(ActivityKind.WebSearch, tracker.Current.Kind);
        Assert.Equal(scope.Status, tracker.Current);
        Assert.Equal(1, changes);

        scope.Dispose();
        scope.Dispose(); // Ending twice is harmless.
        Assert.Null(tracker.Current);
        Assert.Equal(2, changes);

        // As the reference says them: the model is "Working" on an answer, and a web lookup is "Looking into it".
        Assert.Equal(["Searching", "Searching", "Looking into it", "Working", "Working"],
            new[] { ActivityKind.WindowsSearch, ActivityKind.FileSearch, ActivityKind.WebSearch, ActivityKind.Model, ActivityKind.Tool }
                .Select(ActivityStatus.DefaultText));
    }

    [Fact]
    public void TheLatestRunningOperationShowsAndEarlierOnesReturnWhenItEnds()
    {
        var tracker = new ActivityTracker();
        using var search = tracker.Begin(ActivityKind.WindowsSearch);
        var tool = tracker.Begin(ActivityKind.Tool, "Reading");
        Assert.Equal("Reading", tracker.Current!.Text);
        tool.Update("Reading more");
        Assert.Equal("Reading more", tracker.Current.Text);
        tool.Dispose();
        Assert.Equal(ActivityKind.WindowsSearch, tracker.Current!.Kind);
        Assert.Throws<ArgumentException>(() => tracker.Begin(ActivityKind.Tool, " "));
        Assert.Throws<ArgumentException>(() => search.Update(""));
    }

    [Fact]
    public void CancellingStopsTheCurrentOperationShowingAtOnceAndCancelsItsToken()
    {
        var tracker = new ActivityTracker();
        Assert.False(tracker.CancelCurrent());

        using var first = tracker.Begin(ActivityKind.WindowsSearch);
        var second = tracker.Begin(ActivityKind.Model);
        var changes = 0;
        tracker.Changed += (_, _) => changes++;

        Assert.True(tracker.CancelCurrent());
        Assert.True(second.CancellationToken.IsCancellationRequested);
        Assert.False(first.CancellationToken.IsCancellationRequested);
        Assert.Equal(ActivityKind.WindowsSearch, tracker.Current!.Kind);
        Assert.Equal(1, changes);

        // The cancelled operation winds down in its own time; ending it changes nothing that shows.
        second.Dispose();
        Assert.Equal(ActivityKind.WindowsSearch, tracker.Current!.Kind);
        Assert.True(tracker.CancelCurrent());
        Assert.Null(tracker.Current);
        Assert.False(tracker.CancelCurrent());
    }

    [Fact]
    public void AnOperationsOwnTokenEndsTheActivityAndIsLinkedToTheScope()
    {
        var tracker = new ActivityTracker();
        using var source = new CancellationTokenSource();
        var scope = tracker.Begin(ActivityKind.FileSearch, cancellationToken: source.Token);
        Assert.NotNull(tracker.Current);

        source.Cancel();
        Assert.True(scope.CancellationToken.IsCancellationRequested);
        Assert.Null(tracker.Current);

        // A token that is already cancelled shows nothing.
        using (tracker.Begin(ActivityKind.Tool, cancellationToken: source.Token))
        {
            Assert.Null(tracker.Current);
        }
    }

    [Fact]
    public async Task BeginningAndEndingFromManyThreadsLeavesNothingRunning()
    {
        var tracker = new ActivityTracker();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                using var scope = tracker.Begin(ActivityKind.Tool);
                var current = tracker.Current;
                Assert.True(current is null || current.Kind == ActivityKind.Tool);
                if (i % 7 == 0) tracker.CancelCurrent();
            }
        })));
        Assert.Null(tracker.Current);
    }

    [Fact]
    public async Task ASearchIsReportedForAsLongAsItRunsAndCancellingReachesIt()
    {
        var tracker = new ActivityTracker();
        var inner = new FakeSearch();
        var service = new ActivityFileSearchService(inner, tracker);

        var search = service.SearchAsync(new FileSearchQuery("private words"));
        Assert.Equal(ActivityKind.WindowsSearch, tracker.Current!.Kind);
        Assert.DoesNotContain("private", tracker.Current.Text, StringComparison.Ordinal);

        tracker.CancelCurrent();
        Assert.True(inner.Token.IsCancellationRequested);
        inner.Finish.SetResult([]);
        Assert.Empty(await search);
        Assert.Null(tracker.Current);

        // A search that fails stops showing too.
        var failing = new ActivityFileSearchService(new FakeSearch { Throws = true }, tracker);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.SearchAsync(new FileSearchQuery("x")));
        Assert.Null(tracker.Current);
    }

    [Fact]
    public async Task ASearchWithCapabilitiesIsReportedTooAndCarriesTheFlagBack()
    {
        var tracker = new ActivityTracker();
        var inner = new FakeSearch();
        var service = new ActivityFileSearchService(inner, tracker);

        var search = service.SearchWithCapabilitiesAsync(new FileSearchQuery { ContentTerm = "private words" });
        Assert.Equal(ActivityKind.WindowsSearch, tracker.Current!.Kind);
        Assert.DoesNotContain("private", tracker.Current.Text, StringComparison.Ordinal);

        tracker.CancelCurrent();
        Assert.True(inner.Token.IsCancellationRequested);
        inner.Finish.SetResult([]);
        var outcome = await search;
        Assert.Empty(outcome.Items);
        Assert.Equal(ContentSearchSupport.NotRequested, outcome.ContentSearch.Support);
        Assert.Null(tracker.Current);

        var failing = new ActivityFileSearchService(new FakeSearch { Throws = true }, tracker);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.SearchWithCapabilitiesAsync(new FileSearchQuery("x")));
        Assert.Null(tracker.Current);
    }

    [Fact]
    public async Task AToolCallIsReportedForAsLongAsItRuns()
    {
        var tracker = new ActivityTracker();
        var seen = new List<ActivityStatus?>();
        var service = new ActivityToolExecutor(new FakeTools(call =>
        {
            seen.Add(tracker.Current);
            return new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}");
        }), tracker);

        await service.ExecuteAsync(new ToolCall("1", "read_file_text", "{\"path\":\"secret\"}"));
        Assert.Equal([ActivityKind.Tool], seen.Select(status => status!.Kind));
        Assert.Equal("Working", seen[0]!.Text);
        Assert.Null(tracker.Current);
    }

    [Fact]
    public async Task WhileTheUserIsAskedAboutACallTheActivitySaysItIsWaitingForThem_AndTheRunsClockIsHeldToo()
    {
        var tracker = new ActivityTracker();
        var seen = new List<string>();
        var run = new CountingPause();
        var service = new ActivityToolExecutor(new AskingTools(tracker, seen), tracker);

        await service.ExecuteAsync(new ToolCall("1", "send_message", "{}"), new ToolContext(Guid.NewGuid(), null, run));

        Assert.Equal(["Working", "Waiting for you", "Working"], seen);
        Assert.Equal((1, 1), (run.Paused, run.Resumed));
        Assert.Null(tracker.Current);
    }

    [Fact]
    public async Task TheUsersWaitIsReportedEvenWhenTheRunHasNoClockToHold()
    {
        var tracker = new ActivityTracker();
        var seen = new List<string>();

        await new ActivityToolExecutor(new AskingTools(tracker, seen), tracker).ExecuteAsync(new ToolCall("1", "send_message", "{}"), new ToolContext(Guid.NewGuid()));

        Assert.Equal(["Working", "Waiting for you", "Working"], seen);
    }

    private sealed class CountingPause : IRunPause
    {
        public int Paused { get; private set; }

        public int Resumed { get; private set; }

        public IDisposable Pause()
        {
            Paused++;
            return new Release(this);
        }

        private sealed class Release(CountingPause owner) : IDisposable
        {
            public void Dispose() => owner.Resumed++;
        }
    }

    // A tool executor that asks the user something in the middle of a call, holding the run's clock as the real one does.
    private sealed class AskingTools(IActivityTracker tracker, List<string> seen) : IToolExecutor
    {
        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default) =>
            ExecuteAsync(call, new ToolContext(Guid.Empty), cancellationToken);

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken cancellationToken = default)
        {
            seen.Add(tracker.Current!.Text);
            using (context.RunPause!.Pause())
            {
                seen.Add(tracker.Current!.Text);
            }

            seen.Add(tracker.Current!.Text);
            return Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}"));
        }
    }

    [Fact]
    public async Task TheModelShowsUntilItsFirstChunkAndTheCallersTokenStillStopsIt()
    {
        var tracker = new ActivityTracker();
        var seenDuring = new List<bool>();
        using var caller = new CancellationTokenSource();
        var inner = new FakeModel(seenDuring, tracker);
        var service = new ActivityModelService(inner, tracker);

        var request = new ModelRequest("", []);
        var chunks = 0;
        await foreach (var chunk in service.GenerateAsync(request, caller.Token))
        {
            chunks++;
            Assert.Null(tracker.Current); // The answer is streaming: nothing is being waited for.
            if (chunks == 2) caller.Cancel();
        }

        Assert.Equal(3, chunks);
        Assert.Equal([true], seenDuring); // Waiting for the first chunk showed the Thinking state.
        Assert.True(inner.Token.IsCancellationRequested); // The caller's cancellation reached the model after it ended.
        Assert.Null(tracker.Current);
    }

    [Fact]
    public async Task CancellingTheActivityCancelsTheGeneration()
    {
        var tracker = new ActivityTracker();
        var inner = new FakeModel([], tracker) { WaitForCancellation = true };
        var service = new ActivityModelService(inner, tracker);
        var consumed = Task.Run(async () =>
        {
            await foreach (var _ in service.GenerateAsync(new ModelRequest("", []))) { }
        });

        while (tracker.Current is null) await Task.Delay(5);
        Assert.Equal("Working", tracker.Current.Text);
        tracker.CancelCurrent();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumed);
        Assert.Null(tracker.Current);
    }

    private sealed class FakeSearch : IFileSearchService
    {
        public TaskCompletionSource<IReadOnlyList<SearchResultItem>> Finish { get; } = new();
        public CancellationToken Token { get; private set; }
        public bool Throws { get; init; }

        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(FileSearchQuery query, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            return Throws ? throw new InvalidOperationException() : Finish.Task;
        }

        public async Task<FileSearchOutcome> SearchWithCapabilitiesAsync(FileSearchQuery query, CancellationToken cancellationToken = default) =>
            new(await SearchAsync(query, cancellationToken), ContentSearchCapability.NotRequested);
    }

    private sealed class FakeTools(Func<ToolCall, ToolResult> run) : IToolExecutor
    {
        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default) =>
            Task.FromResult(run(call));
    }

    private sealed class FakeModel(List<bool> seenDuring, IActivityTracker tracker) : IModelService
    {
        public CancellationToken Token { get; private set; }
        public bool WaitForCancellation { get; init; }

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) => Task.FromResult<ModelInfo?>(null);

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            seenDuring.Add(tracker.Current?.Kind == ActivityKind.Model);
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            yield return AssistantResponseChunk.ForTextDelta("a");
            yield return AssistantResponseChunk.ForTextDelta("b");
            yield return AssistantResponseChunk.ForTextDelta("c");
            await Task.Yield();
        }
    }
}
