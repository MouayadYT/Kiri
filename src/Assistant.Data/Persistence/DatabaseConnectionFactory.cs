using Microsoft.Data.Sqlite;

namespace Assistant.Data.Persistence;

/// <summary>Opens connections to the app's database.</summary>
public interface IDatabaseConnectionFactory
{
    /// <summary>
    /// Opens a new connection, creating the database file (and its folder) if they do not exist. The caller disposes it.
    /// </summary>
    SqliteConnection Open();
}

/// <summary>
/// Opens connections that enforce foreign keys and overwrite deleted content (<c>secure_delete</c>, PROJECT_SPEC §3.5),
/// which SQLite only keeps per connection, and that can fold text for a search (<see cref="TextFolding"/>).
/// </summary>
/// <remarks>
/// Connections are not pooled: a pooled connection keeps the file open after it is disposed, which would stop the
/// database from being deleted, replaced or copied by name (deleting all history, restoring a backup).
/// </remarks>
public sealed class DatabaseConnectionFactory : IDatabaseConnectionFactory
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    /// <summary>Creates a factory for the database <paramref name="options"/> names.</summary>
    public DatabaseConnectionFactory(DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _databasePath = options.DatabasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 10,
        }.ToString();
    }

    /// <inheritdoc/>
    public SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            connection.Execute("PRAGMA secure_delete = ON");
            TextFolding.Register(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
