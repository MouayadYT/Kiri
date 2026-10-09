using Assistant.Core.Audit;
using Assistant.Core.Contracts;
using Assistant.Core.Settings;
using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Assistant.Data;

/// <summary>
/// The activity log of what the Assistant did, in its local SQLite database (PROJECT_SPEC §3.5, §5.9, step 117): runs of the agent with their steps, and
/// integrations that were looked for, offered, installed, updated and removed. It holds no argument, result, message, prompt or answer, and keeps what it holds
/// on this PC.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The database is created and brought up to date the first time it is needed, away from the caller's thread, like the conversation history that shares it.
/// The first time it is, what was going on when the app last closed is marked interrupted and what is older than <see cref="AuditRetention.MaxAgeFor"/> (at most
/// <see cref="AuditRetention.MaxAge"/>, and no more than the user keeps their conversations for), or beyond <see cref="AuditRetention.MaxTasks"/> and
/// <see cref="AuditRetention.MaxActions"/>, is deleted; the same limits are applied again as a run ends.</item>
/// <item>A run is kept with all its steps in one transaction, so a run and its steps are saved together or not at all.</item>
/// <item>Nothing here logs a name, a summary or a count of anything private: only the type of a failure.</item>
/// </list>
/// </remarks>
public sealed partial class SqliteAuditStore(
    IDatabaseConnectionFactory connections,
    IDatabaseInitializer initializer,
    IAuditRepository audit,
    TimeProvider clock,
    ILogger<SqliteAuditStore> logger,
    ISettingsService? settings = null) : IAuditStore
{
    private readonly object _gate = new();
    private Task? _ready;

    /// <inheritdoc/>
    public Task SaveTaskAsync(AgentTaskSnapshot task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        return RunAsync(
            "saved",
            connection =>
            {
                audit.SaveTask(connection, task);
                if (task.Status.IsFinished())
                {
                    Prune(connection);
                }

                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task SaveActionAsync(AuditEntry action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync(
            "saved",
            connection =>
            {
                audit.SaveAction(connection, action);
                if (action.Status.IsFinished())
                {
                    Prune(connection);
                }

                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ActivityItem>> ListAsync(int limit, CancellationToken cancellationToken = default) =>
        RunAsync("read", connection => audit.List(connection, limit), cancellationToken);

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(
            "cleared",
            connection =>
            {
                audit.Clear(connection);

                // Deleted rows must not stay in the database's own files (the write-ahead log keeps old pages until it is emptied).
                connection.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        LogCleared(logger);
    }

    /// <inheritdoc/>
    public Task PruneAsync(TimeSpan maxAge, CancellationToken cancellationToken = default) =>
        RunAsync(
            "pruned",
            connection =>
            {
                audit.Prune(connection, clock.GetUtcNow() - maxAge, AuditRetention.MaxTasks, AuditRetention.MaxActions);
                return true;
            },
            cancellationToken);

    // Applies the limits of what is kept: the age the user's choice of how long to keep conversations allows (at most 90 days), the hours after which the user
    // has the logs deleted (48 unless changed), and how many. Called with the connection of a write that just ended a run or an action, on a thread of the pool.
    private void Prune(SqliteConnection connection)
    {
        var retention = HistoryRetention.UntilDeleted;
        var maxAge = AuditRetention.MaxAgeFor(retention);
        if (settings is not null)
        {
            try
            {
                var saved = settings.LoadAsync().GetAwaiter().GetResult();
                maxAge = AuditRetention.MaxAgeFor(saved.Privacy.HistoryRetention);
                if (saved.Cleanup.DeleteLogs)
                {
                    maxAge = TimeSpan.FromHours(Math.Min(saved.Cleanup.LogHours, maxAge.TotalHours));
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Settings that cannot be read are the default: the longest the log is kept. Nothing is deleted on a guess.
                maxAge = AuditRetention.MaxAgeFor(HistoryRetention.UntilDeleted);
            }
        }

        audit.Prune(connection, clock.GetUtcNow() - maxAge, AuditRetention.MaxTasks, AuditRetention.MaxActions);
    }

    // Creates the database if it is missing and brings its schema up to date, once; then tidies what the last run of the app left. A call after a failure tries again.
    private Task InitializeAsync()
    {
        lock (_gate)
        {
            if (_ready is null or { IsFaulted: true } or { IsCanceled: true })
            {
                _ready = Task.Run(() =>
                {
                    initializer.Initialize();
                    using var connection = connections.Open();
                    var interrupted = audit.MarkInterrupted(connection);
                    Prune(connection);
                    LogOpened(logger, interrupted);
                });
            }

            return _ready;
        }
    }

    // Opens the database (once) and runs the work on a connection of its own, on the thread pool. A failure of the database or its files is a DatabaseException
    // that says what could not be done, logged by the type of the failure alone.
    private async Task<T> RunAsync<T>(string what, Func<SqliteConnection, T> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var connection = connections.Open();
                    return work(connection);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DatabaseException || DatabaseException.IsStorageFailure(exception))
        {
            LogFailed(logger, what, exception.GetType().Name);
            throw new DatabaseException($"The activity log could not be {what}.", exception);
        }
    }

    [LoggerMessage(EventId = 6310, Level = LogLevel.Debug, Message = "The activity log was opened; {Interrupted} rows had been left going on")]
    private static partial void LogOpened(ILogger logger, int interrupted);

    [LoggerMessage(EventId = 6311, Level = LogLevel.Information, Message = "The activity log was deleted")]
    private static partial void LogCleared(ILogger logger);

    [LoggerMessage(EventId = 6312, Level = LogLevel.Warning, Message = "The activity log could not be {EventName}: {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string eventName, string exceptionType);
}
