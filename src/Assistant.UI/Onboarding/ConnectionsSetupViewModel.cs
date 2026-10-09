using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.Home;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Mcp.Auth;
using Assistant.Tools.Search;
using Assistant.UI.Settings;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Onboarding;

/// <summary>Explicit, optional MCP connections shared by setup and Settings.</summary>
public sealed class ConnectionsSetupViewModel : NotifyingObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IAppEventBus _events;
    private readonly IInstalledIntegrationRegistry? _registry;
    private readonly IIntegrationConnector? _connector;
    private readonly IIntegrationManager? _manager;
    private readonly IHomeAssistant? _home;
    private readonly Dispatcher _dispatcher;
    private readonly RelayCommand _connect, _add, _save, _cancel, _cancelEditor, _copyLink;
    private CancellationTokenSource? _stop;
    private string _status = "", _name = "", _address = "", _authentication = "Browser sign-in", _link = "";
    private bool _editor, _disposed;

    public ConnectionsSetupViewModel(ISettingsService settings, IAppEventBus events,
        IInstalledIntegrationRegistry? registry = null, IIntegrationConnector? connector = null, IIntegrationManager? manager = null, IHomeAssistant? home = null)
    {
        _settings = settings; _events = events; _registry = registry; _connector = connector; _manager = manager; _home = home;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Apps.Add(new(KnownEndpoints.For("microsofttodopipedream")!, "Tasks, lists and reminders · via Pipedream", "todo"));
        Apps.Add(new(KnownEndpoints.For("discordpipedream")!, "Servers, channels and messages · via Pipedream", "discord"));
        Apps.Add(new(KnownEndpoints.For("beeper")!, "Your chats · requires Beeper Desktop on this PC", "beeper"));

        // Home Assistant is the user's own server, at an address only they know: its choice opens the form, which asks for the address and a token and nothing else.
        // It is reached through its own API (IHomeAssistant), and is offered only where there is one.
        if (_home is not null)
        {
            Apps.Add(new(HomeAssistant, "Lights, switches, fans, scenes and sensors · your own Home Assistant", "homeassistant", needsAddress: true));
            _home.Changed += OnHomeChanged;
        }

        _connect = new(
            parameter =>
            {
                if (parameter is not McpAppChoice app) return;
                if (app.NeedsAddress) OpenEditor(app);
                else _ = RunAsync(() => ConnectChoiceAsync(app.Endpoint));
            },
            _ => IsIdle && IsAvailable);
        _add = new(_ => OpenEditor(null), _ => IsIdle && IsAvailable);
        _save = new(_ => _ = RunAsync(AddServerAsync), _ => IsIdle && IsAvailable && ServerName.Trim().Length > 0 && ServerAddress.Trim().Length > 0);
        _cancel = new(_ => _stop?.Cancel(), _ => IsBusy);
        _cancelEditor = new(_ => { ShowAddEditor = false; PendingToken = null; _editorApp = null; RaiseEditor(); }, _ => IsIdle);
        _copyLink = new(_ => CopyLink(), _ => HasSignInLink);
        if (_registry is not null) _registry.Changed += OnRegistryChanged;
    }

    /// <summary>
    /// Home Assistant, as the setup offers it. The address is only where its form starts from (Home Assistant's usual name and port on a home network); the
    /// user's own is what is kept.
    /// </summary>
    internal static readonly KnownEndpoint HomeAssistant = new("homeassistant", "Home Assistant", "http://homeassistant.local:8123", "home-assistant.io");

    /// <summary>The user's Home Assistant, when the app has one to connect.</summary>
    internal IHomeAssistant? Home => _home;

    private McpAppChoice? _editorApp;

    public ObservableCollection<McpAppChoice> Apps { get; } = [];
    public IReadOnlyList<string> AuthenticationOptions { get; } = ["Browser sign-in", "Access token", "No authentication"];

    /// <summary>What the form is for: "Add an MCP server", or the app whose choice opened it ("Connect Home Assistant").</summary>
    public string EditorTitle => _editorApp is { } app ? "Connect " + app.Name : "Add an MCP server";

    /// <summary>What the user has to do in the app before the form can connect it; empty for a server of their own.</summary>
    public string EditorHelp => IsHomeEditor
        ? "Give your Home Assistant's address and an access token. To make a token, open Home Assistant, click your name at the bottom left, open Security, and under "
          + "Long-lived access tokens choose Create token. Nothing has to be added to Home Assistant."
        : string.Empty;

    /// <summary>Whether there is an <see cref="EditorHelp"/>.</summary>
    public bool HasEditorHelp => EditorHelp.Length > 0;

    /// <summary>Whether the form is Home Assistant's, which asks only for an address and a token.</summary>
    public bool IsHomeEditor => _editorApp?.Endpoint.AppKey == HomeAssistant.AppKey;

    /// <summary>Whether the form is for an MCP server of the user's own, with a name and a way of signing in to choose.</summary>
    public bool IsServerEditor => !IsHomeEditor;

    /// <summary>What the address field is called.</summary>
    public string AddressLabel => IsHomeEditor ? "Address" : "Server URL";

    /// <summary>What is said under the address field.</summary>
    public string AddressHint => IsHomeEditor
        ? "Such as http://homeassistant.local:8123 or http://192.168.1.20:8123. Plain http works on your own network; anywhere else it must be https."
        : "HTTPS, or HTTP for a server on this PC or your own network. Use the Streamable HTTP endpoint supplied by the app.";
    public bool IsAvailable => _registry is not null && _connector is not null;
    public bool IsBusy => _stop is not null;
    public bool IsIdle => !IsBusy;
    public bool ShowAddEditor { get => _editor; set => Set(ref _editor, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ServerName { get => _name; set { Set(ref _name, value); _save.RaiseCanExecuteChanged(); } }
    public string ServerAddress { get => _address; set { Set(ref _address, value); _save.RaiseCanExecuteChanged(); } }
    public string Authentication { get => _authentication; set { Set(ref _authentication, value); OnPropertyChanged(nameof(UsesToken)); } }
    public bool UsesToken => IsHomeEditor || Authentication == "Access token";
    public bool HasSignInLink => _link.Length > 0;
    // Held only while the user types; never persisted or logged.
    internal string? PendingToken { get; set; }
    internal Func<string, bool> ConfirmCloudConnection { get; set; } = _ => false;
    internal Func<string, bool> CopyText { get; set; } = address => { try { System.Windows.Clipboard.SetText(address); return true; } catch (System.Runtime.InteropServices.ExternalException) { return false; } };
    public ICommand ConnectCommand => _connect;
    public ICommand AddCommand => _add;
    public ICommand SaveServerCommand => _save;
    public ICommand CancelCommand => _cancel;
    public ICommand CancelEditorCommand => _cancelEditor;
    public ICommand CopySignInLinkCommand => _copyLink;

    public Task InitializeAsync() => RefreshAsync();

    /// <summary>Opens the form for the app with <paramref name="appKey"/> that needs an address (Home Assistant), as its Connect does: to connect it, or to change its address or token.</summary>
    internal bool OpenEditorFor(string appKey)
    {
        if (!IsIdle || !IsAvailable || Apps.FirstOrDefault(app => app.Endpoint.AppKey == appKey && app.NeedsAddress) is not { } choice)
        {
            return false;
        }

        OpenEditor(choice);
        return true;
    }

    // Opens the form: empty for a server of the user's own, or filled in with what the app's server is usually.
    private void OpenEditor(McpAppChoice? app)
    {
        _editorApp = app;
        PendingToken = null;
        if (app is null)
        {
            ServerName = ""; ServerAddress = ""; Authentication = "Browser sign-in";
        }
        else
        {
            // The address it has already, when it is being changed; otherwise where Home Assistant usually is.
            ServerName = app.Name; ServerAddress = _home?.Address ?? app.Endpoint.Endpoint; Authentication = "Access token";
        }

        RaiseEditor();
        ShowAddEditor = true;
    }

    private void RaiseEditor()
    {
        foreach (var name in new[] { nameof(EditorTitle), nameof(EditorHelp), nameof(HasEditorHelp), nameof(IsHomeEditor), nameof(IsServerEditor), nameof(AddressLabel), nameof(AddressHint), nameof(UsesToken) })
            OnPropertyChanged(name);
    }

    private async Task ConnectChoiceAsync(KnownEndpoint endpoint)
    {
        if (!await AllowEndpointAsync(endpoint)) return;
        var record = await _registry!.GetAsync(endpoint.IntegrationId, _stop!.Token);
        if (record is { Enabled: false })
            record = await _registry.UpdateAsync(record.Id, current => current with { Enabled = true }, _stop.Token);
        if (record is not null && record.Authentication.Kind != IntegrationAuthKind.OAuth)
        {
            Status = _manager is null ? "Manage this server's authentication in Settings → Integrations."
                : (await _manager.ReconnectAsync(record.Id, _stop.Token)).Message;
            return;
        }
        await ConnectAsync(endpoint);
    }

    private async Task<bool> AllowEndpointAsync(KnownEndpoint endpoint)
    {
        var settings = await _settings.LoadAsync(_stop!.Token);
        if (endpoint.RunsOnThisPc || !settings.Privacy.LocalOnly) return true;
        if (!ConfirmCloudConnection(endpoint.Name)) { Status = "Connection skipped. Local Only is still on."; return false; }
        var saved = await _settings.UpdateAsync(current => current with { Privacy = current.Privacy with { LocalOnly = false } }, _stop.Token);
        await _events.PublishAsync(new SettingsSaved(saved));
        return true;
    }

    private async Task ConnectAsync(KnownEndpoint endpoint)
    {
        if (!await AllowEndpointAsync(endpoint)) return;
        Status = endpoint.IsThroughPipedream
            ? $"Sign in to Pipedream in your browser and select {endpoint.Name}. Return here when you're connected."
            : $"Follow the sign-in in your browser to connect {endpoint.Name}.";
        var stop = _stop!;
        var outcome = await _connector!.ConnectAsync(endpoint, cancellationToken: stop.Token, options: new OAuthSignInOptions
        {
            AddressReady = address => _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_stop, stop) && !stop.IsCancellationRequested) { _link = address.AbsoluteUri; RaiseState(); }
            }),
        });
        Status = outcome.Message;
    }

    // Home Assistant's form: the address and the token are tried, and kept only when Home Assistant answers to them.
    private async Task ConnectHomeAsync()
    {
        if (string.IsNullOrWhiteSpace(PendingToken)) { Status = "Paste the access token you made in Home Assistant, then try again."; return; }
        if (HomeAssistantWords.NeedsInternet(ServerAddress) is { } remote && !await AllowEndpointAsync(remote)) return;
        Status = "Asking your Home Assistant…";
        var status = await _home!.ConnectAsync(ServerAddress, PendingToken, _stop!.Token);
        Status = HomeAssistantWords.Connected(status);
        if (status.IsReady)
        {
            ShowAddEditor = false; _editorApp = null; RaiseEditor();
        }
    }

    private async Task AddServerAsync()
    {
        if (IsHomeEditor && _home is not null) { await ConnectHomeAsync(); return; }
        var name = ServerName.Trim(); var address = ServerAddress.Trim();
        if (name.Length > IntegrationRules.MaxNameLength || name.Any(char.IsControl)
            || address.Length > 2048 || address.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || !McpNetwork.IsAllowed(uri)
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            Status = "Enter a name and an HTTPS MCP server URL, or an HTTP URL on this PC or your own network. Keep passwords and tokens out of the URL.";
            return;
        }

        // An app the setup knows keeps its own id, so that it is the same connection wherever it is listed; a server of the user's own is named after its address.
        var id = _editorApp is { } known
            ? known.Endpoint.IntegrationId
            : "mcp" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..20].ToLowerInvariant();
        if (Authentication == "Access token" && string.IsNullOrWhiteSpace(PendingToken))
        { Status = "Paste the server's access token, then try again."; return; }
        var existing = await _registry!.GetAsync(id, _stop!.Token);
        var authKind = Authentication == "Browser sign-in" ? IntegrationAuthKind.OAuth
            : Authentication == "Access token" ? IntegrationAuthKind.BearerToken : IntegrationAuthKind.None;
        if (existing is not null && existing.Authentication.Kind != authKind)
        { Status = "This URL is already added with another sign-in method. Remove it in Settings → Integrations before adding it again."; return; }
        var endpoint = new KnownEndpoint(id, name, uri.AbsoluteUri, uri.Host, RunsOnThisPc: uri.IsLoopback);
        if (!await AllowEndpointAsync(endpoint)) return;
        if (existing is { Enabled: false })
            await _registry.UpdateAsync(id, current => current with { Enabled = true }, _stop.Token);
        if (Authentication == "Browser sign-in") await ConnectAsync(endpoint);
        else
        {
            var record = await _registry!.GetAsync(id, _stop!.Token);
            if (record is null) record = await _registry.AddAsync(new InstalledIntegration
            {
                Id = id, Name = name, Source = new(IntegrationSourceKind.UserAdded, uri.Host), Enabled = true,
                Transport = new() { Kind = McpTransportKind.StreamableHttp, Endpoint = uri.AbsoluteUri },
                Permissions = new() { LeavesThisPc = !uri.IsLoopback },
                Authentication = Authentication == "Access token"
                    ? new() { Kind = IntegrationAuthKind.BearerToken, State = IntegrationAuthState.NeedsSignIn,
                        Secrets = [new("Authorization", id + ".token")] }
                    : IntegrationAuthentication.None,
            }, _stop.Token);
            if (Authentication == "Access token")
            {
                Status = (await _connector!.UseTokenAsync(record.Id, PendingToken!, _stop.Token)).Message;
            }
            else if (_manager is not null)
            {
                Status = (await _manager.ReconnectAsync(record.Id, _stop.Token)).Message;
            }
            else Status = "Server added. You can manage its connection in Settings → Integrations.";
        }
        // Failed or unfinished sign-ins keep the form available for retrying.
        var latest = await _registry!.GetAsync(id, _stop!.Token);
        if (latest?.Health.Status == IntegrationHealthStatus.Healthy)
        {
            ShowAddEditor = false; _editorApp = null; RaiseEditor();
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy || !IsAvailable) return;
        _stop = new(); RaiseState();
        try { await action(); }
        catch (OperationCanceledException) { Status = "Connection canceled. You can try again anytime."; }
        catch (Exception exception) when (exception is IntegrationException or McpException or IOException or SecretStoreException or HttpRequestException)
        { Status = "The connection could not be completed. Check the server and try again."; }
        finally
        {
            _stop.Dispose(); _stop = null; _link = ""; PendingToken = null; RaiseState();
            await RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        if (_home is not null)
        {
            try { await _home.LoadAsync(); } catch (OperationCanceledException) { }
            Apps.FirstOrDefault(app => app.Endpoint.AppKey == HomeAssistant.AppKey)?.ShowConnected(_home.IsConnected);
        }

        if (_registry is null) return;
        try
        {
            var installed = await _registry.ListAsync();
            foreach (var app in Apps.Where(app => !app.NeedsAddress)) app.Show(installed.FirstOrDefault(record => record.Id == app.Endpoint.IntegrationId));
            foreach (var gone in Apps.Where(app => app.IsCustom && installed.All(record => record.Id != app.Endpoint.IntegrationId)).ToList()) Apps.Remove(gone);
            foreach (var record in installed.Where(record => record.Transport.Kind != McpTransportKind.Stdio && Apps.All(app => app.Endpoint.IntegrationId != record.Id)
                && HostedSearchProviders.All.All(provider => provider.IntegrationId != record.Id)))
            {
                if (!Uri.TryCreate(record.Transport.Endpoint, UriKind.Absolute, out var uri)) continue;
                var app = new McpAppChoice(new(record.Id, record.Name, uri.AbsoluteUri, uri.Host, uri.IsLoopback), uri.IsLoopback ? "MCP server · on this PC" : "MCP server · " + uri.Host, "mcp", true);
                app.Show(record); Apps.Add(app);
            }
        }
        catch (Exception exception) when (exception is IntegrationException or IOException) { Status = "Your saved connections could not be read just now."; }
    }

    private void OnRegistryChanged(object? sender, IntegrationsChangedEventArgs args) => _dispatcher.InvokeAsync(async () => await RefreshAsync());
    private void OnHomeChanged(object? sender, EventArgs args) => _dispatcher.InvokeAsync(async () => await RefreshAsync());
    private void CopyLink() { if (HasSignInLink) Status = CopyText(_link) ? "Sign-in link copied. Open it in the browser or profile you prefer." : "The clipboard is busy. Try again."; }
    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(HasSignInLink));
        foreach (var command in new[] { _connect, _add, _save, _cancel, _cancelEditor, _copyLink }) command.RaiseCanExecuteChanged();
    }
    public void Dispose()
    {
        _disposed = true; _stop?.Cancel();
        if (_registry is not null) _registry.Changed -= OnRegistryChanged;
        if (_home is not null) _home.Changed -= OnHomeChanged;
    }
}

public sealed class McpAppChoice(KnownEndpoint endpoint, string detail, string icon, bool isCustom = false, bool needsAddress = false) : NotifyingObject
{
    private string _state = "Not connected", _label = "Connect";
    internal KnownEndpoint Endpoint => endpoint;
    public string Name => endpoint.Name;
    public string Detail => detail;
    public string Icon => icon;
    public bool IsCustom => isCustom;

    /// <summary>Whether the app is a server of the user's own, at an address they have to give (Home Assistant), so that Connect opens the form.</summary>
    public bool NeedsAddress => needsAddress;

    /// <summary>Whether the app is among the user's connections, whatever state it is in.</summary>
    internal bool IsAdded { get; private set; }
    public string State { get => _state; private set => Set(ref _state, value); }
    public string Label { get => _label; private set => Set(ref _label, value); }
    internal void Show(InstalledIntegration? record)
    {
        IsAdded = record is not null;
        State = record is null ? "Not connected" : !record.Enabled ? "Turned off"
            : record.Authentication.State is IntegrationAuthState.NeedsSignIn or IntegrationAuthState.Expired or IntegrationAuthState.Rejected ? "Needs sign-in"
            : record.Health.Status == IntegrationHealthStatus.Healthy ? "Connected" : "Added · check connection";
        Label = State == "Connected" ? "Reconnect" : record is null ? "Connect" : "Try again";
    }

    /// <summary>Shows an app that is not one of the MCP connections (Home Assistant) as connected or not; its button changes its address or token once it is.</summary>
    internal void ShowConnected(bool connected)
    {
        IsAdded = connected;
        State = connected ? "Connected" : "Not connected";
        Label = connected ? "Change" : "Connect";
    }
}
