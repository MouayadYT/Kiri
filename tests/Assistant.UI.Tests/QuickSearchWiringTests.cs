using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Events;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.QuickSearch.Clipboard;
using Assistant.Core.QuickSearch.Routing;
using Assistant.Core.Settings;
using Assistant.Search.Applications;
using Assistant.Search.Files;
using Assistant.Tools;
using Assistant.UI.Bootstrap;
using Assistant.UI.Search;
using Assistant.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>How the app puts instant search together (PROJECT_SPEC §4.1, §5.3): the providers, the ranking, the router, the tools, and the clipboard history that follows its permission.</summary>
public sealed class QuickSearchWiringTests
{
    [Fact]
    public void TheAppWiresTheFourProvidersTheCoordinatorTheRankerAndTheRouter()
    {
        using var host = AppHost.Create();
        var services = host.Services;

        var providers = services.GetRequiredService<IEnumerable<IQuickSearchProvider>>().ToList();
        Assert.Equal(4, providers.Count);
        Assert.Single(providers.OfType<ApplicationsQuickSearchProvider>());
        Assert.Single(providers.OfType<FilesQuickSearchProvider>());
        Assert.Single(providers.OfType<ActionsQuickSearchProvider>());
        Assert.Single(providers.OfType<ClipboardQuickSearchProvider>());
        Assert.Equal(
            [QuickSearchResultType.Applications, QuickSearchResultType.Files, QuickSearchResultType.Actions, QuickSearchResultType.Clipboard],
            providers.Select(provider => provider.ResultType).Order());
        Assert.Equal(providers.Count, providers.Select(provider => provider.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.IsType<QuickSearchCoordinator>(services.GetRequiredService<IQuickSearchCoordinator>());
        Assert.NotNull(services.GetRequiredService<QuickSearchRanker>());
        Assert.IsType<QuickSearchResultsSource>(services.GetRequiredService<IQuickSearchResultsSource>());
        Assert.Same(services.GetRequiredService<SearchResultsViewModel>(), services.GetRequiredService<SearchResultsViewModel>());
        Assert.NotNull(services.GetRequiredService<CalculationAnswers>());
    }

    [Fact]
    public void TheClipboardHistoryIsOffUntilTheUserTurnsItOn_AndASumGoesToTheCalculator()
    {
        using var host = AppHost.Create();
        var services = host.Services;

        var history = services.GetRequiredService<IClipboardHistory>();
        Assert.False(history.IsEnabled);
        Assert.False(services.GetRequiredService<IClipboardWatcher>().IsRunning);
        Assert.Contains(services.GetServices<IHostedService>(), service => service is ClipboardHistoryController);
        Assert.Contains(services.GetServices<IHostedService>(), service => service is QuickSearchWarmUp);

        // The calculator is registered, so a sum is a calculation and Enter works it out without asking a model.
        var router = services.GetRequiredService<IQueryRouter>();
        Assert.NotNull(services.GetRequiredService<IToolRegistry>().Find("calculate"));
        Assert.Equal(QueryRouteKind.Calculation, router.Route("9+10").Kind);
        Assert.Equal("9 + 10", router.Route("what is 9+10").Expression);
    }

    [Fact]
    public void EveryActionTheAppRunsIsInTheCatalogAndNoActionThatCanDestroyOrIsNotBuiltIsRunnable()
    {
        using var host = AppHost.Create();
        var executor = host.Services.GetRequiredService<IQuickActionExecutor>();
        var catalog = host.Services.GetRequiredService<IQuickActionCatalog>();

        Assert.All(catalog.All, definition => Assert.Equal(definition.IsSafeToRun, executor.CanRun(definition.Id)));
        Assert.All(catalog.All.Where(definition => definition.Availability != QuickActionAvailability.Available), definition => Assert.False(executor.CanRun(definition.Id)));
    }

    [Fact]
    public void TheAppGivesTheFullWindowAndTheSetupWhatTheyNeedToWork()
    {
        using var host = AppHost.Create();

        // The full window has a microphone of its own (not the floating conversation's) and something to answer a new conversation with.
        var history = host.Services.GetRequiredService<Assistant.UI.ViewModels.HistoryViewModel>();
        Assert.NotNull(history.Voice);
        Assert.NotSame(host.Services.GetRequiredService<Assistant.UI.ViewModels.ConversationViewModel>().Voice, history.Voice);
        Assert.True(history.NewConversationCommand.CanExecute(null));

        // The app's pipe takes the request a second start of the app sends, and hands it on to show the full window.
        var pipe = host.Services.GetRequiredService<Assistant.UI.Explorer.ExplorerIntegration>();
        Assert.True(pipe.Handle(Assistant.Core.Ipc.InvocationRequest.ShowFullView).IsAccepted);

        // The setup and Settings have the Home Assistant to connect, which is the one the home tools use.
        var home = host.Services.GetRequiredService<Assistant.Core.Home.IHomeAssistant>();
        Assert.Same(home, host.Services.GetRequiredService<Assistant.UI.Onboarding.ConnectionsSetupViewModel>().Home);
        Assert.Contains(host.Services.GetRequiredService<Assistant.UI.Onboarding.ConnectionsSetupViewModel>().Apps, app => app.Name == "Home Assistant");
    }

    [Fact]
    public void TheFilesListedWhileTheUserTypesAreSearchedForWithoutTheSearchingChip()
    {
        using var host = AppHost.Create();
        var files = host.Services.GetRequiredService<IEnumerable<IQuickSearchProvider>>().OfType<FilesQuickSearchProvider>().Single();
        var search = typeof(FilesQuickSearchProvider)
            .GetField("_search", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(files);

        // The list under the bar is what shows a search as it is typed: it checks the Files permission, and reports no activity, so no chip hangs under it.
        Assert.IsType<Assistant.Search.PermissionCheckedFileSearchService>(search);

        // A request for files that is asked, and the model's own searches, still say that they are searching.
        Assert.IsType<Assistant.Core.Activity.ActivityFileSearchService>(host.Services.GetRequiredService<Assistant.Core.Contracts.IFileSearchService>());
    }

    [Fact]
    public async Task TheAppsToolsAreTheRegisteredOnesOnlyAndTheExecutorEnforcesWhatTheyNeed()
    {
        using var host = AppHost.Create();
        var registry = host.Services.GetRequiredService<IToolRegistry>();

        Assert.Equal(
            [
                "search_files", "read_file_text", "read_screen_text", "calculate", "open_application", "open_file", "reveal_file",
                "open_folder", "get_volume", "set_volume", "mute", "unmute", "take_screenshot", "set_do_not_disturb", "remember", "get_time", "set_alarm", "start_timer",
                "control_timer", "stopwatch", "start_focus_session", "set_clock_display", "get_calendar_events", "search_calendar_events",
                "draft_message", "send_message", "remember_person", "control_home_device", "get_home_devices", "search_web",
            ],
            registry.Tools.Select(tool => tool.Name));
        Assert.DoesNotContain(registry.Tools, tool => tool.RiskLevel == RiskLevel.Destructive);
        Assert.Equal(
            ["calculate", "draft_message", "get_calendar_events", "get_home_devices", "get_time", "get_volume", "read_file_text", "read_screen_text", "search_calendar_events", "search_files", "search_web"],
            registry.Tools.Where(tool => tool.RiskLevel == RiskLevel.ReadOnly).Select(tool => tool.Name).Order());
        Assert.Equal(PermissionCapability.Files, registry.Find("read_file_text")!.RequiredPermission);
        Assert.Equal(PermissionCapability.ScreenCapture, registry.Find("take_screenshot")!.RequiredPermission);

        // Whatever the model calls that is not registered is a failed result, never run.
        var executor = host.Services.GetRequiredService<IToolExecutor>();
        foreach (var name in new[] { "run_powershell", "cmd", "execute", "open_item", "Calculate", "calculate " })
        {
            var result = await executor.ExecuteAsync(new ToolCall("c1", name, """{"command":"dir"}"""));

            Assert.Equal(ToolResultStatus.Failed, result.Status);
        }

        Assert.NotNull(host.Services.GetRequiredService<ToolExecutor>());
    }

    [Fact]
    public async Task TheRealPipelineAnswersTypedWordsWithoutAProviderFailingOrAnythingBeingRun()
    {
        // Read-only: the real providers, coordinator and ranker over this PC's own Start menu, folders and Windows Search index.
        using var host = AppHost.Create();
        var coordinator = host.Services.GetRequiredService<IQuickSearchCoordinator>();
        var ranker = host.Services.GetRequiredService<QuickSearchRanker>();
        var catalog = host.Services.GetRequiredService<IQuickActionCatalog>();

        foreach (var typed in new[] { "downloads", "volume 30", "mute", "settings" })
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var outcome = await coordinator.SearchAsync(new QuickSearchRequest(typed), null, limit.Token);

            Assert.DoesNotContain(outcome.Providers, answer => answer.Status == QuickSearchProviderStatus.Failed);
            var ranking = ranker.Rank(typed, outcome.Results, DateTimeOffset.Now);
            Assert.NotEmpty(ranking.Ordered);
            Assert.True(ranking.Ordered.Count <= QuickSearchRequest.HardMaxResults);
        }

        using var all = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var downloads = await coordinator.SearchAsync(new QuickSearchRequest("downloads"), null, all.Token);
        Assert.Contains(
            downloads.Results.Where(result => result.ResultType == QuickSearchResultType.Actions),
            result => result.Id.Contains(QuickActionIds.OpenDownloads, StringComparison.Ordinal));
        Assert.NotNull(catalog.Find(QuickActionIds.OpenDownloads));
    }

    // ---- The history follows the permission ----

    private sealed class Settings(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; set; } = current;

        public bool Throws { get; set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Throws ? throw new System.IO.IOException("unreadable") : Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class Watcher : IClipboardWatcher
    {
        public event EventHandler<ClipboardCopiedEventArgs>? TextCopied;

        public bool IsRunning { get; private set; }

        public int Starts { get; private set; }

        public bool FailToStart { get; set; }

        public void Start()
        {
            if (FailToStart)
            {
                throw new InvalidOperationException("no window");
            }

            Starts++;
            IsRunning = true;
        }

        public void Stop() => IsRunning = false;

        public void Copy(string text) => TextCopied?.Invoke(this, new ClipboardCopiedEventArgs(text));

        public void Dispose() => Stop();
    }

    private static AppSettings WithClipboard(bool on) =>
        new() { Permissions = new PermissionSettings { ClipboardHistory = on } };

    private static (ClipboardHistoryController Controller, ClipboardHistory History, Watcher Watcher, Settings Settings, AppEventBus Bus) Controller(bool on)
    {
        var history = new ClipboardHistory();
        var watcher = new Watcher();
        var settings = new Settings(WithClipboard(on));
        var bus = new AppEventBus(NullLogger<AppEventBus>.Instance);
        return (new ClipboardHistoryController(settings, history, watcher, bus, NullLogger<ClipboardHistoryController>.Instance), history, watcher, settings, bus);
    }

    [Fact]
    public async Task WithThePermissionOffNothingIsWatchedOrKept()
    {
        var (controller, history, watcher, _, _) = Controller(on: false);

        await controller.StartAsync(CancellationToken.None);
        watcher.Copy("secret");

        Assert.False(history.IsEnabled);
        Assert.False(watcher.IsRunning);
        Assert.Equal(0, watcher.Starts);
        Assert.Empty(history.Items);
    }

    [Fact]
    public async Task WithThePermissionOnWhatIsCopiedIsKept_AndTurningItOffForgetsAllAtOnce()
    {
        var (controller, history, watcher, settings, bus) = Controller(on: true);

        await controller.StartAsync(CancellationToken.None);
        watcher.Copy("first copy");
        Assert.True(history.IsEnabled);
        Assert.True(watcher.IsRunning);
        Assert.Equal(["first copy"], history.Items.Select(item => item.Text));

        settings.Current = WithClipboard(false);
        await bus.PublishAsync(new SettingsSaved(settings.Current));

        Assert.False(history.IsEnabled);
        Assert.False(watcher.IsRunning);
        Assert.Empty(history.Items);
    }

    [Fact]
    public async Task SavingTheSettingsTurnsTheHistoryOnWhileTheAppRuns()
    {
        var (controller, history, watcher, settings, bus) = Controller(on: false);
        await controller.StartAsync(CancellationToken.None);

        await bus.PublishAsync(new SettingsSaved(WithClipboard(true)));
        watcher.Copy("kept");

        Assert.True(history.IsEnabled);
        Assert.True(watcher.IsRunning);
        Assert.Equal(["kept"], history.Items.Select(item => item.Text));
        Assert.False(settings.Current.Permissions.ClipboardHistory);
    }

    [Fact]
    public async Task ClosingTheAppStopsTheWatcherAndForgetsTheHistory()
    {
        var (controller, history, watcher, _, _) = Controller(on: true);
        await controller.StartAsync(CancellationToken.None);
        watcher.Copy("copied");

        await controller.StopAsync(CancellationToken.None);

        Assert.False(watcher.IsRunning);
        Assert.False(history.IsEnabled);
        Assert.Empty(history.Items);
    }

    [Fact]
    public async Task AWatcherThatCannotStartLeavesTheHistoryOff()
    {
        var (controller, history, watcher, _, _) = Controller(on: true);
        watcher.FailToStart = true;

        await controller.StartAsync(CancellationToken.None);

        Assert.False(history.IsEnabled);
        Assert.False(watcher.IsRunning);
    }

    [Fact]
    public async Task SettingsThatCannotBeReadLeaveTheHistoryOff()
    {
        var (controller, history, watcher, settings, _) = Controller(on: true);
        settings.Throws = true;

        await controller.StartAsync(CancellationToken.None);

        Assert.False(history.IsEnabled);
        Assert.False(watcher.IsRunning);
    }

    [Fact]
    public async Task TheClipboardHistoryIsOnlyOnWhereThisBuildCanDoItAndTheUserAllowsIt()
    {
        // The same decision the Permissions page and every other check make: the settings alone do not turn it on if this build refuses.
        var (controller, history, _, _, _) = Controller(on: true);

        await controller.StartAsync(CancellationToken.None);

        Assert.True(history.IsEnabled);
        Assert.True(Assistant.Core.Permissions.PermissionCatalog.Get(PermissionCapability.ClipboardHistory).IsAvailable);
    }
}
