using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Assistant.Core.Audit;
using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Settings;

/// <summary>
/// Activity (PROJECT_SPEC §4.9, step 117): what the Assistant did on the user's behalf, newest first. A run of the agent that took several steps is a line whose steps can
/// be opened (each tool: what it was, how it ended, whether the user allowed it, how long it took), with the one short line that says where it could not go on, and a button
/// that stops a run that is still going; the integrations it looked for, offered, installed, updated and removed are lines of their own, with what the user answered. The
/// page holds no private content, and neither does what it lists: no message, no argument, no answer, no address and no key. It is kept on this PC for up to 90 days while
/// history is on, or for the hours the user has logs deleted after (48 unless changed, and on unless turned off), and the user can clear it. The log is listed a page at
/// a time (<see cref="PageSize"/>). Like the people, the log is not a setting: it is read when the window is opened (<see cref="LoadAsync"/>) and follows what happens
/// while the window is open.
/// </summary>
public sealed class ActivityPage : SettingsPage, IDisposable
{
    /// <summary>How long the page waits after the log changes before it reads it again, so that the steps of a run that come close together are read once.</summary>
    public static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(150);

    private readonly IAuditHistory? _history;
    private readonly TimeProvider _clock;
    private readonly Dispatcher _dispatcher;
    private readonly RelayCommand _clear;
    private readonly RelayCommand _confirmClear;
    private readonly RelayCommand _keep;
    private readonly RelayCommand _previousPage;
    private readonly RelayCommand _nextPage;
    private int _pageIndex;
    private bool _deleteLogs = true;
    private string _notice = string.Empty;
    private bool _loading;
    private bool _confirmingClear;
    private bool _refreshing;
    private bool _refreshAgain;
    private bool _following;
    private bool _disposed;

    internal ActivityPage(SettingsViewModel root, IAuditHistory? history = null, TimeProvider? clock = null, Dispatcher? dispatcher = null)
        : base(root, SettingsSection.Activity)
    {
        _history = history;
        _clock = clock ?? TimeProvider.System;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _clear = new RelayCommand(_ => ConfirmingClear = true, _ => _history is not null && Items.Count > 0 && !_confirmingClear);
        _confirmClear = new RelayCommand(_ => _ = ClearAsync(), _ => _confirmingClear);
        _keep = new RelayCommand(_ => ConfirmingClear = false, _ => _confirmingClear);
        _previousPage = new RelayCommand(_ => PageIndex--, _ => _pageIndex > 0);
        _nextPage = new RelayCommand(_ => PageIndex++, _ => _pageIndex < PageCount - 1);
        LogHours = new NumberField(
            hours => hours is >= SettingsLimits.MinCleanupHours and <= SettingsLimits.MaxCleanupHours
                ? ContextAdvice.None
                : new ContextAdvice(ContextAdviceLevel.Error, $"Enter a number from {NumberField.Format(SettingsLimits.MinCleanupHours)} to {NumberField.Format(SettingsLimits.MaxCleanupHours)}."),
            hours => Commit(settings => settings with { Cleanup = settings.Cleanup with { LogHours = hours } }));
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(HasNoItems));
            _clear.RaiseCanExecuteChanged();
            ShowPage();
        };
        if (_history is not null)
        {
            _history.Changed += OnHistoryChanged;
        }
    }

    /// <summary>The runs and actions, newest first.</summary>
    public ObservableCollection<ActivityRow> Items { get; } = [];

    /// <summary>Whether the page can show the log at all.</summary>
    public bool HasHistory => _history is not null;

    /// <summary>Whether anything is listed.</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>Whether nothing is, so the page says what it is for.</summary>
    public bool HasNoItems => Items.Count == 0;

    /// <summary>The most logs a page lists.</summary>
    public const int PageSize = 10;

    /// <summary>The logs of the page that is open, newest first.</summary>
    public ObservableCollection<ActivityRow> PageItems { get; } = [];

    /// <summary>Which page is open, from 0. It stays inside the pages there are.</summary>
    public int PageIndex
    {
        get => _pageIndex;
        private set
        {
            if (Set(ref _pageIndex, Math.Clamp(value, 0, Math.Max(0, PageCount - 1))))
            {
                ShowPage();
            }
        }
    }

    /// <summary>How many pages there are; at least one.</summary>
    public int PageCount => Math.Max(1, (Items.Count + PageSize - 1) / PageSize);

    /// <summary>Whether there is more than one page, so the page's buttons show.</summary>
    public bool HasPages => PageCount > 1;

    /// <summary>"Page 2 of 5".</summary>
    public string PageText => $"Page {_pageIndex + 1} of {PageCount}";

    /// <summary>Opens the page before.</summary>
    public ICommand PreviousPageCommand => _previousPage;

    /// <summary>Opens the page after.</summary>
    public ICommand NextPageCommand => _nextPage;

    /// <summary>Whether logs are deleted after <see cref="LogHours"/>. On until the user turns it off.</summary>
    public bool DeleteLogs
    {
        get => _deleteLogs;
        set
        {
            if (Set(ref _deleteLogs, value))
            {
                Commit(settings => settings with { Cleanup = settings.Cleanup with { DeleteLogs = value } });
            }
        }
    }

    /// <summary>How many hours after they happened logs are deleted, when that is on.</summary>
    public NumberField LogHours { get; }


    /// <summary>What the page says when nothing is listed.</summary>
    public string EmptyText => "Nothing yet.";

    /// <summary>What the page says when the log could not be read, or empty.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    /// <summary>Whether there is a <see cref="Notice"/>.</summary>
    public bool HasNotice => _notice.Length > 0;

    /// <summary>Whether the log is being read.</summary>
    public bool IsLoading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    /// <summary>Whether the page asks if the log may be cleared.</summary>
    public bool ConfirmingClear
    {
        get => _confirmingClear;
        private set
        {
            if (Set(ref _confirmingClear, value))
            {
                _clear.RaiseCanExecuteChanged();
                _confirmClear.RaiseCanExecuteChanged();
                _keep.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>What the page asks before it clears the log.</summary>
    public string ClearQuestion => "Clear all logs?";

    /// <summary>Starts clearing the log: the page asks first.</summary>
    public ICommand ClearCommand => _clear;

    /// <summary>Clears the log, after the page asked.</summary>
    public ICommand ConfirmClearCommand => _confirmClear;

    /// <summary>Keeps the log.</summary>
    public ICommand KeepCommand => _keep;

    /// <summary>
    /// Reads the log and lists it, keeping the lines the user has open. It never throws: when the log cannot be read, the page says so and lists what this session
    /// did.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_history is null)
        {
            return;
        }

        // From here the page follows the log, until the window is put away (StopFollowing): a log that changes with every step of every run is not read while no one looks.
        _following = true;
        IsLoading = true;
        try
        {
            var items = await _history.ListAsync(AuditRetention.ListLimit, cancellationToken).ConfigureAwait(true);
            Show(items);
            Notice = string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Notice = "The activity log couldn't be read. Check that the disk has room, then open Settings again.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Stops reading the log as it changes, because the window is put away. Opening the window reads it again (<see cref="LoadAsync"/>).</summary>
    internal void StopFollowing() => _following = false;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_history is not null)
        {
            _history.Changed -= OnHistoryChanged;
        }
    }

    // The log itself is not a setting; how long it is kept is.
    internal override void Apply(AppSettings settings, bool fresh)
    {
        DeleteLogs = settings.Cleanup.DeleteLogs;
        if (fresh)
        {
            LogHours.Reset(settings.Cleanup.LogHours);
        }
        else
        {
            LogHours.Show(settings.Cleanup.LogHours);
        }
    }

    // Lists the page that is open: the logs from PageSize times its index.
    private void ShowPage()
    {
        if (_pageIndex > Math.Max(0, PageCount - 1))
        {
            _pageIndex = Math.Max(0, PageCount - 1);
            OnPropertyChanged(nameof(PageIndex));
        }

        var wanted = Items.Skip(_pageIndex * PageSize).Take(PageSize).ToList();
        for (var index = 0; index < wanted.Count; index++)
        {
            var at = PageItems.IndexOf(wanted[index]);
            if (at < 0)
            {
                PageItems.Insert(Math.Min(index, PageItems.Count), wanted[index]);
            }
            else if (at != index)
            {
                PageItems.Move(at, index);
            }
        }

        foreach (var gone in PageItems.Where(row => !wanted.Contains(row)).ToList())
        {
            PageItems.Remove(gone);
        }

        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(PageText));
        _previousPage.RaiseCanExecuteChanged();
        _nextPage.RaiseCanExecuteChanged();
    }

    // Lists the runs and actions in their order. A line that was listed is updated where it stands, so that one the user opened stays open and nothing flickers.
    private void Show(IReadOnlyList<ActivityItem> items)
    {
        var existing = Items.ToDictionary(row => row.Id);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            ActivityRow row;
            if (existing.TryGetValue(item.Id, out var known))
            {
                known.Show(item);
                row = known;
            }
            else
            {
                row = new ActivityRow(item, _clock, _history is null ? null : _history.CancelTask);
            }

            var at = Items.IndexOf(row);
            if (at < 0)
            {
                Items.Insert(Math.Min(index, Items.Count), row);
            }
            else if (at != index)
            {
                Items.Move(at, Math.Min(index, Items.Count - 1));
            }
        }

        var wanted = items.Select(item => item.Id).ToHashSet();
        foreach (var gone in Items.Where(row => !wanted.Contains(row.Id)).ToList())
        {
            Items.Remove(gone);
        }
    }

    // Something changed in the log, from whichever thread did it: the page reads it again, once at a time, on the user interface's thread.
    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (_disposed || !_following)
        {
            return;
        }

        _ = _dispatcher.InvokeAsync(async () =>
        {
            if (_disposed || !_following)
            {
                return;
            }

            if (_refreshing)
            {
                _refreshAgain = true;
                return;
            }

            _refreshing = true;
            try
            {
                do
                {
                    // A run changes with every step: what changes together is read once.
                    await Task.Delay(RefreshDelay).ConfigureAwait(true);
                    _refreshAgain = false;
                    await LoadAsync().ConfigureAwait(true);
                }
                while (_refreshAgain && !_disposed);
            }
            finally
            {
                _refreshing = false;
            }
        });
    }

    private async Task ClearAsync()
    {
        if (_history is null)
        {
            return;
        }

        ConfirmingClear = false;
        try
        {
            await _history.ClearAsync().ConfigureAwait(true);
            Notice = string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // What is listed stays, and so does what the page says about it.
            Notice = "The activity log couldn't be cleared. Check that the disk has room, then try again.";
            return;
        }

        await LoadAsync().ConfigureAwait(true);
    }
}
