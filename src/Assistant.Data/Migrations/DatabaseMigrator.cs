using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Assistant.Data.Migrations;

/// <summary>Brings a database's schema up to the newest version.</summary>
public interface IDatabaseMigrator
{
    /// <summary>
    /// Applies every migration the database on <paramref name="connection"/> has not had, oldest first, all or none.
    /// A database that already has content is backed up first.
    /// </summary>
    /// <exception cref="DatabaseTooNewException">The database is at a version this build has no migration for.</exception>
    /// <exception cref="DatabaseException">The backup or a migration failed; the database is as it was.</exception>
    MigrationResult Migrate(SqliteConnection connection);
}

/// <summary>
/// A simple forward-only migrator. The database's version is the highest row in the schema-version table; the
/// migrations after it are applied in one transaction that also records them, so an upgrade either completes or leaves
/// the database exactly as it was.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>An empty database is created with no backup. Any other is backed up before its first change, and a failed
/// backup stops the upgrade before the database is touched.</item>
/// <item>A database at a higher version than the catalog knows was written by a newer build and is never touched.</item>
/// <item>Foreign keys are off while the migrations run, since dropping a table that others refer to would otherwise
/// delete their rows, and are checked before the upgrade commits.</item>
/// <item>Nothing here reads a row's content, and nothing logged is more than a version number.</item>
/// </list>
/// </remarks>
public sealed partial class DatabaseMigrator(
    MigrationCatalog catalog,
    ISchemaVersionRepository versions,
    IDatabaseBackup backups,
    TimeProvider time,
    ILogger<DatabaseMigrator> logger) : IDatabaseMigrator
{
    /// <inheritdoc/>
    public MigrationResult Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        versions.EnsureTable(connection);
        var version = versions.GetCurrentVersion(connection);
        if (version > catalog.LatestVersion)
        {
            throw new DatabaseTooNewException(version, catalog.LatestVersion);
        }

        if (version == catalog.LatestVersion)
        {
            LogUpToDate(logger, version);
            return new MigrationResult(version, version, BackupPath: null);
        }

        // A new database has nothing to lose. Any other is copied before its schema changes.
        var backupPath = version > 0 ? Backup(connection, version) : null;
        var (previous, current) = Apply(connection);
        return new MigrationResult(previous, current, backupPath);
    }

    private string Backup(SqliteConnection connection, int version)
    {
        try
        {
            return backups.Create(connection, version);
        }
        catch (Exception exception) when (DatabaseException.IsStorageFailure(exception) || exception is DatabaseException)
        {
            throw new DatabaseException(
                $"The database at schema version {version} could not be backed up, so it was not upgraded.",
                exception);
        }
    }

    private (int Previous, int Current) Apply(SqliteConnection connection)
    {
        // Turning foreign keys off does nothing inside a transaction, so it is done around it.
        var foreignKeysWereOn = connection.ExecuteScalar<int>("PRAGMA foreign_keys") != 0;
        connection.Execute("PRAGMA foreign_keys = OFF");
        try
        {
            // IMMEDIATE takes the write lock now, so nobody can change the schema between the read below and the commit.
            using var transaction = connection.BeginTransaction(deferred: false);

            // Read again under the lock: another connection may have upgraded the database since the first read.
            var previous = versions.GetCurrentVersion(connection);
            if (previous > catalog.LatestVersion)
            {
                throw new DatabaseTooNewException(previous, catalog.LatestVersion);
            }

            var current = previous;
            foreach (var migration in catalog.After(previous))
            {
                ApplyOne(connection, migration);
                current = migration.Version;
            }

            RequireIntactForeignKeys(connection);
            transaction.Commit();
            LogUpgraded(logger, previous, current);
            return (previous, current);
        }
        finally
        {
            connection.Execute(foreignKeysWereOn ? "PRAGMA foreign_keys = ON" : "PRAGMA foreign_keys = OFF");
        }
    }

    private void ApplyOne(SqliteConnection connection, Migration migration)
    {
        try
        {
            connection.Execute(migration.Sql);
            versions.Record(connection, migration.Version, migration.Name, time.GetUtcNow());
        }
        catch (SqliteException exception)
        {
            throw new DatabaseException(
                $"Migration {migration.Version} ({migration.Name}) failed, so the upgrade was rolled back.",
                exception);
        }
    }

    private static void RequireIntactForeignKeys(SqliteConnection connection)
    {
        // One row per row that refers to a row that is not there. Only how many is reported, never which.
        var broken = connection.Query("PRAGMA foreign_key_check", static _ => 0).Count;
        if (broken > 0)
        {
            throw new DatabaseException($"The upgrade left {broken} rows that refer to rows that do not exist, so it was rolled back.");
        }
    }

    [LoggerMessage(EventId = 6000, Level = LogLevel.Debug, Message = "The database is at schema version {Version}, which is current")]
    private static partial void LogUpToDate(ILogger logger, int version);

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "Upgraded the database from schema version {PreviousVersion} to {CurrentVersion}")]
    private static partial void LogUpgraded(ILogger logger, int previousVersion, int currentVersion);
}
