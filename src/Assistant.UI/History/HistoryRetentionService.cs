using Assistant.Core.Audit;
using Assistant.Core.Contracts;
using Assistant.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.History;

/// <summary>
/// Deletes the conversations that are older than the user chose to keep them: every conversation by Settings > Privacy > "Keep history for", and
/// by Settings > Privacy the chats from the bar after some hours and the chats of the full window after some days, each when its switch is on. A
/// conversation's age is when it was last updated, so one that is still going on is never deleted. It runs a little after the application starts,
/// again whenever a setting changes (it looks once a minute, which costs nothing as settings are held in memory) and at least every six hours, or
/// every ten minutes while Cleanup deletes anything, since its times are short; with nothing chosen it deletes nothing. What it deletes goes the
/// way a conversation the user deletes goes (messages, cards and search entries). Only counts are logged.
/// </summary>
internal sealed partial class HistoryRetentionService(
    IConversationService history, ISettingsService settings, TimeProvider clock, ILogger<HistoryRetentionService> logger,
    ConversationSurfaces? surfaces = null, AuditLog? logs = null) : IHostedService, IDisposable
{
    private static readonly TimeSpan FirstLook = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Look = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan AtLeastEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan CleanupEvery = TimeSpan.FromMinutes(10);

    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _stopping.Dispose();

    /// <summary>Deletes what is older than <paramref name="retention"/> allows, and returns how many conversations that was.</summary>
    internal Task<int> PruneAsync(HistoryRetention retention, CancellationToken cancellationToken) =>
        PruneAsync(retention, new CleanupSettings(), cancellationToken);

    /// <summary>
    /// Deletes what is older than <paramref name="retention"/> and <paramref name="cleanup"/> allow, and returns how many conversations that was. A
    /// chat is the bar's only when it is known to be (<see cref="ConversationSurfaces"/>); any other is the full window's, which is kept for longer.
    /// </summary>
    internal async Task<int> PruneAsync(HistoryRetention retention, CleanupSettings cleanup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        if (retention == HistoryRetention.UntilDeleted && !cleanup.DeletesAnything)
        {
            return 0;
        }

        var now = clock.GetUtcNow();
        DateTimeOffset? oldest = retention == HistoryRetention.UntilDeleted ? null : now - TimeSpan.FromDays((int)retention);
        DateTimeOffset? oldestInBar = cleanup.DeleteBarChats ? now - TimeSpan.FromHours(cleanup.BarChatHours) : null;
        DateTimeOffset? oldestInWindow = cleanup.DeleteWindowChats ? now - TimeSpan.FromDays(cleanup.WindowChatDays) : null;
        var deleted = 0;
        var kept = new HashSet<Guid>();
        foreach (var conversation in await history.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            var ofItsKind = surfaces?.Of(conversation.Id) == ConversationSurface.Bar ? oldestInBar : oldestInWindow;
            if (conversation.UpdatedAt < oldest || conversation.UpdatedAt < ofItsKind)
            {
                await history.DeleteAsync(conversation.Id, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
            else
            {
                kept.Add(conversation.Id);
            }
        }

        // What was deleted, here or by the user, is forgotten in the record of where each chat was had.
        surfaces?.Keep(kept);
        return deleted;
    }

    /// <summary>
    /// Deletes the logs (what the Assistant did, Settings > Activity) that are older than the hours the user chose, when they have that on. It runs
    /// with the chats' cleanup; the log's own store also does it each time a run ends, so this is for an Assistant that has been running quietly.
    /// </summary>
    internal async Task PruneLogsAsync(CleanupSettings cleanup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        if (logs is not null && cleanup.DeleteLogs)
        {
            await logs.PruneAsync(TimeSpan.FromHours(cleanup.LogHours), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        (HistoryRetention Retention, CleanupSettings Cleanup)? last = null;
        var lastRun = DateTimeOffset.MinValue;
        try
        {
            await Task.Delay(FirstLook, clock, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var saved = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
                    var chosen = (saved.Privacy.HistoryRetention, saved.Cleanup);
                    var now = clock.GetUtcNow();
                    if (chosen != last || now - lastRun >= (chosen.Cleanup.DeletesAnything || chosen.Cleanup.DeleteLogs ? CleanupEvery : AtLeastEvery))
                    {
                        last = chosen;
                        lastRun = now;
                        await PruneLogsAsync(chosen.Cleanup, cancellationToken).ConfigureAwait(false);
                        var deleted = await PruneAsync(chosen.HistoryRetention, chosen.Cleanup, cancellationToken).ConfigureAwait(false);
                        if (deleted > 0)
                        {
                            LogPruned(logger, deleted);
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogFailed(logger, exception.GetType().Name);
                }

                await Task.Delay(Look, clock, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The application is closing.
        }
    }

    [LoggerMessage(EventId = 2410, Level = LogLevel.Information, Message = "Conversations older than the chosen times were deleted: {Count} conversations")]
    private static partial void LogPruned(ILogger logger, int count);

    [LoggerMessage(EventId = 2411, Level = LogLevel.Warning, Message = "Old history could not be deleted: {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
