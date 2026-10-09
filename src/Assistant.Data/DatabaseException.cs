using Microsoft.Data.Sqlite;

namespace Assistant.Data;

/// <summary>
/// The database could not be opened, backed up or upgraded. Messages carry versions and step names only, never rows,
/// paths or SQL, so they are safe to show and to copy (PROJECT_SPEC §3.3); the cause is the inner exception.
/// </summary>
public class DatabaseException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DatabaseException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its cause.</summary>
    public DatabaseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is a failure of the database or its files (SQLite, the disk, access to the
    /// folder), which the Data layer reports as a <see cref="DatabaseException"/>, rather than a defect in the app.
    /// </summary>
    internal static bool IsStorageFailure(Exception exception) =>
        exception is SqliteException or IOException or UnauthorizedAccessException;
}

/// <summary>
/// The database was written by a newer build than this one (its schema version is higher than any migration this build
/// knows). It is never changed: migrations only go forward.
/// </summary>
public sealed class DatabaseTooNewException : DatabaseException
{
    /// <summary>Creates the exception.</summary>
    public DatabaseTooNewException(int databaseVersion, int supportedVersion)
        : base(
            $"The database is at schema version {databaseVersion}, but this build only knows versions up to " +
            $"{supportedVersion}. It was left as it is.")
    {
        DatabaseVersion = databaseVersion;
        SupportedVersion = supportedVersion;
    }

    /// <summary>The schema version the database is at.</summary>
    public int DatabaseVersion { get; }

    /// <summary>The newest schema version this build has a migration for.</summary>
    public int SupportedVersion { get; }
}
