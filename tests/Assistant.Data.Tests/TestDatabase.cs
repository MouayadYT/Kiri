using Assistant.Data.Migrations;
using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Data.Tests;

/// <summary>A database in a folder of its own, deleted with it, and the pieces that work on it.</summary>
internal sealed class TestDatabase : IDisposable
{
    public TestDatabase(int backupsToKeep = DatabaseOptions.DefaultBackupsToKeep)
    {
        Root = Path.Combine(Path.GetTempPath(), "assistant-data-tests", Guid.NewGuid().ToString("N"));
        Options = new DatabaseOptions(
            Path.Combine(Root, "data", DatabaseOptions.DatabaseFileName),
            Path.Combine(Root, "data", DatabaseOptions.BackupsFolderName),
            backupsToKeep);
    }

    public string Root { get; }

    public DatabaseOptions Options { get; }

    /// <summary>The clock the backups' names come from. It stands still until a test moves it.</summary>
    public TestTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 30, 10, 15, 0, TimeSpan.Zero));

    public IDatabaseConnectionFactory Connections => new DatabaseConnectionFactory(Options);

    public SqliteConnection Open() => Connections.Open();

    public DatabaseBackup CreateBackup(ILoggerFactory? loggers = null) =>
        new(Options, Time, (loggers ?? NullLoggerFactory.Instance).CreateLogger<DatabaseBackup>());

    public DatabaseMigrator CreateMigrator(MigrationCatalog catalog, ILoggerFactory? loggers = null) =>
        new(
            catalog,
            new SchemaVersionRepository(),
            CreateBackup(loggers),
            Time,
            (loggers ?? NullLoggerFactory.Instance).CreateLogger<DatabaseMigrator>());

    public DatabaseInitializer CreateInitializer(MigrationCatalog? catalog = null) =>
        new(Connections, CreateMigrator(catalog ?? MigrationCatalog.LoadDefault()));

    /// <summary>Creates the database with the app's migrations and opens it.</summary>
    public SqliteConnection Initialize()
    {
        CreateInitializer().Initialize();
        return Open();
    }

    /// <summary>The backups in the backups folder, oldest name first.</summary>
    public string[] BackupFiles() =>
        System.IO.Directory.Exists(Options.BackupsDirectory)
            ? [.. System.IO.Directory.EnumerateFiles(Options.BackupsDirectory).Order(StringComparer.Ordinal)]
            : [];

    public void Dispose()
    {
        // SQLite may hold a file for a moment after its connection closes.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(Root))
                {
                    System.IO.Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

internal sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
