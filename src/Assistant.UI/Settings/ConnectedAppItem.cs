using System.IO;
using System.ComponentModel;
using System.Windows.Input;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp.Auth;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Settings;

/// <summary>
/// One connected app in Settings > Integrations (PROJECT_SPEC §4.8, step 109): its name, where it came from, its version, whether it is on, how it is
/// doing, what it may do, and whether a newer version is known; and the things the user can do with it: turn it on or off, reconnect, update and remove.
/// Nothing here changes an integration on its own: every change is the user's click. An update is shown as the same approval panel an installation
/// has, under the app, and is made only when the user clicks Update on it. Removing asks once more first, since it deletes what was installed.
/// </summary>
public sealed class ConnectedAppItem : NotifyingObject
{
    private readonly IIntegrationManager _manager;
    private readonly IIntegrationOffers? _offers;
    private readonly Assistant.Core.Permissions.IPermissionGate? _gate;
    private readonly RelayCommand _reconnect;
    private readonly RelayCommand _update;
    private readonly RelayCommand _remove;
    private readonly RelayCommand _confirmRemove;
    private readonly RelayCommand _keep;
    private readonly IIntegrationConnector? _connector;
    private readonly RelayCommand _signIn;
    private readonly RelayCommand _signInPrivate;
    private readonly RelayCommand _copySignInLink;
    private readonly RelayCommand _cancelSignIn;
    private CancellationTokenSource? _signInStop;
    private string _signInLink = string.Empty;
    private readonly RelayCommand _signOut;
    private readonly RelayCommand _useToken;
    private string _tokenText = string.Empty;
    private IntegrationInfo _info;
    private IntegrationAccess _access;
    private bool _enabled;
    private bool _applying;
    private bool _busy;
    private bool _confirmingRemove;
    private string _message = string.Empty;
    private IntegrationOfferContent? _offer;

    internal ConnectedAppItem(
        IntegrationInfo info, IIntegrationManager manager, IIntegrationOffers? offers, Assistant.Core.Permissions.IPermissionGate? gate = null,
        IIntegrationConnector? connector = null)
    {
        _connector = connector;
        _manager = manager;
        _offers = offers;
        _gate = gate;
        _info = info;
        _signIn = new RelayCommand(_ => _ = SignInAsync(privateWindow: false), _ => !Busy && _connector is not null && _info.CanSignIn);
        _signInPrivate = new RelayCommand(_ => _ = SignInAsync(privateWindow: true), _ => !Busy && _connector is not null && _info.CanSignIn);
        _copySignInLink = new RelayCommand(_ => CopySignInLink(), _ => HasSignInLink);
        _cancelSignIn = new RelayCommand(_ => _signInStop?.Cancel(), _ => IsSigningIn);
        _signOut = new RelayCommand(_ => _ = SignOutAsync(), _ => !Busy && _connector is not null && _info.SignedIn);
        _useToken = new RelayCommand(_ => _ = UseTokenAsync(), _ => !Busy && _connector is not null && _info.CanSignIn && _tokenText.Trim().Length > 0);
        _enabled = info.Enabled;
        _access = info.Access;
        ShowKeys();
        _reconnect = new RelayCommand(_ => _ = ReconnectAsync(), _ => !Busy && Enabled);
        _update = new RelayCommand(_ => _ = UpdateAsync(), _ => !Busy && CanUpdate && !IsOfferOpen);
        _remove = new RelayCommand(_ => ConfirmingRemove = true, _ => !Busy && !ConfirmingRemove);
        _confirmRemove = new RelayCommand(_ => _ = RemoveAsync(), _ => !Busy && ConfirmingRemove);
        _keep = new RelayCommand(_ => ConfirmingRemove = false);
    }

    /// <summary>Raised after the integration was removed, so the page can take it out of its list.</summary>
    internal event EventHandler? Removed;

    /// <summary>The integration's id.</summary>
    public string Id => _info.Id;

    /// <summary>The app's name.</summary>
    public string Name => _info.Name;

    /// <summary>Where it came from, in words.</summary>
    public string Source => _info.Source;

    /// <summary>The version that is installed.</summary>
    public string Version => _info.Version == "—" ? "No version (it is hosted)" : "Version " + _info.Version;

    /// <summary>The name, source and version in one line.</summary>
    public string Summary => $"{Source} · {Version}";

    /// <summary>How it is doing, in words.</summary>
    public string Health => _info.Health;

    /// <summary>How it is doing, for the colour of the words.</summary>
    public IntegrationHealthLevel HealthLevel => _info.HealthLevel;

    /// <summary>What it may do and what it needs.</summary>
    public string Permissions => _info.Permissions;

    /// <summary>How many tools it offered when it was last connected to.</summary>
    public string ToolsText => _info.ToolCount switch
    {
        0 => "Its tools are read the first time it is used.",
        1 => "Offers 1 tool.",
        var count => $"Offers {count} tools.",
    };

    /// <summary>Whether a newer version is known.</summary>
    public bool HasUpdate => _info.UpdateVersion is not null;

    /// <summary>"Update available: version 13.4.0", or empty.</summary>
    public string UpdateText => _info.UpdateVersion is { } version ? $"Update available: version {version}" : string.Empty;

    /// <summary>Whether the Assistant installed it from a package registry and so can look for and make an update.</summary>
    public bool CanUpdate => _info.CanUpdate;

    /// <summary>The label of the Update button.</summary>
    public string UpdateLabel => HasUpdate ? "Update" : "Check for update";

    /// <summary>Whether it may be used. Changing it turns the integration on or off at once.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            // The value is always taken; only a change the user made asks the manager to act on it.
            if (!Set(ref _enabled, value))
            {
                return;
            }

            _reconnect.RaiseCanExecuteChanged();
            if (!_applying)
            {
                _ = SetEnabledAsync(value);
            }
        }
    }

    /// <summary>Whether the Assistant may use the tools of this app that only read.</summary>
    public bool AllowReads
    {
        get => _access.Reads;
        set => ChangeAccess(new IntegrationAccessChange { Reads = value }, _access.Reads != value);
    }

    /// <summary>Whether the Assistant may use the tools of this app that change something. Each use is still confirmed.</summary>
    public bool AllowChanges
    {
        get => _access.Changes;
        set => ChangeAccess(new IntegrationAccessChange { Changes = value }, _access.Changes != value);
    }

    /// <summary>Whether the Assistant may connect to this app over a network.</summary>
    public bool AllowNetwork
    {
        get => _access.Network;
        set => ChangeAccess(new IntegrationAccessChange { Network = value }, _access.Network != value);
    }

    /// <summary>Whether this app may use the account it signs in to.</summary>
    public bool AllowAccount
    {
        get => _access.Account;
        set => ChangeAccess(new IntegrationAccessChange { Account = value }, _access.Account != value);
    }

    /// <summary>Whether the Assistant may look for a newer version of this app.</summary>
    public bool AllowUpdates
    {
        get => _access.Updates;
        set => ChangeAccess(new IntegrationAccessChange { Updates = value }, _access.Updates != value);
    }

    /// <summary>Whether the network choice means something for this app: it is reached over a network, or reaches out itself.</summary>
    public bool ShowsNetwork => _access.NetworkApplies;

    /// <summary>Whether the account choice means something for this app: it signs in.</summary>
    public bool ShowsAccount => _access.AccountApplies;

    /// <summary>Whether the update choice means something for this app: the Assistant installed it from a package registry.</summary>
    public bool ShowsUpdates => _access.UpdatesApply;

    private bool _isExpanded;

    /// <summary>
    /// Whether the app's details show under its line: what it may do, its sign-in and keys, and the buttons that reconnect, update and remove it. Closed, the list is
    /// a line for each app with its switch, which is all most visits to the page need.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(DetailsLabel));
            }
        }
    }

    /// <summary>What the button that opens and closes the details says.</summary>
    public string DetailsLabel => _isExpanded ? "Hide details" : "Manage";

    private ICommand? _toggleDetails;

    /// <summary>Opens the details, or closes them.</summary>
    public ICommand ToggleDetailsCommand => _toggleDetails ??= new RelayCommand(_ => IsExpanded = !IsExpanded);

    /// <summary>Whether something the user asked for is running.</summary>
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                _reconnect.RaiseCanExecuteChanged();
                _update.RaiseCanExecuteChanged();
                _remove.RaiseCanExecuteChanged();
                _confirmRemove.RaiseCanExecuteChanged();
                _signIn.RaiseCanExecuteChanged();
                _signInPrivate.RaiseCanExecuteChanged();
                _signOut.RaiseCanExecuteChanged();
                _useToken.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether the user was asked to confirm removing it, and has not answered.</summary>
    public bool ConfirmingRemove
    {
        get => _confirmingRemove;
        private set
        {
            if (Set(ref _confirmingRemove, value))
            {
                _remove.RaiseCanExecuteChanged();
                _confirmRemove.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The question asked before it is removed.</summary>
    public string RemoveQuestion => _info.IsManaged
        ? $"Remove {Name}? The files the Assistant installed for it are deleted. {Name} itself is not touched."
        : $"Remove {Name} from the Assistant? {Name} itself is not touched.";

    /// <summary>What the last thing the user asked for came to, or empty.</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    /// <summary>Whether there is a <see cref="Message"/>.</summary>
    public bool HasMessage => _message.Length > 0;

    /// <summary>The approval panel of an update, shown under the app while the user decides, or <see langword="null"/>.</summary>
    public IntegrationOfferContent? Offer
    {
        get => _offer;
        private set
        {
            if (Set(ref _offer, value))
            {
                OnPropertyChanged(nameof(IsOfferOpen));
                _update.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether an update waits for the user's answer.</summary>
    public bool IsOfferOpen => _offer is not null;

    /// <summary>Whether the user can sign in to this app, in the browser, or give it an access token.</summary>
    public bool CanSignIn => _connector is not null && _info.CanSignIn;

    /// <summary>How the app's sign-in stands, in words.</summary>
    public string SignInText => !_info.CanSignIn ? string.Empty : _info.SignedIn ? "Signed in." : _info.NeedsSignIn ? "Needs you to sign in." : "No sign-in needed so far.";

    /// <summary>The label of the sign-in button.</summary>
    public string SignInLabel => _info.SignedIn ? "Sign in again" : "Sign in";

    /// <summary>Whether a sign-in is in place, so that it can be taken away.</summary>
    public bool IsSignedIn => _info.SignedIn;

    /// <summary>An access token the user made in the app themselves, typed here. It is kept in Windows' credential store and cleared from here at once.</summary>
    public string TokenText
    {
        get => _tokenText;
        set
        {
            if (Set(ref _tokenText, value ?? string.Empty))
            {
                _useToken.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The keys this app was installed with, each with a box to give its value in; empty for an app that needs none.</summary>
    public System.Collections.ObjectModel.ObservableCollection<KeyEntry> Keys { get; } = [];

    /// <summary>Whether the app has keys to give.</summary>
    public bool HasKeys => Keys.Count > 0;

    // Gives a key to the app: the value goes to the connector and out of the box at once, and what came of it is said under the app.
    internal async Task SetKeyAsync(KeyEntry entry)
    {
        var value = entry.Text;
        entry.Text = string.Empty;
        Busy = true;
        try
        {
            var outcome = await _connector!.SetKeyAsync(Id, entry.Name, value).ConfigureAwait(true);
            Message = outcome.IsInstalled ? outcome.Message : outcome.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private void ShowKeys()
    {
        foreach (var gone in Keys.Where(entry => !_info.KeyNames.Contains(entry.Name)).ToList())
        {
            Keys.Remove(gone);
        }

        foreach (var name in _info.KeyNames.Where(name => Keys.All(entry => entry.Name != name)))
        {
            Keys.Add(new KeyEntry(name, this));
        }

        OnPropertyChanged(nameof(HasKeys));
    }

    /// <summary>Opens the browser on the app's own page to sign in; the page lists the accounts, so the one wanted can be chosen.</summary>
    public ICommand SignInCommand => _signIn;

    /// <summary>
    /// Opens the app's sign-in page in a private browser window (InPrivate, incognito), which has none of the accounts the everyday browser is signed in to, so that another account
    /// than the one a school or work sign-in leaves there can be used.
    /// </summary>
    public ICommand SignInPrivateCommand => _signInPrivate;

    /// <summary>Copies the sign-in page's address, to open it in whichever browser or profile has the account wanted. Only while a sign-in is waiting.</summary>
    public ICommand CopySignInLinkCommand => _copySignInLink;

    /// <summary>Stops waiting for a sign-in, so that it can be started again. Only while a sign-in is waiting.</summary>
    public ICommand CancelSignInCommand => _cancelSignIn;

    /// <summary>Whether a sign-in has been started and the Assistant is waiting for the user to finish it in the browser.</summary>
    public bool IsSigningIn => _signInStop is not null;

    /// <summary>Whether the address of the waiting sign-in's page is known, so that it can be copied.</summary>
    public bool HasSignInLink => _signInLink.Length > 0;

    /// <summary>The address of the waiting sign-in's page, or empty. It is the service's own page; it signs no one in.</summary>
    internal string SignInLink => _signInLink;

    /// <summary>How text is put on the clipboard; replaced in tests.</summary>
    internal Func<string, bool> CopyText { get; set; } = TryCopyToClipboard;

    /// <summary>Forgets the sign-in.</summary>
    public ICommand SignOutCommand => _signOut;

    /// <summary>Keeps the access token that was typed and uses it to sign in.</summary>
    public ICommand UseTokenCommand => _useToken;

    /// <summary>Starts the app's program again and reads its tools.</summary>
    public ICommand ReconnectCommand => _reconnect;

    /// <summary>Looks for a newer version and, when there is one, shows what it would install for the user to approve.</summary>
    public ICommand UpdateCommand => _update;

    /// <summary>Asks whether to remove it.</summary>
    public ICommand RemoveCommand => _remove;

    /// <summary>Removes it, after the question.</summary>
    public ICommand ConfirmRemoveCommand => _confirmRemove;

    /// <summary>Keeps it, after the question.</summary>
    public ICommand KeepCommand => _keep;

    /// <summary>Shows what the manager says of the integration now, leaving what the user is doing (a question, an update panel) alone.</summary>
    internal void Show(IntegrationInfo info)
    {
        _applying = true;
        try
        {
            _info = info;
            _access = info.Access;
            Enabled = info.Enabled;
        }
        finally
        {
            _applying = false;
        }

        foreach (var name in new[]
                 {
                     nameof(Name), nameof(Source), nameof(Version), nameof(Summary), nameof(Health), nameof(HealthLevel), nameof(Permissions), nameof(ToolsText),
                     nameof(HasUpdate), nameof(UpdateText), nameof(CanUpdate), nameof(UpdateLabel), nameof(RemoveQuestion),
                     nameof(AllowReads), nameof(AllowChanges), nameof(AllowNetwork), nameof(AllowAccount), nameof(AllowUpdates),
                     nameof(ShowsNetwork), nameof(ShowsAccount), nameof(ShowsUpdates),
                     nameof(CanSignIn), nameof(SignInText), nameof(SignInLabel), nameof(IsSignedIn),
                 })
        {
            OnPropertyChanged(name);
        }

        ShowKeys();

        _reconnect.RaiseCanExecuteChanged();
        _update.RaiseCanExecuteChanged();
        _signIn.RaiseCanExecuteChanged();
        _signInPrivate.RaiseCanExecuteChanged();
        _signOut.RaiseCanExecuteChanged();
    }

    // The user clicked Sign in (or Sign in in a private window): their browser opens on the app's own page, and what comes of it is said under the app. While it waits the
    // page's address can be copied and the wait can be ended, because a page that fails in the browser (an account the organization does not allow) never comes back here.
    private async Task SignInAsync(bool privateWindow)
    {
        using var stop = new CancellationTokenSource();
        Busy = true;
        _signInStop = stop;
        RaiseSignInState();
        Message = privateWindow
            ? $"A private browser window is opening so you can sign in to {Name} with any account. I will wait up to five minutes."
            : $"Your browser is opening so you can sign in to {Name}. If it uses the wrong account, choose 'Use another account' there, or copy the sign-in link below and open it in another browser or a private window. I will wait up to five minutes.";
        var context = SynchronizationContext.Current;
        var options = new OAuthSignInOptions
        {
            PrivateWindow = privateWindow,
            AddressReady = address =>
            {
                void Show()
                {
                    if (ReferenceEquals(_signInStop, stop))
                    {
                        _signInLink = address.AbsoluteUri;
                        RaiseSignInState();
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
            var outcome = await _connector!.SignInAsync(Id, progress: null, stop.Token, options).ConfigureAwait(true);
            Message = outcome.IsInstalled ? $"You are signed in to {Name}." : outcome.Message;
        }
        catch (OperationCanceledException)
        {
            Message = $"I stopped before you signed in to {Name}, so nothing was connected.";
        }
        finally
        {
            _signInStop = null;
            _signInLink = string.Empty;
            RaiseSignInState();
            Busy = false;
        }
    }

    private void CopySignInLink()
    {
        if (_signInLink.Length == 0)
        {
            return;
        }

        Message = CopyText(_signInLink)
            ? $"The sign-in link is copied. Paste it into the address bar of the browser, private window or profile with the account you want; I am still waiting for {Name}."
            : "The clipboard is busy. Try again.";
    }

    private void RaiseSignInState()
    {
        OnPropertyChanged(nameof(IsSigningIn));
        OnPropertyChanged(nameof(HasSignInLink));
        _copySignInLink.RaiseCanExecuteChanged();
        _cancelSignIn.RaiseCanExecuteChanged();
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

    private async Task SignOutAsync()
    {
        Busy = true;
        try
        {
            await _connector!.SignOutAsync(Id).ConfigureAwait(true);
            Message = $"You are signed out of {Name}. Nothing it had of yours is kept here.";
        }
        catch (Exception exception) when (exception is IntegrationException or IOException or Assistant.Core.Contracts.SecretStoreException)
        {
            Message = $"I could not sign you out of {Name}. Try again.";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task UseTokenAsync()
    {
        var token = _tokenText;
        TokenText = string.Empty;
        Busy = true;
        try
        {
            var outcome = await _connector!.UseTokenAsync(Id, token).ConfigureAwait(true);
            Message = outcome.IsInstalled ? $"{Name} now uses the access token you gave." : outcome.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    // A choice the user made for what this app may do: it is made at once, and the switch goes back where it was if it could not be made.
    private void ChangeAccess(IntegrationAccessChange change, bool changed)
    {
        if (!changed || _applying)
        {
            return;
        }

        _ = ApplyAccessAsync(change);
    }

    /// <summary>Changes what the app may do and shows what the manager says came of it; the switches read the saved values again if that could not be done.</summary>
    internal async Task ApplyAccessAsync(IntegrationAccessChange change)
    {
        try
        {
            var info = await _manager.SetAccessAsync(Id, change).ConfigureAwait(true);
            if (info is not null)
            {
                Show(info);
                Message = $"What {Name} may do was changed.";
            }
            else
            {
                Message = $"{Name} is not installed any more.";
            }
        }
        catch (Exception exception) when (exception is IntegrationException or IOException)
        {
            Show(_info);
            Message = $"What {Name} may do could not be changed. Check that the disk has room, then try again.";
        }
    }

    /// <summary>Turns the integration on or off, putting the switch back where it was if that could not be done.</summary>
    internal async Task SetEnabledAsync(bool enabled)
    {
        try
        {
            await _manager.SetEnabledAsync(Id, enabled).ConfigureAwait(true);
            Message = enabled ? $"{Name} is turned on. It starts when you need it." : $"{Name} is turned off.";
        }
        catch (Exception exception) when (exception is IntegrationException or IOException)
        {
            _applying = true;
            try
            {
                Enabled = !enabled;
            }
            finally
            {
                _applying = false;
            }

            Message = $"{Name} could not be turned {(enabled ? "on" : "off")}. Check that the disk has room, then try again.";
        }
    }

    /// <summary>Starts the integration's program again, reads its tools and says how it went.</summary>
    internal async Task ReconnectAsync()
    {
        Busy = true;
        Message = $"Connecting to {Name}…";
        try
        {
            var outcome = await _manager.ReconnectAsync(Id).ConfigureAwait(true);
            Message = outcome.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Looks for a newer version and shows it as an offer, or says why there is none.</summary>
    internal async Task UpdateAsync()
    {
        Busy = true;
        Message = $"Looking for a newer version of {Name}…";
        try
        {
            // Looking for a newer version is a use of External Web and Image Search, asked about each time when it is set to (step 119).
            var approval = await AskAboutLookUpAsync().ConfigureAwait(true);
            if (approval is null)
            {
                Message = "You did not allow the web look-up this time, so nothing was looked up.";
                return;
            }

            UpdatePreparation preparation;
            using (approval)
            {
                preparation = await _manager.PrepareUpdateAsync(Id).ConfigureAwait(true);
            }

            if (preparation is { Status: UpdatePreparationStatus.Offered, Offer: { } offer } && _offers is not null)
            {
                Message = preparation.Message;
                var panel = new IntegrationOfferContent(
                    offer, (progress, cancellationToken) => _offers.AcceptAsync(offer.OfferId, progress, cancellationToken), () => _offers.Decline(offer.OfferId));
                panel.PropertyChanged += OnOfferChanged;
                Offer = panel;
            }
            else
            {
                Message = preparation.Message;
            }
        }
        finally
        {
            Busy = false;
        }
    }

    // The scope of the user's yes to a look on the web, or a scope that changes nothing when the permission allows it anyway or is off (the manager says why not), or null for a no.
    private async Task<IDisposable?> AskAboutLookUpAsync()
    {
        if (_gate is null)
        {
            return new NoApproval();
        }

        var grant = await _gate.RequestAsync(
            Assistant.Core.Domain.PermissionCapability.ExternalSearch, $"Look up the latest version of {Name}. Only the package's name is sent.").ConfigureAwait(true);
        return grant.Decision.Reason is Assistant.Core.Domain.PermissionDecisionReason.Declined or Assistant.Core.Domain.PermissionDecisionReason.CouldNotAsk ? null : grant.Enter();
    }

    private sealed class NoApproval : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>Removes the integration, and the files the Assistant kept for it.</summary>
    internal async Task RemoveAsync()
    {
        Busy = true;
        try
        {
            var outcome = await _manager.RemoveAsync(Id).ConfigureAwait(true);
            Message = outcome.Message;
            ConfirmingRemove = false;
            if (outcome.Removed)
            {
                Removed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    // An update the user turned down goes away; one that was made leaves its result in view until the user leaves.
    private void OnOfferChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IntegrationOfferContent.State) || sender is not IntegrationOfferContent panel)
        {
            return;
        }

        if (panel.State == IntegrationOfferState.Cancelled)
        {
            panel.PropertyChanged -= OnOfferChanged;
            Offer = null;
            Message = "Not updated. Nothing was downloaded or changed.";
        }
        else if (panel.State == IntegrationOfferState.Installed)
        {
            Message = string.Empty;
        }
    }
}

/// <summary>One key an app asks for in Settings > Integrations: its name, and the box the user types its value in. The value is given to the connector and cleared at once.</summary>
public sealed class KeyEntry : NotifyingObject
{
    private readonly ConnectedAppItem _owner;
    private string _text = string.Empty;

    internal KeyEntry(string name, ConnectedAppItem owner)
    {
        Name = name;
        _owner = owner;
        _set = new RelayCommand(_ => _ = _owner.SetKeyAsync(this), _ => _text.Trim().Length > 0);
    }

    /// <summary>The key's name, as the app asks for it.</summary>
    public string Name { get; }

    /// <summary>What the user typed.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value ?? string.Empty))
            {
                _set.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Keeps the key.</summary>
    public System.Windows.Input.ICommand SetCommand => _set;

    private readonly RelayCommand _set;
}
