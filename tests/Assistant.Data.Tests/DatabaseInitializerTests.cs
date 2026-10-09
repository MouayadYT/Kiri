using Assistant.Data.Migrations;
using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Assistant.Data.Tests;

public sealed class DatabaseInitializerTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public void CreatesTheDatabaseAndItsFolder_AtTheNewestSchema()
    {
        Assert.False(Directory.Exists(Path.GetDirectoryName(_database.Options.DatabasePath)));

        var result = _database.CreateInitializer().Initialize();

        var latest = MigrationCatalog.LoadDefault().LatestVersion;
        Assert.True(File.Exists(_database.Options.DatabasePath));
        Assert.Equal(new MigrationResult(0, latest, BackupPath: null), result);
        Assert.True(result.Upgraded);
        using var connection = _database.Open();
        var history = new SchemaVersionRepository().GetHistory(connection);
        Assert.Equal(1, history[0].Version);
        Assert.Equal("initial_schema", history[0].Name);
        Assert.Equal(_database.Time.GetUtcNow(), history[0].AppliedAt);
        Assert.Empty(_database.BackupFiles());
    }

    [Fact]
    public void KeepsTheDatabaseInWriteAheadMode()
    {
        _database.CreateInitializer().Initialize();

        using var connection = _database.Open();

        Assert.Equal("wal", connection.ExecuteScalar<string>("PRAGMA journal_mode"));
    }

    [Fact]
    public void EveryConnectionEnforcesForeignKeysAndOverwritesDeletedContent()
    {
        _database.CreateInitializer().Initialize();

        using var connection = _database.Open();

        Assert.Equal(1, connection.ExecuteScalar<int>("PRAGMA foreign_keys"));
        Assert.Equal(1, connection.ExecuteScalar<int>("PRAGMA secure_delete"));
    }

    [Fact]
    public void RunningItAgainChangesNothing()
    {
        var initializer = _database.CreateInitializer();
        initializer.Initialize();
        _database.Time.Advance(TimeSpan.FromHours(1));

        var result = initializer.Initialize();

        Assert.False(result.Upgraded);
        Assert.Null(result.BackupPath);
        using var connection = _database.Open();
        var history = new SchemaVersionRepository().GetHistory(connection);
        Assert.Equal(MigrationCatalog.LoadDefault().LatestVersion, history.Count);
        Assert.All(history, entry => Assert.True(entry.AppliedAt < _database.Time.GetUtcNow()));
        Assert.Empty(_database.BackupFiles());
    }

    [Fact]
    public void AFileThatIsNotADatabaseIsReportedAndLeftAlone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_database.Options.DatabasePath)!);
        var garbage = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 + 7)).ToArray();
        File.WriteAllBytes(_database.Options.DatabasePath, garbage);

        var exception = Assert.Throws<DatabaseException>(() => _database.CreateInitializer().Initialize());

        Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Equal(garbage, File.ReadAllBytes(_database.Options.DatabasePath));
        Assert.DoesNotContain(_database.Options.DatabasePath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADatabaseFromANewerBuildIsReportedAsSuch_NotAsAFailureToOpen()
    {
        var initializer = _database.CreateInitializer();
        var latest = initializer.Initialize().CurrentVersion;
        using (var connection = _database.Open())
        {
            // What a newer build would have recorded.
            new SchemaVersionRepository().Record(connection, latest + 1, "from_the_future", DateTimeOffset.UnixEpoch);
        }

        var exception = Assert.Throws<DatabaseTooNewException>(initializer.Initialize);

        Assert.Equal(latest + 1, exception.DatabaseVersion);
        Assert.Equal(latest, exception.SupportedVersion);
        Assert.Empty(_database.BackupFiles());
    }
}
