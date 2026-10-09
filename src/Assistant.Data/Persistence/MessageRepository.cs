using System.Security.Cryptography;
using Assistant.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Assistant.Data.Persistence;

/// <summary>
/// The <c>messages</c> table and the rows that belong to a message: its context descriptors and its cards.
/// </summary>
/// <remarks>
/// Each method works on the connection it is given, so it takes part in that connection's transaction. A message is
/// saved one at a time: a message that is new goes after the conversation's last, and one that is saved already is
/// changed where it stands, so a conversation is never written again as a whole to add a turn. Only descriptors are
/// stored for context (a name and, for a file or an image file, where it is), never what the context held, so nothing of
/// a file, a screenshot or a selection is copied into the database (PROJECT_SPEC §3.5).
/// </remarks>
public interface IMessageRepository
{
    /// <summary>
    /// Saves <paramref name="message"/> as a message of the conversation <paramref name="conversationId"/>, which must
    /// exist: a new message is added after the last, and a saved one (the same <see cref="Message.Id"/>) is changed in
    /// place, its context descriptors and cards replaced by the ones it has now. Its tool calls, tool result and sources
    /// are not stored.
    /// </summary>
    /// <returns>The message's place in the conversation, counting from 0.</returns>
    /// <exception cref="DatabaseException">The message is saved in another conversation.</exception>
    /// <exception cref="ArgumentException">A card sits beyond the end of the message's text.</exception>
    int Save(SqliteConnection connection, Guid conversationId, Message message);

    /// <summary>
    /// Replaces the messages of <paramref name="conversationId"/> with <paramref name="messages"/>, in that order. This
    /// rewrites the whole conversation; adding a turn uses <see cref="Save"/>.
    /// </summary>
    void Replace(SqliteConnection connection, Guid conversationId, IReadOnlyList<Message> messages);

    /// <summary>
    /// The messages of the conversation, oldest first, each with its context descriptors and cards. A message, item or
    /// card of a kind this build does not know, which a newer build may have saved, is left out.
    /// </summary>
    IReadOnlyList<Message> GetAll(SqliteConnection connection, Guid conversationId);
}

/// <inheritdoc/>
public sealed class MessageRepository : IMessageRepository
{
    /// <inheritdoc/>
    public int Save(SqliteConnection connection, Guid conversationId, Message message)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(message);
        RequireCardsWithinText(message);

        var conversation = DatabaseIds.ToText(conversationId);
        var saved = connection.Query(
            "SELECT conversation_id, position FROM messages WHERE id = $id",
            reader => (Conversation: reader.GetString(0), Position: reader.GetInt32(1)),
            ("$id", DatabaseIds.ToText(message.Id)));

        int position;
        if (saved.Count == 0)
        {
            position = connection.ExecuteScalar<int>(
                "SELECT COALESCE(MAX(position), -1) + 1 FROM messages WHERE conversation_id = $conversation",
                ("$conversation", conversation));
            Insert(connection, conversationId, position, message);
        }
        else
        {
            if (saved[0].Conversation != conversation)
            {
                throw new DatabaseException("The message is already saved in another conversation.");
            }

            position = saved[0].Position;
            connection.Execute(
                "UPDATE messages SET role = $role, text = $text, created_at = $createdAt, outcome = $outcome WHERE id = $id",
                RowValues(message));
        }

        ReplaceDescriptors(connection, message);
        return position;
    }

    /// <inheritdoc/>
    public void Replace(SqliteConnection connection, Guid conversationId, IReadOnlyList<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(messages);
        foreach (var message in messages)
        {
            RequireCardsWithinText(message);
        }

        // Rows that belong to a message go with it (ON DELETE CASCADE), and the search index follows by trigger.
        connection.Execute(
            "DELETE FROM messages WHERE conversation_id = $conversation",
            ("$conversation", DatabaseIds.ToText(conversationId)));
        for (var position = 0; position < messages.Count; position++)
        {
            Insert(connection, conversationId, position, messages[position]);
            ReplaceDescriptors(connection, messages[position]);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<Message> GetAll(SqliteConnection connection, Guid conversationId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var conversation = ("$conversation", (object?)DatabaseIds.ToText(conversationId));

        var items = connection.Query(
            """
            SELECT message_context_items.message_id, message_context_items.id, message_context_items.type,
                   message_context_items.display_name, message_context_items.file_path
            FROM message_context_items
            JOIN messages ON messages.id = message_context_items.message_id
            WHERE messages.conversation_id = $conversation
            ORDER BY messages.position, message_context_items.position
            """,
            reader => (Message: reader.GetId(0), Item: ReadContextItem(reader, 1)),
            conversation);
        var cards = connection.Query(
            """
            SELECT message_cards.message_id, message_cards.kind, message_cards.data, message_cards.text_offset
            FROM message_cards
            JOIN messages ON messages.id = message_cards.message_id
            WHERE messages.conversation_id = $conversation
            ORDER BY messages.position, message_cards.position
            """,
            reader => (Message: reader.GetId(0), Card: ReadCard(reader)),
            conversation);
        var itemsByMessage = items.Where(row => row.Item is not null).ToLookup(row => row.Message, row => row.Item!);
        var cardsByMessage = cards.Where(row => row.Card is not null).ToLookup(row => row.Message, row => row.Card!);

        var messages = new List<Message>();
        connection.Query(
            "SELECT id, role, text, created_at, outcome FROM messages WHERE conversation_id = $conversation ORDER BY position",
            reader =>
            {
                if (!reader.TryGetEnum<MessageRole>(1, out var role))
                {
                    return 0;
                }

                var id = reader.GetId(0);
                var text = reader.GetString(2);
                messages.Add(new Message(id, role, text, reader.GetTimestamp(3))
                {
                    Outcome = reader.TryGetEnum<MessageOutcome>(4, out var outcome) ? outcome : MessageOutcome.Complete,
                    ContextItems = [.. itemsByMessage[id]],
                    Cards = [.. cardsByMessage[id].Select(card => new CardMetadata(card.Kind, card.DataJson, Math.Min(card.TextOffset, text.Length)))],
                });
                return 0;
            },
            conversation);
        return messages;
    }

    /// <summary>
    /// Reads a context descriptor from the four columns that begin at <paramref name="first"/>: the item's id, type,
    /// display name and file path, as <c>message_context_items</c> holds them. Returns <see langword="null"/> for a type
    /// this build does not know. It reads a message's items, and the image a conversation's card shows.
    /// </summary>
    internal static ContextItem? ReadContextItem(SqliteDataReader reader, int first) =>
        reader.TryGetEnum<ContextItemType>(first + 1, out var type)
            ? new ContextItem(reader.GetId(first), type, reader.GetString(first + 2))
            {
                FilePath = reader.GetStringOrNull(first + 3),
            }
            : null;

    private static void Insert(SqliteConnection connection, Guid conversationId, int position, Message message) =>
        connection.Execute(
            """
            INSERT INTO messages (id, conversation_id, position, role, text, created_at, outcome)
            VALUES ($id, $conversation, $position, $role, $text, $createdAt, $outcome)
            """,
            [.. RowValues(message), ("$conversation", DatabaseIds.ToText(conversationId)), ("$position", position)]);

    // The values of the message's own row, which saving writes whether the message is new or saved already.
    private static (string Name, object? Value)[] RowValues(Message message) =>
    [
        ("$id", DatabaseIds.ToText(message.Id)),
        ("$role", message.Role.ToString()),
        ("$text", message.Text),
        ("$createdAt", DatabaseTimestamps.ToText(message.CreatedAt)),
        ("$outcome", message.Outcome.ToString()),
    ];

    private static void ReplaceDescriptors(SqliteConnection connection, Message message)
    {
        var messageId = DatabaseIds.ToText(message.Id);
        connection.Execute("DELETE FROM message_context_items WHERE message_id = $message", ("$message", messageId));
        connection.Execute("DELETE FROM message_cards WHERE message_id = $message", ("$message", messageId));

        for (var position = 0; position < message.ContextItems.Count; position++)
        {
            var item = message.ContextItems[position];
            connection.Execute(
                """
                INSERT INTO message_context_items (id, message_id, position, type, display_name, file_path)
                VALUES ($id, $message, $position, $type, $name, $path)
                """,
                ("$id", DatabaseIds.ToText(ItemRowId(message.Id, position))),
                ("$message", messageId),
                ("$position", position),
                ("$type", item.Type.ToString()),
                ("$name", item.DisplayName),
                ("$path", item.FilePath));
        }

        for (var position = 0; position < message.Cards.Count; position++)
        {
            var card = message.Cards[position];
            connection.Execute(
                """
                INSERT INTO message_cards (message_id, position, kind, text_offset, data)
                VALUES ($message, $position, $kind, $offset, $data)
                """,
                ("$message", messageId),
                ("$position", position),
                ("$kind", card.Kind),
                ("$offset", card.TextOffset),
                ("$data", card.DataJson));
        }
    }

    // A row's id is made from its message and its place, so the same item attached to two messages, or a message saved
    // again, never collides with another row. The item's own Id is not kept: nothing refers to it once it is saved.
    private static Guid ItemRowId(Guid messageId, int position)
    {
        Span<byte> input = stackalloc byte[20];
        messageId.TryWriteBytes(input);
        BitConverter.TryWriteBytes(input[16..], position);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }

    private static void RequireCardsWithinText(Message message)
    {
        if (message.Cards.Any(card => card.TextOffset > message.Text.Length))
        {
            throw new ArgumentException("A card cannot sit beyond the end of the message's text.", nameof(message));
        }
    }

    private static CardMetadata? ReadCard(SqliteDataReader reader)
    {
        try
        {
            return new CardMetadata(reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
        }
        catch (ArgumentException)
        {
            // Not a card this build can read.
            return null;
        }
    }
}
