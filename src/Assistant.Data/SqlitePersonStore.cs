using Assistant.Core.People;
using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Assistant.Data;

/// <summary>
/// The people the user told the Assistant about, in its local SQLite database (PROJECT_SPEC §3.5, §5.9, step 112): added, changed and removed for real, and
/// kept on this PC. Nothing here sends a name or an address anywhere.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The database is created and brought up to date the first time it is needed, away from the caller's thread, like the conversation history that
/// shares it. Every operation does its file work on the thread pool, so none of it blocks the UI.</item>
/// <item>Every person is tidied by <see cref="PersonRules.Normalize"/> before it is kept, and each save is one transaction, so a person and all that
/// belongs to them are saved together or not at all.</item>
/// <item>Nothing here logs a name, an alias, a relationship or an address: only counts, and the type of a failure.</item>
/// </list>
/// </remarks>
public sealed partial class SqlitePersonStore(
    IDatabaseConnectionFactory connections,
    IDatabaseInitializer initializer,
    IPersonRepository people,
    TimeProvider clock,
    ILogger<SqlitePersonStore> logger) : IPersonStore
{
    private readonly object _gate = new();
    private Task? _ready;

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Person>> ListAsync(CancellationToken cancellationToken = default)
    {
        var all = await RunAsync("read", people.List, cancellationToken).ConfigureAwait(false);
        LogListed(logger, all.Count);
        return all;
    }

    /// <inheritdoc/>
    public Task<Person?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        RunAsync("read", connection => people.Get(connection, id), cancellationToken);

    /// <inheritdoc/>
    public async Task<Person> SaveAsync(Person person, CancellationToken cancellationToken = default)
    {
        var tidy = PersonRules.Normalize(person);
        var saved = await RunAsync(
            "saved",
            connection =>
            {
                // IMMEDIATE takes the write lock now, so the count and the write cannot be split by another save.
                using var transaction = connection.BeginTransaction(deferred: false);
                var existing = people.Get(connection, tidy.Id);
                if (existing is null && people.Count(connection) >= PersonRules.MaxPeople)
                {
                    throw new PersonValidationException($"The Assistant keeps at most {PersonRules.MaxPeople} people.");
                }

                var now = clock.GetUtcNow();
                people.Upsert(connection, tidy with { CreatedAt = existing?.CreatedAt ?? now, UpdatedAt = now });
                transaction.Commit();
                return people.Get(connection, tidy.Id)!;
            },
            cancellationToken).ConfigureAwait(false);
        LogSaved(logger, saved.Aliases.Count, saved.Relationships.Count, saved.Identifiers.Count);
        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = await RunAsync(
            "removed",
            connection =>
            {
                using var transaction = connection.BeginTransaction(deferred: false);
                var removed = people.Delete(connection, id);
                transaction.Commit();

                // Deleted rows must not stay in the database's own files (the write-ahead log keeps old pages until it is emptied).
                if (removed)
                {
                    connection.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                }

                return removed;
            },
            cancellationToken).ConfigureAwait(false);
        LogRemoved(logger, deleted);
        if (deleted)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return deleted;
    }

    // Creates the database if it is missing and brings its schema up to date, once; a call after a failure tries again.
    private Task InitializeAsync()
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

    // Opens the database (once) and runs the work on a connection of its own, on the thread pool. A failure of the database or its files is
    // a PersonStoreException that says what could not be done, logged by the type of the failure alone.
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
            throw new PersonStoreException($"The people could not be {what}.", exception);
        }
    }

    [LoggerMessage(EventId = 6300, Level = LogLevel.Debug, Message = "People listed: {People} people")]
    private static partial void LogListed(ILogger logger, int people);

    [LoggerMessage(EventId = 6301, Level = LogLevel.Debug, Message = "A person was saved with {Aliases} aliases, {Relationships} relationships and {Identifiers} identifiers")]
    private static partial void LogSaved(ILogger logger, int aliases, int relationships, int identifiers);

    [LoggerMessage(EventId = 6302, Level = LogLevel.Information, Message = "A person was removed: {Removed}")]
    private static partial void LogRemoved(ILogger logger, bool removed);

    [LoggerMessage(EventId = 6303, Level = LogLevel.Warning, Message = "The people could not be {EventName}: {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string eventName, string exceptionType);
}
