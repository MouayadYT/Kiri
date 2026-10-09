using Assistant.Core.Activity;
using Assistant.Core.Agent;
using Assistant.Core.Assets;
using System.Net.Http;
using Assistant.Core.Audit;
using Assistant.Core.Budgeting;
using Assistant.Core.Confirmation;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Diagnostics;
using Assistant.Core.Events;
using Assistant.Core.Gaming;
using Assistant.Core.Hardware;
using Assistant.Core.Imaging;
using Assistant.Core.Ipc;
using Assistant.Core.ImageSearch;
using Assistant.Core.ModelHosting;
using Assistant.Core.ModelProfiles;
using Assistant.Core.MultiFile;
using Assistant.Core.Ocr;
using Assistant.Core.Orchestration;
using Assistant.Core.Permissions;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.QuickSearch.Clipboard;
using Assistant.Core.QuickSearch.Routing;
using Assistant.Core.Startup;
using Assistant.Core.Storage;
using Assistant.Core.Tools;
using Assistant.Core.Voice;
using Assistant.Data;
using Assistant.Data.QuickSearch;
using Assistant.Data.Settings;
using Assistant.Documents;
using Assistant.Search;
using Assistant.Search.Applications;
using Assistant.Search.Files;
using Assistant.Search.Planning;
using Assistant.Tools;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Browser;
using Assistant.UI.Capture;
using Assistant.UI.Explorer;
using Assistant.UI.Gaming;
using Assistant.UI.History;
using Assistant.UI.ImageSearch;
using Assistant.UI.Messages;
using Assistant.UI.Search;
using Assistant.UI.Selection;
using Assistant.UI.Settings;
using Assistant.UI.Tray;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Voice;
using Assistant.UI.Windowing;
using Assistant.Voice;
using Assistant.Windows.Audio;
using Assistant.Windows.Backdrop;
using Assistant.Windows.Capture;
using Assistant.Windows.Clipboard;
using Assistant.Windows.Credentials;
using Assistant.Windows.Frame;
using Assistant.Windows.Gaming;
using Assistant.Windows.Hardware;
using Assistant.Windows.Hotkeys;
using Assistant.Windows.Imaging;
using Assistant.Windows.Ocr;
using Assistant.Windows.Placement;
using Assistant.Windows.Selection;
using Assistant.Windows.Shell;
using Assistant.Windows.Startup;
using Assistant.Windows.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Bootstrap;

/// <summary>Service registrations for the composition root (PROJECT_SPEC §5.2).</summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers an implementation of every Core contract. Implementations from <c>Bootstrap/Placeholders</c> are
    /// temporary: each module's step replaces its registration with the real one.
    /// </summary>
    public static IServiceCollection AddAssistantServices(this IServiceCollection services)
    {
        // Resolved inside the bootstrapper's error handling, so a missing profile folder is logged.
        services.AddSingleton(_ => AppPaths.ForCurrentUser());
        services.AddSingleton<IAppEventBus, AppEventBus>();
        services.AddSingleton(TimeProvider.System);

        // The user's settings, kept in a versioned JSON file in the app's folder: a damaged file falls back on the last good
        // copy or the defaults and never fails the start. Tokens and passwords are not settings: they go to Windows
        // Credential Manager through ISecretStore, and nothing uses one yet.
        services.AddAssistantSettings();
        services.AddSingleton<ISecretStore, WindowsCredentialSecretStore>();

        // What the user allows the Assistant to use (files, the screen, selected text and more), read from the same
        // settings the Permissions page changes. Whatever reads a file, the screen or a selection asks it first.
        services.AddSingleton<IPermissionPolicy, SettingsPermissionPolicy>();

        // A permission can be set to ask every time (step 119): the gate decides, and asks the user when it is, in a small window of its own (a use inside a conversation is asked in
        // the conversation instead). The services that read a file, the screen or a selection check the policy themselves, so what the gate allows is all they see.
        services.AddSingleton<IPermissionPrompt, Assistant.UI.Permissions.WpfPermissionPrompt>();
        services.AddSingleton<IPermissionGate>(provider => new PermissionGate(
            provider.GetRequiredService<IPermissionPolicy>(), provider.GetRequiredService<IPermissionPrompt>(), provider.GetRequiredService<ILogger<PermissionGate>>()));

        // The conversation history in the local SQLite database: created and brought up to date when it is first needed,
        // and read and written away from the UI thread.
        services.AddAssistantData();

        // What the Assistant did on the user's behalf (step 117): each tool of a multi-step run and what the user answered when asked, and the integrations it looked for,
        // offered, installed, updated and removed. One log, three views of it: the runs of the agent report to it, the integration code records in it, and the activity page
        // and the panel in the conversation read it. Names, codes and fixed words only; the database keeps it while history is on.
        services.AddSingleton(provider => new AuditLog(
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<AuditLog>>(),
            provider.GetService<IAuditStore>(), provider.GetService<ISettingsService>()));
        services.AddSingleton<IAgentTaskLog>(provider => provider.GetRequiredService<AuditLog>());
        services.AddSingleton<IAuditTrail>(provider => provider.GetRequiredService<AuditLog>());
        services.AddSingleton<IAuditHistory>(provider => provider.GetRequiredService<AuditLog>());
        services.AddHostedService(provider => new AuditShutdown(provider.GetRequiredService<AuditLog>()));

        // Searches, model calls and tool calls report themselves at the contract boundary, whatever the implementation,
        // so the Searching chip shows while any of them runs.
        services.AddSingleton<IActivityTracker, ActivityTracker>();
        // Files and folders come from the Windows Search index (never a crawl of the disk). A request to find files is planned
        // (by rule, or by the local model under a strict schema, falling back on the request's own words), checked against the
        // Files permission and searched; the bar's results and the conversation's answers both go through it. A result opens or
        // shows in File Explorer through the shell.
        services.AddSingleton<WindowsFileSearchService>();
        services.AddSingleton<IFileSearchService>(provider => new ActivityFileSearchService(
            new PermissionCheckedFileSearchService(provider.GetRequiredService<WindowsFileSearchService>(), provider.GetRequiredService<IPermissionPolicy>()),
            provider.GetRequiredService<IActivityTracker>()));
        services.AddSingleton<IFileLauncher, ShellFileLauncher>();
        // The text of the user's own documents (.txt, .md, .pdf, .docx, .pptx), read by type with where each part came from. A
        // question about an attached file is given the passages of it that its words point to (read, cut into passages, ranked by
        // keyword), and the reading shows the Searching chip ("Reading"). A file of any other type is reported Unsupported and never
        // parsed on a guess.
        services.AddAssistantDocuments();
        services.AddSingleton<IDocumentContextService>(provider => new ActivityDocumentContextService(
            new PermissionCheckedDocumentContextService(provider.GetRequiredService<DocumentContextService>(), provider.GetRequiredService<IPermissionPolicy>()),
            provider.GetRequiredService<IActivityTracker>()));
        // What the model may do with the user's files, in the middle of an answer: find them (search_files) and read one that was found
        // or attached in the conversation (read_file_text). Both only read, and each asks the Files permission first.
        services.AddAssistantTools();
        // Everything that changes something is confirmed by the user, in the conversation it was asked in, before it is done (step 115): the question
        // ("may I do this?", with exactly what it would do) is published, the conversation that is streaming the answer shows it inline, and the call runs only on a yes.
        services.AddSingleton<IPermissionService, ConfirmationBroker>();

        // Model-host and tool failures are logged at the contract boundary, whatever the implementation. The model
        // service generates with the local model in the model host, loading the model the settings name on its first use.
        // Model profiles say which files make a model and how to run it; a hardware preset says how much context and which
        // engine options the machine gets. The model service loads the profile the settings choose (the default one when
        // none is chosen and no file was picked by hand) once its files are in the models folder.
        services.AddSingleton<IModelProfileCatalog, ModelProfileCatalog>();

        // What this PC offers a model (step 124): the processor, the memory and the graphics cards with the video memory each has, read from Windows on this PC and
        // never sent anywhere. One reading decides the hardware preset and the recommendation: the profile and context window used until the user chooses their own.
        services.AddSingleton<IHardwareProfileService, WindowsHardwareProfileService>();
        services.AddHostedService<HardwareProfileWarmUp>();
        services.AddSingleton<IHardwareInfoProvider>(provider => new HardwareProfileInfoProvider(
            provider.GetRequiredService<IHardwareProfileService>(), new EnvironmentHardwareInfoProvider()));
        services.AddSingleton<IModelRecommender, ModelRecommender>();

        // The assets that come packaged with the Assistant (step 123): the models and the text-to-speech engines in the folder beside the program files, each with a manifest
        // of sizes and SHA-256 checksums that is checked in the background soon after the app starts and again before a packaged model is loaded. A model the user added
        // to their own models folder is used first and listed nowhere.
        services.AddSingleton(_ => PackagedAssetPaths.ForCurrentProcess());
        services.AddSingleton<IAssetCheckCache>(provider =>
            new JsonAssetCheckCache(System.IO.Path.Combine(provider.GetRequiredService<AppPaths>().CacheDirectory, "asset-checks.json")));
        services.AddSingleton<PackagedAssets>(provider => new PackagedAssets(
            provider.GetRequiredService<PackagedAssetPaths>(), provider.GetRequiredService<IAssetCheckCache>(),
            provider.GetRequiredService<IAppEventBus>(), provider.GetRequiredService<ILogger<PackagedAssets>>()));
        services.AddSingleton<IPackagedAssets>(provider => provider.GetRequiredService<PackagedAssets>());
        services.AddSingleton<ITextToSpeechAssets, TextToSpeechAssets>();
        services.AddHostedService<PackagedAssetsStartupCheck>();

        services.AddSingleton<IModelProfileResolver>(provider => new ModelProfileResolver(
            provider.GetRequiredService<IModelProfileCatalog>(), provider.GetRequiredService<IHardwareInfoProvider>(), provider.GetRequiredService<AppPaths>(),
            recommender: provider.GetRequiredService<IModelRecommender>(),
            packagedModelsDirectory: provider.GetRequiredService<PackagedAssetPaths>().ModelsDirectory));
        services.AddSingleton<LocalModelService>();
        services.AddSingleton<IModelPreloader>(provider => provider.GetRequiredService<LocalModelService>());

        // The Windows Clock app, which the clock tools set alarms, timers, the stopwatch and focus sessions in.
        services.AddSingleton<Assistant.Core.Clock.IClockApp>(provider => new Assistant.Windows.Clock.WindowsClockApp(
            provider.GetService<Assistant.Core.Memory.IMemoryStore>(), provider.GetService<ILogger<Assistant.Windows.Clock.WindowsClockApp>>()));

        // This PC's displays, for a window the user wants on one of them (the Clock app's), and the mark that shows them which display is meant.
        services.AddSingleton<Assistant.Core.Displays.IDisplays, Assistant.Windows.Displays.WindowsDisplays>();
        services.AddSingleton<Assistant.Core.Displays.IDisplayPointer, Assistant.UI.Views.DisplayPointer>();

        // The model's context window follows the conversation: the ordinary one, and the larger while a conversation carries files.
        services.AddSingleton<IModelContextDemand>(provider => provider.GetRequiredService<LocalModelService>());
        services.AddSingleton<IModelService>(provider => new ActivityModelService(
            ActivatorUtilities.CreateInstance<LoggingModelService>(provider, provider.GetRequiredService<LocalModelService>()),
            provider.GetRequiredService<IActivityTracker>()));
        services.AddSingleton<IFileSearchPlanner, FileSearchPlanner>();
        services.AddSingleton<IFileMatchReviewer, ModelFileMatchReviewer>();
        services.AddSingleton<IFileRequestService, FileRequestService>();
        // A chat turn: the prompt built in one place and fitted into the model's context window under the user's limits,
        // its images made ready for a vision model (copies, scaled down and re-encoded by the Windows codecs when too
        // large), the model's answer streamed, the in-memory conversation updated.
        services.AddSingleton<ITokenEstimator, HeuristicTokenEstimator>();
        services.AddSingleton<ContextBudgeter>();

        // The one place context is kept and fitted: everything that supplies context gives it here, and the prompt is fitted through it.
        services.AddSingleton<IContextService, ContextService>();
        services.AddSingleton(provider => new PromptBuilder(
            provider.GetRequiredService<IContextService>(), provider.GetRequiredService<TimeProvider>(), provider.GetService<Assistant.Core.Memory.IMemoryStore>()));
        services.AddSingleton<IImagePreprocessor>(_ => new ImagePreprocessor(ImagePreprocessingOptions.Default));

        // The text in a screenshot, read on this PC by the Windows OCR engine when it is wanted and not before: for a model that cannot see
        // images, in place of the picture, and for one that can, when it asks for the exact words (the read_screen_text tool).
        services.AddSingleton<IOcrEngine, WindowsOcrEngine>();
        services.AddSingleton<IScreenText, ScreenTextService>();
        // How an agent run went (rounds, offered tools, timings, why it stopped; names and counts only, in memory, step 114), kept apart from the conversation.
        services.AddSingleton<AgentTraceStore>();
        services.AddSingleton<IAgentTraceSink>(provider => provider.GetRequiredService<AgentTraceStore>());
        services.AddSingleton<IAssistantOrchestrator, AssistantOrchestrator>();

        // The files of a question, however many and however long: read a few at a time, and when what the question needs of them does
        // not fit one prompt, the local model takes short notes on each piece of them first and the answer is put together from the
        // notes (map/reduce), within fixed limits on files, requests at once and in all, and text held in memory. Its requests to the
        // model are not reported as "Thinking": it says itself how far it has got ("Reading 3 of 9").
        services.AddSingleton<IMultiFileProcessor>(provider => new MultiFileProcessor(
            provider.GetRequiredService<IDocumentContextService>(),
            ActivatorUtilities.CreateInstance<LoggingModelService>(provider, provider.GetRequiredService<LocalModelService>()),
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<PromptBuilder>(),
            provider.GetRequiredService<ITokenEstimator>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IActivityTracker>(),
            provider.GetRequiredService<ILogger<MultiFileProcessor>>()));
        // The local model: the app starts its model host the first time a model is loaded, and follows the model's status.
        services.AddSingleton(_ => ModelHostLaunchOptions.InDirectory(AppContext.BaseDirectory));
        services.AddSingleton<IModelHostLauncher, ModelHostProcessLauncher>();
        services.AddSingleton<ModelLifecycle>();
        services.AddSingleton<IModelLifecycle>(provider => provider.GetRequiredService<ModelLifecycle>());
        // The tray menu's Pause Local AI: the same object, which also refuses to load the model while the user has paused it.
        services.AddSingleton<ILocalAiPause>(provider => provider.GetRequiredService<ModelLifecycle>());
        services.AddHostedService<ModelLifecycleShutdown>();
        services.AddSingleton<IToolExecutor>(provider => new ActivityToolExecutor(
            ActivatorUtilities.CreateInstance<LoggingToolExecutor>(provider, provider.GetRequiredService<ToolExecutor>()),
            provider.GetRequiredService<IActivityTracker>()));

        // Instant search (PROJECT_SPEC §4.1): providers that answer for what is typed, side by side, from memory or the Windows Search index,
        // ranked by fixed rules and never by the model. What the user runs is remembered as salted hashes in the cache folder, so what they
        // use comes first. The applications are the ones Start lists, read in the background and kept; their icons come from the shell.
        services.AddSingleton<IQuickSearchUsageStorage>(provider =>
            new JsonQuickSearchUsageStorage(System.IO.Path.Combine(provider.GetRequiredService<AppPaths>().CacheDirectory, "quick-search-usage.json")));
        services.AddSingleton<IQuickSearchUsage>(provider => new QuickSearchUsage(
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IQuickSearchUsageStorage>()));
        // Beside what Start lists, the applications PowerToys Run finds on the Desktop: its shortcuts, programs and game launchers' links (a game
        // installed by Epic or Steam is only that). One list, each application once.
        services.AddSingleton<ShellApplicationSource>();
        services.AddSingleton<ProgramShortcutSource>();
        services.AddSingleton<IApplicationSource>(provider => new CombinedApplicationSource(
            provider.GetRequiredService<ShellApplicationSource>(), provider.GetRequiredService<ProgramShortcutSource>()));
        services.AddSingleton<IApplicationIconSource, ShellApplicationIconSource>();
        services.AddSingleton<IApplicationLauncher, ShellApplicationLauncher>();
        services.AddSingleton<IApplicationCatalog>(provider => new ApplicationCatalog(
            provider.GetRequiredService<IApplicationSource>(), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new ApplicationsQuickSearchProvider(
            provider.GetRequiredService<IApplicationCatalog>(), provider.GetRequiredService<IApplicationIconSource>(),
            provider.GetRequiredService<IQuickSearchUsage>(), provider.GetRequiredService<TimeProvider>()));
        services.AddHostedService<QuickSearchWarmUp>();
        services.AddSingleton<IQuickActionCatalog>(QuickActionCatalog.Default);
        services.AddSingleton<ISystemActions, WindowsSystemActions>();

        // What the user copies, kept only while they have allowed it: in memory, a few items, forgotten when it is turned off.
        services.AddSingleton<IClipboardHistory>(provider => new ClipboardHistory(clock: provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IClipboardWatcher>(provider => new ClipboardWatcher(provider.GetRequiredService<IClipboardHistory>().Limits));
        services.AddSingleton<ClipboardHistoryController>();
        services.AddHostedService(provider => provider.GetRequiredService<ClipboardHistoryController>());

        // Window-level services from Assistant.Windows, used by the views.
        services.AddSingleton<IWindowBackdropFactory, WindowBackdropFactory>();
        services.AddSingleton<IWindowPlacementService, WindowPlacementService>();
        services.AddSingleton<IWindowFrameFactory, WindowFrameFactory>();
        services.AddSingleton<GlobalHotkeyService>();

        // The screen, captured locally into memory (never saved), and the second shortcut Windows is given: the one for Visual Intelligence.
        services.AddSingleton<ScreenCaptureService>();
        services.AddSingleton<IScreenCapture>(provider => new PermissionCheckedScreenCapture(
            provider.GetRequiredService<ScreenCaptureService>(), provider.GetRequiredService<IPermissionPolicy>()));
        services.AddKeyedSingleton(
            VisualIntelligenceHotkey,
            (provider, _) => new GlobalHotkeyService(
                provider.GetRequiredService<ILogger<GlobalHotkeyService>>(), GlobalHotkeyService.VisualIntelligenceHotkeyId));
        // One microphone, shared (step 125): the voice glow and the orb read its level, speech recognition its audio, and the wake word listener its audio while the
        // user has the wake word on. The device is open only while one of them needs it.
        services.AddSingleton<MicrophoneChoice>();
        services.AddSingleton<IMicrophoneDevices, WindowsMicrophoneDevices>();
        services.AddSingleton<IMicrophoneProbe, MicrophoneProbe>();
        services.AddSingleton<MicrophoneLevelMeter>();
        services.AddSingleton<IMicrophoneLevelMeter>(provider => provider.GetRequiredService<MicrophoneLevelMeter>());
        services.AddSingleton<IMicrophoneAudioSource>(provider => provider.GetRequiredService<MicrophoneLevelMeter>());

        // Local voices, recognition and wake word use managed or packaged files. Optional speech APIs require explicit
        // remote-speech consent; keys are endpoint-scoped and spoken text is never logged. CUDA runs in a separate worker.
        services.AddSingleton(provider => new VoiceModelFolders(provider.GetRequiredService<AppPaths>(), provider.GetRequiredService<IPackagedAssets>()));
        services.AddKeyedSingleton("speech-api", (_, _) => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(65) });
        services.AddSingleton<ITextToSpeechEngineFactory>(provider => new TextToSpeechEngineFactory(provider.GetRequiredService<VoiceModelFolders>(),
            settings: provider.GetRequiredService<ISettingsService>(), secrets: provider.GetRequiredService<ISecretStore>(), http: provider.GetRequiredKeyedService<HttpClient>("speech-api"), paths: provider.GetRequiredService<AppPaths>()));
        services.AddSingleton<IAudioOutput, WasapiAudioOutput>();
        services.AddSingleton<ITextToSpeechService>(provider => new TextToSpeechService(
            provider.GetRequiredService<ITextToSpeechEngineFactory>(), provider.GetRequiredService<IAudioOutput>(),
            logger: provider.GetService<ILogger<TextToSpeechService>>()));
        services.AddSingleton<HandyIntegration>();
        services.AddSingleton<ISpeechToTextService, SelectableSpeechToTextService>();
        services.AddSingleton<IWakeWordService>(provider => new SherpaWakeWordService(
            provider.GetRequiredService<VoiceModelFolders>(), provider.GetRequiredService<AppPaths>(), logger: provider.GetService<ILogger<SherpaWakeWordService>>()));
        services.AddSingleton(provider => new MicrophoneRouter(
            provider.GetRequiredService<IMicrophoneAudioSource>(), provider.GetRequiredService<ISpeechToTextService>(),
            provider.GetRequiredService<IWakeWordService>(), provider.GetRequiredService<TimeProvider>(), provider.GetService<ILogger<MicrophoneRouter>>()));
        services.AddSingleton<IVoiceInput>(provider => provider.GetRequiredService<MicrophoneRouter>());
        services.AddSingleton<ISpokenAnswers>(provider => new SpokenAnswers(
            provider.GetRequiredService<ITextToSpeechService>(), action => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action)));
        services.AddSingleton<VoiceRuntime>();
        services.AddSingleton<IVoiceRuntime>(provider => provider.GetRequiredService<VoiceRuntime>());
        services.AddHostedService(provider => provider.GetRequiredService<VoiceRuntime>());
        services.AddSingleton<WakeWordController>();

        // Game mode: while a game runs, the local model, the voice, the recognizer and the wake word are let go of, and put back when it ends. The
        // detector is told by Windows when the window in front changes and runs only while the user has game mode on. Registered after the voice,
        // which it suspends, so that it starts after it.
        services.AddSingleton<IVoiceSuspension>(provider => provider.GetRequiredService<VoiceRuntime>());
        services.AddSingleton<IGameDetector>(provider => new GameDetector(
            provider.GetService<ILogger<GameDetector>>(), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new GameModeController(
            provider.GetRequiredService<ISettingsService>(), provider.GetRequiredService<IAppEventBus>(), provider.GetRequiredService<IGameDetector>(),
            provider.GetRequiredService<ILocalAiPause>(), provider.GetRequiredService<IVoiceSuspension>(), provider.GetRequiredService<HandyIntegration>(),
            provider.GetRequiredService<AppPaths>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<GameModeController>>(),
            provider.GetRequiredService<MicrophoneRouter>()));
        services.AddSingleton<IGameMode>(provider => provider.GetRequiredService<GameModeController>());
        services.AddHostedService(provider => provider.GetRequiredService<GameModeController>());

        // The text selected in the application in front, read through UI Automation when the user asks and never kept, and the third
        // shortcut Windows is given: the one that reads it and opens the Ask panel with it.
        services.AddSingleton<SelectionService>();
        services.AddSingleton<ISelectionService>(provider => new PermissionCheckedSelectionService(
            provider.GetRequiredService<SelectionService>(), provider.GetRequiredService<IPermissionPolicy>()));
        services.AddKeyedSingleton(
            SelectedTextHotkey,
            (provider, _) => new GlobalHotkeyService(
                provider.GetRequiredService<ILogger<GlobalHotkeyService>>(), GlobalHotkeyService.SelectedTextHotkeyId));

        // The fallback for apps that do not share their selection (step 89): it presses Copy, reads the clipboard and puts it back, only when
        // the user has allowed it and pressed the fourth shortcut Windows is given.
        services.AddSingleton<CopySelectionService>();
        services.AddSingleton<ICopySelectionService>(provider => new PermissionCheckedCopySelectionService(
            provider.GetRequiredService<CopySelectionService>(), provider.GetRequiredService<IPermissionPolicy>()));
        services.AddKeyedSingleton(
            SelectedTextByCopyHotkey,
            (provider, _) => new GlobalHotkeyService(
                provider.GetRequiredService<ILogger<GlobalHotkeyService>>(), GlobalHotkeyService.SelectedTextByCopyHotkeyId));

        return services;
    }

    // Puts a service of the container's behind another that wraps it, as it was registered: the same instance in the container, with one more layer around it.
    // Nothing is done when the service was never registered.
    private static IServiceCollection DecorateSingleton<T>(this IServiceCollection services, Func<IServiceProvider, T, T> decorate)
        where T : class
    {
        var original = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(T) && descriptor.ServiceKey is null);
        if (original is null)
        {
            return services;
        }

        services.Remove(original);
        services.AddSingleton<T>(provider => decorate(provider, original switch
        {
            { ImplementationInstance: T instance } => instance,
            { ImplementationFactory: { } factory } => (T)factory(provider),
            _ => (T)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!),
        }));
        return services;
    }

    /// <summary>The name the calculator's tool will be registered under; arithmetic is a calculation only while a tool of this name exists.</summary>
    internal const string CalculatorToolName = CalculationToolResults.Calculate;

    /// <summary>The key of the <see cref="GlobalHotkeyService"/> that holds the Visual Intelligence shortcut.</summary>
    internal const string VisualIntelligenceHotkey = "visual-intelligence";

    /// <summary>The key of the <see cref="GlobalHotkeyService"/> that holds the selected-text (Ask Selection) shortcut.</summary>
    internal const string SelectedTextHotkey = "selected-text";

    /// <summary>The key of the <see cref="GlobalHotkeyService"/> that holds the copy-fallback (Ask Selection by copy) shortcut.</summary>
    internal const string SelectedTextByCopyHotkey = "selected-text-by-copy";

    /// <summary>Registers windows and their view models, which are resolved from the container.</summary>
    public static IServiceCollection AddUserInterface(this IServiceCollection services)
    {
        // Each surface has its own voice input, so a microphone opened in one never shows in another.
        services.AddTransient<VoiceInputViewModel>();

        // What the Searching chip shows, shared by the bar and the floating conversation.
        services.AddSingleton<ActivityViewModel>();

        // The categories listed under the bar while it is empty: choosing one narrows the results to that kind, which is then browsed.
        services.AddSingleton(provider => new LauncherViewModel(
            QuickSearchLauncherCommands.Create(provider.GetRequiredService<SearchResultsViewModel>())));

        // What is typed is routed by fixed rules: names and short commands are searched as the user types, requests for files by a
        // structured file search, questions and sums are left for Enter. Instant search is a set of providers (applications, files by
        // name, actions, the clipboard history) that answer side by side, each after its own pause, and are ranked by fixed rules. What
        // the user chooses runs outside the model; the one sample left is the "demo" query, which lists a sample of each other kind.
        services.AddSingleton<IQueryRouter>(provider =>
        {
            var files = provider.GetRequiredService<IFileRequestService>();
            var tools = provider.GetRequiredService<IToolRegistry>();
            return new QueryRouter(files.IsFileRequest, () => tools.Find(CalculatorToolName) is not null);
        });
        services.AddSingleton<IAssistantCommands, AssistantCommands>();
        services.AddSingleton<IQuickActionExecutor>(provider => new QuickActionExecutor(
            provider.GetRequiredService<IQuickActionCatalog>(), provider.GetRequiredService<ISystemActions>(),
            provider.GetRequiredService<IFileLauncher>(), provider.GetRequiredService<IAssistantCommands>(),
            provider.GetRequiredService<IClipboardHistory>()));
        services.AddSingleton<IQuickSearchProvider>(provider => provider.GetRequiredService<ApplicationsQuickSearchProvider>());
        // The files listed while the user types are searched for without the Searching chip: the list under the bar is what shows the search, and it
        // only changes when the new rows are there. (A request for files that is asked, and the model's own searches, still say they are searching.)
        services.AddSingleton<IQuickSearchProvider>(provider => new FilesQuickSearchProvider(
            new PermissionCheckedFileSearchService(provider.GetRequiredService<WindowsFileSearchService>(), provider.GetRequiredService<IPermissionPolicy>()),
            provider.GetRequiredService<IFileSearchPlanner>(),
            provider.GetRequiredService<IPermissionPolicy>(), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IQuickSearchProvider>(provider => new ActionsQuickSearchProvider(
            provider.GetRequiredService<IQuickActionCatalog>(), provider.GetRequiredService<IQuickActionExecutor>(),
            provider.GetRequiredService<IPermissionPolicy>(), provider.GetRequiredService<IQuickSearchUsage>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IQuickSearchProvider>(provider => new ClipboardQuickSearchProvider(
            provider.GetRequiredService<IClipboardHistory>(), provider.GetRequiredService<IPermissionPolicy>()));
        services.AddSingleton<IQuickSearchCoordinator>(provider => new QuickSearchCoordinator(
            provider.GetRequiredService<IEnumerable<IQuickSearchProvider>>(), provider.GetRequiredService<TimeProvider>(),
            logger: provider.GetService<ILogger<QuickSearchCoordinator>>()));
        services.AddSingleton(provider => new QuickSearchRanker(
            provider.GetRequiredService<IEnumerable<IQuickSearchProvider>>(), provider.GetRequiredService<IQuickSearchUsage>()));
        services.AddSingleton<AttachRequests>();
        services.AddSingleton(provider => new QuickSearchActionRunner(
            provider.GetRequiredService<IApplicationLauncher>(), provider.GetRequiredService<IFileLauncher>(),
            provider.GetRequiredService<ITextClipboard>(), provider.GetRequiredService<AttachRequests>(),
            provider.GetRequiredService<IQuickActionExecutor>(), provider.GetRequiredService<IClipboardHistory>(),
            provider.GetRequiredService<IQuickSearchUsage>()));
        services.AddSingleton<IQuickSearchResultsSource>(provider => new QuickSearchResultsSource(
            provider.GetRequiredService<IQuickSearchCoordinator>(), provider.GetRequiredService<QuickSearchRanker>(),
            provider.GetRequiredService<IQueryRouter>(), provider.GetRequiredService<QuickSearchActionRunner>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IClipboardHistory>(), provider.GetService<ISettingsService>()));

        // The sections of results listed under the bar while something is typed: a sample of each other kind for "demo", and otherwise
        // whatever the providers find.
        services.AddSingleton<ISearchResultsSource>(_ => new PlaceholderSearchResults(includeBraveSample: false, includeDemoSample: DemoAnswerProvider.DemosRequested));
        // As in the reference, the best match is beside the text at once and the whole list follows when the typing pauses; a sum is answered
        // in the bar as it is typed, by the calculator's own arithmetic.
        services.AddSingleton(provider => new SearchResultsViewModel(
            provider.GetRequiredService<ISearchResultsSource>(), quickSource: provider.GetRequiredService<IQuickSearchResultsSource>(),
            settings: provider.GetService<ISettingsService>(), revealDelay: SearchResultsViewModel.ReferenceRevealDelay,
            clock: provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ICalculator, Assistant.Tools.Calculator.ArithmeticCalculator>();

        // As in the reference, the bar opens as the bar alone: its categories come under it when the pointer moves or the arrow keys ask.
        services.AddSingleton(provider =>
        {
            var bar = ActivatorUtilities.CreateInstance<SearchOrAskViewModel>(provider);
            bar.LauncherWaitsForPointer = true;
            return bar;
        });

        // Sample answers stand in for answers that need tools, search and rich content, until they exist; copy buttons use
        // the clipboard. "demo orb" opens a window for watching the assistant orb move, until voice input drives it.
        services.AddSingleton<IOrbPreview, OrbPreviewLauncher>();

        // "demo model" opens a window for loading and unloading the local model and watching its status, the developer's
        // way to load a model file by hand; the settings window's Model page shows the same status and the profiles.
        services.AddSingleton<ModelStatusViewModel>();
        services.AddSingleton<IModelPreview>(provider =>
            new ModelPreviewLauncher(() => provider.GetRequiredService<ModelStatusViewModel>()));
        services.AddSingleton<ITextClipboard, WpfTextClipboard>();

        // Questions without a sample answer go to the local model, whose answer streams into the conversation. "demo image"
        // asks for an image file and sends it with a question, to test image requests end to end.
        // A request to find the user's own files is answered with what Windows Search finds, before the model is asked anything.
        // The attached documents are read for the question they are attached to (Files permission first): their passages go with it, or
        // the notes taken on them when they are too long to read at once.
        services.AddSingleton<AttachedDocuments>();

        // The parts of the screen that conversations are about: given to the context service for each question until the user takes them
        // off, and then let go of everywhere they are held.
        services.AddSingleton(provider => new ScreenAttachments(
            provider.GetRequiredService<IContextService>(),
            provider.GetRequiredService<IAnswerProvider>(),
            provider.GetService<IScreenText>(),
            provider.GetRequiredService<TimeProvider>()));

        // Screenshots do not outlive their use: unused ones are let go of after a while, and the folder for temporary captures (which
        // nothing fills today) is emptied when the application starts and stops, so that a crash leaves nothing behind.
        services.AddSingleton<TemporaryCaptureCleaner>(provider => new TemporaryCaptureCleaner(
            provider.GetRequiredService<AppPaths>(), provider.GetRequiredService<ILogger<TemporaryCaptureCleaner>>()));
        services.AddHostedService(provider => new CaptureLifetime(
            provider.GetRequiredService<ScreenAttachments>(),
            provider.GetRequiredService<TemporaryCaptureCleaner>(),
            action => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action),
            provider.GetRequiredService<ILogger<CaptureLifetime>>()));
        services.AddSingleton<ModelAnswerProvider>();
        services.AddSingleton<FileRequestAnswers>();
        services.AddSingleton(provider => new CalculationAnswers(
            provider.GetRequiredService<IQueryRouter>(), provider.GetRequiredService<IToolExecutor>(), provider.GetRequiredService<ITextClipboard>()));
        services.AddSingleton<IImagePicker, OpenFileImagePicker>();
        services.AddSingleton<IVisualIntelligenceDemo, VisualIntelligenceDemo>();

        // "demo integration" offers a made-up sample integration, served from this PC and installed through the real approval panel, to try the
        // installation of integrations without the web: it can then be reconnected, turned off and removed in Settings > Integrations.
        services.AddSingleton<ISampleIntegrationDemo>(provider => new SampleIntegrationDemo(
            provider.GetRequiredService<Assistant.Tools.Integrations.IntegrationLayout>(),
            provider.GetRequiredService<Assistant.Tools.Integrations.IInstalledIntegrationRegistry>(),
            provider.GetRequiredService<Assistant.Tools.Integrations.IManagedRuntimes>(),
            provider.GetRequiredService<Assistant.Tools.Mcp.IMcpClientFactory>(),
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<IPermissionPolicy>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>(),
            provider.GetService<ISettingsLauncher>(),
            audit: provider.GetService<IAuditTrail>()));

        // "demo integration request" (step 110) turns the sample on for the request that is asked next: the Assistant's answer to a request for an app offers the
        // sample instead of searching the web, and the offer is accepted by the demo's own installer, the only one that may download from this PC. Every other
        // request and offer is the Assistant's own, unchanged.
        services.DecorateSingleton<Assistant.Core.Contracts.IConnectedAppRequestHandler>(
            (provider, inner) => new SampleAwareRequestHandler(inner, provider.GetRequiredService<ISampleIntegrationDemo>()));
        services.DecorateSingleton<Assistant.Tools.Integrations.IIntegrationOffers>(
            (provider, inner) => new SampleAwareOffers(inner, provider.GetRequiredService<ISampleIntegrationDemo>()));

        // "demo reminder" (step 116) is the whole request "Check my calendar for exams in the next two weeks and message my brother to remind him", carried out with made-up parts: the
        // Assistant offers a made-up calendar and a made-up messaging app (installed by the real installer, on the user's click) and carries on with the request, which the real model
        // then does in the real loop, asking before it sends. While the demo is on, "my brother" is a made-up Omar and Calendar and Messaging are allowed (the permission policy asks the
        // temporary grants, which are empty unless the demo is on); when it is off, none of that is in effect.
        services.AddSingleton<ICalendarReminderDemo>(provider => new CalendarReminderDemo(
            new SampleBundleHost(
                provider.GetRequiredService<Assistant.Tools.Integrations.IntegrationLayout>(),
                provider.GetRequiredService<Assistant.Tools.Integrations.IInstalledIntegrationRegistry>(),
                provider.GetRequiredService<Assistant.Tools.Integrations.IManagedRuntimes>(),
                provider.GetRequiredService<Assistant.Tools.Mcp.IMcpClientFactory>(),
                provider.GetRequiredService<ISettingsService>(),
                provider.GetRequiredService<IPermissionPolicy>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILoggerFactory>(),
                audit: provider.GetService<IAuditTrail>()),
            provider.GetRequiredService<Assistant.Tools.Integrations.IInstalledIntegrationRegistry>(),
            provider.GetRequiredService<Assistant.Tools.Integrations.IIntegrationResolver>(),
            provider.GetRequiredService<Assistant.Tools.Integrations.CapabilityNeedSwitch>(),
            provider.GetRequiredService<Assistant.Core.Permissions.TemporaryPermissionGrants>(),
            provider.GetRequiredService<TimeProvider>()));
        services.DecorateSingleton<Assistant.Core.Contracts.IConnectedAppRequestHandler>(
            (provider, inner) => new ReminderAwareRequestHandler(inner, provider.GetRequiredService<ICalendarReminderDemo>()));
        services.DecorateSingleton<Assistant.Tools.Integrations.IIntegrationOffers>(
            (provider, inner) => new ReminderAwareOffers(inner, provider.GetRequiredService<ICalendarReminderDemo>()));
        services.DecorateSingleton<Assistant.Core.People.IPersonResolver>(
            (provider, inner) => new ReminderAwarePeople(inner, provider.GetRequiredService<ICalendarReminderDemo>()));
        services.AddSingleton<Assistant.Core.Permissions.TemporaryPermissionGrants>();
        services.AddSingleton<Assistant.Core.Permissions.ITemporaryPermissionGrants>(provider => provider.GetRequiredService<Assistant.Core.Permissions.TemporaryPermissionGrants>());
        // The developer's test of the question asked before something is changed ("demo confirm"): the real tool, executor and question, over a made-up person.
        services.AddSingleton<IConfirmationDemo>(provider => new ConfirmationDemo(
            provider.GetRequiredService<IAppEventBus>(), provider.GetRequiredService<IPermissionService>(), provider.GetRequiredService<TimeProvider>()));
        // The developer's test of a task with several steps ("demo task", "demo task fail", step 117): the real loop, panel, question and activity log, over a made-up plan.
        services.AddSingleton<IAgentTaskDemo>(provider => new AgentTaskDemo(
            provider.GetRequiredService<IAppEventBus>(), provider.GetRequiredService<IAgentTaskLog>(), provider.GetRequiredService<IPermissionService>(),
            provider.GetRequiredService<ISettingsService>(), provider.GetRequiredService<IImagePreprocessor>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ISettingsLauncher>()));
        // The samples ("demo task", "demo photos" and the rest) are a developer's: they answer only when the app was started with ASSISTANT_DEMOS=1.
        services.AddSingleton<IAnswerProvider>(provider =>
        {
            var answers = ActivatorUtilities.CreateInstance<DemoAnswerProvider>(provider);
            answers.DemosEnabled = DemoAnswerProvider.DemosRequested;
            return answers;
        });
        services.AddSingleton<ConversationViewModel>();

        // File Explorer's Ask Assistant: the app serves its pipe while it runs, gathers the files the entry point hands over, and
        // the window opens the conversation with them attached; the menu entry is added and removed by the entry point itself.
        services.AddSingleton(_ => new ExplorerIntegrationOptions(AppPipe.ForCurrentUser()));
        services.AddSingleton<ExplorerFileRequests>();

        // The same pipe takes the request the app sends itself when it is opened while it is already running: this copy shows its full window.
        services.AddSingleton<FullViewRequests>();
        services.AddSingleton<IExplorerMenuInstaller>(_ => new ExplorerMenuInstaller(AppContext.BaseDirectory));
        services.AddSingleton<ExplorerIntegration>();
        services.AddHostedService(provider => provider.GetRequiredService<ExplorerIntegration>());

        // The browser bridge (PROJECT_SPEC §4.5): the same pipe also carries the native-messaging host's selections, which the app takes
        // here and opens the Ask panel with it, the page's title and address and the browser's name; the host's registration is added and
        // removed by the host itself.
        services.AddSingleton<IBrowserBridgeInstaller>(_ => new BrowserBridgeInstaller(AppContext.BaseDirectory));
        services.AddSingleton<BrowserSelectionRequests>();
        services.AddSingleton<BrowserBridgeIntegration>();
        services.AddSingleton(provider => new AskBrowserSelectionController(
            provider.GetRequiredService<IPermissionPolicy>(),
            provider.GetRequiredService<AssistantWindowStateController>(),
            provider.GetRequiredService<ConversationViewModel>(),
            provider.GetRequiredService<ILogger<AskBrowserSelectionController>>(),
            provider.GetRequiredService<IPermissionGate>()));
        services.AddSingleton<IBrowserSelectionSink>(provider => provider.GetRequiredService<BrowserBridgeIntegration>());
        services.AddHostedService(provider => provider.GetRequiredService<BrowserBridgeIntegration>());

        // One window is both the bar and the floating conversation, and a controller moves it between the two.
        services.AddSingleton<AssistantWindow>();
        services.AddSingleton<IAssistantWindow>(provider => provider.GetRequiredService<AssistantWindow>());
        services.AddSingleton<AssistantWindowStateController>();

        // Visual Intelligence: the shortcut dims a snapshot of every monitor, the user selects a part of it, and its chips ask the Assistant
        // about it (the conversation opens with it attached), copy it, or, one day, search the web with it.
        services.AddSingleton<ICaptureOverlay, CaptureOverlay>();
        services.AddSingleton<IImageClipboard, WpfImageClipboard>();
        services.AddSingleton(provider => new VisualIntelligenceController(
            provider.GetRequiredService<IScreenCapture>(),
            provider.GetRequiredService<IPermissionPolicy>(),
            provider.GetRequiredService<ICaptureOverlay>(),
            provider.GetRequiredService<IAssistantWindow>(),
            provider.GetRequiredService<AssistantWindowStateController>(),
            provider.GetRequiredService<ConversationViewModel>(),
            provider.GetRequiredService<IImageClipboard>(),
            cancellationToken => provider.GetRequiredService<ImageSearchLauncher>().GetChipAvailabilityAsync(cancellationToken),
            provider.GetRequiredService<ILogger<VisualIntelligenceController>>(),
            region => provider.GetRequiredService<ImageSearchLauncher>().SearchAsync(region),
            provider.GetRequiredService<IPermissionGate>()));

        // What the model's take_screenshot tool does: the Assistant's window goes, the monitor the user is on is captured into memory, and the
        // picture is attached to the conversation as a chip the user can take off. It is asked for only by that tool, after the Screen Capture
        // permission and the user's confirmation, and it finds the window and the attachments when it needs them (they depend on the tools).
        services.AddSingleton<IScreenshotTaker>(provider => new ConversationScreenshotTaker(
            provider.GetRequiredService<IScreenCapture>(), provider, provider.GetRequiredService<ILogger<ConversationScreenshotTaker>>()));

        // Ask Selection: the shortcut reads the text selected in the application in front and opens the Ask panel with it attached and
        // the quick actions (Summarize, Explain, Rewrite, Solve, Define, Ask Anything) listed. It asks nothing on its own.
        services.AddSingleton(provider => new AskSelectionController(
            provider.GetRequiredService<ISelectionService>(),
            provider.GetRequiredService<IPermissionPolicy>(),
            provider.GetRequiredService<AssistantWindowStateController>(),
            provider.GetRequiredService<ConversationViewModel>(),
            provider.GetRequiredService<ILogger<AskSelectionController>>(),
            copy: provider.GetRequiredService<ICopySelectionService>(),
            settings: provider.GetRequiredService<ISettingsService>(),
            gate: provider.GetRequiredService<IPermissionGate>()));

        // Image Search, the one part of Visual Intelligence that can send something off this PC: off while Local Only mode is on, behind its
        // own permission, and, for a provider that sends the picture away, asked about for each picture. The provider is Bing's visual search
        // (BingImageSearchService); "demo results" still shows the sample. What was found is shown in a panel of its own.
        services.AddSingleton<IImageSearchService>(provider => new Assistant.Search.ImageSearch.BingImageSearchService(
            provider.GetService<IImagePreprocessor>(), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IImageSearchConfirmation, WpfImageSearchConfirmation>();
        services.AddSingleton<IUrlLauncher, ShellUrlLauncher>();
        services.AddSingleton<Assistant.Tools.Mcp.Auth.IOAuthBrowser, Assistant.UI.Integrations.UrlLauncherOAuthBrowser>();
        services.AddSingleton(provider => new ImageSearchFlow(
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<IPermissionPolicy>(),
            provider.GetRequiredService<IImageSearchService>(),
            provider.GetRequiredService<IImageSearchConfirmation>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ImageSearchFlow>>(),
            provider.GetRequiredService<IPermissionGate>()));
        services.AddSingleton<ImageSearchResultsWindow>();
        services.AddSingleton<IImageSearchResultsWindow>(provider =>
            new LazyImageSearchResultsWindow(() => provider.GetRequiredService<ImageSearchResultsWindow>()));
        services.AddSingleton<ImageSearchLauncher>();

        // Every message is saved to the history as it is asked and answered, one at a time, off the UI thread, and the
        // history is opened, and its last messages written, with the application.
        services.AddSingleton<MessageMapper>();
        services.AddSingleton(provider => new ConversationSurfaces(provider.GetRequiredService<AppPaths>()));
        services.AddSingleton<ConversationRecorder>();
        services.AddSingleton<IConversationRecorder>(provider => provider.GetRequiredService<ConversationRecorder>());
        services.AddHostedService<HistoryLifetime>();
        services.AddHostedService<HistoryRetentionService>();
        services.AddHostedService<BundledIntegrationsStartup>();

        // The full window for history and long conversations, created the first time a conversation is opened in it. Its
        // list is the saved history, read when the window is shown, and its search reads the same history.
        services.AddSingleton<IHistorySource, ConversationHistorySource>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<HistoryWindow>();
        services.AddSingleton<Func<IHistoryWindow>>(provider => () => provider.GetRequiredService<HistoryWindow>());

        // Looking for a newer release (Settings > About, and, when a look is due, about once a month: as the app starts and when the bar or the full window
        // is opened, never on a timer). Only the bootstrapper starts the due look, so a test that builds this container asks GitHub nothing. What it finds
        // is said in a small window; nothing is downloaded or installed.
        services.AddSingleton<Assistant.Core.Updates.IUpdateChecker>(provider => new Assistant.Core.Updates.GitHubUpdateChecker(
            provider.GetRequiredService<ISettingsService>(), provider.GetRequiredService<IAppEventBus>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<Assistant.Core.Updates.GitHubUpdateChecker>>()));
        services.AddSingleton(provider => new Assistant.UI.Updates.UpdateNotifier(
            provider.GetRequiredService<Assistant.Core.Updates.IUpdateChecker>(), System.Windows.Application.Current?.Dispatcher));
        services.AddSingleton(provider => new HistoryWindowController(
            provider.GetRequiredService<ConversationViewModel>(), provider.GetRequiredService<HistoryViewModel>(), provider.GetRequiredService<IAssistantWindow>(),
            provider.GetRequiredService<Func<IHistoryWindow>>()));

        // The settings window, created the first time it is opened ("demo settings" opens it until the tray has a menu).
        // Its pages read and save the real settings, and show the model profiles and the local model's status.
        services.AddSingleton<SettingsViewModel>();
        services.AddKeyedSingleton("model-downloads", (_, _) => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        services.AddSingleton<Assistant.Core.Models.IModelLibrary>(provider => new Assistant.Data.Models.ModelLibrary(provider.GetRequiredService<AppPaths>(), provider.GetRequiredKeyedService<HttpClient>("model-downloads")));
        services.AddSingleton<Assistant.UI.Onboarding.ConnectionsSetupViewModel>();
        services.AddSingleton<Assistant.UI.Onboarding.SearchSetupViewModel>();
        services.AddSingleton<Assistant.UI.Onboarding.SetupViewModel>();
        services.AddSingleton<Assistant.UI.Onboarding.OnboardingController>();
        services.AddSingleton<SettingsWindow>();
        services.AddSingleton<ISettingsLauncher>(provider =>
            new SettingsLauncher(() => provider.GetRequiredService<SettingsWindow>()));

        // The Assistant's icon in the notification area and its menu (PROJECT_SPEC §4.9), and starting with Windows: an entry in the user's own Run key that starts this
        // copy of the app in the background, kept pointing at it while the setting is on.
        services.AddSingleton<ILaunchAtLogin>(_ => new RegistryLaunchAtLogin(ApplicationPath.Executable()));
        services.AddHostedService<LaunchAtLoginRefresh>();
        services.AddSingleton(provider =>
        {
            var window = provider.GetRequiredService<AssistantWindowStateController>();
            var lifetime = provider.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
            return new TrayController(
                new NotificationAreaIcon(TrayController.Tooltip, System.IO.Path.Combine(AppContext.BaseDirectory, "Assistant.ico")),

                // A click on the icon opens the full window, as opening the app from Start does; the bar is Alt+A.
                () => provider.GetRequiredService<HistoryWindowController>().ShowHistory(),
                window.OpenNewConversation,
                () => provider.GetRequiredService<ISettingsLauncher>().Show(),
                provider.GetRequiredService<ILocalAiPause>(),
                lifetime.StopApplication,
                action => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action),
                provider.GetRequiredService<ILogger<TrayController>>());
        });
        return services;
    }
}
