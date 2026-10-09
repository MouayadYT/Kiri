using Assistant.Core.Contracts;
using Assistant.Core.Startup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Tray;

/// <summary>
/// Points the sign-in entry at this copy of the app when the Assistant starts, if the user has turned Start Assistant on boot on
/// (PROJECT_SPEC §4.9), so a moved or rebuilt app is the one Windows starts next time. The same is done for File Explorer's menu entry.
/// Nothing waits for it, and a failure costs only the entry.
/// </summary>
internal sealed class LaunchAtLoginRefresh(ISettingsService settings, ILaunchAtLogin launchAtLogin, ILogger<LaunchAtLoginRefresh> logger) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = RefreshAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    /// <summary>Refreshes the entry when the setting asks for one. Returns whether it did and it worked.</summary>
    internal async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var current = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!current.LaunchAtLogin.Enabled)
            {
                return false;
            }

            var refreshed = await launchAtLogin.RefreshAsync(cancellationToken).ConfigureAwait(false);
            TrayLog.SignInEntryRefreshed(logger, refreshed);
            return refreshed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TrayLog.SignInEntryRefreshFailed(logger, exception);
            return false;
        }
    }
}
