using Assistant.Data.Migrations;
using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;

namespace Assistant.Data.Tests;

/// <summary>Small readers and writers that tests use to look at a database directly, with parameters like the app.</summary>
internal static class SqlHelpers
{
    public const string ConversationId = "11111111-1111-1111-1111-111111111111";
    public const string Timestamp = "2026-09-30T10:15:00.0000000Z";

    public static void InsertConversation(this SqliteConnection connection, string id = ConversationId, string title = "Trip plans") =>
        connection.Execute(
            "INSERT INTO conversations (id, title, created_at, updated_at) VALUES ($id, $title, $at, $at)",
            ("$id", id),
            ("$title", title),
            ("$at", Timestamp));

    public static void InsertMessage(
        this SqliteConnection connection,
        string id,
        string conversationId = ConversationId,
        int position = 0,
        string text = "Book the hotel") =>
        connection.Execute(
            "INSERT INTO messages (id, conversation_id, position, role, text, created_at) " +
            "VALUES ($id, $conversation, $position, 'User', $text, $at)",
            ("$id", id),
            ("$conversation", conversationId),
            ("$position", position),
            ("$text", text),
            ("$at", Timestamp));

    public static int Count(this SqliteConnection connection, string table) =>
        connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table}");

    /// <summary>
    /// The tables the schema declares, virtual (full-text) ones included. The tables SQLite's full-text search keeps for
    /// itself (its shadow tables) are not listed: they are its own storage, not part of the app's schema.
    /// </summary>
    public static List<string> Tables(this SqliteConnection connection) =>
        connection.Query(
            "SELECT name FROM pragma_table_list WHERE schema = 'main' AND type IN ('table', 'virtual') " +
            "AND name NOT LIKE 'sqlite_%' ORDER BY name",
            reader => reader.GetString(0));

    public static List<string> Columns(this SqliteConnection connection, string table) =>
        connection.Query("SELECT name FROM pragma_table_info($table) ORDER BY cid", reader => reader.GetString(0), ("$table", table));

    /// <summary>Every version the app's migrations reach, from 1 to the latest.</summary>
    public static List<int> AllMigrationVersions() => [.. Enumerable.Range(1, MigrationCatalog.LoadDefault().LatestVersion)];

    /// <summary>The versions the schema-version table holds, lowest first.</summary>
    public static List<int> AppliedVersions(this SqliteConnection connection) =>
        connection.Query("SELECT version FROM schema_version ORDER BY version", reader => reader.GetInt32(0));

    /// <summary>Opens the file at <paramref name="path"/> read-only, for looking inside a backup.</summary>
    public static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
