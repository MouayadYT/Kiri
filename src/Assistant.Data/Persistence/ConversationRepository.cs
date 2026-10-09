using Assistant.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Assistant.Data.Persistence;

/// <summary>The <c>conversations</c> table: the rows that hold a conversation's title and times.</summary>
/// <remarks>
/// Each method works on the connection it is given, so it takes part in that connection's transaction. Titles and
/// message text are private content (PROJECT_SPEC §3.2): they travel only as parameters and are never logged.
/// </remarks>
public interface IConversationRepository
{
    /// <summary>Whether the conversation <paramref name="id"/> is saved.</summary>
    bool Exists(SqliteConnection connection, Guid id);

    /// <summary>
    /// Makes sure the conversation <paramref name="id"/> exists, with no title, starting at <paramref name="createdAt"/>,
    /// and records that it changed at <paramref name="changedAt"/>. A conversation that exists only has its update time
    /// raised: it never goes back in time.
    /// </summary>
    /// <returns><see langword="true"/> when the conversation did not exist and was created.</returns>
    bool Touch(SqliteConnection connection, Guid id, DateTimeOffset createdAt, DateTimeOffset changedAt);

    /// <summary>Creates the conversation, or replaces its title and times when it exists. Its messages are left alone.</summary>
    void Upsert(SqliteConnection connection, Conversation conversation);

    /// <summary>Gives the conversation <paramref name="title"/> if it has none.</summary>
    /// <returns><see langword="true"/> when the title was set.</returns>
    bool SetTitleIfEmpty(SqliteConnection connection, Guid id, string title);

    /// <summary>Sets the title of the conversation <paramref name="id"/>.</summary>
    /// <returns><see langword="true"/> when the conversation exists.</returns>
    bool Rename(SqliteConnection connection, Guid id, string title);

    /// <summary>
    /// The conversation's title and times, without messages, or <see langword="null"/> when it does not exist.
    /// </summary>
    Conversation? Get(SqliteConnection connection, Guid id);

    /// <summary>
    /// Lists conversations, most recently updated first, each with what a card or a row shows of its messages. With
    /// <paramref name="only"/>, just those of them that exist.
    /// </summary>
    IReadOnlyList<ConversationSummary> List(SqliteConnection connection, IReadOnlyCollection<Guid>? only = null);

    /// <summary>Deletes the conversation, and with it its messages, their context descriptors and cards.</summary>
    /// <returns><see langword="true"/> when the conversation existed.</returns>
    bool Delete(SqliteConnection connection, Guid id);

    /// <summary>Deletes every conversation. Returns how many there were.</summary>
    int DeleteAll(SqliteConnection connection);
}

/// <inheritdoc/>
public sealed class ConversationRepository : IConversationRepository
{
    /// <summary>
    /// The order conversations are listed and found in: most recently updated first, then by id so that two changed at
    /// the same moment always come in the same order.
    /// </summary>
    internal const string NewestFirst = "conversations.updated_at DESC, conversations.id";

    // A preview shows a few lines of an answer; this much of the text is enough to find them past a block of code.
    private const string LatestAnswerLength = "4000";

    // The image most recently attached to each conversation: the last image descriptor of its last message that has one.
    // Only images that are files have a path to read them from again.
    private const string ListSql =
        $"""
        WITH latest_image AS (
            SELECT messages.conversation_id AS conversation_id,
                   message_context_items.id AS id,
                   message_context_items.type AS type,
                   message_context_items.display_name AS display_name,
                   message_context_items.file_path AS file_path,
                   ROW_NUMBER() OVER (
                       PARTITION BY messages.conversation_id
                       ORDER BY messages.position DESC, message_context_items.position DESC) AS place
            FROM message_context_items
            JOIN messages ON messages.id = message_context_items.message_id
            WHERE message_context_items.type = 'Image' AND message_context_items.file_path IS NOT NULL
        )
        SELECT conversations.id,
               conversations.title,
               conversations.created_at,
               conversations.updated_at,
               (SELECT COUNT(*) FROM messages WHERE messages.conversation_id = conversations.id),
               (SELECT substr(messages.text, 1, {LatestAnswerLength}) FROM messages
                 WHERE messages.conversation_id = conversations.id AND messages.role = 'Assistant' AND messages.text <> ''
                 ORDER BY messages.position DESC LIMIT 1),
               latest_image.id,
               latest_image.type,
               latest_image.display_name,
               latest_image.file_path
        FROM conversations
        LEFT JOIN latest_image ON latest_image.conversation_id = conversations.id AND latest_image.place = 1
        """;

    private const string ListOrder = $" ORDER BY {NewestFirst}";

    private const string ListOnly = " WHERE conversations.id IN (SELECT value FROM json_each($ids))";

    /// <inheritdoc/>
    public bool Exists(SqliteConnection connection, Guid id)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ExecuteScalar<int>(
            "SELECT EXISTS (SELECT 1 FROM conversations WHERE id = $id)",
            ("$id", DatabaseIds.ToText(id))) != 0;
    }

    /// <inheritdoc/>
    public bool Touch(SqliteConnection connection, Guid id, DateTimeOffset createdAt, DateTimeOffset changedAt)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var created = !Exists(connection, id);
        connection.Execute(
            """
            INSERT INTO conversations (id, title, created_at, updated_at) VALUES ($id, '', $createdAt, $changedAt)
            ON CONFLICT (id) DO UPDATE SET updated_at = MAX(updated_at, excluded.updated_at)
            """,
            ("$id", DatabaseIds.ToText(id)),
            ("$createdAt", DatabaseTimestamps.ToText(createdAt)),
            ("$changedAt", DatabaseTimestamps.ToText(changedAt)));
        return created;
    }

    /// <inheritdoc/>
    public void Upsert(SqliteConnection connection, Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(conversation);
        connection.Execute(
            """
            INSERT INTO conversations (id, title, created_at, updated_at) VALUES ($id, $title, $createdAt, $updatedAt)
            ON CONFLICT (id) DO UPDATE SET title = excluded.title, created_at = excluded.created_at, updated_at = excluded.updated_at
            """,
            ("$id", DatabaseIds.ToText(conversation.Id)),
            ("$title", conversation.Title),
            ("$createdAt", DatabaseTimestamps.ToText(conversation.CreatedAt)),
            ("$updatedAt", DatabaseTimestamps.ToText(conversation.UpdatedAt)));
    }

    /// <inheritdoc/>
    public bool SetTitleIfEmpty(SqliteConnection connection, Guid id, string title)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(title);
        return connection.Execute(
            "UPDATE conversations SET title = $title WHERE id = $id AND title = ''",
            ("$id", DatabaseIds.ToText(id)),
            ("$title", title)) > 0;
    }

    /// <inheritdoc/>
    public bool Rename(SqliteConnection connection, Guid id, string title)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(title);
        return connection.Execute(
            "UPDATE conversations SET title = $title WHERE id = $id",
            ("$id", DatabaseIds.ToText(id)),
            ("$title", title)) > 0;
    }

    /// <inheritdoc/>
    public Conversation? Get(SqliteConnection connection, Guid id)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Query(
                "SELECT id, title, created_at, updated_at FROM conversations WHERE id = $id",
                reader => new Conversation(reader.GetId(0), reader.GetString(1), reader.GetTimestamp(2), reader.GetTimestamp(3)),
                ("$id", DatabaseIds.ToText(id)))
            .FirstOrDefault();
    }

    /// <inheritdoc/>
    public IReadOnlyList<ConversationSummary> List(SqliteConnection connection, IReadOnlyCollection<Guid>? only = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (only is null)
        {
            return connection.Query(ListSql + ListOrder, ReadSummary);
        }

        if (only.Count == 0)
        {
            return [];
        }

        return connection.Query(ListSql + ListOnly + ListOrder, ReadSummary, ("$ids", DatabaseIds.ToJsonArray(only)));
    }

    /// <inheritdoc/>
    public bool Delete(SqliteConnection connection, Guid id)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Execute("DELETE FROM conversations WHERE id = $id", ("$id", DatabaseIds.ToText(id))) > 0;
    }

    /// <inheritdoc/>
    public int DeleteAll(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Execute("DELETE FROM conversations");
    }

    private static ConversationSummary ReadSummary(SqliteDataReader reader) =>
        new(reader.GetId(0), reader.GetString(1), reader.GetTimestamp(2), reader.GetTimestamp(3), reader.GetInt32(4))
        {
            LatestAnswerText = reader.GetStringOrNull(5),
            LatestImage = reader.IsDBNull(6) ? null : MessageRepository.ReadContextItem(reader, 6),
        };
}
