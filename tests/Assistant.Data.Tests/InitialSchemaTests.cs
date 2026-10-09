using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Assistant.Data.Tests;

/// <summary>What the first migration builds: its tables, its rules, and what it deliberately leaves out.</summary>
public sealed class InitialSchemaTests : IDisposable
{
    private const int ConstraintError = 19;

    private readonly TestDatabase _database = new();
    private readonly SqliteConnection _connection;

    public InitialSchemaTests() => _connection = _database.Initialize();

    public void Dispose()
    {
        _connection.Dispose();
        _database.Dispose();
    }

    [Fact]
    public void CreatesTheTablesTheAppNeeds()
    {
        Assert.Equal(
            [
                "agent_tasks",
                "conversation_title_search",
                "conversations",
                "message_cards",
                "message_context_items",
                "message_search",
                "messages",
                "people",
                "person_aliases",
                "person_identifiers",
                "person_relationships",
                "schema_version",
                "settings_metadata",
                "task_audit_records",
                "tool_permissions",
            ],
            _connection.Tables());
    }

    [Fact]
    public void EveryTableIsStrict_AndNoColumnCanHoldABlob()
    {
        var lax = _connection.Query(
            "SELECT name FROM pragma_table_list WHERE schema = 'main' AND type = 'table' " +
            "AND name NOT LIKE 'sqlite_%' AND strict = 0",
            reader => reader.GetString(0));
        // The app's own tables; a full-text index keeps binary pieces of itself in shadow tables, which are SQLite's.
        var types = _connection.Query(
            "SELECT DISTINCT c.type FROM pragma_table_list l, pragma_table_info(l.name) c " +
            "WHERE l.schema = 'main' AND l.type = 'table' AND l.name NOT LIKE 'sqlite_%' ORDER BY c.type",
            reader => reader.GetString(0));

        Assert.Empty(lax);

        // No model files, screenshots or other bytes are stored in the database (PROJECT_SPEC §3.5, P7).
        Assert.Equal(["INTEGER", "TEXT"], types);
    }

    [Fact]
    public void AContextItemIsADescriptor_WithNowhereForCapturedContent()
    {
        Assert.Equal(
            ["id", "message_id", "position", "type", "display_name", "file_path"],
            _connection.Columns("message_context_items"));
    }

    [Fact]
    public void AnAuditRecordHoldsNoArgumentsOrOutput()
    {
        Assert.Equal(
            [
                "id", "conversation_id", "tool_name", "risk_level", "outcome", "started_at", "completed_at",
                "task_id", "kind", "step_number", "summary", "confirmation", "error_code",
            ],
            _connection.Columns("task_audit_records"));
    }

    [Fact]
    public void DeletingAConversationDeletesItsMessagesAndTheirContextItems()
    {
        _connection.InsertConversation();
        _connection.InsertConversation("22222222-2222-2222-2222-222222222222", "Other");
        _connection.InsertMessage("m1");
        _connection.InsertMessage("m2", "22222222-2222-2222-2222-222222222222");
        _connection.Execute(
            "INSERT INTO message_context_items (id, message_id, position, type, display_name, file_path) " +
            "VALUES ('c1', 'm1', 0, 'File', 'report.docx', 'C:\\docs\\report.docx')");

        _connection.Execute("DELETE FROM conversations WHERE id = $id", ("$id", SqlHelpers.ConversationId));

        Assert.Equal(1, _connection.Count("conversations"));
        Assert.Equal(["m2"], _connection.Query("SELECT id FROM messages", reader => reader.GetString(0)));
        Assert.Equal(0, _connection.Count("message_context_items"));
    }

    [Fact]
    public void AnAuditRecordOutlivesItsConversation()
    {
        _connection.InsertConversation();
        _connection.Execute(
            "INSERT INTO task_audit_records (id, conversation_id, tool_name, risk_level, outcome, started_at) " +
            "VALUES ('a1', $conversation, 'copy_text', 'SideEffect', 'Succeeded', $at)",
            ("$conversation", SqlHelpers.ConversationId),
            ("$at", SqlHelpers.Timestamp));

        _connection.Execute("DELETE FROM conversations");

        var link = _connection.ExecuteScalar<string>("SELECT conversation_id FROM task_audit_records WHERE id = 'a1'");
        Assert.Equal(1, _connection.Count("task_audit_records"));
        Assert.Null(link);
    }

    [Fact]
    public void DeletingAPersonDeletesTheirRelationships()
    {
        _connection.Execute("INSERT INTO people (id, display_name, created_at, updated_at) VALUES ('p1', 'Sara', $at, $at)", ("$at", SqlHelpers.Timestamp));
        _connection.Execute("INSERT INTO person_relationships (person_id, relationship) VALUES ('p1', 'sister')");

        _connection.Execute("DELETE FROM people WHERE id = 'p1'");

        Assert.Equal(0, _connection.Count("person_relationships"));
    }

    [Fact]
    public void AMessageNeedsAnExistingConversation()
    {
        var exception = Assert.Throws<SqliteException>(() => _connection.InsertMessage("m1", "99999999-9999-9999-9999-999999999999"));

        Assert.Equal(ConstraintError, exception.SqliteErrorCode);
        Assert.Equal(0, _connection.Count("messages"));
    }

    [Fact]
    public void AMessageHasItsOwnPlaceInItsConversation()
    {
        _connection.InsertConversation();
        _connection.InsertMessage("m1", position: 0);

        var exception = Assert.Throws<SqliteException>(() => _connection.InsertMessage("m2", position: 0));

        Assert.Equal(ConstraintError, exception.SqliteErrorCode);
    }

    [Fact]
    public void PositionsAreNotNegative()
    {
        _connection.InsertConversation();

        var exception = Assert.Throws<SqliteException>(() => _connection.InsertMessage("m1", position: -1));

        Assert.Equal(ConstraintError, exception.SqliteErrorCode);
    }

    [Fact]
    public void ColumnsHoldOnlyTheirDeclaredType()
    {
        _connection.InsertConversation();

        var exception = Assert.Throws<SqliteException>(() =>
            _connection.Execute(
                "INSERT INTO messages (id, conversation_id, position, role, text, created_at) " +
                "VALUES ('m1', $conversation, 'first', 'User', 'hi', $at)",
                ("$conversation", SqlHelpers.ConversationId),
                ("$at", SqlHelpers.Timestamp)));

        Assert.Equal(ConstraintError, exception.SqliteErrorCode);
    }

    [Fact]
    public void APersonNeedsANameAndARelationshipNeedsALabel()
    {
        Assert.Throws<SqliteException>(() =>
            _connection.Execute("INSERT INTO people (id, display_name, created_at, updated_at) VALUES ('p1', '   ', $at, $at)", ("$at", SqlHelpers.Timestamp)));

        _connection.Execute("INSERT INTO people (id, display_name, created_at, updated_at) VALUES ('p1', 'Sara', $at, $at)", ("$at", SqlHelpers.Timestamp));
        Assert.Throws<SqliteException>(() =>
            _connection.Execute("INSERT INTO person_relationships (person_id, relationship) VALUES ('p1', ' ')"));
    }

    [Fact]
    public void RelationshipsAreMatchedWithoutRegardToCase()
    {
        _connection.Execute("INSERT INTO people (id, display_name, created_at, updated_at) VALUES ('p1', 'Sara', $at, $at)", ("$at", SqlHelpers.Timestamp));
        _connection.Execute("INSERT INTO people (id, display_name, created_at, updated_at) VALUES ('p2', 'Omar', $at, $at)", ("$at", SqlHelpers.Timestamp));
        _connection.Execute("INSERT INTO person_relationships (person_id, relationship) VALUES ('p1', 'Sister')");
        _connection.Execute("INSERT INTO person_relationships (person_id, relationship) VALUES ('p2', 'brother')");

        var duplicate = Assert.Throws<SqliteException>(() =>
            _connection.Execute("INSERT INTO person_relationships (person_id, relationship) VALUES ('p1', 'SISTER')"));
        var found = _connection.Query(
            "SELECT p.display_name FROM people p JOIN person_relationships r ON r.person_id = p.id WHERE r.relationship = $label",
            reader => reader.GetString(0),
            ("$label", "sister"));

        Assert.Equal(ConstraintError, duplicate.SqliteErrorCode);
        Assert.Equal(["Sara"], found);
    }

    [Fact]
    public void ARetentionCutOffIsAPlainTextComparison()
    {
        var now = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.FromHours(3));
        foreach (var (id, age) in new[] { ("old", 40), ("recent", 5) })
        {
            var at = DatabaseTimestamps.ToText(now.AddDays(-age));
            _connection.Execute(
                "INSERT INTO conversations (id, title, created_at, updated_at) VALUES ($id, '', $at, $at)",
                ("$id", id),
                ("$at", at));
        }

        var deleted = _connection.Execute(
            "DELETE FROM conversations WHERE updated_at < $cutoff",
            ("$cutoff", DatabaseTimestamps.ToText(now.AddDays(-30))));

        Assert.Equal(1, deleted);
        Assert.Equal(["recent"], _connection.Query("SELECT id FROM conversations", reader => reader.GetString(0)));
    }

    [Fact]
    public void SettingsMetadataAndToolPermissionsAreKeyedByName()
    {
        _connection.Execute("INSERT INTO settings_metadata (key, value, updated_at) VALUES ('settings_schema', '1', $at)", ("$at", SqlHelpers.Timestamp));
        _connection.Execute("INSERT INTO tool_permissions (tool_name, decision, decided_at) VALUES ('copy_text', 'Allow', $at)", ("$at", SqlHelpers.Timestamp));

        Assert.Throws<SqliteException>(() =>
            _connection.Execute("INSERT INTO settings_metadata (key, value, updated_at) VALUES ('settings_schema', '2', $at)", ("$at", SqlHelpers.Timestamp)));
        Assert.Throws<SqliteException>(() =>
            _connection.Execute("INSERT INTO tool_permissions (tool_name, decision, decided_at) VALUES ('copy_text', 'Deny', $at)", ("$at", SqlHelpers.Timestamp)));
    }
}
