using System.Windows;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Events;
using Assistant.Core.Permissions;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Clipboard;
using Assistant.Core.Settings;
using Assistant.Search.Applications;
using Assistant.UI.ViewModels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Search;

/// <summary>
/// Gets the instant search ready while the app starts (PROJECT_SPEC §4.1): reads the applications Start lists and their icons in the
/// background, a little at a time, so that the first thing the user types is answered from memory. It starts nothing the user can see,
/// never waits for anything, and a failure only means the first search reads the applications itself.
/// </summary>
internal sealed class QuickSearchWarmUp(ApplicationsQuickSearchProvider applications) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => applications.WarmUpAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose() => _stop.Dispose();
}

/// <summary>
/// Follows the Clipboard History permission (PROJECT_SPEC §4.9): while the user has allowed it, the watcher notices what they copy and the
/// history keeps it, in memory; the moment they turn it off, or the app closes, the watcher stops and everything that was kept is
/// forgotten. Nothing is watched, kept or read before the user turns the permission on, and nothing is ever written to disk. What is
/// copied is private content and is never logged.
/// </summary>
internal sealed class ClipboardHistoryController : IHostedService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IClipboardHistory _history;
    private readonly IClipboardWatcher _watcher;
    private readonly IAppEventBus _events;
    private readonly SearchResultsViewModel? _results;
    private readonly ILogger<ClipboardHistoryController> _logger;
    private IDisposable? _subscription;

    public ClipboardHistoryController(
        ISettingsService settings, IClipboardHistory history, IClipboardWatcher watcher, IAppEventBus events,
        ILogger<ClipboardHistoryController> logger, SearchResultsViewModel? results = null)
    {
        _settings = settings;
        _history = history;
        _watcher = watcher;
        _events = events;
        _results = results;
        _logger = logger;
        _watcher.TextCopied += OnTextCopied;
        _history.Changed += OnHistoryChanged;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _events.Subscribe<ClipboardHistoryController, SettingsSaved>(
            this, static (controller, saved, _) => controller.Follow(saved.Settings.Permissions));
        try
        {
            await Follow((await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Permissions).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Settings that cannot be read leave the history off, which is where it starts.
            ClipboardHistoryLog.NotStarted(_logger, exception.GetType().Name);
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Turn(on: false);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _subscription?.Dispose();
        _watcher.TextCopied -= OnTextCopied;
        _history.Changed -= OnHistoryChanged;
        _watcher.Dispose();
    }

    // The permission as saved: on only while this build can do it and the user has allowed it.
    private Task Follow(PermissionSettings permissions)
    {
        Turn(SettingsPermissionPolicy.Decide(permissions, PermissionCapability.ClipboardHistory).IsAllowed);
        return Task.CompletedTask;
    }

    private void Turn(bool on)
    {
        if (!on)
        {
            _watcher.Stop();
            _history.SetEnabled(false);
            return;
        }

        try
        {
            _history.SetEnabled(true);
            _watcher.Start();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A watcher that could not start leaves the history off, so nothing is listed that could not have been kept up.
            _history.SetEnabled(false);
            ClipboardHistoryLog.NotStarted(_logger, exception.GetType().Name);
        }
    }

    private void OnTextCopied(object? sender, ClipboardCopiedEventArgs e) => _history.Add(e.Text);

    // A list of what was copied that is on screen follows the history, which changes on the watcher's thread.
    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (_results is null || Application.Current?.Dispatcher is not { } dispatcher)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            if (_results.Scope == QuickSearchResultType.Clipboard)
            {
                _results.Refresh();
            }
        });
    }
}

/// <summary>Clipboard history log messages: that it could not start, and the kind of failure; never what was copied.</summary>
internal static partial class ClipboardHistoryLog
{
    [LoggerMessage(EventId = 6200, Level = LogLevel.Warning, Message = "The clipboard history could not start: {ExceptionType}")]
    public static partial void NotStarted(ILogger logger, string exceptionType);
}
