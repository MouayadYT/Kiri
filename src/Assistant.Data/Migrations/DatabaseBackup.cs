using System.Globalization;
using System.Text.RegularExpressions;
using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Assistant.Data.Migrations;

/// <summary>Copies of the database taken before its schema changes.</summary>
public interface IDatabaseBackup
{
    /// <summary>
    /// Writes a copy of the database <paramref name="connection"/> is open on, at schema version
    /// <paramref name="schemaVersion"/>, checks that the copy is sound, and returns its path.
    /// </summary>
    /// <exception cref="DatabaseException">The copy did not pass its check.</exception>
    /// <exception cref="SqliteException">SQLite could not write the copy.</exception>
    /// <exception cref="IOException">The copy could not be stored.</exception>
    string Create(SqliteConnection connection, int schemaVersion);

    /// <summary>
    /// Deletes every backup. A backup is a copy of the history, so deleting the history (PROJECT_SPEC §3.5) must delete
    /// these too. Returns how many files were deleted.
    /// </summary>
    int DeleteAll();
}

/// <summary>
/// Keeps the newest <see cref="DatabaseOptions.BackupsToKeep"/> backups, each named for when it was taken and the
/// schema version it holds: <c>assistant-20260930T101500123Z-v0001.db</c>.
/// </summary>
/// <remarks>
/// A backup is made with <c>VACUUM INTO</c>, which writes a consistent copy of a database that is in use and carries
/// over only rows that still exist, so content the user has deleted does not survive in a backup.
/// </remarks>
public sealed partial class DatabaseBackup(DatabaseOptions options, TimeProvider time, ILogger<DatabaseBackup> logger)
    : IDatabaseBackup
{
    private const string FilePrefix = "assistant-";
    private const string FileExtension = ".db";
    private const string PartialExtension = ".tmp";

    // A finished backup: assistant-<UTC yyyyMMddTHHmmssfff>Z-v<version>.db. Names sort in time order.
    [GeneratedRegex(@"^assistant-\d{8}T\d{9}Z-v\d{4,}\.db$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupNamePattern();

    // Anything a backup can leave in the folder: the backup, its unfinished form, and SQLite's side files.
    [GeneratedRegex(@"^assistant-\d{8}T\d{9}Z-v\d{4,}\.db(\.tmp)?(-wal|-shm|-journal)?$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupFilePattern();

    /// <inheritdoc/>
    public string Create(SqliteConnection connection, int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Directory.CreateDirectory(options.BackupsDirectory);
        var stamp = time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var path = Path.Combine(options.BackupsDirectory, $"{FilePrefix}{stamp}-v{schemaVersion:0000}{FileExtension}");

        // Written under another name and renamed once it has been checked, so a file with a backup's name is always whole.
        var partialPath = path + PartialExtension;
        try
        {
            File.Delete(partialPath);
            connection.Execute("VACUUM INTO $path", ("$path", partialPath));
            Verify(partialPath);
            File.Move(partialPath, path);
        }
        catch
        {
            // Nothing more to do if it stays: the caller is already reporting the failure that led here.
            FileCleanup.TryDelete(partialPath);
            throw;
        }

        LogBackedUp(logger, schemaVersion);
        Prune();
        return path;
    }

    /// <inheritdoc/>
    public int DeleteAll()
    {
        if (!Directory.Exists(options.BackupsDirectory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(options.BackupsDirectory))
        {
            if (BackupFilePattern().IsMatch(Path.GetFileName(file)))
            {
                File.Delete(file);
                deleted++;
            }
        }

        return deleted;
    }

    internal static void Verify(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        using var backup = new SqliteConnection(connectionString);
        backup.Open();

        // "ok" when the file is sound; otherwise one row for each problem found.
        if (backup.ExecuteScalar<string>("PRAGMA quick_check") != "ok")
        {
            throw new DatabaseException("The backup did not pass its integrity check.");
        }
    }

    // The new backup is safe, so an old one that cannot be removed now is not worth failing an upgrade over: it is logged
    // and removed by a later backup.
    private void Prune()
    {
        try
        {
            // Only files this class named are ever removed, never anything else that is in the folder.
            var backups = Directory.EnumerateFiles(options.BackupsDirectory)
                .Where(file => BackupNamePattern().IsMatch(Path.GetFileName(file)));
            if (FileCleanup.DeleteAllButNewest(backups, options.BackupsToKeep) > 0)
            {
                LogPruneFailed(logger);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogPruneFailed(logger);
        }
    }

    [LoggerMessage(EventId = 6100, Level = LogLevel.Information, Message = "Backed up the database at schema version {SchemaVersion}")]
    private static partial void LogBackedUp(ILogger logger, int schemaVersion);

    [LoggerMessage(EventId = 6101, Level = LogLevel.Warning, Message = "Old database backups could not be removed")]
    private static partial void LogPruneFailed(ILogger logger);
}
