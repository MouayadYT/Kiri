using System.Text.RegularExpressions;
using Assistant.Data.Migrations;
using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Assistant.Data.Tests;

/// <summary>
/// Upgrades from an existing schema, which the app's own catalog does not have yet: the tests add migrations of their
/// own after the real initial one.
/// </summary>
public sealed class DatabaseMigratorTests : IDisposable
{
    private static readonly Migration Initial = MigrationCatalog.LoadDefault().Migrations[0];

    private static readonly Migration AddNote = new(
        2,
        "add_conversation_note",
        "ALTER TABLE conversations ADD COLUMN note TEXT NOT NULL DEFAULT '';" +
        "INSERT INTO settings_metadata (key, value, updated_at) VALUES ('note_added', 'yes', '2026-09-30T00:00:00.0000000Z');");

    private static readonly Migration AddArchived = new(
        3,
        "add_conversation_archived",
        "ALTER TABLE conversations ADD COLUMN archived INTEGER NOT NULL DEFAULT 0");

    private readonly TestDatabase _database = new();
    private readonly SchemaVersionRepository _versions = new();

    public void Dispose() => _database.Dispose();

    private static MigrationCatalog Catalog(params Migration[] after) => new([Initial, .. after]);

    private SqliteConnection OpenAtInitialSchema()
    {
        var connection = _database.Open();
        _database.CreateMigrator(Catalog()).Migrate(connection);
        return connection;
    }

    [Fact]
    public void ANewDatabaseGetsEveryMigrationAndNoBackup()
    {
        using var connection = _database.Open();

        var result = _database.CreateMigrator(Catalog(AddNote, AddArchived)).Migrate(connection);

        Assert.Equal(new MigrationResult(0, 3, BackupPath: null), result);
        Assert.Equal([1, 2, 3], connection.AppliedVersions());
        Assert.Empty(_database.BackupFiles());
    }

    [Fact]
    public void AnExistingDatabaseIsBackedUpBeforeItIsUpgraded()
    {
        using var connection = OpenAtInitialSchema();
        connection.InsertConversation();
        connection.InsertMessage("m1");

        var result = _database.CreateMigrator(Catalog(AddNote)).Migrate(connection);

        Assert.Equal(1, result.PreviousVersion);
        Assert.Equal(2, result.CurrentVersion);
        var backupPath = Assert.IsType<string>(result.BackupPath);
        Assert.Equal([backupPath], _database.BackupFiles());
        Assert.Equal(_database.Options.BackupsDirectory, Path.GetDirectoryName(backupPath));
        Assert.Equal("assistant-20260930T101500000Z-v0001.db", Path.GetFileName(backupPath));

        // The backup holds the database as it was: the old schema, and the data.
        using (var backup = SqlHelpers.OpenReadOnly(backupPath))
        {
            Assert.Equal([1], backup.AppliedVersions());
            Assert.DoesNotContain("note", backup.Columns("conversations"));
            Assert.Equal(1, backup.Count("messages"));
        }

        // The database itself has the new schema and all of its data.
        Assert.Equal([1, 2], connection.AppliedVersions());
        Assert.Contains("note", connection.Columns("conversations"));
        Assert.Equal("Trip plans", connection.ExecuteScalar<string>("SELECT title FROM conversations"));
        Assert.Equal(1, connection.Count("messages"));
    }

    [Fact]
    public void ADatabaseThatIsCurrentIsLeftAloneAndNotBackedUp()
    {
        using var connection = _database.Open();
        var migrator = _database.CreateMigrator(Catalog(AddNote));
        migrator.Migrate(connection);
        var appliedAt = _versions.GetHistory(connection).Select(entry => entry.AppliedAt).ToArray();
        _database.Time.Advance(TimeSpan.FromDays(1));

        var result = migrator.Migrate(connection);

        Assert.Equal(new MigrationResult(2, 2, BackupPath: null), result);
        Assert.Equal(appliedAt, _versions.GetHistory(connection).Select(entry => entry.AppliedAt));
        Assert.Empty(_database.BackupFiles());
    }

    [Fact]
    public void EachMigrationIsRecordedWithItsNameAndWhenItRan()
    {
        using var connection = OpenAtInitialSchema();
        _database.Time.Advance(TimeSpan.FromDays(2));

        _database.CreateMigrator(Catalog(AddNote, AddArchived)).Migrate(connection);

        var history = _versions.GetHistory(connection);
        Assert.Equal(["initial_schema", "add_conversation_note", "add_conversation_archived"], history.Select(entry => entry.Name));
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 10, 15, 0, TimeSpan.Zero), history[2].AppliedAt);
        Assert.True(history[0].AppliedAt < history[1].AppliedAt);
    }

    [Fact]
    public void AFailedMigrationRollsTheWholeUpgradeBack_AndCanBeRetried()
    {
        using var connection = OpenAtInitialSchema();
        connection.InsertConversation();
        var broken = new Migration(
            3,
            "broken_step",
            "CREATE TABLE half_done (id INTEGER); INSERT INTO no_such_table VALUES (1);");

        var exception = Assert.Throws<DatabaseException>(() => _database.CreateMigrator(Catalog(AddNote, broken)).Migrate(connection));

        // Migration 2 was fine, but the upgrade is all or nothing: the database is exactly as it was.
        Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Contains("Migration 3 (broken_step)", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no_such_table", exception.Message, StringComparison.Ordinal);
        Assert.Equal([1], connection.AppliedVersions());
        Assert.DoesNotContain("note", connection.Columns("conversations"));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM settings_metadata"));
        Assert.DoesNotContain("half_done", connection.Tables());
        Assert.Equal(1, connection.Count("conversations"));
        Assert.Single(_database.BackupFiles());
        Assert.Equal(1, connection.ExecuteScalar<int>("PRAGMA foreign_keys"));

        // The next launch, with the fault fixed, upgrades from where the database was.
        _database.Time.Advance(TimeSpan.FromMinutes(1));
        var result = _database.CreateMigrator(Catalog(AddNote, AddArchived)).Migrate(connection);

        Assert.Equal(1, result.PreviousVersion);
        Assert.Equal(3, result.CurrentVersion);
        Assert.Equal([1, 2, 3], connection.AppliedVersions());
        Assert.Equal(2, _database.BackupFiles().Length);
    }

    [Fact]
    public void RebuildingATableThatOthersReferToKeepsTheirRows()
    {
        using var connection = OpenAtInitialSchema();
        connection.InsertConversation();
        connection.InsertMessage("m1");
        var rebuild = new Migration(
            2,
            "rebuild_conversations",
            """
            CREATE TABLE conversations_new (
                id         TEXT NOT NULL PRIMARY KEY,
                title      TEXT NOT NULL DEFAULT '',
                pinned     INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            ) STRICT;
            INSERT INTO conversations_new (id, title, created_at, updated_at)
                SELECT id, title, created_at, updated_at FROM conversations;
            DROP TABLE conversations;
            ALTER TABLE conversations_new RENAME TO conversations;
            CREATE INDEX ix_conversations_updated_at ON conversations (updated_at DESC);
            """);

        _database.CreateMigrator(Catalog(rebuild)).Migrate(connection);

        // With foreign keys on, dropping conversations would have deleted its messages.
        Assert.Equal(1, connection.Count("messages"));
        Assert.Equal(1, connection.Count("conversations"));
        Assert.Contains("pinned", connection.Columns("conversations"));
        Assert.Empty(connection.Query("PRAGMA foreign_key_check", _ => 0));
        Assert.Equal(1, connection.ExecuteScalar<int>("PRAGMA foreign_keys"));

        // And the rule still holds afterwards.
        connection.Execute("DELETE FROM conversations");
        Assert.Equal(0, connection.Count("messages"));
    }

    [Fact]
    public void AnUpgradeThatLeavesOrphanedRowsIsRolledBack()
    {
        using var connection = OpenAtInitialSchema();
        var orphan = new Migration(
            2,
            "orphan_a_message",
            "INSERT INTO messages (id, conversation_id, position, role, text, created_at) " +
            "VALUES ('m1', 'no-such-conversation', 0, 'User', 'hi', '2026-09-30T00:00:00.0000000Z')");

        var exception = Assert.Throws<DatabaseException>(() => _database.CreateMigrator(Catalog(orphan)).Migrate(connection));

        Assert.Contains("1 rows", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        Assert.Equal([1], connection.AppliedVersions());
        Assert.Equal(0, connection.Count("messages"));
        Assert.Equal(1, connection.ExecuteScalar<int>("PRAGMA foreign_keys"));
    }

    [Fact]
    public void AConnectionThatHadForeignKeysOffGetsThemBackOff()
    {
        using var connection = OpenAtInitialSchema();
        connection.Execute("PRAGMA foreign_keys = OFF");

        _database.CreateMigrator(Catalog(AddNote)).Migrate(connection);

        Assert.Equal(0, connection.ExecuteScalar<int>("PRAGMA foreign_keys"));
    }

    [Fact]
    public void ADatabaseFromANewerBuildIsRefusedAndNotTouched()
    {
        using var connection = _database.Open();
        _database.CreateMigrator(Catalog(AddNote, AddArchived)).Migrate(connection);
        var before = _versions.GetHistory(connection);

        var exception = Assert.Throws<DatabaseTooNewException>(() => _database.CreateMigrator(Catalog(AddNote)).Migrate(connection));

        Assert.Equal(3, exception.DatabaseVersion);
        Assert.Equal(2, exception.SupportedVersion);
        Assert.Equal(before, _versions.GetHistory(connection));
        Assert.Contains("archived", connection.Columns("conversations"));
        Assert.Empty(_database.BackupFiles());
    }

    [Fact]
    public void ABackupThatCannotBeMadeStopsTheUpgradeBeforeAnythingChanges()
    {
        using var connection = OpenAtInitialSchema();
        connection.InsertConversation();
        // A file where the backups folder should be.
        File.WriteAllText(_database.Options.BackupsDirectory, "in the way");

        var exception = Assert.Throws<DatabaseException>(() => _database.CreateMigrator(Catalog(AddNote)).Migrate(connection));

        Assert.Contains("could not be backed up", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(exception.InnerException);
        Assert.Equal([1], connection.AppliedVersions());
        Assert.DoesNotContain("note", connection.Columns("conversations"));
        Assert.Equal(1, connection.Count("conversations"));
    }

    [Fact]
    public void AnUpgradeKeepsOnlyTheNewestBackups()
    {
        using var database = new TestDatabase(backupsToKeep: 2);
        using var connection = database.Open();
        database.CreateMigrator(Catalog()).Migrate(connection);
        var later = new List<Migration>();

        for (var version = 2; version <= 5; version++)
        {
            later.Add(new Migration(version, $"step_{version}", $"CREATE TABLE step_{version} (id INTEGER)"));
            database.Time.Advance(TimeSpan.FromMinutes(1));
            database.CreateMigrator(Catalog([.. later])).Migrate(connection);
        }

        // Backups of versions 1..4 were taken; the two newest remain.
        Assert.Equal(
            ["v0003", "v0004"],
            database.BackupFiles().Select(file => Regex.Match(Path.GetFileName(file), "v\\d{4}").Value));
    }

    [Fact]
    public void ADatabaseUpgradedByAnotherConnectionAfterItWasReadIsNotUpgradedTwice()
    {
        var catalog = Catalog(AddNote);
        using var connection = _database.Open();
        // Between this migrator's first read of the version (0) and its taking the write lock, another one finishes.
        var racing = new RacingVersions(
            _versions,
            afterFirstRead: () =>
            {
                using var other = _database.Open();
                _database.CreateMigrator(catalog).Migrate(other);
            });
        var migrator = new DatabaseMigrator(
            catalog,
            racing,
            _database.CreateBackup(),
            _database.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseMigrator>.Instance);

        var result = migrator.Migrate(connection);

        // It saw the other's work under the lock and had nothing left to apply, instead of failing on "table exists".
        Assert.Equal(new MigrationResult(2, 2, BackupPath: null), result);
        Assert.Equal([1, 2], connection.AppliedVersions());
    }

    [Fact]
    public async Task SeveralProcessesStartingTogetherApplyTheMigrationsOnce()
    {
        var initializers = Enumerable.Range(0, 4).Select(_ => _database.CreateInitializer()).ToArray();

        var results = await Task.WhenAll(initializers.Select(initializer => Task.Run(initializer.Initialize)));

        using var connection = _database.Open();
        Assert.Equal(MigrationCatalog.LoadDefault().LatestVersion, connection.AppliedVersions().Count);
        Assert.Equal(1, results.Count(result => result.Upgraded && result.PreviousVersion == 0));
    }

    /// <summary>Runs <paramref name="afterFirstRead"/> once, right after the first time the version is read.</summary>
    private sealed class RacingVersions(ISchemaVersionRepository inner, Action afterFirstRead) : ISchemaVersionRepository
    {
        private bool _read;

        public void EnsureTable(SqliteConnection connection) => inner.EnsureTable(connection);

        public int GetCurrentVersion(SqliteConnection connection)
        {
            var version = inner.GetCurrentVersion(connection);
            if (!_read)
            {
                _read = true;
                afterFirstRead();
            }

            return version;
        }

        public IReadOnlyList<SchemaVersionEntry> GetHistory(SqliteConnection connection) => inner.GetHistory(connection);

        public void Record(SqliteConnection connection, int version, string name, DateTimeOffset appliedAt) =>
            inner.Record(connection, version, name, appliedAt);
    }
}
