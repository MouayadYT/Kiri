using Assistant.Core.Assets;
using Assistant.Core.Hardware;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Bootstrap;

/// <summary>
/// Reads the hardware (processor, memory, graphics cards) in the background as the app starts (PROJECT_SPEC §5.6, step 124), so that the first
/// question, which chooses a profile and a window from it, and the Settings window find it already read and never wait for the graphics drivers.
/// </summary>
internal sealed class HardwareProfileWarmUp(IHardwareProfileService hardware) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Reading never throws; the profile is kept by the service.
        _ = Task.Run(() => hardware.Current, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Checks the files that came packaged with the Assistant against their manifests' sizes and SHA-256 checksums in the background, soon after the app
/// starts (PROJECT_SPEC §3.5, step 123). The first time this reads every file in full, which for a model is a few seconds of reading the disk; after
/// that a file this PC has already checked is only looked at, until it changes. A model that is asked for before the check has reached it waits for
/// the check that is running, and a model is never loaded from files that failed it.
/// </summary>
internal sealed partial class PackagedAssetsStartupCheck(IPackagedAssets assets, ILogger<PackagedAssetsStartupCheck> logger)
    : IHostedService, IDisposable
{
    // Long enough for the app to finish starting (its shortcuts, its window) before the disk is read.
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stop = new();
    private Task? _run;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _run = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            await Task.WhenAny(_run, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)).ConfigureAwait(false);
        }
    }

    public void Dispose() => _stop.Dispose();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            await assets.VerifyAllAsync(AssetCheckMode.Verify, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The app is closing.
        }
        catch (Exception exception)
        {
            LogFailed(logger, exception);
        }
    }

    [LoggerMessage(EventId = 2141, Level = LogLevel.Warning, Message = "The check of the packaged assets failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
