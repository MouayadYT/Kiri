using System.IO;
using Assistant.Core.Contracts;
using Assistant.Core.Ipc;
using Assistant.UI.Browser;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Explorer;

/// <summary>Where the app listens for File Explorer's requests: its pipe's name.</summary>
/// <param name="PipeName">The app pipe (<see cref="AppPipe.ForCurrentUser"/>), or a test's own.</param>
internal sealed record ExplorerIntegrationOptions(string PipeName);

/// <summary>
/// The app's side of File Explorer's Ask Assistant (PROJECT_SPEC §4.4, §5.7). While the app runs it serves its pipe, where the
/// entry point hands over each selected file; the files of one choice of the menu are gathered (<see cref="InvocationBatcher"/>),
/// checked (<see cref="InvokedFiles"/>: full paths of existing files of a supported type, at most ten) and handed to the window
/// (<see cref="ExplorerFileRequests"/>), which opens the floating conversation with them attached. It also points the menu
/// entry at this copy of the app when it starts, if the user has turned the entry on, so a moved or rebuilt app is the one the
/// entry starts. It logs counts and outcomes, never a path or a name. The pipe is the app's one, so it also takes the browser
/// bridge's selections and passes them to <see cref="IBrowserSelectionSink"/>.
/// </summary>
internal sealed class ExplorerIntegration : IHostedService, IInvocationHandler, IDisposable
{
    private readonly ExplorerFileRequests _requests;
    private readonly IExplorerMenuInstaller _installer;
    private readonly ISettingsService _settings;
    private readonly ILogger<ExplorerIntegration> _logger;
    private readonly IBrowserSelectionSink? _browserSelections;
    private readonly Windowing.FullViewRequests? _fullView;
    private readonly InvocationServer _server;
    private readonly InvocationBatcher _batcher;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _directoryExists;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    // How long after its last arrival the same selection counts as the same choice of the menu: longer than a process holds its claim
    // on the selection (AskHandler), shorter than a user takes to close the conversation and choose the menu again.
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(6);

    private string _lastSelection = "";
    private DateTimeOffset _lastSelectionAt;
    private readonly CancellationTokenSource _stopping = new();
    private Task _serving = Task.CompletedTask;
    private int _disposed;

    public ExplorerIntegration(
        ExplorerIntegrationOptions options, ExplorerFileRequests requests, IExplorerMenuInstaller installer,
        ISettingsService settings, ILogger<InvocationServer> serverLogger, ILogger<ExplorerIntegration> logger,
        TimeProvider? clock = null, Func<string, bool>? fileExists = null, Func<string, bool>? directoryExists = null,
        IBrowserSelectionSink? browserSelections = null, Windowing.FullViewRequests? fullView = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _browserSelections = browserSelections;
        _fullView = fullView;
        _requests = requests;
        _installer = installer;
        _settings = settings;
        _logger = logger;
        _fileExists = fileExists ?? File.Exists;
        _directoryExists = directoryExists ?? Directory.Exists;
        _clock = clock ?? TimeProvider.System;
        _server = new InvocationServer(options.PipeName, this, serverLogger);
        _batcher = new InvocationBatcher(Deliver, clock);
    }

    /// <summary>Completes when the pipe is no longer served.</summary>
    internal Task Serving => _serving;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Neither is waited for: the window must not wait for the pipe or for the registry.
        _serving = Task.Run(() => ServeAsync(_stopping.Token), CancellationToken.None);
        _ = RefreshMenuAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(_serving, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public InvocationReply Handle(InvocationRequest request)
    {
        if (_stopping.IsCancellationRequested)
        {
            return new InvocationReply(InvocationErrorCode.Unavailable);
        }

        if (request.Action == InvocationAction.ShowFullView)
        {
            // The app was opened again while it runs: this one shows its full window, and that start ends.
            if (_fullView is null)
            {
                return new InvocationReply(InvocationErrorCode.UnknownAction);
            }

            _fullView.Post();
            return InvocationReply.Accepted;
        }

        if (request.Action == InvocationAction.AskAboutBrowserSelection)
        {
            // An app built without the bridge answers as one that does not know the action.
            return request.Selection is { } selection && _browserSelections is { } sink
                ? sink.Receive(selection)
                : new InvocationReply(InvocationErrorCode.UnknownAction);
        }

        _batcher.Add(request.Paths);
        return InvocationReply.Accepted;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // The container disposes it once as itself and once as a hosted service.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping.Cancel();
        _batcher.Dispose();
        _stopping.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _server.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ExplorerLog.ServingFailed(_logger, exception);
        }
    }

    // A whole batch, on a thread-pool thread: the paths are checked there, and the window gets the result.
    private void Deliver(IReadOnlyList<string> paths)
    {
        try
        {
            if (IsRepeat(paths))
            {
                ExplorerLog.RepeatIgnored(_logger);
                return;
            }

            var files = InvokedFiles.Check(paths, _fileExists, directoryExists: _directoryExists);
            var attached = ExplorerFiles.From(files);
            ExplorerLog.FilesReceived(
                _logger, files.Count, attached.Pictures.Count, attached.Documents.Count, attached.Notice is not null);
            _requests.Post(attached);
        }
        catch (Exception exception)
        {
            ExplorerLog.DeliveryFailed(_logger, exception);
        }
    }

    // File Explorer starts one process for each selected file, and each reads the whole selection, so the same selection can reach the
    // app more than once while they start. The same files again within a few seconds are the same choice of the menu.
    private bool IsRepeat(IReadOnlyList<string> paths)
    {
        var signature = string.Join(
            "\n",
            paths.Select(path => InvokedPaths.Normalize(path)?.ToUpperInvariant() ?? path)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var repeat = signature == _lastSelection && now - _lastSelectionAt < RepeatWindow;
            _lastSelection = signature;
            _lastSelectionAt = now;
            return repeat;
        }
    }

    private async Task RefreshMenuAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!settings.Integrations.ExplorerContextMenuEnabled)
            {
                return;
            }

            var installed = await _installer.InstallAsync(cancellationToken).ConfigureAwait(false);
            ExplorerLog.MenuRefreshed(_logger, installed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ExplorerLog.MenuRefreshFailed(_logger, exception);
        }
    }
}
