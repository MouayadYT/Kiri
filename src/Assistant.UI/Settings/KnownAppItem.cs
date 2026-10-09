using System.Windows.Input;
using Assistant.Tools.Integrations;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Settings;

/// <summary>
/// One app that can be connected, as Settings > Integrations lists it with a Connect button (PROJECT_SPEC §4.8). The click is the user's approval: it opens their browser
/// on the app's own page, where they choose to allow the Assistant, or, for a server of the user's own (Home Assistant), the form that asks for its address and token.
/// What comes of it is said by the page.
/// </summary>
public sealed class KnownAppItem : NotifyingObject
{
    private readonly KnownEndpoint _endpoint;
    private readonly Func<KnownEndpoint, Task> _connect;
    private readonly RelayCommand _command;
    private readonly string? _detail;
    private readonly string _label;
    private bool _busy;

    /// <param name="endpoint">The app.</param>
    /// <param name="connect">What Connect does.</param>
    /// <param name="detail">What is said under the name, or <see langword="null"/> for what the endpoint says of itself.</param>
    /// <param name="label">The button's words; "Connect" unless given.</param>
    internal KnownAppItem(KnownEndpoint endpoint, Func<KnownEndpoint, Task> connect, string? detail = null, string label = "Connect")
    {
        _endpoint = endpoint;
        _connect = connect;
        _detail = detail;
        _label = label;
        _command = new RelayCommand(_ => _ = RunAsync(), _ => !_busy);
    }

    /// <summary>The app's name. An app that can be reached two ways says which this one is, so that no two lines read the same.</summary>
    public string Name => _endpoint.AppKey switch
    {
        "microsofttodo" => _endpoint.Name + " (direct)",
        _ => _endpoint.IsThroughPipedream ? _endpoint.Name + " (through Pipedream)" : _endpoint.Name,
    };

    /// <summary>Where its server is and what that means for what is sent.</summary>
    public string Detail => _detail ?? (_endpoint.IsThroughPipedream
        ? $"Through Pipedream. Sign in in your browser and select {_endpoint.Name}. Requests to this connection go over the internet."
        : _endpoint.IsBundled
        ? $"Through a small program that comes with the Assistant, which talks to {_endpoint.Name} over the internet. You sign in with {_endpoint.Vendor}."
        : _endpoint.RunsOnThisPc
            ? $"{_endpoint.Name}'s own server on this PC. Nothing goes over the internet."
            : $"{_endpoint.Name}'s own server ({_endpoint.Vendor}). What you ask of it is sent over the internet.");

    /// <summary>The button's label.</summary>
    public string Label => _busy ? "Connecting…" : _label;

    /// <summary>The key of the app, as the Assistant knows it.</summary>
    internal string AppKey => _endpoint.AppKey;

    /// <summary>Connects it.</summary>
    public ICommand ConnectCommand => _command;

    private async Task RunAsync()
    {
        _busy = true;
        OnPropertyChanged(nameof(Label));
        _command.RaiseCanExecuteChanged();
        try
        {
            await _connect(_endpoint).ConfigureAwait(true);
        }
        finally
        {
            _busy = false;
            OnPropertyChanged(nameof(Label));
            _command.RaiseCanExecuteChanged();
        }
    }
}
