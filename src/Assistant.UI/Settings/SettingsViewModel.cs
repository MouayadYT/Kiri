using System.IO;
using Assistant.Core.Assets;
using Assistant.Core.Audit;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.Hardware;
using Assistant.Core.ModelHosting;
using Assistant.Core.People;
using Assistant.Core.QuickSearch.Clipboard;
using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Assistant.UI.Explorer;
using System.Windows.Threading;

namespace Assistant.UI.Settings;

/// <summary>
/// What the Settings window shows and does (PROJECT_SPEC §4.9, §5.10): the sections listed in its sidebar, the page of
/// the one that is chosen, and the settings service they all read and write. A control that changes a setting saves it
/// as soon as it is changed, one save after another so they can never happen out of order, and the pages show the
/// settings as they were saved. A value the app cannot work with is explained and not saved, and a save that fails says
/// so and puts the page back as it was. Nothing here logs, and no path or value leaves the window.
/// </summary>
public sealed class SettingsViewModel : NotifyingObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IAppEventBus _events;
    private readonly ISettingsLoadReport? _report;
    private readonly SettingsPage[] _pages;
    private AppSettings _current = new();
    private SettingsSectionItem _selectedSection;
    private Task _lastSave = Task.CompletedTask;
    private string _loadNotice = string.Empty;
    private string _saveNotice = string.Empty;
    private int _applying;
    private bool _disposed;
    private readonly IDisposable _savedSubscription;

    /// <summary>Creates the view model over the services the pages need.</summary>
    /// <param name="settings">Where the settings are read and saved.</param>
    /// <param name="profiles">The model profiles the Model page lists.</param>
    /// <param name="resolver">Says where each profile's files are and whether they are there.</param>
    /// <param name="hardware">Tells how much memory the PC has, for the Model page's advice.</param>
    /// <param name="lifecycle">The local model, whose status the Model page shows.</param>
    /// <param name="events">Carries the model's status changes.</param>
    /// <param name="paths">The app's folders, shown on the Model and About pages.</param>
    /// <param name="report">Says whether the settings had to fall back on defaults; none when omitted.</param>
    /// <param name="dispatcher">The UI thread's dispatcher; the current one when omitted.</param>
    /// <param name="explorerMenu">Adds and removes File Explorer's menu entry; when omitted, the switch only saves the setting.</param>
    /// <param name="browserBridge">Registers and removes the browsers' native-messaging host; when omitted, the switch only saves the setting.</param>
    /// <param name="clipboardHistory">The clipboard history the Privacy page can clear; when omitted, the page has nothing to clear.</param>
    /// <param name="integrations">Looks after the connected apps the Integrations page lists; when omitted, the page lists none.</param>
    /// <param name="offers">Accepts the update the user approves on the Integrations page; when omitted, an update cannot be approved there.</param>
    /// <param name="people">Keeps the people the People page lists; when omitted, the page cannot keep anyone.</param>
    /// <param name="personResolver">Works out who a name such as "my brother" is, for the People page's check; when omitted, the page has no check.</param>
    /// <param name="activity">The activity log the Activity page lists and can clear; when omitted, the page has nothing to show.</param>
    /// <param name="clock">The time, for how long ago a line of the Activity page happened; the system's when omitted.</param>
    /// <param name="permissionGate">Asks the user before a look for a newer version of a connected app when External Web and Image Search is set to ask every time; when omitted, such a look is refused.</param>
    /// <param name="launchAtLogin">Adds and removes the entry that starts the app when the user signs in; when omitted, the switch only saves the setting.</param>
    /// <param name="hardwareProfile">Reads what this PC offers a model (processor, memory, graphics cards), which the Model page shows; when omitted, the page shows none of it.</param>
    /// <param name="recommender">Recommends the profile and the context window for this PC, which the Model page offers and applies until the user chooses; when omitted, nothing is recommended.</param>
    /// <param name="packagedAssets">Says whether the models that came packaged with the Assistant match the package's checksums; when omitted, the Model page does not mention it.</param>
    /// <param name="voices">Says which text-to-speech engines are installed on this PC, which the Voice page lists; when omitted, the page lists none.</param>
    /// <param name="speech">The Assistant's voice, whose status, sample and measurement the Voice page shows; when omitted, the page has none.</param>
    /// <param name="voiceRuntime">Says how the wake word is doing, which the Voice page shows.</param>
    /// <param name="recognizer">The speech recognizer, whose status the Voice page shows.</param>
    /// <param name="voiceFolders">Where the voices are on this PC, including the user's own voices folder.</param>
    /// <param name="memory">What the Assistant remembers for the user, which the Memory page lists; when omitted, the page lists nothing and can keep nothing.</param>
    /// <param name="gameMode">Says what game mode is doing, which the General page shows under its switch; when omitted, the switch only saves the setting.</param>
    public SettingsViewModel(
        ISettingsService settings, IModelProfileCatalog profiles, IModelProfileResolver resolver, IHardwareInfoProvider hardware,
        IModelLifecycle lifecycle, IAppEventBus events, AppPaths paths, ISettingsLoadReport? report = null,
        Dispatcher? dispatcher = null, IExplorerMenuInstaller? explorerMenu = null, Assistant.UI.Browser.IBrowserBridgeInstaller? browserBridge = null,
        IClipboardHistory? clipboardHistory = null, Assistant.Tools.Integrations.IIntegrationManager? integrations = null,
        Assistant.Tools.Integrations.IIntegrationOffers? offers = null, IPersonStore? people = null, IPersonResolver? personResolver = null,
        IAuditHistory? activity = null, TimeProvider? clock = null, Assistant.Core.Permissions.IPermissionGate? permissionGate = null,
        Assistant.Core.Startup.ILaunchAtLogin? launchAtLogin = null, IHardwareProfileService? hardwareProfile = null,
        IModelRecommender? recommender = null, IPackagedAssets? packagedAssets = null, ITextToSpeechAssets? voices = null,
        Assistant.Core.Voice.ITextToSpeechService? speech = null, Assistant.UI.Voice.IVoiceRuntime? voiceRuntime = null,
        Assistant.Core.Voice.ISpeechToTextService? recognizer = null, Assistant.Voice.VoiceModelFolders? voiceFolders = null,
        Assistant.Tools.Integrations.IIntegrationConnector? connector = null, Assistant.Windows.Audio.IMicrophoneDevices? microphones = null,
        Assistant.UI.Onboarding.SetupViewModel? setup = null, Assistant.UI.Onboarding.OnboardingController? onboarding = null,
        Assistant.UI.Gaming.IGameMode? gameMode = null, Assistant.Core.Memory.IMemoryStore? memory = null,
        Assistant.Core.Home.IHomeAssistant? home = null, Assistant.Core.Updates.IUpdateChecker? updates = null)
    {
        _settings = settings;
        _events = events;
        _report = report;
        Setup = setup;
        ShowOnboardingCommand = onboarding?.ShowCommand;
        General = new GeneralPage(this, launchAtLogin, gameMode, dispatcher ?? Dispatcher.CurrentDispatcher);
        Model = new ModelPage(
            this, profiles, resolver, hardware, lifecycle, events, paths, dispatcher ?? Dispatcher.CurrentDispatcher, hardwareProfile, recommender, packagedAssets);
        Context = new ContextPage(this);
        Privacy = new PrivacyPage(this, clipboardHistory, dispatcher ?? Dispatcher.CurrentDispatcher);
        Cleanup = new CleanupPage(this);
        Permissions = new PermissionsPage(this);
        People = new PeoplePage(this, people, personResolver);
        Memory = new MemoryPage(this, memory, clock, home);
        Hotkeys = new HotkeysPage(this);
        Voice = new VoicePage(this, voices, events, dispatcher ?? Dispatcher.CurrentDispatcher, speech, voiceRuntime, recognizer, voiceFolders, microphones);
        Asr = new AsrPage(this, setup, Voice);
        Integrations = new IntegrationsPage(this, explorerMenu, browserBridge, integrations, offers, dispatcher ?? Dispatcher.CurrentDispatcher, permissionGate, connector, setup?.Connections);
        Activity = new ActivityPage(this, activity, clock, dispatcher ?? Dispatcher.CurrentDispatcher);
        About = new AboutPage(this, paths, updates);

        // The sidebar's order: what is set up first comes first (the model, then how it speaks and listens, then what it is connected to), and what
        // is looked at now and then follows.
        _pages = [General, Model, Voice, Asr, Integrations, Context, Privacy, Cleanup, Permissions, People, Memory, Hotkeys, Activity, About];
        // Context is a part of Model, and Cleanup a part of Privacy: they are drawn there, and are not sections of their own.
        Sections = [.. _pages.Where(page => page.Section is not (SettingsSection.Context or SettingsSection.Cleanup)).Select(page => new SettingsSectionItem(page.Section, Title(page.Section)))];
        _selectedSection = Sections[0];
        var ui = dispatcher ?? Dispatcher.CurrentDispatcher;
        _savedSubscription = events.Subscribe<SettingsViewModel, SettingsSaved>(this, (root, saved, _) =>
        {
            if (!root._disposed) ui.BeginInvoke(() => { if (!root._disposed) root.Show(saved.Settings, fresh: false); });
            return Task.CompletedTask;
        });
    }

    /// <summary>The sidebar's entries, in order.</summary>
    public IReadOnlyList<SettingsSectionItem> Sections { get; }

    /// <summary>The section that is open.</summary>
    public SettingsSectionItem SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is not null && Set(ref _selectedSection, value))
            {
                OnPropertyChanged(nameof(CurrentPage));
            }
        }
    }

    /// <summary>The page of the open section.</summary>
    public SettingsPage CurrentPage => _pages.First(page => page.Section == _selectedSection.Section);

    /// <summary>The General page.</summary>
    public GeneralPage General { get; }

    public Assistant.UI.Onboarding.SetupViewModel? Setup { get; }
    public System.Windows.Input.ICommand? ShowOnboardingCommand { get; }

    /// <summary>The Model page: profiles, location, status and context.</summary>
    public ModelPage Model { get; }

    /// <summary>The Context page.</summary>
    public ContextPage Context { get; }

    /// <summary>The Privacy page.</summary>
    public PrivacyPage Privacy { get; }

    /// <summary>The Cleanup page: chats that are deleted by themselves after a while.</summary>
    public CleanupPage Cleanup { get; }

    /// <summary>The Permissions page.</summary>
    public PermissionsPage Permissions { get; }

    /// <summary>The People page.</summary>
    public PeoplePage People { get; }

    /// <summary>The Memory page: what the Assistant remembers for the user.</summary>
    public MemoryPage Memory { get; }

    /// <summary>The Hotkeys page.</summary>
    public HotkeysPage Hotkeys { get; }

    /// <summary>The Voice page: the voice, the wake word and speech recognition.</summary>
    public VoicePage Voice { get; }

    /// <summary>The ASR page: recognizers, device selection, wake word and downloads.</summary>
    public AsrPage Asr { get; }

    /// <summary>The Integrations page.</summary>
    public IntegrationsPage Integrations { get; }

    /// <summary>The Activity page: what the Assistant did on the user's behalf.</summary>
    public ActivityPage Activity { get; }

    /// <summary>The About page.</summary>
    public AboutPage About { get; }

    /// <summary>The settings as they were last read or saved.</summary>
    public AppSettings Current => _current;

    /// <summary>
    /// What to tell the user about the settings themselves: that a save failed, or that what was saved could not be read
    /// as it was left. Empty when all is well.
    /// </summary>
    public string Notice => _saveNotice.Length > 0 ? _saveNotice : _loadNotice;

    /// <summary>Whether there is a <see cref="Notice"/>.</summary>
    public bool HasNotice => Notice.Length > 0;

    /// <summary>Whether the pages are being filled from the settings, so that nothing they show is saved.</summary>
    internal bool IsApplying => _applying > 0;

    /// <summary>Reads the settings and shows them on every page, dropping anything typed and not accepted.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(true);
        _loadNotice = NoticeFor(_report?.Outcome ?? SettingsLoadOutcome.Loaded);
        _saveNotice = string.Empty;
        Show(settings, fresh: true);
        // Older installers registered the entry without saving the user's choice.
        if (!settings.Integrations.ExplorerContextMenuEnabled && await Integrations.HasRegisteredExplorerEntryAsync())
            await CommitAsync(current => current with { Integrations = current.Integrations with { ExplorerContextMenuEnabled = true } });
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(HasNotice));

        if (Setup is not null) await Setup.InitializeAsync().ConfigureAwait(true);

        // The people are not settings: they are read from their own store, each time the window is opened.
        await People.LoadAsync(cancellationToken).ConfigureAwait(true);

        // Nor is what the Assistant remembers: it is listed as it is now, and followed while the window is open.
        Memory.Load();

        // Nor is the activity log: it is read when the window is opened, and then follows what the Assistant does while it is open.
        await Activity.LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Completes when every change made so far has been saved or has failed.</summary>
    public Task WhenSavedAsync() => _lastSave;

    /// <summary>
    /// Saves a change made to the settings, after the changes made before it, and shows the settings as saved. It never
    /// throws: a change that could not be saved sets the <see cref="Notice"/> and puts the pages back.
    /// </summary>
    internal Task CommitAsync(Func<AppSettings, AppSettings> change) => CommitAsync(change, apply: null, failure: "");

    /// <summary>
    /// Like <see cref="CommitAsync(Func{AppSettings, AppSettings})"/>, for a setting that changes something outside the settings
    /// file too: <paramref name="apply"/> makes that change first, in turn with the saves, and only when it worked is the setting
    /// saved; otherwise the page goes back as it was and <paramref name="failure"/> is the <see cref="Notice"/>.
    /// </summary>
    internal Task CommitAsync(Func<AppSettings, AppSettings> change, Func<Task<bool>>? apply, string failure)
    {
        var save = SaveAsync(_lastSave, change, apply, failure);
        _lastSave = save;
        return save;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _savedSubscription.Dispose();
        General.Dispose();
        Model.Dispose();
        People.Dispose();
        Memory.Dispose();
        Voice.Dispose();
        Integrations.Dispose();
        Activity.Dispose();
    }

    private async Task SaveAsync(Task previous, Func<AppSettings, AppSettings> change, Func<Task<bool>>? apply, string failure)
    {
        await previous.ConfigureAwait(true);
        if (apply is not null && !await apply().ConfigureAwait(true))
        {
            await RestoreAsync(failure).ConfigureAwait(true);
            return;
        }

        try
        {
            var saved = await _settings.UpdateAsync(change).ConfigureAwait(true);
            Show(saved, fresh: false);
            SetSaveNotice(string.Empty);

            // What follows a setting while it runs (the clipboard history) is told the settings as they are kept now.
            await _events.PublishAsync(new SettingsSaved(saved)).ConfigureAwait(true);
        }
        catch (SettingsValidationException)
        {
            await RestoreAsync("That value can't be saved, so the setting was left as it was.").ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await RestoreAsync("Your settings couldn't be saved. Check that the disk has room, then try again.").ConfigureAwait(true);
        }
    }

    // Shows the settings as they are really kept, after a change that could not be kept.
    private async Task RestoreAsync(string notice)
    {
        Show(await _settings.LoadAsync().ConfigureAwait(true), fresh: true);
        SetSaveNotice(notice);
    }

    private void Show(AppSettings settings, bool fresh)
    {
        _current = settings;
        _applying++;
        try
        {
            foreach (var page in _pages)
            {
                page.Apply(settings, fresh);
            }
        }
        finally
        {
            _applying--;
        }

        OnPropertyChanged(nameof(Current));
    }

    private void SetSaveNotice(string notice)
    {
        _saveNotice = notice;
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(HasNotice));
    }

    private static string NoticeFor(SettingsLoadOutcome outcome) => outcome switch
    {
        SettingsLoadOutcome.Repaired => "Some settings couldn't be read and are back to their defaults.",
        SettingsLoadOutcome.RestoredFromBackup => "The settings file was damaged, so the last good copy was restored. The damaged file was kept beside it.",
        SettingsLoadOutcome.ResetToDefaults => "The settings couldn't be read, so the defaults are shown. Changes you make here are saved over what was there.",
        _ => string.Empty,
    };

    private static string Title(SettingsSection section) => section == SettingsSection.Asr ? "ASR" : section.ToString();
}
