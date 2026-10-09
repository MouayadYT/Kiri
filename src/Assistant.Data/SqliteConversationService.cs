using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.History;
using Assistant.Data.Migrations;
using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Assistant.Data;

/// <summary>
/// The app's conversation history in its local SQLite database (PROJECT_SPEC §3.5, §5.9): conversations and their
/// messages, saved a message at a time, listed, searched and deleted for real.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The database is created and brought up to date the first time it is needed (or when <see cref="InitializeAsync"/>
/// is called earlier, such as at startup), away from the caller's thread. Every operation does its file work on the
/// thread pool, so none of it blocks the UI.</item>
/// <item>While history is turned off in the settings, saving writes nothing. Listing, searching and deleting what is
/// already saved still work.</item>
/// <item>Each save is one transaction, so a message and its descriptors are saved together or not at all.</item>
/// <item>Nothing here logs a title, a message, a query or a path: only counts, and the type of a failure.</item>
/// </list>
/// </remarks>
public sealed partial class SqliteConversationService(
    IDatabaseConnectionFactory connections,
    IDatabaseInitializer initializer,
    IConversationRepository conversations,
    IMessageRepository messages,
    IConversationSearchRepository search,
    IDatabaseBackup backups,
    ISettingsService settings,
    ILogger<SqliteConversationService> logger) : IConversationService
{
    private readonly object _gate = new();
    private Task? _ready;

    /// <summary>
    /// Creates the database if it is missing and brings its schema up to date, once. Safe to call again, and from several
    /// threads; a call after a failure tries again.
    /// </summary>
    /// <exception cref="DatabaseTooNewException">A newer build wrote the database; it is left as it is.</exception>
    /// <exception cref="DatabaseException">The database could not be opened, backed up or upgraded.</exception>
    public Task InitializeAsync()
    {
        lock (_gate)
        {
            if (_ready is null or { IsFaulted: true } or { IsCanceled: true })
            {
                _ready = Task.Run(() => initializer.Initialize());
            }

            return _ready;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var summaries = await RunAsync("listed", connection => conversations.List(connection), cancellationToken).ConfigureAwait(false);
        LogListed(logger, summaries.Count);
        return summaries;
    }

    /// <inheritdoc/>
    public Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        RunAsync(
            "opened",
            connection => conversations.Get(connection, id) is { } conversation
                ? conversation with { Messages = messages.GetAll(connection, id) }
                : null,
            cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ConversationSearchResult>> SearchAsync(
        string query, int limit = IConversationService.DefaultSearchLimit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parsed = SearchQuery.Parse(query);
        if (parsed.IsEmpty)
        {
            return [];
        }

        var results = await RunAsync("searched", connection => search.Search(connection, parsed, limit), cancellationToken)
            .ConfigureAwait(false);
        LogSearched(logger, parsed.Terms.Count, results.Count);
        return results;
    }

    /// <inheritdoc/>
    public async Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (!await HistoryIsOnAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await RunInTransactionAsync(
            "saved",
            connection =>
            {
                conversations.Upsert(connection, conversation);
                messages.Replace(connection, conversation.Id, conversation.Messages);
                GiveProvisionalTitle(connection, conversation.Id, conversation.Messages);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
        LogSavedConversation(logger, conversation.Messages.Count);
    }

    /// <inheritdoc/>
    public async Task SaveMessageAsync(
        Guid conversationId, Message message, DateTimeOffset changedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!await HistoryIsOnAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var position = await RunInTransactionAsync(
            "saved",
            connection =>
            {
                conversations.Touch(connection, conversationId, message.CreatedAt, changedAt);
                var placed = messages.Save(connection, conversationId, message);
                GiveProvisionalTitle(connection, conversationId, [message]);
                return placed;
            },
            cancellationToken).ConfigureAwait(false);
        LogSavedMessage(logger, position);
    }

    /// <inheritdoc/>
    public async Task RenameAsync(Guid id, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var renamed = await RunInTransactionAsync(
            "renamed",
            connection => conversations.Rename(connection, id, ConversationTitle.OneLine(title)),
            cancellationToken).ConfigureAwait(false);
        LogRenamed(logger, renamed);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = await DeleteAndReclaimAsync(connection => conversations.Delete(connection, id), cancellationToken)
            .ConfigureAwait(false);
        LogDeleted(logger, deleted ? 1 : 0);
    }

    /// <inheritdoc/>
    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        var deleted = await DeleteAndReclaimAsync(conversations.DeleteAll, cancellationToken).ConfigureAwait(false);

        // The backups taken before an upgrade are copies of the history, so they go too (PROJECT_SPEC §3.5).
        var backupsDeleted = await OffThreadAsync("deleted", backups.DeleteAll, cancellationToken).ConfigureAwait(false);
        LogDeletedAll(logger, deleted, backupsDeleted);
    }

    // Gives the conversation a provisional title, if it has none, from the first thing the user asked among the messages:
    // the words of the first request, or, for a request that is only an attachment, the name of what was attached.
    private void GiveProvisionalTitle(SqliteConnection connection, Guid conversationId, IEnumerable<Message> saved)
    {
        var request = saved.FirstOrDefault(message => message.Role == MessageRole.User);
        if (request is null)
        {
            return;
        }

        var title = ConversationTitle.Provisional(request.Text);
        if (title.Length == 0 && request.ContextItems.Count > 0)
        {
            title = ConversationTitle.Provisional(request.ContextItems[0].DisplayName);
        }

        if (title.Length > 0)
        {
            conversations.SetTitleIfEmpty(connection, conversationId, title);
        }
    }

    // Deletes in one transaction, then takes what was deleted out of the search index and the database's own files, where
    // it must not stay behind.
    private async Task<T> DeleteAndReclaimAsync<T>(Func<SqliteConnection, T> delete, CancellationToken cancellationToken)
    {
        var deleted = await RunInTransactionAsync("deleted", delete, cancellationToken).ConfigureAwait(false);
        await RunAsync("cleaned up", Reclaim, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    // What deleting leaves behind: the index still holds the words of the messages it forgot, and the write-ahead log
    // still holds the old pages. Merging the index rewrites its pieces, and secure_delete zeroes the ones it frees; the
    // checkpoint then empties the log of what it kept.
    private int Reclaim(SqliteConnection connection)
    {
        search.Compact(connection);
        connection.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        return 0;
    }

    private async Task<bool> HistoryIsOnAsync(CancellationToken cancellationToken)
    {
        var current = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Privacy.HistoryEnabled)
        {
            return true;
        }

        LogHistoryOff(logger);
        return false;
    }

    private Task<T> RunInTransactionAsync<T>(string what, Func<SqliteConnection, T> work, CancellationToken cancellationToken) =>
        RunAsync(
            what,
            connection =>
            {
                // IMMEDIATE takes the write lock now, so two saves in a row never interleave their rows.
                using var transaction = connection.BeginTransaction(deferred: false);
                var result = work(connection);
                transaction.Commit();
                return result;
            },
            cancellationToken);

    // Opens the database (once) and runs the work on a connection of its own, on the thread pool.
    private async Task<T> RunAsync<T>(string what, Func<SqliteConnection, T> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InitializeAsync().ConfigureAwait(false);
        return await OffThreadAsync(
            what,
            () =>
            {
                using var connection = connections.Open();
                return work(connection);
            },
            cancellationToken).ConfigureAwait(false);
    }

    // Runs the work on the thread pool, and reports a failure of the database or its files as a DatabaseException that
    // says what could not be done, logged by the type of the failure alone.
    private async Task<T> OffThreadAsync<T>(string what, Func<T> work, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return work();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (DatabaseException.IsStorageFailure(exception))
        {
            LogFailed(logger, what, exception.GetType().Name);
            throw new DatabaseException($"The conversation history could not be {what}.", exception);
        }
    }

    [LoggerMessage(EventId = 6200, Level = LogLevel.Debug, Message = "History listed: {Conversations} conversations")]
    private static partial void LogListed(ILogger logger, int conversations);

    [LoggerMessage(EventId = 6201, Level = LogLevel.Debug, Message = "History searched: {Terms} words, {Conversations} conversations found")]
    private static partial void LogSearched(ILogger logger, int terms, int conversations);

    [LoggerMessage(EventId = 6202, Level = LogLevel.Debug, Message = "History saved a message at place {Position}")]
    private static partial void LogSavedMessage(ILogger logger, int position);

    [LoggerMessage(EventId = 6203, Level = LogLevel.Debug, Message = "History saved a conversation of {Messages} messages")]
    private static partial void LogSavedConversation(ILogger logger, int messages);

    [LoggerMessage(EventId = 6204, Level = LogLevel.Debug, Message = "History renamed a conversation: {Renamed}")]
    private static partial void LogRenamed(ILogger logger, bool renamed);

    [LoggerMessage(EventId = 6205, Level = LogLevel.Information, Message = "History deleted {Conversations} conversations")]
    private static partial void LogDeleted(ILogger logger, int conversations);

    [LoggerMessage(EventId = 6206, Level = LogLevel.Information, Message = "History deleted {Conversations} conversations and {Backups} backups")]
    private static partial void LogDeletedAll(ILogger logger, int conversations, int backups);

    [LoggerMessage(EventId = 6207, Level = LogLevel.Debug, Message = "History is off, so nothing was saved")]
    private static partial void LogHistoryOff(ILogger logger);

    [LoggerMessage(EventId = 6208, Level = LogLevel.Warning, Message = "The history could not be {EventName}: {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string eventName, string exceptionType);
}
