using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using Assistant.Core.Settings;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp.Auth;
using Assistant.UI.Browser;
using Assistant.UI.Explorer;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Settings;

/// <summary>
/// Integrations: the File Explorer menu entry and the browser extension's bridge (PROJECT_SPEC §4.4, §4.5), and the connected apps the Assistant installed
/// (§4.8, steps 108-109). Turning the File Explorer switch on adds Ask Assistant to the menu for the current user, and turning it off removes it; the setting
/// is saved only once that worked, and otherwise the switch goes back and the window says why. The bridge's switch registers and removes the browsers'
/// native-messaging host the same way. The connected apps are listed with what the Assistant knows of each (where it came from, its version, whether it is
/// on, how it is doing, what it may do and whether a newer version is known) and the user can turn each on or off, reconnect it, update it and remove it.
/// Looking for newer versions is off until the user turns it on, happens at most once a day and only while this page is open, and never installs anything.
/// </summary>
public sealed class IntegrationsPage : SettingsPage, IDisposable
{
    private readonly IExplorerMenuInstaller? _explorerMenu;
    private readonly IBrowserBridgeInstaller? _browserBridge;
    private readonly IIntegrationManager? _manager;
    private readonly IIntegrationOffers? _offers;
    private readonly IIntegrationConnector? _connector;
    private readonly Assistant.UI.Onboarding.ConnectionsSetupViewModel? _connections;
    private readonly Dispatcher _dispatcher;
    private string _connectStatus = string.Empty;
    private CancellationTokenSource? _connectStop;
    private string _connectLink = string.Empty;
    private readonly RelayCommand _copyConnectLink;
    private readonly RelayCommand _cancelConnect;
    private string _microsoftClientId = string.Empty;
    private bool _microsoftClientIdInvalid;
    private readonly Assistant.Core.Permissions.IPermissionGate? _gate;
    private readonly RelayCommand _checkNow;
    private string _firstMenuNotice = string.Empty;
    private bool _explorer;
    private bool _browser;
    private bool _checkForUpdates;
    private bool _checking;
    private string _updateNote = string.Empty;
    private string _updateSummary = string.Empty;
    private bool _disposed;
    private IReadOnlyList<IntegrationInfo> _installed = [];

    internal IntegrationsPage(
        SettingsViewModel root, IExplorerMenuInstaller? explorerMenu = null, IBrowserBridgeInstaller? browserBridge = null,
        IIntegrationManager? integrations = null, IIntegrationOffers? offers = null, Dispatcher? dispatcher = null,
        Assistant.Core.Permissions.IPermissionGate? gate = null, IIntegrationConnector? connector = null,
        Assistant.UI.Onboarding.ConnectionsSetupViewModel? connections = null)
        : base(root, SettingsSection.Integrations)
    {
        _connector = connector;
        _connections = connections;
        _gate = gate;
        _explorerMenu = explorerMenu;
        _browserBridge = browserBridge;
        _manager = integrations;
        _offers = offers;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _checkNow = new RelayCommand(_ => _ = CheckNowAsync(), _ => !_checking && _checkForUpdates && _manager is not null);
        _copyConnectLink = new RelayCommand(_ => CopyConnectLink(), _ => HasConnectLink);
        _cancelConnect = new RelayCommand(_ => _connectStop?.Cancel(), _ => IsConnecting);
        OpenDeveloperSettingsCommand = new RelayCommand(_ => OpenDeveloperSettings());
        CheckExplorerMenuAgainCommand = new RelayCommand(_ => _ = CheckExplorerMenuAgainAsync());
        if (_manager is not null)
        {
            _manager.Changed += OnManagerChanged;
        }

        if (_connections?.Home is { } home)
        {
            HomeAssistant = new HomeAssistantItem(home, () => _connections.OpenEditorFor(Assistant.UI.Onboarding.ConnectionsSetupViewModel.HomeAssistant.AppKey), _dispatcher);
            HomeAssistant.PropertyChanged += OnHomeAssistantChanged;
        }
    }

    /// <summary>The user's Home Assistant, which has a line among the connections while one is connected; <see langword="null"/> where the app has none to connect.</summary>
    public HomeAssistantItem? HomeAssistant { get; }

    /// <summary>Whether a Home Assistant is connected.</summary>
    public bool HasHomeAssistant => HomeAssistant is { IsConnected: true };

    /// <summary>Opens Windows' own Settings page where Developer Mode is turned on; what it does is Windows', and the Assistant changes nothing there.</summary>
    public ICommand OpenDeveloperSettingsCommand { get; }

    /// <summary>Adds the entry again and says whether it is in the first menu now.</summary>
    public ICommand CheckExplorerMenuAgainCommand { get; }

    /// <summary>How Settings opens a page of Windows' own; replaced in tests.</summary>
    internal Action<string> OpenSettingsPage { get; set; } = address =>
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    };

    /// <summary>
    /// What the page says while the File Explorer switch is on but Ask Assistant is only under Show more options: Windows 11 puts an app's entry in the first menu only for
    /// a registered package, and takes the Assistant's only while Developer Mode is on. Empty when it is in the first menu, or the switch is off.
    /// </summary>
    public string ExplorerFirstMenuNotice
    {
        get => _firstMenuNotice;
        private set
        {
            if (Set(ref _firstMenuNotice, value))
            {
                OnPropertyChanged(nameof(HasExplorerFirstMenuNotice));
            }
        }
    }

    /// <summary>Whether there is an <see cref="ExplorerFirstMenuNotice"/>.</summary>
    public bool HasExplorerFirstMenuNotice => _firstMenuNotice.Length > 0;

    /// <summary>The connected apps the Assistant installed, in the order they were installed.</summary>
    public ObservableCollection<ConnectedAppItem> ConnectedApps { get; } = [];

    /// <summary>The apps whose server the Assistant knows and that are not connected yet, each with a Connect button.</summary>
    public ObservableCollection<KnownAppItem> AvailableApps { get; } = [];

    /// <summary>Whether the page can connect apps: there is a connector and a list to show it in.</summary>
    public bool CanConnect => _connector is not null && _manager is not null;

    /// <summary>Whether there is anything to add a connection with: the apps the Assistant knows how to connect, or the form for a server of the user's own.</summary>
    public bool CanAddConnection => CanConnect || _connections is { IsAvailable: true };

    /// <summary>What the last Connect came to, in words, or empty.</summary>
    public string ConnectStatus
    {
        get => _connectStatus;
        private set
        {
            if (Set(ref _connectStatus, value))
            {
                OnPropertyChanged(nameof(HasConnectStatus));
            }
        }
    }

    /// <summary>Whether there is a <see cref="ConnectStatus"/>.</summary>
    public bool HasConnectStatus => _connectStatus.Length > 0;

    /// <summary>Whether an app was asked to connect and the Assistant is waiting for the user to finish signing in, in the browser.</summary>
    public bool IsConnecting => _connectStop is not null;

    /// <summary>Whether the address of the waiting sign-in's page is known, so that it can be copied.</summary>
    public bool HasConnectLink => _connectLink.Length > 0;

    /// <summary>Copies the waiting sign-in's page address, to open in whichever browser, private window or profile has the account wanted.</summary>
    public ICommand CopyConnectLinkCommand => _copyConnectLink;

    /// <summary>Stops waiting for the sign-in, so that it can be started again.</summary>
    public ICommand CancelConnectCommand => _cancelConnect;

    /// <summary>The address of the waiting sign-in's page, or empty.</summary>
    internal string ConnectLink => _connectLink;

    /// <summary>How text is put on the clipboard; replaced in tests.</summary>
    internal Func<string, bool> CopyText { get; set; } = TryCopyToClipboard;

    /// <summary>
    /// The application (client) ID the Assistant signs in to Microsoft with, for Microsoft To Do, when the user made an app of their own in Microsoft Entra. Empty uses Microsoft's own
    /// "Graph Command Line Tools" app. It is an identifier, not a secret, and is saved only when it is a valid ID or empty.
    /// </summary>
    public string MicrosoftClientId
    {
        get => _microsoftClientId;
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (!Set(ref _microsoftClientId, text))
            {
                return;
            }

            var valid = text.Length == 0 || Guid.TryParse(text, out _);
            MicrosoftClientIdInvalid = !valid;
            if (valid && !IsApplying)
            {
                var id = text.Length == 0 ? null : text.ToLowerInvariant();
                Commit(settings => settings with { Integrations = settings.Integrations with { MicrosoftClientId = id } });
            }
        }
    }

    /// <summary>Whether what was typed as the application ID is not one, so that it is not saved.</summary>
    public bool MicrosoftClientIdInvalid
    {
        get => _microsoftClientIdInvalid;
        private set => Set(ref _microsoftClientIdInvalid, value);
    }

    /// <summary>Whether the page can show connected apps at all.</summary>
    public bool HasManager => _manager is not null;

    /// <summary>Whether any connected app is installed.</summary>
    public bool HasConnectedApps => ConnectedApps.Count > 0;

    /// <summary>Whether nothing is connected, so the page says how something gets to be.</summary>
    public bool HasNoConnectedApps => ConnectedApps.Count == 0 && !HasHomeAssistant;

    /// <summary>What the page says when no connected app is installed.</summary>
    public string EmptyText => "Nothing connected yet.";

    /// <summary>Whether the File Explorer context-menu entry is installed.</summary>
    public bool ExplorerContextMenu
    {
        get => _explorer;
        set
        {
            if (!Set(ref _explorer, value))
            {
                return;
            }

            AppSettings Change(AppSettings settings) =>
                settings with { Integrations = settings.Integrations with { ExplorerContextMenuEnabled = value } };
            if (_explorerMenu is not { } menu)
            {
                Commit(Change);
            }
            else if (value)
            {
                Commit(Change, async () =>
                {
                    var added = await menu.InstallAsync().ConfigureAwait(true);
                    await RefreshExplorerMenuNoticeAsync().ConfigureAwait(true);
                    return added;
                }, "Ask Assistant couldn't be added to File Explorer's menu.");
            }
            else
            {
                Commit(Change, () => menu.RemoveAsync(), "Ask Assistant couldn't be removed from File Explorer's menu.");
                ExplorerFirstMenuNotice = string.Empty;
            }
        }
    }

    /// <summary>Whether the bridge to the Edge and Chrome extension is installed.</summary>
    public bool BrowserBridge
    {
        get => _browser;
        set
        {
            if (!Set(ref _browser, value))
            {
                return;
            }

            AppSettings Change(AppSettings settings) =>
                settings with { Integrations = settings.Integrations with { BrowserBridgeEnabled = value } };
            if (_browserBridge is not { } bridge)
            {
                Commit(Change);
            }
            else if (value)
            {
                Commit(Change, () => bridge.InstallAsync(), "The browser bridge couldn't be turned on.");
            }
            else
            {
                Commit(Change, () => bridge.RemoveAsync(), "The browser bridge couldn't be turned off.");
            }
        }
    }

    /// <summary>
    /// Whether the Assistant may look for newer versions of the connected apps it installed. Off until the user turns it on. It looks at most once a day, only
    /// while this page is open, sends only the names of the packages, and never installs anything.
    /// </summary>
    public bool CheckForUpdates
    {
        get => _checkForUpdates;
        set
        {
            if (!Set(ref _checkForUpdates, value))
            {
                return;
            }

            _checkNow.RaiseCanExecuteChanged();
            Commit(settings => settings with { Integrations = settings.Integrations with { CheckForIntegrationUpdates = value } });
            if (value && !IsApplying)
            {
                _ = CheckNowAsync(force: false);
            }
            else if (!value)
            {
                UpdateSummary = string.Empty;
            }
        }
    }

    /// <summary>What the page says about looking for updates: how it works, or what is stopping it.</summary>
    public string UpdateNote
    {
        get => _updateNote;
        private set => Set(ref _updateNote, value);
    }

    /// <summary>What the last look found, or empty.</summary>
    public string UpdateSummary
    {
        get => _updateSummary;
        private set
        {
            if (Set(ref _updateSummary, value))
            {
                OnPropertyChanged(nameof(HasUpdateSummary));
            }
        }
    }

    /// <summary>Whether there is an <see cref="UpdateSummary"/>.</summary>
    public bool HasUpdateSummary => _updateSummary.Length > 0;

    /// <summary>Whether a look for updates is running.</summary>
    public bool IsChecking
    {
        get => _checking;
        private set
        {
            if (Set(ref _checking, value))
            {
                _checkNow.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Looks for newer versions now.</summary>
    public ICommand CheckNowCommand => _checkNow;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_manager is not null)
        {
            _manager.Changed -= OnManagerChanged;
        }

        if (HomeAssistant is { } home)
        {
            home.PropertyChanged -= OnHomeAssistantChanged;
            home.Dispose();
        }
    }

    // Home Assistant was connected or disconnected: its line comes or goes, and so does its offer among the apps to add.
    private void OnHomeAssistantChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeAssistantItem.IsConnected))
        {
            OnPropertyChanged(nameof(HasHomeAssistant));
            OnPropertyChanged(nameof(HasNoConnectedApps));
            ShowAvailableApps(_installed);
        }
    }

    internal override void Apply(AppSettings settings, bool fresh)
    {
        ExplorerContextMenu = settings.Integrations.ExplorerContextMenuEnabled;
        if (fresh)
        {
            _ = RefreshExplorerMenuNoticeAsync();
        }

        BrowserBridge = settings.Integrations.BrowserBridgeEnabled;
        CheckForUpdates = settings.Integrations.CheckForIntegrationUpdates;
        MicrosoftClientId = settings.Integrations.MicrosoftClientId ?? string.Empty;
        if (fresh && _manager is not null)
        {
            // The window was opened (or a change was refused): the list is read again, and, when the user turned it on, newer versions are looked for.
            _ = RefreshAsync(checkForUpdates: settings.Integrations.CheckForIntegrationUpdates);
        }
    }

    internal async Task<bool> HasRegisteredExplorerEntryAsync() => _explorerMenu is { } menu && await menu.IsInstalledAsync() == true;

    private void OpenDeveloperSettings() => OpenSettingsPage("ms-settings:developers");

    private async Task CheckExplorerMenuAgainAsync()
    {
        if (_explorerMenu is { } menu && _explorer)
        {
            await menu.InstallAsync().ConfigureAwait(true);
        }

        await RefreshExplorerMenuNoticeAsync().ConfigureAwait(true);
    }

    // Says so when the entry is under Show more options only, so the user knows what to do about it.
    private async Task RefreshExplorerMenuNoticeAsync()
    {
        if (_explorerMenu is not { } menu || !_explorer)
        {
            ExplorerFirstMenuNotice = string.Empty;
            return;
        }

        var inFirstMenu = await menu.IsInFirstMenuAsync().ConfigureAwait(true);
        ExplorerFirstMenuNotice = _explorer && !inFirstMenu
            ? "Ask Assistant is under Show more options. Windows 11 puts it in the first menu only while Developer Mode is on: turn that on in Windows, then press Check again."
            : string.Empty;
    }

    /// <summary>Reads the installed connected apps and shows them. When <paramref name="checkForUpdates"/>, newer versions are looked for first, at most once a day.</summary>
    public async Task RefreshAsync(bool checkForUpdates = false)
    {
        if (HomeAssistant is { } home)
        {
            await home.RefreshAsync().ConfigureAwait(true);
        }

        if (_manager is null)
        {
            return;
        }

        try
        {
            if (checkForUpdates && await _manager.GetUpdateCheckStateAsync().ConfigureAwait(true) == UpdateCheckState.Ready)
            {
                var summary = await _manager.CheckForUpdatesAsync(force: false).ConfigureAwait(true);
                UpdateSummary = summary.Available > 0 || summary.Failed > 0 ? summary.Message : string.Empty;
            }

            await ShowAsync().ConfigureAwait(true);
            await ExplainUpdatesAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IntegrationException or IOException)
        {
            // The list could not be read just now; what is shown stays as it was.
        }
    }

    // Looks for newer versions because the user asked, or turned the setting on.
    private async Task CheckNowAsync(bool force = true)
    {
        if (_manager is null)
        {
            return;
        }

        IsChecking = true;
        try
        {
            // A look the user asked for with the button is a use of External Web and Image Search: asked about when it is set to ask each time (step 119). The daily look that
            // runs by itself when the page opens is not asked about, so it is refused until the permission is allowed.
            var approval = force && _gate is not null ? await AskAsync().ConfigureAwait(true) : null;
            if (force && _gate is not null && approval is null)
            {
                UpdateSummary = "You did not allow the web look-up this time, so nothing was looked up.";
                return;
            }

            UpdateCheckSummary summary;
            using (approval)
            {
                summary = await _manager.CheckForUpdatesAsync(force).ConfigureAwait(true);
            }

            UpdateSummary = summary.Message;
            await ShowAsync().ConfigureAwait(true);
            await ExplainUpdatesAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IntegrationException or IOException)
        {
            UpdateSummary = "I could not look for updates just now.";
        }
        finally
        {
            IsChecking = false;
        }
    }

    // Asks about a look that sends the names of the installed packages to their registries, when the permission asks each time. The scope returned lasts for the look; it is a
    // no-op scope when the permission allows it anyway or is off (the manager then says so itself), and null when the user said no.
    private async Task<IDisposable?> AskAsync()
    {
        var grant = await _gate!.RequestAsync(
            Assistant.Core.Domain.PermissionCapability.ExternalSearch, "Look up the latest versions of the connected apps you installed. Only the packages' names are sent.")
            .ConfigureAwait(true);
        return grant.Decision.Reason is Assistant.Core.Domain.PermissionDecisionReason.Declined or Assistant.Core.Domain.PermissionDecisionReason.CouldNotAsk ? null : grant.Enter();
    }

    // Shows the manager's list, keeping the item a user is working with so that nothing they have open is lost.
    private async Task ShowAsync()
    {
        var list = await _manager!.ListAsync().ConfigureAwait(true);
        foreach (var gone in ConnectedApps.Where(item => list.All(info => info.Id != item.Id)).ToList())
        {
            ConnectedApps.Remove(gone);
        }

        for (var index = 0; index < list.Count; index++)
        {
            var info = list[index];
            if (ConnectedApps.FirstOrDefault(item => item.Id == info.Id) is { } existing)
            {
                existing.Show(info);
                continue;
            }

            var item = new ConnectedAppItem(info, _manager, _offers, _gate, _connector);
            item.Removed += OnItemRemoved;
            ConnectedApps.Insert(Math.Min(index, ConnectedApps.Count), item);
        }

        OnPropertyChanged(nameof(HasConnectedApps));
        OnPropertyChanged(nameof(HasNoConnectedApps));
        ShowAvailableApps(list);
    }

    // The known apps that are not connected: the ones that are are in the list above.
    private void ShowAvailableApps(IReadOnlyList<IntegrationInfo> installed)
    {
        _installed = installed;
        if (_connector is null)
        {
            return;
        }

        // An app that is connected one way is not offered the other way too (Microsoft To Do, direct and through Pipedream): it is in the list above already.
        var wanted = KnownEndpoints.All
            .Where(endpoint => installed.All(info => info.Id != endpoint.IntegrationId && !string.Equals(info.Name, endpoint.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Home Assistant is the user's own server: its line opens the form that asks for its address and token. Once connected it is in the list above.
        var homeAssistant = Assistant.UI.Onboarding.ConnectionsSetupViewModel.HomeAssistant;
        if (_connections is { IsAvailable: true, Home: not null } && !HasHomeAssistant)
        {
            wanted.Add(homeAssistant);
        }

        foreach (var gone in AvailableApps.Where(item => wanted.All(endpoint => endpoint.AppKey != item.AppKey)).ToList())
        {
            AvailableApps.Remove(gone);
        }

        foreach (var endpoint in wanted.Where(endpoint => AvailableApps.All(item => item.AppKey != endpoint.AppKey)))
        {
            AvailableApps.Add(endpoint.AppKey == homeAssistant.AppKey
                ? new KnownAppItem(
                    endpoint, OpenConnectionFormAsync,
                    "Your own Home Assistant. You give its address and an access token you make in it; nothing has to be added to Home Assistant, and on your own network nothing goes over the internet.", "Set up")
                : new KnownAppItem(endpoint, ConnectAsync));
        }

        OnPropertyChanged(nameof(HasAvailableApps));
    }

    /// <summary>Whether any app is left to connect.</summary>
    public bool HasAvailableApps => AvailableApps.Count > 0;

    // An app that needs an address: the form under the list is opened, filled in with what the app's is usually.
    private Task OpenConnectionFormAsync(KnownEndpoint endpoint)
    {
        ConnectStatus = _connections?.OpenEditorFor(endpoint.AppKey) == true
            ? $"Fill in the form below to connect {endpoint.Name}."
            : "Finish or cancel the connection that is waiting first.";
        return Task.CompletedTask;
    }

    // The user clicked Connect: the browser opens on the app's page, and the answer is said under the list. While it waits the page's address can be copied and the wait
    // ended, because a page that fails in the browser (an account the organization does not allow) never comes back here.
    private async Task ConnectAsync(KnownEndpoint endpoint)
    {
        if (_connectStop is not null)
        {
            ConnectStatus = "Finish or cancel the sign-in that is waiting first.";
            return;
        }

        using var stop = new CancellationTokenSource();
        _connectStop = stop;
        RaiseConnectState();
        ConnectStatus = $"Your browser is opening so you can sign in to {endpoint.Name}. If it uses the wrong account, choose 'Use another account' there, or copy the sign-in link below and open it in another browser or a private window. I will wait up to five minutes.";
        var context = SynchronizationContext.Current;
        var options = new OAuthSignInOptions
        {
            AddressReady = address =>
            {
                void Show()
                {
                    if (ReferenceEquals(_connectStop, stop))
                    {
                        _connectLink = address.AbsoluteUri;
                        RaiseConnectState();
                    }
                }

                if (context is null)
                {
                    Show();
                }
                else
                {
                    context.Post(_ => Show(), null);
                }
            },
        };
        try
        {
            var outcome = await _connector!.ConnectAsync(endpoint, progress: null, stop.Token, options).ConfigureAwait(true);
            ConnectStatus = outcome.Message;
        }
        catch (OperationCanceledException)
        {
            ConnectStatus = $"I stopped before you signed in to {endpoint.Name}, so nothing was connected.";
        }
        finally
        {
            _connectStop = null;
            _connectLink = string.Empty;
            RaiseConnectState();
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private void CopyConnectLink()
    {
        if (_connectLink.Length > 0)
        {
            ConnectStatus = CopyText(_connectLink)
                ? "The sign-in link is copied. Paste it into the address bar of the browser, private window or profile with the account you want; I am still waiting."
                : "The clipboard is busy. Try again.";
        }
    }

    private void RaiseConnectState()
    {
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(HasConnectLink));
        _copyConnectLink.RaiseCanExecuteChanged();
        _cancelConnect.RaiseCanExecuteChanged();
    }

    private static bool TryCopyToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    private async Task ExplainUpdatesAsync()
    {
        UpdateNote = await _manager!.GetUpdateCheckStateAsync().ConfigureAwait(true) switch
        {
            UpdateCheckState.Blocked =>
                "Looking for updates needs the web: turn Local Only mode off in Settings > Privacy and allow External Web and Image Search in Settings > Permissions.",
            _ => "It looks at most once a day, only while this page is open, and sends only the names of the packages. It never installs anything: an update is only "
                + "offered, and you decide.",
        };
    }

    private void OnItemRemoved(object? sender, EventArgs e)
    {
        if (sender is ConnectedAppItem item)
        {
            item.Removed -= OnItemRemoved;
            ConnectedApps.Remove(item);
            OnPropertyChanged(nameof(HasConnectedApps));
            OnPropertyChanged(nameof(HasNoConnectedApps));
        }
    }

    // The list changed somewhere (an integration was installed from the conversation, or removed): the page shows it, on the UI thread.
    private void OnManagerChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            _dispatcher.InvokeAsync(async () =>
            {
                if (!_disposed)
                {
                    await RefreshAsync().ConfigureAwait(true);
                }
            });
        }
    }
}
