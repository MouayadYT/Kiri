using Assistant.Data.Migrations;
using Assistant.Data.Persistence;

namespace Assistant.Data;

/// <summary>Makes the app's database ready to use: creates it if it is missing and brings its schema up to date.</summary>
public interface IDatabaseInitializer
{
    /// <summary>
    /// Opens the database, creating it if there is none, and applies the migrations it lacks (backing it up first when
    /// that changes an existing one). It does file I/O, so call it away from the UI thread. Safe to call again.
    /// </summary>
    /// <exception cref="DatabaseTooNewException">A newer build wrote the database; it is left as it is.</exception>
    /// <exception cref="DatabaseException">The database could not be opened, backed up or upgraded.</exception>
    MigrationResult Initialize();
}

/// <inheritdoc/>
public sealed class DatabaseInitializer(IDatabaseConnectionFactory connections, IDatabaseMigrator migrator) : IDatabaseInitializer
{
    /// <inheritdoc/>
    public MigrationResult Initialize()
    {
        try
        {
            using var connection = connections.Open();

            // Write-ahead logging lets the History window read while a conversation is being saved. The mode is kept in
            // the file, so it stays on for every later connection.
            connection.Execute("PRAGMA journal_mode = WAL");
            return migrator.Migrate(connection);
        }
        catch (Exception exception) when (DatabaseException.IsStorageFailure(exception))
        {
            throw new DatabaseException("The database could not be opened or upgraded.", exception);
        }
    }
}
