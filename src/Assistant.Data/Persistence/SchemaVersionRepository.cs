using Microsoft.Data.Sqlite;

namespace Assistant.Data.Persistence;

/// <summary>One migration the database has applied, as the schema-version table records it.</summary>
/// <param name="Version">The migration's version number.</param>
/// <param name="Name">The migration's name.</param>
/// <param name="AppliedAt">When it was applied.</param>
public sealed record SchemaVersionEntry(int Version, string Name, DateTimeOffset AppliedAt);

/// <summary>
/// The schema-version table: one row per migration the database has applied. The database's schema version is the
/// highest version in it, or 0 when it has none.
/// </summary>
/// <remarks>Each method works on the connection it is given, so it takes part in that connection's transaction.</remarks>
public interface ISchemaVersionRepository
{
    /// <summary>Creates the table if it does not exist. Safe to call every time the database is opened.</summary>
    void EnsureTable(SqliteConnection connection);

    /// <summary>The database's schema version: the highest applied version, or 0 when none has been applied.</summary>
    int GetCurrentVersion(SqliteConnection connection);

    /// <summary>The applied migrations, oldest version first.</summary>
    IReadOnlyList<SchemaVersionEntry> GetHistory(SqliteConnection connection);

    /// <summary>Records that migration <paramref name="version"/> was applied at <paramref name="appliedAt"/>.</summary>
    void Record(SqliteConnection connection, int version, string name, DateTimeOffset appliedAt);
}

/// <inheritdoc/>
public sealed class SchemaVersionRepository : ISchemaVersionRepository
{
    /// <inheritdoc/>
    public void EnsureTable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.Execute(
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                version    INTEGER NOT NULL PRIMARY KEY,
                name       TEXT    NOT NULL,
                applied_at TEXT    NOT NULL
            ) STRICT
            """);
    }

    /// <inheritdoc/>
    public int GetCurrentVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ExecuteScalar<int>("SELECT COALESCE(MAX(version), 0) FROM schema_version");
    }

    /// <inheritdoc/>
    public IReadOnlyList<SchemaVersionEntry> GetHistory(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Query(
            "SELECT version, name, applied_at FROM schema_version ORDER BY version",
            reader => new SchemaVersionEntry(
                reader.GetInt32(0),
                reader.GetString(1),
                DatabaseTimestamps.FromText(reader.GetString(2))));
    }

    /// <inheritdoc/>
    public void Record(SqliteConnection connection, int version, string name, DateTimeOffset appliedAt)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        connection.Execute(
            "INSERT INTO schema_version (version, name, applied_at) VALUES ($version, $name, $appliedAt)",
            ("$version", version),
            ("$name", name),
            ("$appliedAt", DatabaseTimestamps.ToText(appliedAt)));
    }
}
