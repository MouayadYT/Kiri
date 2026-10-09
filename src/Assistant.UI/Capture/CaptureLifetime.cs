using Assistant.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Capture;

/// <summary>
/// Ties the screenshots to the application's life (PROJECT_SPEC §3.5, P7): when it starts, whatever an earlier run that ended badly left
/// in the temporary captures folder is deleted; while it runs, a capture that nobody has asked about for
/// <see cref="ScreenAttachments.IdleLifetime"/> is let go of (checked every <see cref="CheckEvery"/>); and when it stops, the folder is
/// emptied again. The pictures themselves are in memory and go with the process.
/// </summary>
internal sealed partial class CaptureLifetime(
    ScreenAttachments screens,
    TemporaryCaptureCleaner cleaner,
    Action<Action> onUiThread,
    ILogger<CaptureLifetime> logger,
    TimeSpan? checkEvery = null) : IHostedService, IDisposable
{
    /// <summary>How often the idle captures are looked for.</summary>
    internal static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private Timer? _timer;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cleaner.DeleteAll();
        lock (_gate)
        {
            var every = checkEvery ?? CheckEvery;
            _timer ??= new Timer(_ => onUiThread(ReleaseIdle), null, every, every);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        cleaner.DeleteAll();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    // Runs on the UI thread, where the conversations and their chips are.
    internal void ReleaseIdle()
    {
        try
        {
            if (screens.ReleaseIdle() is > 0 and var released)
            {
                LogReleased(logger, released);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A failure here must not take the Assistant down: it is logged by type, and the next check tries again.
            LogFailed(logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 2632, Level = LogLevel.Information, Message = "{Count} screenshots were let go of after sitting unused")]
    private static partial void LogReleased(ILogger logger, int count);

    [LoggerMessage(EventId = 2633, Level = LogLevel.Warning, Message = "Idle screenshots could not be let go of ({ExceptionType})")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
