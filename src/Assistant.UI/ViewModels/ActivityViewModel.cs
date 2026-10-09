using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Assistant.Core.Contracts;

namespace Assistant.UI.ViewModels;

/// <summary>
/// What the Searching chip shows (PROJECT_SPEC §4.1): whether it is up, and the words it says, following the
/// <see cref="IActivityTracker"/> while a search, the model or a tool runs. Every surface that hosts the chip shares
/// one. It holds back a moment before showing, so an operation that finishes at once never flashes it, and it goes
/// the moment the operation ends or is cancelled. It only reads the tracker's status texts, which are never private
/// content, and it never logs.
/// </summary>
public sealed class ActivityViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>How long an operation runs before the chip shows, so quick ones show nothing.</summary>
    public static readonly TimeSpan DefaultShowDelay = TimeSpan.FromMilliseconds(250);

    private readonly IActivityTracker _tracker;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _showDelay;
    private readonly Dispatcher _dispatcher;
    private readonly RelayCommand _cancelCommand;
    private ITimer? _timer;
    private Guid? _waitingFor;
    private bool _isVisible;
    private string _statusText = "";
    private bool _disposed;

    public ActivityViewModel(
        IActivityTracker tracker, TimeProvider? clock = null, TimeSpan? showDelay = null, Dispatcher? dispatcher = null)
    {
        _tracker = tracker;
        _clock = clock ?? TimeProvider.System;
        _showDelay = showDelay ?? DefaultShowDelay;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _cancelCommand = new RelayCommand(_ => Cancel(), _ => _isVisible);
        _tracker.Changed += OnTrackerChanged;
        Refresh();
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Whether the chip is up: an operation has run for the show delay and has not ended or been cancelled.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                OnPropertyChanged();
                _cancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// What the chip says, such as "Searching". It keeps the last words while the chip leaves, so it never empties
    /// mid-animation.
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText != value)
            {
                _statusText = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Cancels the operation the chip shows: the chip when pressed, and Esc.</summary>
    public ICommand CancelCommand => _cancelCommand;

    /// <summary>
    /// Cancels the operation in progress, if there is one, even before the chip has shown.
    /// </summary>
    /// <returns><see langword="true"/> when there was an operation to cancel.</returns>
    public bool Cancel() => _tracker.CancelCurrent();

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
        _tracker.Changed -= OnTrackerChanged;
        _timer?.Dispose();
        _timer = null;
    }

    // The tracker raises from whichever thread the operation runs on.
    private void OnTrackerChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            Refresh();
        }
        else
        {
            _dispatcher.BeginInvoke((Action)Refresh);
        }
    }

    // Makes the chip match the operation now in progress.
    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        if (_tracker.Current is not { } current)
        {
            StopWaiting();
            IsVisible = false;
            return;
        }

        StatusText = current.Text;
        if (_isVisible || _waitingFor == current.Id)
        {
            return;
        }

        // A new operation waits for the delay before it shows; one that ends first shows nothing.
        StopWaiting();
        if (_showDelay <= TimeSpan.Zero)
        {
            IsVisible = true;
            return;
        }

        _waitingFor = current.Id;
        _timer = _clock.CreateTimer(
            _ => _dispatcher.BeginInvoke((Action)ShowIfStillRunning), null, _showDelay, Timeout.InfiniteTimeSpan);
    }

    private void ShowIfStillRunning()
    {
        if (_disposed || _waitingFor is null)
        {
            return;
        }

        var waitedFor = _waitingFor;
        StopWaiting();
        if (_tracker.Current is { } current && current.Id == waitedFor)
        {
            StatusText = current.Text;
            IsVisible = true;
        }
    }

    private void StopWaiting()
    {
        _waitingFor = null;
        _timer?.Dispose();
        _timer = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
