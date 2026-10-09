using Assistant.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.History;

/// <summary>
/// Ties the local history to the application's life (PROJECT_SPEC §3.5): the database is opened, and brought up to date,
/// in the background when the application starts, so the History window and the first save do not wait for it; and when
/// the application stops, the messages still waiting to be written are written first.
/// </summary>
internal sealed partial class HistoryLifetime(
    SqliteConversationService history, ConversationRecorder recorder, ILogger<HistoryLifetime> logger) : IHostedService
{
    // How long stopping waits for the last messages to be written before it gives up on them.
    private static readonly TimeSpan FlushTime = TimeSpan.FromSeconds(3);

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Not awaited: starting must not wait for the database. A failure is logged, and the next use tries again.
        _ = OpenAsync();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!await recorder.FlushAsync(FlushTime).ConfigureAwait(false))
        {
            LogNotFlushed(logger);
        }
    }

    private async Task OpenAsync()
    {
        try
        {
            await history.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogNotOpened(logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 2402, Level = LogLevel.Warning, Message = "The history could not be opened: {ExceptionType}")]
    private static partial void LogNotOpened(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 2403, Level = LogLevel.Warning, Message = "The last messages were not written to the history before the application stopped")]
    private static partial void LogNotFlushed(ILogger logger);
}
