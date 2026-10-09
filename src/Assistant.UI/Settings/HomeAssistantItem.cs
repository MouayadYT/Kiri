using System.Windows.Input;
using System.Windows.Threading;
using Assistant.Core.Home;
using Assistant.UI.Onboarding;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Settings;

/// <summary>
/// The user's Home Assistant on the Integrations page: one line among their connections, with where it is and how it is, and behind Manage the buttons that
/// ask it again, change its address or token, and disconnect it. It is not one of the MCP connections: it is reached through Home Assistant's own API
/// (<see cref="IHomeAssistant"/>), so it has none of their sign-in buttons.
/// </summary>
public sealed class HomeAssistantItem : NotifyingObject, IDisposable
{
    private readonly IHomeAssistant _home;
    private readonly Func<bool> _openForm;
    private readonly Dispatcher _dispatcher;
    private readonly RelayCommand _check;
    private readonly RelayCommand _disconnect;
    private string _health = "Checking…";
    private string _message = string.Empty;
    private bool _problem;
    private bool _expanded;
    private bool _busy;
    private bool _disposed;

    internal HomeAssistantItem(IHomeAssistant home, Func<bool> openForm, Dispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(openForm);
        _home = home;
        _openForm = openForm;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _check = new RelayCommand(_ => _ = RefreshAsync(), _ => !_busy && IsConnected);
        _disconnect = new RelayCommand(_ => _ = DisconnectAsync(), _ => !_busy && IsConnected);
        ToggleDetailsCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
        ChangeCommand = new RelayCommand(_ => Message = _openForm()
            ? "Fill in the form under Add a connection to change the address or the token."
            : "Finish or cancel the connection that is waiting first.");
        _home.Changed += OnChanged;
    }

    /// <summary>Its name.</summary>
    public string Name => "Home Assistant";

    /// <summary>Whether one is connected; the line shows only then.</summary>
    public bool IsConnected => _home.IsConnected;

    /// <summary>Where it is, and how it is reached.</summary>
    public string Summary => _home.Address is { } address ? $"Your own server at {address} · reached directly, with the token you gave" : string.Empty;

    /// <summary>How it is now: connected with so many devices, or what is wrong.</summary>
    public string Health { get => _health; private set => Set(ref _health, value); }

    /// <summary>Whether <see cref="Health"/> says something is wrong.</summary>
    public bool HasProblem { get => _problem; private set => Set(ref _problem, value); }

    /// <summary>What the last button pressed came to.</summary>
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

    /// <summary>Whether the details show under the line.</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (Set(ref _expanded, value))
            {
                OnPropertyChanged(nameof(DetailsLabel));
            }
        }
    }

    /// <summary>What the button that shows and hides the details says.</summary>
    public string DetailsLabel => _expanded ? "Hide details" : "Manage";

    /// <summary>Shows or hides the details.</summary>
    public ICommand ToggleDetailsCommand { get; }

    /// <summary>Asks Home Assistant again whether it is there and takes the token.</summary>
    public ICommand CheckCommand => _check;

    /// <summary>Opens the form that changes the address or the token.</summary>
    public ICommand ChangeCommand { get; }

    /// <summary>Forgets the address and the token.</summary>
    public ICommand DisconnectCommand => _disconnect;

    /// <summary>Reads whether one is connected, and when one is, asks it how it is.</summary>
    internal async Task RefreshAsync()
    {
        if (_disposed || _busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await _home.LoadAsync().ConfigureAwait(true);
            Raise();
            if (!_home.IsConnected)
            {
                return;
            }

            Health = "Checking…";
            var status = await _home.CheckAsync().ConfigureAwait(true);
            Health = HomeAssistantWords.Health(status);
            HasProblem = HomeAssistantWords.IsProblem(status);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _home.Changed -= OnChanged;
        }
    }

    private async Task DisconnectAsync()
    {
        SetBusy(true);
        try
        {
            await _home.DisconnectAsync().ConfigureAwait(true);
            IsExpanded = false;
            Message = string.Empty;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _check.RaiseCanExecuteChanged();
        _disconnect.RaiseCanExecuteChanged();
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(Summary));
        _check.RaiseCanExecuteChanged();
        _disconnect.RaiseCanExecuteChanged();
    }

    // Connected, changed or disconnected, from here or from the setup: the line follows.
    private void OnChanged(object? sender, EventArgs e) => _dispatcher.InvokeAsync(async () =>
    {
        Raise();
        Message = string.Empty;
        await RefreshAsync().ConfigureAwait(true);
    });
}
