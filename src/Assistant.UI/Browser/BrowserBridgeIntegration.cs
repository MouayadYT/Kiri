using Assistant.Core.Contracts;
using Assistant.Core.Ipc;
using Assistant.Windows.Placement;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Browser;

/// <summary>What the app does with the text a browser's extension captured and the native-messaging host forwarded on its pipe.</summary>
internal interface IBrowserSelectionSink
{
    /// <summary>Takes <paramref name="selection"/>, whose limits have been checked, and answers at once. Called on a thread-pool thread.</summary>
    InvocationReply Receive(BrowserSelection selection);
}

/// <summary>
/// The app's side of the browser bridge (PROJECT_SPEC §4.5, §5.7). The host reaches it over the app pipe (<see cref="ExplorerIntegration"/>
/// serves the pipe and passes a selection on here). It also points the host's registration at this copy of the app when it starts, if the
/// user has turned the bridge on, so a moved or rebuilt app is the one the browsers start.
/// </summary>
/// <remarks>
/// The selection is handed to the window (<see cref="BrowserSelectionRequests"/>, opened by <see cref="AskBrowserSelectionController"/>) and
/// counted; neither it nor the page's title or address is logged. The window in front at that moment, which is the browser the user just
/// chose the entry in, goes with it, so that the panel can open beside it (only where it and the pointer were, never what it shows).
/// </remarks>
internal sealed class BrowserBridgeIntegration : IHostedService, IBrowserSelectionSink, IDisposable
{
    private readonly IBrowserBridgeInstaller _installer;
    private readonly ISettingsService _settings;
    private readonly BrowserSelectionRequests _requests;
    private readonly ILogger<BrowserBridgeIntegration> _logger;
    private readonly IWindowPlacementService? _placement;
    private readonly CancellationTokenSource _stopping = new();

    public BrowserBridgeIntegration(
        IBrowserBridgeInstaller installer, ISettingsService settings, BrowserSelectionRequests requests,
        ILogger<BrowserBridgeIntegration> logger, IWindowPlacementService? placement = null)
    {
        _placement = placement;
        _installer = installer;
        _settings = settings;
        _requests = requests;
        _logger = logger;
    }

    /// <inheritdoc/>
    public InvocationReply Receive(BrowserSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (_stopping.IsCancellationRequested)
        {
            return new InvocationReply(InvocationErrorCode.Unavailable);
        }

        BrowserBridgeLog.SelectionReceived(
            _logger, selection.Text.Length, selection.IsTruncated, selection.PageTitle.Length > 0, selection.PageUrl.Length > 0,
            selection.NearbyBefore.Length + selection.NearbyAfter.Length);
        _requests.Post(selection, FindBrowserWindow());
        return InvocationReply.Accepted;
    }

    // The browser is still the window in front when its click reaches the app, even when the click had to start the app; if the user has
    // already gone elsewhere, the panel opens where it usually does. Never an error: the panel opens either way.
    private NearWindowTarget? FindBrowserWindow()
    {
        try
        {
            var browser = _placement?.DescribeForegroundWindow(Environment.ProcessId);
            BrowserBridgeLog.BrowserWindowNoted(_logger, browser is not null);
            return browser;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            BrowserBridgeLog.BrowserWindowFailed(_logger, exception.GetType().Name);
            return null;
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Not waited for: the window must not wait for the registry.
        _ = RefreshRegistrationAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose() => _stopping.Dispose();

    private async Task RefreshRegistrationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!settings.Integrations.BrowserBridgeEnabled)
            {
                return;
            }

            var installed = await _installer.InstallAsync(cancellationToken).ConfigureAwait(false);
            BrowserBridgeLog.RegistrationRefreshed(_logger, installed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            BrowserBridgeLog.RegistrationRefreshFailed(_logger, exception);
        }
    }
}
