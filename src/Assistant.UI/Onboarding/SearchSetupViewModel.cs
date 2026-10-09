using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using System.Windows.Threading;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.Settings;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Mcp.Auth;
using Assistant.Tools.Search;
using Assistant.UI.Settings;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Onboarding;

/// <summary>The same optional hosted-search setup in onboarding and Settings.</summary>
public sealed class SearchSetupViewModel : NotifyingObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IAppEventBus _events;
    private readonly IWebSearchService? _search;
    private readonly IInstalledIntegrationRegistry? _registry;
    private readonly IIntegrationConnector? _connector;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly RelayCommand _apply, _test, _connect, _cancel, _copy, _key;
    private CancellationTokenSource? _stop;
    private HostedSearchProvider _selected = HostedSearchProviders.All[0];
    private bool _enabled, _signedIn;
    private string _status = "", _link = "";
    private string? _pendingKey;

    public SearchSetupViewModel(ISettingsService settings, IAppEventBus events, IWebSearchService? search = null,
        IInstalledIntegrationRegistry? registry = null, IIntegrationConnector? connector = null)
    {
        _settings = settings; _events = events; _search = search; _registry = registry; _connector = connector;
        _apply = new(_ => _ = ApplyAsync(), _ => !IsBusy && IsAvailable);
        _test = new(_ => _ = RunAsync(TestAsync), _ => !IsBusy && CanContinue && Enabled && IsAvailable);
        _connect = new(_ => _ = RunAsync(ConnectAsync), _ => !IsBusy && Enabled && NeedsSignIn && IsAvailable && _connector is not null);
        _cancel = new(_ => _stop?.Cancel(), _ => IsBusy);
        _copy = new(_ => { try { System.Windows.Clipboard.SetText(_link); Status = "Sign-in link copied."; } catch (System.Runtime.InteropServices.ExternalException) { Status = "The clipboard is busy. Try again."; } }, _ => HasSignInLink);
        _key = new(_ => _ = RunAsync(UseKeyAsync), _ => !IsBusy && Enabled && IsAvailable && _connector is not null && !string.IsNullOrWhiteSpace(PendingApiKey));
    }

    public IReadOnlyList<HostedSearchProvider> Providers => HostedSearchProviders.All;
    public bool IsAvailable => _search is not null && _registry is not null;
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) { Status = ""; RaiseState(); } } }
    public HostedSearchProvider SelectedProvider { get => _selected; set { if (value is not null && Set(ref _selected, value)) { _signedIn = false; PendingApiKey = null; Status = ""; RaiseState(); _ = RefreshSignInAsync(); } } }
    public bool NeedsSignIn => SelectedProvider.NeedsSignIn;
    public bool IsBusy => _stop is not null;
    public bool IsIdle => !IsBusy;
    public bool CanContinue => !IsBusy && (!Enabled || IsAvailable && (!NeedsSignIn || _signedIn));
    public bool HasSignInLink => _link.Length > 0;
    public string ConnectLabel => _signedIn ? "Reconnect HasData" : "Sign in to HasData";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public ICommand ApplyCommand => _apply;
    public ICommand TestCommand => _test;
    public ICommand ConnectCommand => _connect;
    public ICommand CancelCommand => _cancel;
    public ICommand CopySignInLinkCommand => _copy;
    public ICommand UseKeyCommand => _key;
    // The PasswordBox hands this directly to Credential Manager. Never included in settings or logs.
    internal string? PendingApiKey { get => _pendingKey; set { if (Set(ref _pendingKey, value)) _key.RaiseCanExecuteChanged(); } }

    public async Task InitializeAsync()
    {
        if (IsBusy) return;
        var saved = await _settings.LoadAsync();
        _enabled = saved.WebSearch.Enabled;
        _selected = HostedSearchProviders.Find(saved.WebSearch.Provider);
        OnPropertyChanged(nameof(Enabled)); OnPropertyChanged(nameof(SelectedProvider));
        await RefreshSignInAsync(); RaiseState();
    }

    public Task<bool> ApplyAsync() => RunAsync(async () =>
    {
        if (!CanApply()) throw new IOException("Sign in to HasData, choose another engine, or turn search off to continue.");
        await SaveAsync();
        Status = Enabled ? $"{SelectedProvider.Name} selected. Web search is enabled." : "Web search is off.";
    });

    private bool CanApply() => !Enabled || IsAvailable && (!NeedsSignIn || _signedIn);
    private async Task SaveAsync()
    {
        var choice = new WebSearchSettings { Enabled = Enabled, Provider = SelectedProvider.Id };
        if (_search is not null) await _search.ConfigureAsync(choice, _stop!.Token);
        var saved = await _settings.UpdateAsync(current => current with
        {
            WebSearch = choice,
            Privacy = Enabled ? current.Privacy with { LocalOnly = false } : current.Privacy,
            Permissions = Enabled ? current.Permissions with { ExternalSearch = true } : current.Permissions,
        }, _stop!.Token);
        await _events.PublishAsync(new SettingsSaved(saved));
    }

    private async Task ConnectAsync()
    {
        await SaveAsync();
        Status = "Sign in to HasData in your browser, then return here. No software is installed.";
        var stop = _stop!;
        var outcome = await _connector!.ConnectAsync(SelectedProvider.SignInEndpoint, cancellationToken: stop.Token, options: new OAuthSignInOptions
        {
            AddressReady = address => _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_stop, stop) && !stop.IsCancellationRequested) { _link = address.AbsoluteUri; RaiseState(); }
            }),
        });
        // Restrict the signed-in integration to the single vetted search operation.
        await _search!.ConfigureAsync(new() { Enabled = Enabled, Provider = SelectedProvider.Id }, stop.Token);
        await RefreshSignInAsync();
        Status = outcome.Message;
    }

    private async Task UseKeyAsync()
    {
        var key = PendingApiKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > SecretNames.MaxSecretLength || key.Any(char.IsControl))
            throw new IOException("Enter an API key without line breaks, up to 1,024 characters.");
        await SaveAsync();
        var provider = SelectedProvider;
        var previous = await _registry!.GetAsync(provider.IntegrationId, _stop!.Token);
        if (previous?.Authentication.Kind == IntegrationAuthKind.OAuth)
            await _connector!.SignOutAsync(provider.IntegrationId, _stop.Token);
        var auth = new IntegrationAuthentication
        {
            Kind = provider.Id == WebSearchProvider.Tavily ? IntegrationAuthKind.BearerToken : IntegrationAuthKind.HeaderKey,
            State = IntegrationAuthState.NeedsSignIn,
            Secrets = [new(provider.Id == WebSearchProvider.Tavily ? "Authorization" : "x-api-key", provider.IntegrationId + ".key")],
        };
        if (previous is null)
            await _registry.AddAsync(new InstalledIntegration
            {
                Id = provider.IntegrationId, Name = provider.Name, Enabled = true, Transport = provider.Transport,
                Permissions = provider.Permissions, Source = new(IntegrationSourceKind.Bundled, new Uri(provider.Endpoint).Host), Authentication = auth,
            }, _stop.Token);
        else await _registry.UpdateAsync(provider.IntegrationId, current => current with { Authentication = auth }, _stop.Token);
        var outcome = provider.Id == WebSearchProvider.Tavily
            ? await _connector!.UseTokenAsync(provider.IntegrationId, key, _stop.Token)
            : await _connector!.SetKeyAsync(provider.IntegrationId, "x-api-key", key, _stop.Token);
        await RefreshSignInAsync();
        Status = outcome.Status == InstallStatus.Installed ? "API key saved in Windows Credential Manager. Use Test search to verify access. Your account's limits apply." : outcome.Message;
    }

    private async Task TestAsync()
    {
        await SaveAsync();
        Status = "Searching for ‘Windows Clock Microsoft’…";
        var started = Stopwatch.GetTimestamp();
        var response = await _search!.SearchAsync("Windows Clock Microsoft", _stop!.Token);
        Status = response.Result.IsError ? "The search engine rejected the test. Check sign-in or try again later if its usage limit was reached."
            : $"{response.Provider} returned a search response in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s.";
    }

    private async Task RefreshSignInAsync()
    {
        var selected = SelectedProvider;
        var record = _registry is null ? null : await _registry.GetAsync(selected.IntegrationId);
        if (SelectedProvider == selected) _signedIn = record?.Authentication.State == IntegrationAuthState.Ready;
        RaiseState();
    }

    private async Task<bool> RunAsync(Func<Task> action)
    {
        if (IsBusy) return false;
        _stop = new(); RaiseState();
        try { await action(); return true; }
        catch (OperationCanceledException) { Status = "Canceled. You can retry or leave web search off."; }
        catch (Exception exception) when (exception is IOException or IntegrationException or McpException or HttpRequestException or SecretStoreException)
        { Status = exception is IOException ? exception.Message : "Search setup could not be completed. Check the connection and try again."; }
        finally { _stop.Dispose(); _stop = null; _link = ""; PendingApiKey = null; RaiseState(); }
        return false;
    }

    private void RaiseState()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(IsIdle), nameof(CanContinue), nameof(NeedsSignIn), nameof(HasSignInLink), nameof(ConnectLabel) }) OnPropertyChanged(name);
        foreach (var command in new[] { _apply, _test, _connect, _cancel, _copy, _key }) command.RaiseCanExecuteChanged();
    }
    public void Dispose() => _stop?.Cancel();
}
