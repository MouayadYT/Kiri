using Assistant.Data.Migrations;
using Assistant.Data.Persistence;
using Xunit;

namespace Assistant.Data.Tests;

/// <summary>Stored content is private (PROJECT_SPEC §3.2): the migrator and the backups never read it into a log or a message.</summary>
public sealed class DatabasePrivacyTests : IDisposable
{
    private const string Secret = "my-private-diagnosis-notes";

    private static readonly Migration Initial = MigrationCatalog.LoadDefault().Migrations[0];

    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public void LoggingAnUpgradeMentionsVersionsOnly()
    {
        var logs = new CapturingLoggerProvider();
        using var loggers = logs.CreateFactory();
        using var connection = _database.Open();
        _database.CreateMigrator(new MigrationCatalog([Initial]), loggers).Migrate(connection);
        connection.InsertConversation(title: Secret);
        connection.InsertMessage("m1", text: Secret);
        var upgrade = new Migration(2, "add_note", "ALTER TABLE conversations ADD COLUMN note TEXT NOT NULL DEFAULT ''");
        var broken = new Migration(3, "broken", $"INSERT INTO no_such_table VALUES ('{Secret}')");

        _database.CreateMigrator(new MigrationCatalog([Initial, upgrade]), loggers).Migrate(connection);
        _database.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Throws<DatabaseException>(() =>
            _database.CreateMigrator(new MigrationCatalog([Initial, upgrade, broken]), loggers).Migrate(connection));
        _database.CreateBackup(loggers).DeleteAll();

        var text = logs.AllText;
        Assert.Contains("Upgraded the database from schema version 0 to 1", text, StringComparison.Ordinal);
        Assert.Contains("Backed up the database at schema version 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(_database.Root, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DatabaseOptions.DatabaseFileName, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailureMessagesCarryNeitherContentNorPathsNorSql()
    {
        using var connection = _database.Open();
        _database.CreateMigrator(new MigrationCatalog([Initial])).Migrate(connection);
        connection.InsertConversation(title: Secret);
        var broken = new Migration(2, "broken", $"INSERT INTO no_such_table VALUES ('{Secret}')");
        var messages = new List<string>();

        messages.Add(Assert.Throws<DatabaseException>(() =>
            _database.CreateMigrator(new MigrationCatalog([Initial, broken])).Migrate(connection)).Message);
        Directory.Delete(_database.Options.BackupsDirectory, recursive: true);
        File.WriteAllText(_database.Options.BackupsDirectory, "in the way");
        _database.Time.Advance(TimeSpan.FromMinutes(1));
        messages.Add(Assert.Throws<DatabaseException>(() =>
            _database.CreateMigrator(new MigrationCatalog([Initial, broken])).Migrate(connection)).Message);
        messages.Add(Assert.Throws<DatabaseTooNewException>(() =>
            _database.CreateMigrator(new MigrationCatalog([Initial])).Migrate(RecordFutureVersion(connection))).Message);

        Assert.Equal(3, messages.Count);
        Assert.All(messages, message =>
        {
            Assert.DoesNotContain(Secret, message, StringComparison.Ordinal);
            Assert.DoesNotContain("no_such_table", message, StringComparison.Ordinal);
            Assert.DoesNotContain(_database.Root, message, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static Microsoft.Data.Sqlite.SqliteConnection RecordFutureVersion(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        new SchemaVersionRepository().Record(connection, 9, "from_the_future", DateTimeOffset.UnixEpoch);
        return connection;
    }
}
