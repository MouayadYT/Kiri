using System.Text;
using Assistant.Core.Domain;
using Assistant.Core.History;
using Microsoft.Data.Sqlite;

namespace Assistant.Data.Persistence;

/// <summary>
/// Searches the saved conversations by their titles and their messages' text, with SQLite's full-text index. It runs
/// entirely in the local database; nothing here reaches for a model or the network.
/// </summary>
/// <remarks>
/// Each method works on the connection it is given. What a user typed is never part of the SQL text: the words travel
/// as parameters, the full-text syntax is built around them here, and a word is quoted so that it can never be read as
/// an operator.
/// </remarks>
public interface IConversationSearchRepository
{
    /// <summary>
    /// Finds the conversations that contain every word of <paramref name="query"/>, in the title or in any message, most
    /// recently updated first. A word matches text that begins with it, without regard to case or accents; when no
    /// conversation has words that begin so, a word is looked for anywhere in the text, which takes a slower pass over
    /// the messages.
    /// </summary>
    /// <param name="connection">The connection to search on.</param>
    /// <param name="query">What was searched for.</param>
    /// <param name="limit">The most conversations to return.</param>
    IReadOnlyList<ConversationSearchResult> Search(SqliteConnection connection, SearchQuery query, int limit);

    /// <summary>
    /// Merges the index's pieces into one, which is what takes the words of deleted messages out of it: until then a
    /// deleted message's words are still in the pieces it was indexed in. Call it after deleting.
    /// </summary>
    void Compact(SqliteConnection connection);
}

/// <inheritdoc/>
public sealed class ConversationSearchRepository(IConversationRepository conversations) : IConversationSearchRepository
{
    /// <summary>The most results a search returns, whatever limit is asked for.</summary>
    public const int MaxLimit = 500;

    /// <inheritdoc/>
    public IReadOnlyList<ConversationSearchResult> Search(SqliteConnection connection, SearchQuery query, int limit)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);
        if (query.IsEmpty || limit <= 0)
        {
            return [];
        }

        limit = Math.Min(limit, MaxLimit);
        var ids = FindByWords(connection, query, limit);
        var byWords = ids.Count > 0;
        if (!byWords)
        {
            ids = FindBySubstring(connection, query, limit);
        }

        if (ids.Count == 0)
        {
            return [];
        }

        var snippets = FindSnippets(connection, query, ids, byWords);
        return
        [
            .. conversations.List(connection, ids).Select(
                conversation => new ConversationSearchResult(
                    conversation,
                    snippets.GetValueOrDefault(conversation.Id))),
        ];
    }

    /// <inheritdoc/>
    public void Compact(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.Execute("INSERT INTO message_search (message_search) VALUES ('optimize')");
        connection.Execute("INSERT INTO conversation_title_search (conversation_title_search) VALUES ('optimize')");
    }

    // The conversations whose title or messages have a word that begins with each of the query's words.
    private static List<Guid> FindByWords(SqliteConnection connection, SearchQuery query, int limit)
    {
        var sql = new StringBuilder("conversations.id IN (");
        var parameters = new List<(string Name, object? Value)>();
        for (var index = 0; index < query.Terms.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(" INTERSECT ");
            }

            // Only the position of the term is in the text of the SQL; the term itself is a parameter.
            sql.Append(
                $"""
                SELECT conversation_id FROM (
                    SELECT messages.conversation_id AS conversation_id
                    FROM message_search JOIN messages ON messages.seq = message_search.rowid
                    WHERE message_search MATCH $match{index}
                    UNION
                    SELECT conversation_id FROM conversation_title_search
                    WHERE conversation_title_search MATCH $match{index})
                """);
            parameters.Add(($"$match{index}", WordsBeginningWith(query.Terms[index])));
        }

        sql.Append(')');
        try
        {
            return NewestConversations(connection, sql.ToString(), parameters, limit);
        }
        catch (SqliteException)
        {
            // A word the index cannot read as words has no matches there; the slower pass looks for it as text.
            return [];
        }
    }

    // The conversations whose title or messages have each of the query's words anywhere in them.
    private static List<Guid> FindBySubstring(SqliteConnection connection, SearchQuery query, int limit)
    {
        var conditions = Enumerable.Range(0, query.Terms.Count).Select(
            index =>
                $"""
                ({Contains("conversations.title", index)}
                 OR EXISTS (SELECT 1 FROM messages
                            WHERE messages.conversation_id = conversations.id AND {Contains("messages.text", index)}))
                """);
        return NewestConversations(connection, string.Join(" AND ", conditions), FoldedTerms(query), limit);
    }

    // The ids of the conversations that meet the condition, most recently updated first, and no more than the limit.
    private static List<Guid> NewestConversations(
        SqliteConnection connection, string condition, List<(string Name, object? Value)> parameters, int limit) =>
        connection.Query(
            $"SELECT conversations.id FROM conversations WHERE {condition} " +
            $"ORDER BY {ConversationRepository.NewestFirst} LIMIT $limit",
            reader => reader.GetId(0),
            [.. parameters, ("$limit", limit)]);

    // For each conversation, the words around the match in its latest message that has one.
    private static Dictionary<Guid, SearchSnippet> FindSnippets(
        SqliteConnection connection, SearchQuery query, List<Guid> ids, bool byWords)
    {
        var (matching, parameters) = byWords ? MatchingByWords(query) : MatchingBySubstring(query);
        parameters.Add(("$ids", DatabaseIds.ToJsonArray(ids)));

        // The latest message of each conversation that matches, chosen without reading any text; only the chosen one is read.
        var sql =
            $"""
            SELECT chosen.conversation_id, messages.id, messages.text
            FROM (
                SELECT matches.conversation_id AS conversation_id, matches.seq AS seq,
                       ROW_NUMBER() OVER (PARTITION BY matches.conversation_id ORDER BY matches.position DESC) AS place
                FROM ({matching}) AS matches
                WHERE matches.conversation_id IN (SELECT value FROM json_each($ids))
            ) AS chosen
            JOIN messages ON messages.seq = chosen.seq
            WHERE chosen.place = 1
            """;

        var snippets = new Dictionary<Guid, SearchSnippet>();
        connection.Query(
            sql,
            reader =>
            {
                var snippet = SearchSnippets.Build(reader.GetId(1), reader.GetString(2), [.. query.Terms]);
                if (snippet is not null)
                {
                    snippets[reader.GetId(0)] = snippet;
                }

                return 0;
            },
            [.. parameters]);
        return snippets;
    }

    private static (string Sql, List<(string Name, object? Value)> Parameters) MatchingByWords(SearchQuery query) =>
        (
            """
            SELECT messages.conversation_id AS conversation_id, messages.seq AS seq, messages.position AS position
            FROM message_search JOIN messages ON messages.seq = message_search.rowid
            WHERE message_search MATCH $any
            """,
            [("$any", string.Join(" OR ", query.Terms.Select(WordsBeginningWith)))]);

    private static (string Sql, List<(string Name, object? Value)> Parameters) MatchingBySubstring(SearchQuery query)
    {
        var conditions = Enumerable.Range(0, query.Terms.Count).Select(index => Contains("messages.text", index));
        var sql =
            $"""
            SELECT messages.conversation_id AS conversation_id, messages.seq AS seq, messages.position AS position
            FROM messages
            WHERE {string.Join(" OR ", conditions)}
            """;
        return (sql, FoldedTerms(query));
    }

    // The slower pass: whether the column, folded, holds the query's term at that index anywhere. Only the index is in the
    // SQL; the folded term is the parameter FoldedTerms makes for it.
    private static string Contains(string column, int index) => $"instr(fold({column}), $text{index}) > 0";

    // Each of the query's terms, folded the way the fold() function folds the text it is compared with.
    private static List<(string Name, object? Value)> FoldedTerms(SearchQuery query) =>
        [.. query.Terms.Select((term, index) => ($"$text{index}", (object?)TextFolding.Fold(term.Text)))];

    // The full-text query for text whose words begin with the term: a phrase of its words, the last of them a prefix. The
    // term is put in quotes, with any quote inside doubled, so nothing a user typed is read as query syntax.
    private static string WordsBeginningWith(SearchTerm term) => $"\"{term.Text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"*";
}
