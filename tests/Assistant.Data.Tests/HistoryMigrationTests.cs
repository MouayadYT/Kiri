using Assistant.Core.Domain;
using Assistant.Data.Migrations;
using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Assistant.Data.Tests;

/// <summary>The migrations that add saved conversations (2) and their search (3), from a database that is already in use.</summary>
public sealed class HistoryMigrationTests : IDisposable
{
    private const string FirstConversation = "11111111-1111-1111-1111-111111111111";
    private const string SecondConversation = "22222222-2222-2222-2222-222222222222";

    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private static MigrationCatalog UpTo(int version) =>
        new(MigrationCatalog.LoadDefault().Migrations.Where(migration => migration.Version <= version));

    // A database as version 1 left it, with two conversations of messages, a context descriptor and an audit record.
    private SqliteConnection CreateVersionOneWithHistory()
    {
        var connection = _database.Open();
        _database.CreateMigrator(UpTo(1)).Migrate(connection);
        connection.InsertConversation(FirstConversation, "Trip plans");
        connection.InsertConversation(SecondConversation, "Recipes");
        connection.InsertMessage("m1", FirstConversation, 0, "Book the hotel in Lisbon");
        connection.InsertMessage("m2", FirstConversation, 1, "Near the river please");
        connection.InsertMessage("m3", SecondConversation, 0, "Pasta with spinach");
        connection.Execute(
            "INSERT INTO message_context_items (id, message_id, position, type, display_name, file_path) " +
            "VALUES ('c1', 'm1', 0, 'File', 'itinerary.docx', 'C:\\docs\\itinerary.docx')");
        connection.Execute(
            "INSERT INTO task_audit_records (id, conversation_id, tool_name, risk_level, outcome, started_at) " +
            "VALUES ('a1', $c, 'copy_text', 'SideEffect', 'Succeeded', $at)",
            ("$c", FirstConversation),
            ("$at", SqlHelpers.Timestamp));
        return connection;
    }

    [Fact]
    public void UpgradingKeepsEveryRowAndEveryLink()
    {
        using var connection = CreateVersionOneWithHistory();

        var result = _database.CreateMigrator(MigrationCatalog.LoadDefault()).Migrate(connection);

        Assert.Equal(1, result.PreviousVersion);
        Assert.Equal(MigrationCatalog.LoadDefault().LatestVersion, result.CurrentVersion);
        Assert.NotNull(result.BackupPath);
        Assert.Equal(SqlHelpers.AllMigrationVersions(), connection.AppliedVersions());
        Assert.Equal(
            [("m1", 0, "Book the hotel in Lisbon"), ("m2", 1, "Near the river please"), ("m3", 0, "Pasta with spinach")],
            connection.Query(
                "SELECT id, position, text FROM messages ORDER BY conversation_id, position",
                reader => (reader.GetString(0), reader.GetInt32(1), reader.GetString(2))));
        Assert.Equal("m1", connection.ExecuteScalar<string>("SELECT message_id FROM message_context_items WHERE id = 'c1'"));
        Assert.Empty(connection.Query("PRAGMA foreign_key_check", _ => 0));
        Assert.Equal(1, connection.ExecuteScalar<int>("PRAGMA foreign_keys"));
        Assert.Equal(1, connection.Count("task_audit_records"));
        Assert.All(
            connection.Query("SELECT outcome FROM messages", reader => reader.GetString(0)),
            outcome => Assert.Equal("Complete", outcome));
    }

    [Fact]
    public void UpgradingIndexesTheMessagesAndTitlesThatWereAlreadySaved()
    {
        using var connection = CreateVersionOneWithHistory();

        _database.CreateMigrator(MigrationCatalog.LoadDefault()).Migrate(connection);

        Assert.Equal([FirstConversation], MessageHits(connection, "lisbon"));
        Assert.Equal([SecondConversation], MessageHits(connection, "spinach"));
        Assert.Equal([SecondConversation], TitleHits(connection, "recipes"));
        Assert.Equal([FirstConversation], TitleHits(connection, "trip"));
        connection.Execute("INSERT INTO message_search (message_search, rank) VALUES ('integrity-check', 1)");
    }

    [Fact]
    public void AfterUpgradingEveryKindOfChangeKeepsTheIndexInStep()
    {
        using var connection = CreateVersionOneWithHistory();
        _database.CreateMigrator(MigrationCatalog.LoadDefault()).Migrate(connection);

        connection.InsertMessage("m4", FirstConversation, 2, "Dinner at nine");
        Assert.Equal([FirstConversation], MessageHits(connection, "dinner"));

        connection.Execute("UPDATE messages SET text = 'Lunch at noon' WHERE id = 'm4'");
        Assert.Empty(MessageHits(connection, "dinner"));
        Assert.Equal([FirstConversation], MessageHits(connection, "lunch"));

        connection.Execute("DELETE FROM messages WHERE id = 'm4'");
        Assert.Empty(MessageHits(connection, "lunch"));

        connection.Execute("UPDATE conversations SET title = 'Holiday' WHERE id = $id", ("$id", FirstConversation));
        Assert.Empty(TitleHits(connection, "trip"));
        Assert.Equal([FirstConversation], TitleHits(connection, "holiday"));

        // A cascade fires the same triggers: nothing of the conversation stays in either index.
        connection.Execute("DELETE FROM conversations WHERE id = $id", ("$id", FirstConversation));
        Assert.Empty(MessageHits(connection, "lisbon"));
        Assert.Empty(TitleHits(connection, "holiday"));
        connection.Execute("INSERT INTO message_search (message_search, rank) VALUES ('integrity-check', 1)");
    }

    [Fact]
    public void AMessageHasAnExplicitIntegerKey_SoTheIndexSurvivesVacuum()
    {
        using var connection = CreateVersionOneWithHistory();
        _database.CreateMigrator(MigrationCatalog.LoadDefault()).Migrate(connection);
        var key = connection.Query(
            "SELECT type, pk FROM pragma_table_info('messages') WHERE name = 'seq'",
            reader => (reader.GetString(0), reader.GetInt32(1)));
        Assert.Equal([("INTEGER", 1)], key);

        // Rows are deleted and added so a vacuum has holes to close, then the database is vacuumed and copied the way a
        // backup is: the index must still name the right messages.
        connection.InsertMessage("m4", FirstConversation, 2, "Temporary words");
        connection.InsertMessage("m5", FirstConversation, 3, "More temporary words");
        connection.Execute("DELETE FROM messages WHERE id IN ('m1', 'm4')");
        connection.Execute("VACUUM");

        Assert.Equal([FirstConversation], MessageHits(connection, "river"));
        Assert.Equal([FirstConversation], MessageHits(connection, "more"));
        Assert.Empty(MessageHits(connection, "lisbon"));
        connection.Execute("INSERT INTO message_search (message_search, rank) VALUES ('integrity-check', 1)");

        var copy = Path.Combine(_database.Root, "copy.db");
        connection.Execute("VACUUM INTO $path", ("$path", copy));

        // Opened for writing: the index's own check is written as an insert, which a read-only file refuses.
        using var opened = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        opened.Open();
        Assert.Equal([FirstConversation], MessageHits(opened, "river"));
        opened.Execute("INSERT INTO message_search (message_search, rank) VALUES ('integrity-check', 1)");
    }

    [Fact]
    public void ANewDatabaseIsCreatedAtTheLatestVersionAtOnce_WithoutABackup()
    {
        using var connection = _database.Open();

        var result = _database.CreateMigrator(MigrationCatalog.LoadDefault()).Migrate(connection);

        Assert.Equal(0, result.PreviousVersion);
        Assert.Equal(MigrationCatalog.LoadDefault().LatestVersion, result.CurrentVersion);
        Assert.Null(result.BackupPath);
        Assert.Empty(_database.BackupFiles());
        Assert.Contains("message_cards", connection.Tables());
        Assert.Contains("outcome", connection.Columns("messages"));
    }

    [Fact]
    public void CardsHaveNowhereToHoldContentOnlyAKindAPlaceAndData()
    {
        using var connection = _database.Initialize();

        Assert.Equal(["message_id", "position", "kind", "text_offset", "data"], connection.Columns("message_cards"));
    }

    [Fact]
    public void ACardsDataMustBeJson_AndItsKindAShortName()
    {
        using var connection = _database.Initialize();
        connection.InsertConversation();
        connection.InsertMessage("m1");

        Assert.Throws<SqliteException>(() => connection.Execute(
            "INSERT INTO message_cards (message_id, position, kind, text_offset, data) VALUES ('m1', 0, 'code', 0, 'not json {')"));
        Assert.Throws<SqliteException>(() => connection.Execute(
            "INSERT INTO message_cards (message_id, position, kind, text_offset, data) VALUES ('m1', 0, '', 0, '{}')"));
        Assert.Throws<SqliteException>(() => connection.Execute(
            "INSERT INTO message_cards (message_id, position, kind, text_offset, data) VALUES ('m1', 0, 'code', -1, '{}')"));
        connection.Execute("INSERT INTO message_cards (message_id, position, kind, text_offset, data) VALUES ('m1', 0, 'code', 0, '{}')");
        Assert.Throws<SqliteException>(() => connection.Execute(
            "INSERT INTO message_cards (message_id, position, kind, text_offset, data) VALUES ('m1', 0, 'code', 0, '{}')"));
    }

    [Fact]
    public void ACardGoesWithItsMessage()
    {
        using var connection = _database.Initialize();
        connection.InsertConversation();
        connection.InsertMessage("m1");
        connection.Execute("INSERT INTO message_cards (message_id, position, kind, text_offset, data) VALUES ('m1', 0, 'code', 0, '{}')");

        connection.Execute("DELETE FROM conversations");

        Assert.Equal(0, connection.Count("message_cards"));
    }

    [Fact]
    public void AFailedUpgradeLeavesTheVersionOneDatabaseAsItWas()
    {
        using var connection = CreateVersionOneWithHistory();
        var broken = new Migration(3, "history_search", "CREATE VIRTUAL TABLE message_search USING fts5(text, tokenize = 'no_such_tokenizer')");
        var catalog = new MigrationCatalog([.. MigrationCatalog.LoadDefault().Migrations.Take(2), broken]);

        Assert.Throws<DatabaseException>(() => _database.CreateMigrator(catalog).Migrate(connection));

        Assert.Equal([1], connection.AppliedVersions());
        Assert.Equal(3, connection.Count("messages"));
        Assert.DoesNotContain("outcome", connection.Columns("messages"));
        Assert.Equal(1, connection.ExecuteScalar<int>("PRAGMA foreign_keys"));
    }

    private static List<string> MessageHits(SqliteConnection connection, string word) =>
        connection.Query(
            "SELECT DISTINCT messages.conversation_id FROM message_search JOIN messages ON messages.seq = message_search.rowid " +
            "WHERE message_search MATCH $q ORDER BY 1",
            reader => reader.GetString(0),
            ("$q", $"\"{word}\"*"));

    private static List<string> TitleHits(SqliteConnection connection, string word) =>
        connection.Query(
            "SELECT conversation_id FROM conversation_title_search WHERE conversation_title_search MATCH $q ORDER BY 1",
            reader => reader.GetString(0),
            ("$q", $"\"{word}\"*"));
}
