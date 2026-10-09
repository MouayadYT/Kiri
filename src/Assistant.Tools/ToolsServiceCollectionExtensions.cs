using Assistant.Core.Audit;
using Assistant.Core.Calendar;
using Assistant.Core.Contracts;
using Assistant.Core.Files;
using Assistant.Core.Memory;
using Assistant.Core.Messaging;
using Assistant.Core.People;
using Assistant.Core.Permissions;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.Storage;
using Assistant.Tools.Apps;
using Assistant.Tools.Audio;
using Assistant.Tools.Calculator;
using Assistant.Tools.Calendar;
using Assistant.Tools.Files;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Mcp.Auth;
using Assistant.Tools.Messaging;
using Assistant.Tools.Messaging.ConnectedApps;
using Assistant.Tools.Screen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools;

/// <summary>Registers the tools the model may call.</summary>
public static class ToolsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the tools: <c>search_files</c>, <c>read_file_text</c> and <c>read_screen_text</c>, the calculator (<c>calculate</c>), and the
    /// tools that act on Windows (<c>open_application</c>, <c>open_file</c>, <c>reveal_file</c>, <c>open_folder</c>, <c>get_volume</c>,
    /// <c>set_volume</c>, <c>mute</c>, <c>unmute</c> and <c>take_screenshot</c>); the catalog of them (<see cref="IToolRegistry"/>), the
    /// conversations' lists of known files (<see cref="IConversationFiles"/>) and the executor that runs a call
    /// (<see cref="ToolExecutor"/>, left for the caller to wrap before it is offered as <see cref="IToolExecutor"/>). The services the
    /// tools use (<see cref="IFileRequestService"/>, <see cref="IDocumentContextService"/>, <see cref="IPermissionPolicy"/>,
    /// <see cref="IModelService"/>, <see cref="ISettingsService"/>, <see cref="ISystemActions"/>, <see cref="IApplicationCatalog"/>,
    /// <see cref="IApplicationLauncher"/>, <see cref="IFileLauncher"/>, <see cref="IScreenshotTaker"/>, <see cref="IScreenText"/>) must be
    /// registered too.
    /// </summary>
    public static IServiceCollection AddAssistantTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IConversationFiles, ConversationFiles>();
        services.AddSingleton<ITool, SearchFilesTool>();
        services.AddSingleton<ITool, ReadFileTextTool>();
        services.AddSingleton<ITool, ReadScreenTextTool>();

        // The calculator, and what the model may do to Windows: each does one fixed thing, names what it acts on the way the user would
        // (an application by its name, a file by its id in the conversation, a folder by its name), and the ones that change something are
        // confirmed by the user each time. None runs a command, a script or a program the model writes.
        services.AddSingleton(CalculateTool.Create());
        services.AddSingleton<ITool, OpenApplicationTool>();
        services.AddSingleton<ITool, OpenFileTool>();
        services.AddSingleton<ITool, RevealFileTool>();
        services.AddSingleton<ITool, OpenFolderTool>();
        services.AddSingleton(provider => VolumeTools.GetVolume(provider.GetRequiredService<ISystemActions>()));
        services.AddSingleton(provider => VolumeTools.SetVolume(provider.GetRequiredService<ISystemActions>()));
        services.AddSingleton(provider => VolumeTools.Mute(provider.GetRequiredService<ISystemActions>()));
        services.AddSingleton(provider => VolumeTools.Unmute(provider.GetRequiredService<ISystemActions>()));
        services.AddSingleton<ITool, TakeScreenshotTool>();
        services.AddSingleton(provider => Quiet.DoNotDisturbTools.Set(provider.GetRequiredService<ISystemActions>()));

        // What the Assistant remembers for the user between conversations (Settings, under Memory): one small file in the app's folder, read when first needed and
        // written only when something is remembered, rewritten or forgotten. Without the app's folder (a host that has none) it is kept in memory alone.
        services.TryAddSingleton<IMemoryStore>(provider => provider.GetService<AppPaths>() is { } paths
            ? new JsonMemoryStore(Path.Combine(paths.RootDirectory, JsonMemoryStore.FileName))
            : new InMemoryMemoryStore());
        services.AddSingleton(provider => Memory.MemoryTools.Remember(provider.GetRequiredService<IMemoryStore>(), provider.GetService<TimeProvider>() ?? TimeProvider.System));

        // What time and what day it is, read from the PC's own clock: a model does not know, and is asked.
        services.AddSingleton(provider => Time.TimeTools.GetTime(provider.GetService<TimeProvider>() ?? TimeProvider.System));

        // The Windows Clock app, worked for the user: an alarm, a timer (started, and stopped, paused, resumed or restarted), the stopwatch and a focus
        // session (which turns Windows' focus on). They are offered only while the app registers an IClockApp, the Clock app is installed and the
        // request is about the clock.
        services.AddSingleton<ITool>(provider => new Clock.SetAlarmTool(provider.GetService<Assistant.Core.Clock.IClockApp>()));
        services.AddSingleton<ITool>(provider => new Clock.StartTimerTool(provider.GetService<Assistant.Core.Clock.IClockApp>(), provider.GetService<TimeProvider>()));
        services.AddSingleton<ITool>(provider => new Clock.ControlTimerTool(provider.GetService<Assistant.Core.Clock.IClockApp>()));
        services.AddSingleton<ITool>(provider => new Clock.StopwatchTool(provider.GetService<Assistant.Core.Clock.IClockApp>()));
        services.AddSingleton<ITool>(provider => new Clock.StartFocusSessionTool(provider.GetService<Assistant.Core.Clock.IClockApp>()));

        // Which display the Clock app's window is put on, when the user says ("always put the alarm window on the left monitor"): they are shown the display and
        // asked whether it is the right one, and the choice is remembered. It is offered while the app registers the PC's displays.
        services.AddSingleton<ITool>(provider => new Clock.SetClockDisplayTool(
            provider.GetService<Assistant.Core.Clock.IClockApp>(), provider.GetService<Assistant.Core.Displays.IDisplays>(), provider.GetService<IMemoryStore>(),
            provider.GetService<Assistant.Core.Displays.IDisplayPointer>(), provider.GetService<TimeProvider>()));

        // The calendar tools (step 111) read whichever ICalendarProvider the app has, and are offered only while there is one, and not for a request that a connected
        // app's own tools serve (the generic tool system comes first). The app registers no provider today, so they are never offered.
        services.AddSingleton<ITool>(provider => new GetCalendarEventsTool(
            provider.GetService<ICalendarProvider>(), provider.GetRequiredService<TimeProvider>(), provider.GetService<IDynamicToolSource>(),
            provider.GetService<IPermissionPolicy>()));
        services.AddSingleton<ITool>(provider => new SearchCalendarEventsTool(
            provider.GetService<ICalendarProvider>(), provider.GetRequiredService<TimeProvider>(), provider.GetService<IDynamicToolSource>(),
            provider.GetService<IPermissionPolicy>()));

        // The messaging tools (step 113) send through whichever IMessagingProvider the app has, to people the user saved (IPersonResolver), and are offered only while there
        // is a provider and the request seems to be about messaging. The app registers no provider today, so they are never offered, and the Messaging permission stays unavailable.
        services.AddSingleton<ITool>(provider => new DraftMessageTool(
            provider.GetService<IMessagingProvider>(), provider.GetService<IPersonResolver>(), provider.GetService<IPermissionPolicy>()));
        services.AddSingleton<ITool>(provider => new SendMessageTool(
            provider.GetService<IMessagingProvider>(), provider.GetService<IPersonResolver>(), provider.GetService<IPermissionPolicy>(),
            provider.GetService<IPermissionService>()));
        services.AddSingleton<ITool>(provider => new RememberPersonTool(
            provider.GetService<IMessagingProvider>(), provider.GetService<IPersonStore>(), provider.GetRequiredService<TimeProvider>(), provider.GetService<IPermissionPolicy>()));
        services.AddConnectedApps();

        // The user's Home Assistant, through its own API with the token they pasted: a tool that does one thing to one device, confirmed each time, and one that
        // reads what the devices are. They are offered only while a Home Assistant is connected and the request is about the home.
        services.TryAddSingleton<Assistant.Core.Home.IHomeAssistant>(provider => new Home.HomeAssistantService(
            provider.GetRequiredService<ISettingsService>(), provider.GetService<ISecretStore>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<Home.HomeAssistantService>>(), provider.GetService<IInstalledIntegrationRegistry>()));
        services.AddSingleton<ITool>(provider => new Home.ControlHomeDeviceTool(provider.GetService<Assistant.Core.Home.IHomeAssistant>(), provider.GetService<IMemoryStore>()));
        services.AddSingleton<ITool>(provider => new Home.GetHomeDevicesTool(provider.GetService<Assistant.Core.Home.IHomeAssistant>()));
        services.AddSingleton<Search.WebSearchService>();
        services.AddSingleton<Search.IWebSearchService>(provider => provider.GetRequiredService<Search.WebSearchService>());
        services.AddSingleton<IMcpToolVeto>(provider => provider.GetRequiredService<Search.WebSearchService>());
        services.AddSingleton<ITool, Search.SearchWebTool>();
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<ToolExecutor>();
        return services;
    }

    /// <summary>
    /// Registers the connected apps (PROJECT_SPEC section 4.8, step 104): the registry of installed integrations (kept in
    /// <c>integrations.json</c> in the app's folder), the MCP client factory, the connection manager and the source that loads the tools of
    /// the apps a request is about, which the tool registry and the executor take in beside the built-in tools. Nothing is read or connected
    /// to until a request needs it, and with no integration installed it costs nothing. Needs <see cref="AppPaths"/>, <see cref="ISettingsService"/>,
    /// <see cref="ISecretStore"/> and <see cref="TimeProvider"/> to be registered, and ends the connections when the container is disposed.
    /// </summary>
    public static IServiceCollection AddConnectedApps(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new McpLoadingOptions());
        services.TryAddSingleton(new McpClientOptions());
        services.TryAddSingleton<IInstalledIntegrationStore>(
            provider => new JsonInstalledIntegrationStore(provider.GetRequiredService<AppPaths>().IntegrationsFilePath));
        services.TryAddSingleton<IInstalledIntegrationRegistry>(provider => new InstalledIntegrationRegistry(
            provider.GetRequiredService<IInstalledIntegrationStore>(),
            provider.GetRequiredService<ILogger<InstalledIntegrationRegistry>>(),
            provider.GetService<ISecretStore>()));

        // Signing in to a connected app with OAuth (the browser opens on the app's own page; the Assistant never sees a password). The browser is the UI's; without one
        // a sign-in cannot start. What a sign-in gives is kept in the secret store and renewed when the connection is made.
        services.TryAddSingleton<IOAuthBrowser, NoOAuthBrowser>();
        services.TryAddSingleton(provider => new McpOAuthClient(provider.GetRequiredService<IOAuthBrowser>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<McpOAuthSessions>(provider => new McpOAuthSessions(
            provider.GetRequiredService<ISecretStore>(), provider.GetRequiredService<McpOAuthClient>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IMcpAccessTokens>(provider => provider.GetRequiredService<McpOAuthSessions>());
        services.TryAddSingleton<IMcpClientFactory>(
            provider => new McpClientFactory(
                provider.GetService<ISecretStore>(), provider.GetRequiredService<McpClientOptions>(),
                provider.GetService<ISecretStore>() is null ? null : provider.GetService<IMcpAccessTokens>()));
        services.TryAddSingleton<IMcpToolSelector>(LexicalMcpToolSelector.Instance);

        // Messaging through a connected messaging app (step 116): what the Assistant's own draft and send tools use when an integration can send a text. It is there for every
        // request and costs nothing while no such integration is installed; the tools are offered only while one is and Messaging is allowed. It also keeps that app's own
        // sending tool from the model, so that a message goes only to someone the user saved.
        services.TryAddSingleton(provider => new McpMessagingProvider(
            provider.GetRequiredService<IInstalledIntegrationRegistry>(),
            provider.GetRequiredService<McpConnectionManager>(),
            provider.GetService<IPermissionPolicy>(),
            provider.GetRequiredService<ILogger<McpMessagingProvider>>(),
            provider.GetService<IMemoryStore>()));
        services.TryAddSingleton<IMessagingProvider>(provider => provider.GetRequiredService<McpMessagingProvider>());
        services.AddSingleton<IMcpToolVeto>(provider => provider.GetRequiredService<McpMessagingProvider>());
        services.TryAddSingleton<IMcpToolCache>(provider => new JsonMcpToolCache(Path.Combine(provider.GetRequiredService<AppPaths>().CacheDirectory, "integration-tools.json")));
        services.TryAddSingleton(provider => new McpConnectionManager(
            provider.GetRequiredService<IInstalledIntegrationRegistry>(),
            provider.GetRequiredService<IMcpClientFactory>(),
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<McpLoadingOptions>(),
            provider.GetRequiredService<ILogger<McpConnectionManager>>(),
            provider.GetService<IMcpToolCache>(),
            provider.GetService<ISecretStore>() is null ? null : provider.GetService<IMcpAccessTokens>()));
        services.TryAddSingleton<IMcpCatalogProvider>(provider => provider.GetRequiredService<McpConnectionManager>());
        services.TryAddSingleton<IDynamicToolSource, McpToolSource>();
        services.AddIntegrationResolution();
        services.AddIntegrationFinder();
        services.AddIntegrationInstallation();
        return services;
    }

    /// <summary>
    /// Registers what reviews, installs and looks after the integrations the Assistant installs (PROJECT_SPEC section 4.8, steps 107-109): the review of what
    /// the finder found, the downloader with its fixed list of hosts, the managed runtimes (Node.js and Python, set up in the Assistant's own folder only when an
    /// integration the user approved needs them), the installer, and the broker of offers that only the user's click on Install can accept. Needs
    /// <see cref="AppPaths"/>, <see cref="ISettingsService"/>, <see cref="IPermissionPolicy"/>, <see cref="TimeProvider"/> and the services of
    /// <see cref="AddConnectedApps"/>.
    /// </summary>
    public static IServiceCollection AddIntegrationInstallation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registered once, however often this is called.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IIntegrationInstaller)))
        {
            return services;
        }

        services.TryAddSingleton(new CandidateReviewerOptions());
        services.TryAddSingleton<IDiscoveryHttp>(_ => new DiscoveryHttp());
        services.TryAddSingleton<IPackageMetadata>(provider => new RegistryPackageMetadata(provider.GetRequiredService<IDiscoveryHttp>()));
        services.TryAddSingleton<ICandidateReviewer>(provider => new CandidateReviewer(
            provider.GetRequiredService<IPackageMetadata>(),
            provider.GetRequiredService<IInstalledIntegrationRegistry>(),
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<IPermissionPolicy>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<CandidateReviewerOptions>(),
            provider.GetRequiredService<ILogger<CandidateReviewer>>()));
        services.TryAddSingleton(provider => new IntegrationLayout(provider.GetRequiredService<AppPaths>()));
        services.TryAddSingleton<IPackageDownloader>(_ => new PackageDownloader(PackageDownloadPolicy.Standard));
        services.TryAddSingleton<IManagedRuntimes>(provider => new ManagedRuntimes(
            provider.GetRequiredService<IntegrationLayout>(),
            provider.GetRequiredService<IPackageDownloader>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ManagedRuntimes>>()));
        services.TryAddSingleton<IIntegrationConnections>(provider => provider.GetRequiredService<McpConnectionManager>());
        services.TryAddSingleton<IIntegrationInstaller>(provider => Audited(provider, new IntegrationInstaller(
            provider.GetRequiredService<IntegrationLayout>(),
            provider.GetRequiredService<IInstalledIntegrationRegistry>(),
            provider.GetRequiredService<IManagedRuntimes>(),
            provider.GetRequiredService<IPackageDownloader>(),
            provider.GetRequiredService<IMcpClientFactory>(),
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<IPermissionPolicy>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<IntegrationInstaller>>(),
            null,
            new ProcessRunner(),
            provider.GetRequiredService<IIntegrationConnections>())));
        services.TryAddSingleton<IIntegrationConnector>(provider => provider.GetService<IAuditTrail>() is { } audit
            ? new AuditedIntegrationConnector(provider.GetRequiredService<IntegrationConnector>(), audit)
            : provider.GetRequiredService<IntegrationConnector>());
        services.TryAddSingleton(provider => new IntegrationConnector(
            provider.GetRequiredService<IInstalledIntegrationRegistry>(),
            provider.GetRequiredService<McpOAuthClient>(),
            provider.GetRequiredService<McpOAuthSessions>(),
            provider.GetRequiredService<McpConnectionManager>(),
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<ISecretStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<IntegrationConnector>>(),
            events: provider.GetService<IAppEventBus>()));
        services.TryAddSingleton<IIntegrationOffers>(provider => Audited(provider, new IntegrationOffers(
            provider.GetRequiredService<IIntegrationInstaller>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<IntegrationOffers>>(),
            connector: provider.GetService<ISecretStore>() is null ? null : provider.GetService<IIntegrationConnector>())));
        services.TryAddSingleton<IIntegrationManager>(provider => Audited(provider, new IntegrationManager(
            provider.GetRequiredService<IInstalledIntegrationRegistry>(),
            provider.GetRequiredService<IIntegrationConnections>(),
            provider.GetRequiredService<IIntegrationInstaller>(),
            provider.GetRequiredService<IManagedRuntimes>(),
            provider.GetRequiredService<IntegrationLayout>(),
            provider.GetService<IMcpToolCache>(),
            provider.GetRequiredService<IPackageMetadata>(),
            provider.GetRequiredService<ICandidateReviewer>(),
            provider.GetRequiredService<IIntegrationOffers>(),
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<IPermissionPolicy>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<IntegrationManager>>())));
        return services;
    }

    /// <summary>
    /// Registers what resolves a request for an external app (PROJECT_SPEC section 4.8, step 105): the reader that finds the app and the capability in a
    /// request, the resolver (installed integrations first, then servers already set up in other programs on this PC, and only then the finder),
    /// and the source that looks at those other programs' files. It asks the registry, the connection manager and the settings, and
    /// <see cref="IPermissionPolicy"/> (the Files permission for the other programs' files, and a permission an installed integration needs).
    /// </summary>
    public static IServiceCollection AddIntegrationResolution(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registered once, however often this is called: the sources are a list, so a second registration would make each appear twice.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IIntegrationResolver)))
        {
            return services;
        }

        services.TryAddSingleton(new IntegrationResolverOptions());
        services.TryAddSingleton<IIntegrationRequestReader>(IntegrationRequestReader.Instance);
        services.AddSingleton<IAvailableIntegrationSource>(
            provider => new LocalMcpConfigSource(provider.GetRequiredService<ILogger<LocalMcpConfigSource>>(), provider.GetService<IPermissionPolicy>()));
        services.TryAddSingleton<IIntegrationResolver>(provider => new IntegrationResolver(
            provider.GetRequiredService<IInstalledIntegrationRegistry>(),
            provider.GetRequiredService<IMcpCatalogProvider>(),
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<IIntegrationRequestReader>(),
            provider.GetServices<IAvailableIntegrationSource>(),
            provider.GetService<IPermissionPolicy>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IntegrationResolverOptions>(),
            provider.GetRequiredService<ILogger<IntegrationResolver>>()));
        return services;
    }

    /// <summary>
    /// Registers the Integration Finder (PROJECT_SPEC section 4.8, step 106) and the handler that answers a request for an external app that cannot be served:
    /// the places that list integrations (the official MCP registry and GitHub first, npm and PyPI when needed), the reader of a GitHub
    /// repository's README, the finder's cache (<c>integration-discovery.json</c> in the app's cache folder), the local model's judgement of the
    /// few candidates, and <see cref="IConnectedAppRequestHandler"/>, which the orchestrator asks. The finder reaches the network only when
    /// Local Only mode is off and External Web and Image Search is allowed. Needs <see cref="AppPaths"/>, <see cref="ISettingsService"/>,
    /// <see cref="IPermissionPolicy"/>, <see cref="IModelService"/> and <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddIntegrationFinder(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registered once, however often this is called: the sources are a list, so a second registration would make each appear twice.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IIntegrationFinder)))
        {
            return services;
        }

        services.TryAddSingleton(new IntegrationFinderOptions());
        services.TryAddSingleton(new CapabilityNeedSwitch());
        services.TryAddSingleton<IDiscoveryHttp>(_ => new DiscoveryHttp());
        services.AddSingleton<IIntegrationDiscoverySource>(provider => new OfficialMcpRegistrySource(provider.GetRequiredService<IDiscoveryHttp>()));
        services.AddSingleton<IIntegrationDiscoverySource>(provider => new GitHubDiscoverySource(provider.GetRequiredService<IDiscoveryHttp>()));
        services.AddSingleton<IIntegrationDiscoverySource>(provider => new NpmDiscoverySource(provider.GetRequiredService<IDiscoveryHttp>()));
        services.AddSingleton<IIntegrationDiscoverySource>(provider => new PyPiDiscoverySource(provider.GetRequiredService<IDiscoveryHttp>()));
        services.TryAddSingleton<IRepositoryEnricher>(provider => new GitHubRepositoryEnricher(provider.GetRequiredService<IDiscoveryHttp>()));
        services.TryAddSingleton<IDiscoveryCache>(provider => new DiscoveryCache(
            Path.Combine(provider.GetRequiredService<AppPaths>().CacheDirectory, "integration-discovery.json"),
            provider.GetRequiredService<IntegrationFinderOptions>().MaxCacheEntries));
        services.TryAddSingleton<ICandidateAssessor>(provider => new ModelCandidateAssessor(
            provider.GetRequiredService<IModelService>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<ModelCandidateAssessor>>()));
        services.TryAddSingleton<IIntegrationFinder>(provider => Audited(provider, new IntegrationFinder(
            provider.GetServices<IIntegrationDiscoverySource>(),
            provider.GetRequiredService<IRepositoryEnricher>(),
            provider.GetService<ICandidateAssessor>(),
            provider.GetRequiredService<IDiscoveryCache>(),
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<IPermissionPolicy>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IntegrationFinderOptions>(),
            provider.GetRequiredService<ILogger<IntegrationFinder>>())));
        services.TryAddSingleton<IConnectedAppRequestHandler>(provider => new ConnectedAppRequestHandler(
            provider.GetRequiredService<IIntegrationResolver>(),
            provider.GetRequiredService<IIntegrationFinder>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ConnectedAppRequestHandler>>(),
            provider.GetService<ICandidateReviewer>(),
            provider.GetService<IIntegrationOffers>(),
            provider.GetService<IActivityTracker>(),
            provider.GetRequiredService<CapabilityNeedSwitch>(),
            provider.GetService<ICalendarProvider>(),
            provider.GetService<IMessagingProvider>(),
            provider.GetService<IPermissionGate>(),
            provider.GetService<ISettingsService>(),
            KnownEndpoints.For));
        return services;
    }

    // What the Assistant does with integrations is recorded in the activity log (step 117), when the app has one: looking for, offering, installing, updating and
    // removing them. Without a log (a host that has none) they are what they were.
    private static IIntegrationFinder Audited(IServiceProvider provider, IIntegrationFinder inner) =>
        provider.GetService<IAuditTrail>() is { } audit ? new AuditedIntegrationFinder(inner, audit) : inner;

    private static IIntegrationInstaller Audited(IServiceProvider provider, IIntegrationInstaller inner) =>
        provider.GetService<IAuditTrail>() is { } audit ? new AuditedIntegrationInstaller(inner, audit) : inner;

    private static IIntegrationOffers Audited(IServiceProvider provider, IIntegrationOffers inner) =>
        provider.GetService<IAuditTrail>() is { } audit ? new AuditedIntegrationOffers(inner, audit) : inner;

    private static IIntegrationManager Audited(IServiceProvider provider, IIntegrationManager inner) =>
        provider.GetService<IAuditTrail>() is { } audit ? new AuditedIntegrationManager(inner, audit) : inner;
}
