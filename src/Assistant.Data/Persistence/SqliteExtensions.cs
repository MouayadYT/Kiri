using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Assistant.Data.Persistence;

/// <summary>
/// The only way repositories run SQL, and how they read back the values the database's conventions wrote (ids, times,
/// enums by name). Every value travels as a parameter, so no query is ever built from text that came from the user, the
/// model or a document.
/// </summary>
internal static class SqliteExtensions
{
    /// <summary>Runs <paramref name="sql"/> and returns the number of rows it changed.</summary>
    public static int Execute(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, sql, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>Runs <paramref name="sql"/> and returns the first column of its first row, or the default when there is none.</summary>
    public static T? ExecuteScalar<T>(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, sql, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull
            ? default
            : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>Runs <paramref name="sql"/> and maps each row with <paramref name="map"/>.</summary>
    public static List<T> Query<T>(
        this SqliteConnection connection,
        string sql,
        Func<SqliteDataReader, T> map,
        params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    /// <summary>Reads the id in column <paramref name="ordinal"/>, written by <see cref="DatabaseIds.ToText"/>.</summary>
    public static Guid GetId(this SqliteDataReader reader, int ordinal) => DatabaseIds.FromText(reader.GetString(ordinal));

    /// <summary>Reads the moment in column <paramref name="ordinal"/>, written by <see cref="DatabaseTimestamps.ToText"/>.</summary>
    public static DateTimeOffset GetTimestamp(this SqliteDataReader reader, int ordinal) =>
        DatabaseTimestamps.FromText(reader.GetString(ordinal));

    /// <summary>Reads the text in column <paramref name="ordinal"/>, or <see langword="null"/> when the column is NULL.</summary>
    public static string? GetStringOrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// Reads the enum value stored by its name in column <paramref name="ordinal"/>. A name this build does not know was
    /// written by a newer one: it returns <see langword="false"/>, and the caller leaves that row out instead of failing
    /// (PROJECT_SPEC §5.9). Only the exact name counts, never a number or another spelling of it.
    /// </summary>
    public static bool TryGetEnum<TEnum>(this SqliteDataReader reader, int ordinal, out TEnum value)
        where TEnum : struct, Enum
    {
        var name = reader.GetString(ordinal);
        return Enum.TryParse(name, ignoreCase: false, out value)
            && Enum.IsDefined(value)
            && string.Equals(value.ToString(), name, StringComparison.Ordinal);
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
