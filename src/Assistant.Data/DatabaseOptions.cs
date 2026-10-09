using Assistant.Core.Storage;

namespace Assistant.Data;

/// <summary>Where the SQLite database and its backups live, and how many backups are kept.</summary>
public sealed class DatabaseOptions
{
    /// <summary>Name of the database file inside the data directory.</summary>
    public const string DatabaseFileName = "assistant.db";

    /// <summary>Name of the folder, inside the data directory, that holds the backups made before upgrades.</summary>
    public const string BackupsFolderName = "backups";

    /// <summary>How many backups <see cref="BackupsToKeep"/> keeps unless told otherwise.</summary>
    public const int DefaultBackupsToKeep = 3;

    /// <summary>Creates the options.</summary>
    /// <exception cref="ArgumentException">A path is not fully qualified.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="backupsToKeep"/> is less than one.</exception>
    public DatabaseOptions(string databasePath, string backupsDirectory, int backupsToKeep = DefaultBackupsToKeep)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupsDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(backupsToKeep, 1);
        if (!Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException("The database path must be fully qualified.", nameof(databasePath));
        }

        if (!Path.IsPathFullyQualified(backupsDirectory))
        {
            throw new ArgumentException("The backups directory must be fully qualified.", nameof(backupsDirectory));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        BackupsDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupsDirectory));
        BackupsToKeep = backupsToKeep;
    }

    /// <summary>Full path of the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Folder that holds the backups taken before an upgrade.</summary>
    public string BackupsDirectory { get; }

    /// <summary>
    /// How many backups stay in <see cref="BackupsDirectory"/>. Taking a new one removes the oldest beyond this number,
    /// because a backup holds a copy of the history and so must not pile up.
    /// </summary>
    public int BackupsToKeep { get; }

    /// <summary>The database and its backups inside the app's data directory (<see cref="AppPaths.DatabaseDirectory"/>).</summary>
    public static DatabaseOptions For(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new DatabaseOptions(
            Path.Combine(paths.DatabaseDirectory, DatabaseFileName),
            Path.Combine(paths.DatabaseDirectory, BackupsFolderName));
    }
}
