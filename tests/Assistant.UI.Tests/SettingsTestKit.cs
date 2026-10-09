using System.IO;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.ModelHosting;
using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Assistant.UI.Settings;
using Assistant.UI.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    private const long SettingsGiB = 1024L * 1024 * 1024;

    // The folder the Settings tests pretend the models are in. Nothing is created there: the resolver is told which files exist.
    private static readonly AppPaths SettingsPaths = new(Path.Combine(Path.GetTempPath(), "assistant-settings-ui-tests"));

    private static string ProfileFile(string profile, string file) => Path.Combine(SettingsPaths.ModelsDirectory, profile, file);

    // The Settings window's view model over the services it reads: settings in memory (or the one given), the real profile
    // catalog and resolver over a folder whose files are the ones named, a machine with the memory given, and a model
    // lifecycle a test moves by hand.
    private static SettingsKit CreateSettingsKit(
        AppSettings? saved = null, ISettingsService? service = null, ISettingsLoadReport? report = null,
        IEnumerable<string>? existingFiles = null, long memory = 16 * SettingsGiB,
        Assistant.UI.Explorer.IExplorerMenuInstaller? explorerMenu = null,
        Assistant.UI.Browser.IBrowserBridgeInstaller? browserBridge = null,
        Assistant.Core.QuickSearch.Clipboard.IClipboardHistory? clipboardHistory = null,
        Assistant.Tools.Integrations.IIntegrationManager? integrations = null,
        Assistant.Tools.Integrations.IIntegrationOffers? offers = null,
        Assistant.Core.People.IPersonStore? people = null,
        Assistant.Core.People.IPersonResolver? personResolver = null,
        Assistant.Core.Audit.IAuditHistory? activity = null,
        Assistant.Core.Permissions.IPermissionGate? permissionGate = null,
        Assistant.Core.Startup.ILaunchAtLogin? launchAtLogin = null,
        Assistant.Core.Voice.ITextToSpeechService? speech = null, Assistant.UI.Voice.IVoiceRuntime? voiceRuntime = null,
        Assistant.Core.Voice.ISpeechToTextService? recognizer = null, Assistant.Voice.VoiceModelFolders? voiceFolders = null,
        Assistant.Tools.Integrations.IIntegrationConnector? connector = null, Assistant.Windows.Audio.IMicrophoneDevices? microphones = null,
        Assistant.UI.Onboarding.SetupViewModel? setup = null, Assistant.UI.Gaming.IGameMode? gameMode = null,
        Assistant.Core.Memory.IMemoryStore? remembered = null, Assistant.Core.Home.IHomeAssistant? home = null,
        Assistant.Core.Updates.IUpdateChecker? updates = null)
    {
        var settings = service ?? new InMemorySettingsService();
        if (saved is not null)
        {
            settings.SaveAsync(saved).GetAwaiter().GetResult();
        }

        var existing = new HashSet<string>(existingFiles ?? [], StringComparer.OrdinalIgnoreCase);
        var hardware = new FixedHardware(new HardwareInfo(memory, 8));
        var catalog = new ModelProfileCatalog();
        var resolver = new ModelProfileResolver(catalog, hardware, SettingsPaths, path => existing.Contains(path));
        var lifecycle = new SettingsLifecycle();
        var bus = new AppEventBus(NullLogger<AppEventBus>.Instance);
        var model = new SettingsViewModel(
            settings, catalog, resolver, hardware, lifecycle, bus, SettingsPaths, report, System.Windows.Threading.Dispatcher.CurrentDispatcher,
            explorerMenu, browserBridge, clipboardHistory, integrations, offers, people, personResolver, activity, permissionGate: permissionGate, launchAtLogin: launchAtLogin,
            speech: speech, voiceRuntime: voiceRuntime, recognizer: recognizer, voiceFolders: voiceFolders, connector: connector, microphones: microphones, setup: setup, gameMode: gameMode, memory: remembered, home: home, updates: updates);
        // What the window does each time it is shown: read the settings and show them on every page.
        SettingsWait(model.LoadAsync());
        return new SettingsKit(model, settings, lifecycle, bus, existing);
    }

    // Opens the Settings window off screen over a kit, with its settings loaded.
    private static (SettingsWindow Window, FakeFrameFactory Frames, FakePlacement Placement) CreateSettingsWindow(SettingsKit kit)
    {
        var frames = new FakeFrameFactory();
        var placement = new FakePlacement();
        var window = new SettingsWindow(kit.Model, frames, placement)
        {
            Left = -10000, Top = -10000, ShowActivated = false,
        };
        if (kit.Model.Setup is null) SettingsWait(kit.Model.LoadAsync());
        return (window, frames, placement);
    }

    private sealed record SettingsKit(
        SettingsViewModel Model, ISettingsService Settings, SettingsLifecycle Lifecycle, AppEventBus Bus, HashSet<string> Existing)
    {
        public AppSettings Saved => Settings.LoadAsync().GetAwaiter().GetResult();

        // Waits for every change made so far to be saved, as the window does between one change and the next.
        public void Settle() => Model.WhenSavedAsync().GetAwaiter().GetResult();
    }

    // A model lifecycle whose status and loaded model a test sets by hand.
    private sealed class SettingsLifecycle : IModelLifecycle
    {
        public int Unloads { get; private set; }
        public ModelStatusChanged Current { get; set; } = new(ModelStatus.NotLoaded);

        public ModelInfo? LoadedModel { get; set; }

        public ModelInfo? Model => LoadedModel;

        public Task<ModelInfo> LoadAsync(ModelFiles files, CancellationToken cancellationToken = default) =>
            Task.FromResult(LoadedModel ?? new ModelInfo(files.DeriveModelId(), 2048));

        public Task UnloadAsync(CancellationToken cancellationToken = default) { Unloads++; return Task.CompletedTask; }

        public IAsyncEnumerable<ModelHostReply> GenerateAsync(GenerationRequest request, CancellationToken cancellationToken = default) =>
            throw new ModelHostException(ModelHostErrorCode.ModelNotFound, "No model host runs in this test.");
    }

    private sealed class FixedHardware(HardwareInfo info) : IHardwareInfoProvider
    {
        public HardwareInfo Get() => info;
    }
}
