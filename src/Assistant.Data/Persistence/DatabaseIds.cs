using System.Text.Json;

namespace Assistant.Data.Persistence;

/// <summary>
/// How the database writes an id: a Guid as lower-case text with hyphens (<c>0f8fad5b-d9cb-469f-a165-70867728950e</c>,
/// PROJECT_SPEC §5.9). Every repository writes and reads ids through here, so the same id is always the same text.
/// </summary>
internal static class DatabaseIds
{
    /// <summary>Writes <paramref name="id"/> in the database's format.</summary>
    public static string ToText(Guid id) => id.ToString("D");

    /// <summary>Reads an id written by <see cref="ToText"/>.</summary>
    /// <exception cref="FormatException"><paramref name="text"/> is not an id.</exception>
    public static Guid FromText(string text) => Guid.Parse(text);

    /// <summary>
    /// Writes <paramref name="ids"/> as one parameter: a JSON array of ids in the database's format, which the SQL reads
    /// back as rows with <c>json_each</c> (<c>id IN (SELECT value FROM json_each($ids))</c>). A list of any length stays a
    /// single parameter and never becomes part of the SQL text.
    /// </summary>
    public static string ToJsonArray(IEnumerable<Guid> ids) => JsonSerializer.Serialize(ids.Select(ToText));
}
