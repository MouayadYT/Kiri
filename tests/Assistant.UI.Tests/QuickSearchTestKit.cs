using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.QuickSearch.Clipboard;
using Assistant.Core.QuickSearch.Routing;
using Assistant.Search.Files;
using Assistant.UI.Search;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- What the instant-search tests are built from: providers that answer what the test says, over doubles for what runs a result. ----

    private static readonly DateTimeOffset QuickNow = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    // A provider that answers with the function the test gives it, after the pause the test gives it, and remembers what it was asked.
    private sealed class FakeQuickProvider(
        string id, QuickSearchResultType type, int priority = 50, Func<QuickSearchRequest, IReadOnlyList<QuickSearchResult>>? answer = null,
        int minimumLength = 0) : IQuickSearchProvider
    {
        public List<QuickSearchRequest> Asked { get; } = [];

        /// <summary>Set to hold the provider's answer until the test lets it go.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public string Id => id;

        public string DisplayName => id;

        public QuickSearchResultType ResultType => type;

        public int Priority => priority;

        public int MinimumQueryLength => minimumLength;

        public async Task<IReadOnlyList<QuickSearchResult>> SearchAsync(QuickSearchRequest request, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.Add(request);
            }

            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return answer?.Invoke(request) ?? [];
        }
    }

    private static QuickSearchResult QuickApp(string name, string id = "", QuickSearchAction[]? alternates = null, byte[]? icon = null) =>
        new("app:" + (id.Length > 0 ? id : name), QuickSearchResultType.Applications, "applications", name,
            new QuickSearchAction(QuickSearchActionKind.LaunchApplication, "Open", id.Length > 0 ? id : name))
        {
            Icon = new QuickSearchIcon(QuickSearchIconKind.Application, icon),
            Alternates = alternates ?? [],
        };

    private static QuickSearchResult QuickAction(string title, string actionId, string? argument = null) =>
        new("action:" + actionId, QuickSearchResultType.Actions, "actions", title,
            new QuickSearchAction(QuickSearchActionKind.RunAction, "Run", actionId, argument))
        {
            Icon = new QuickSearchIcon(QuickSearchIconKind.Action),
        };

    private sealed class RecordingApplications : IApplicationLauncher
    {
        public List<string> Launched { get; } = [];

        public bool Succeeds { get; set; } = true;

        public bool Launch(string applicationId)
        {
            Launched.Add(applicationId);
            return Succeeds;
        }
    }

    private sealed class RecordingActions : IQuickActionExecutor
    {
        public List<(string Id, string? Argument)> Ran { get; } = [];

        public QuickActionOutcome Outcome { get; set; } = QuickActionOutcome.Done;

        public bool CanRun(string actionId) => true;

        public Task<QuickActionOutcome> RunAsync(string actionId, string? argument, CancellationToken cancellationToken = default)
        {
            Ran.Add((actionId, argument));
            return Task.FromResult(Outcome);
        }
    }

    private sealed class ScriptedFileSearch(IReadOnlyList<SearchResultItem> items) : IFileSearchService
    {
        public List<FileSearchQuery> Queries { get; } = [];

        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(FileSearchQuery query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult(items);
        }

        public async Task<FileSearchOutcome> SearchWithCapabilitiesAsync(FileSearchQuery query, CancellationToken cancellationToken = default) =>
            new(await SearchAsync(query, cancellationToken), ContentSearchCapability.NotRequested);
    }

    private sealed class NoPlanner : IFileSearchPlanner
    {
        public PlannedFileSearch? PlanKnownShape(string request) => null;

        public Task<PlannedFileSearch> PlanAsync(string request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlannedFileSearch(null, FileSearchPlanSource.Read));
    }

    private sealed class QuickFilesPermission(bool allowed = true) : IPermissionPolicy
    {
        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PermissionDecision(capability, allowed ? PermissionDecisionReason.Granted : PermissionDecisionReason.TurnedOff));
    }

    // The real Files provider over a file search that answers with the items it is given for any query.
    private static (FilesQuickSearchProvider Provider, ScriptedFileSearch Search) QuickFiles(
        IReadOnlyList<SearchResultItem> items, string profile = @"C:\Users\someone", bool allowed = true)
    {
        var search = new ScriptedFileSearch(items);
        return (new FilesQuickSearchProvider(search, new NoPlanner(), new QuickFilesPermission(allowed), new FixedClock(QuickNow), profile), search);
    }

    // Everything that runs a result is a double that remembers what it was asked; the source and the runner are the real ones.
    private sealed class QuickKit
    {
        public QuickKit(
            IEnumerable<IQuickSearchProvider> providers, Func<string, bool>? isFileRequest = null, bool calculator = false,
            QuickSearchUsage? usage = null, ClipboardHistory? history = null, ISettingsService? settings = null)
        {
            Providers = [.. providers];
            Usage = usage ?? new QuickSearchUsage(new FixedClock(QuickNow));
            History = history ?? new ClipboardHistory(clock: new FixedClock(QuickNow));
            Router = new QueryRouter(isFileRequest, () => calculator);
            Runner = new QuickSearchActionRunner(Apps, Files, Clipboard, Attach, Actions, History, Usage);
            Source = new QuickSearchResultsSource(
                new QuickSearchCoordinator(Providers), new QuickSearchRanker(Providers, Usage), Router, Runner, new FixedClock(QuickNow), History, settings);
        }

        public IReadOnlyList<IQuickSearchProvider> Providers { get; }

        public QuickSearchUsage Usage { get; }

        public ClipboardHistory History { get; }

        public QueryRouter Router { get; }

        public RecordingApplications Apps { get; } = new();

        public RecordingLauncher Files { get; } = new();

        public FakeClipboard Clipboard { get; } = new();

        public AttachRequests Attach { get; } = new();

        public RecordingActions Actions { get; } = new();

        public QuickSearchActionRunner Runner { get; }

        public QuickSearchResultsSource Source { get; }

        public List<string> Failures { get; } = [];

        public int Finished { get; private set; }

        /// <summary>Subscribes to what the runner says; call before running anything.</summary>
        public QuickKit Listen()
        {
            Runner.Failed += (_, message) => Failures.Add(message);
            Runner.Finished += (_, _) => Finished++;
            return this;
        }

        /// <summary>The snapshots reported for a search, the last being the final one.</summary>
        public async Task<List<SearchResultsSnapshot>> SearchAsync(string query, QuickSearchResultType? scope = null)
        {
            var snapshots = new List<SearchResultsSnapshot>();
            await Source.SearchAsync(query, scope, snapshot => { lock (snapshots) { snapshots.Add(snapshot); } }, CancellationToken.None)
                .ConfigureAwait(false);
            return snapshots;
        }

        public SearchResultsViewModel NewResults() => new(null, quickSource: Source);
    }
}
