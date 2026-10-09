using Assistant.Core.People;
using Microsoft.Data.Sqlite;

namespace Assistant.Data.Persistence;

/// <summary>
/// The <c>people</c> table and what belongs to a person: their relationships, aliases and identifiers (PROJECT_SPEC §3.5, step 112).
/// </summary>
/// <remarks>
/// Each method works on the connection it is given, so it takes part in that connection's transaction. Names, aliases and addresses are private
/// content (PROJECT_SPEC §3.2): they travel only as parameters and are never logged. A person is written as one: the row, then its parts replaced
/// together. A part with a kind this build does not know was written by a newer one and is left out when reading, not failed on.
/// </remarks>
public interface IPersonRepository
{
    /// <summary>Every person, ordered by name, with their relationships, aliases and identifiers.</summary>
    IReadOnlyList<Person> List(SqliteConnection connection);

    /// <summary>The person <paramref name="id"/>, or <see langword="null"/> when there is none.</summary>
    Person? Get(SqliteConnection connection, Guid id);

    /// <summary>How many people there are.</summary>
    int Count(SqliteConnection connection);

    /// <summary>
    /// Adds <paramref name="person"/>, or replaces the name, relationships, aliases and identifiers of the one with the same id. A person who
    /// exists keeps the time they were created at. <paramref name="person"/> must already be tidy (<see cref="PersonRules.Normalize"/>).
    /// </summary>
    void Upsert(SqliteConnection connection, Person person);

    /// <summary>Deletes the person and, with them, everything that belongs to them. Returns whether they existed.</summary>
    bool Delete(SqliteConnection connection, Guid id);
}

/// <inheritdoc/>
public sealed class PersonRepository : IPersonRepository
{
    private const string PersonOrder = " ORDER BY display_name COLLATE NOCASE, id";

    /// <inheritdoc/>
    public IReadOnlyList<Person> List(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var people = connection.Query("SELECT id, display_name, created_at, updated_at FROM people" + PersonOrder, ReadPerson);
        if (people.Count == 0)
        {
            return people;
        }

        var relationships = Group(connection, "SELECT person_id, relationship FROM person_relationships ORDER BY relationship COLLATE NOCASE", reader => reader.GetString(1));
        var aliases = Group(connection, "SELECT person_id, alias FROM person_aliases ORDER BY alias COLLATE NOCASE", reader => reader.GetString(1));
        var identifiers = Group(
            connection,
            "SELECT person_id, kind, service, value FROM person_identifiers ORDER BY kind, service COLLATE NOCASE, value COLLATE NOCASE",
            ReadIdentifier);
        return
        [
            .. people
                .Select(person => PersonRules.Arrange(person with
                {
                    Relationships = relationships.GetValueOrDefault(person.Id) ?? [],
                    Aliases = aliases.GetValueOrDefault(person.Id) ?? [],
                    Identifiers = identifiers.GetValueOrDefault(person.Id) ?? [],
                }))
                .OrderBy(person => person.DisplayName, PersonRules.NameOrder)
                .ThenBy(person => person.Id),
        ];
    }

    /// <inheritdoc/>
    public Person? Get(SqliteConnection connection, Guid id)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var text = ("$id", (object?)DatabaseIds.ToText(id));
        var person = connection.Query("SELECT id, display_name, created_at, updated_at FROM people WHERE id = $id", ReadPerson, text).FirstOrDefault();
        if (person is null)
        {
            return null;
        }

        return PersonRules.Arrange(person with
        {
            Relationships = connection.Query("SELECT relationship FROM person_relationships WHERE person_id = $id ORDER BY relationship COLLATE NOCASE", reader => reader.GetString(0), text),
            Aliases = connection.Query("SELECT alias FROM person_aliases WHERE person_id = $id ORDER BY alias COLLATE NOCASE", reader => reader.GetString(0), text),
            Identifiers = connection
                .Query(
                    "SELECT kind, service, value FROM person_identifiers WHERE person_id = $id ORDER BY kind, service COLLATE NOCASE, value COLLATE NOCASE",
                    reader => ReadIdentifier(reader, 0),
                    text)
                .OfType<PersonIdentifier>()
                .ToList(),
        });
    }

    /// <inheritdoc/>
    public int Count(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM people");
    }

    /// <inheritdoc/>
    public void Upsert(SqliteConnection connection, Person person)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(person);
        var id = DatabaseIds.ToText(person.Id);
        connection.Execute(
            """
            INSERT INTO people (id, display_name, created_at, updated_at) VALUES ($id, $name, $createdAt, $updatedAt)
            ON CONFLICT (id) DO UPDATE SET display_name = excluded.display_name, updated_at = excluded.updated_at
            """,
            ("$id", id),
            ("$name", person.DisplayName),
            ("$createdAt", DatabaseTimestamps.ToText(person.CreatedAt)),
            ("$updatedAt", DatabaseTimestamps.ToText(person.UpdatedAt)));

        connection.Execute("DELETE FROM person_relationships WHERE person_id = $id", ("$id", id));
        connection.Execute("DELETE FROM person_aliases WHERE person_id = $id", ("$id", id));
        connection.Execute("DELETE FROM person_identifiers WHERE person_id = $id", ("$id", id));
        foreach (var relationship in person.Relationships)
        {
            connection.Execute(
                "INSERT INTO person_relationships (person_id, relationship) VALUES ($id, $relationship)", ("$id", id), ("$relationship", relationship));
        }

        foreach (var alias in person.Aliases)
        {
            connection.Execute("INSERT INTO person_aliases (person_id, alias) VALUES ($id, $alias)", ("$id", id), ("$alias", alias));
        }

        foreach (var identifier in person.Identifiers)
        {
            connection.Execute(
                "INSERT INTO person_identifiers (person_id, kind, service, value) VALUES ($id, $kind, $service, $value)",
                ("$id", id),
                ("$kind", identifier.Kind.ToString()),
                ("$service", identifier.Service),
                ("$value", identifier.Value));
        }
    }

    /// <inheritdoc/>
    public bool Delete(SqliteConnection connection, Guid id)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Execute("DELETE FROM people WHERE id = $id", ("$id", DatabaseIds.ToText(id))) > 0;
    }

    private static Person ReadPerson(SqliteDataReader reader) =>
        new(reader.GetId(0), reader.GetString(1), reader.GetTimestamp(2), reader.GetTimestamp(3));

    // Reads an identifier whose kind starts at column <paramref name="first"/>; null when the kind is one a newer build wrote.
    private static PersonIdentifier? ReadIdentifier(SqliteDataReader reader, int first) =>
        reader.TryGetEnum<PersonIdentifierKind>(first, out var kind) ? new PersonIdentifier(kind, reader.GetString(first + 2), reader.GetString(first + 1)) : null;

    private static Dictionary<Guid, List<string>> Group(SqliteConnection connection, string sql, Func<SqliteDataReader, string> read)
    {
        var groups = new Dictionary<Guid, List<string>>();
        foreach (var (person, value) in connection.Query(sql, reader => (reader.GetId(0), read(reader))))
        {
            if (!groups.TryGetValue(person, out var list))
            {
                groups[person] = list = [];
            }

            list.Add(value);
        }

        return groups;
    }

    private static Dictionary<Guid, List<PersonIdentifier>> Group(SqliteConnection connection, string sql, Func<SqliteDataReader, int, PersonIdentifier?> read)
    {
        var groups = new Dictionary<Guid, List<PersonIdentifier>>();
        foreach (var (person, value) in connection.Query(sql, reader => (reader.GetId(0), read(reader, 1))))
        {
            if (value is null)
            {
                continue;
            }

            if (!groups.TryGetValue(person, out var list))
            {
                groups[person] = list = [];
            }

            list.Add(value);
        }

        return groups;
    }
}
