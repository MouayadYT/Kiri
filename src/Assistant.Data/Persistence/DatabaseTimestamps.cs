using System.Globalization;

namespace Assistant.Data.Persistence;

/// <summary>
/// How the database writes a moment in time: UTC ISO-8601 text with a fixed seven fractional digits
/// (<c>2026-09-30T10:15:00.0000000Z</c>), so that comparing two values as text orders them in time and a retention
/// cut-off is a plain <c>&lt;</c>. The offset a value came with is not kept; display converts to local time.
/// </summary>
internal static class DatabaseTimestamps
{
    private const string Format = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'";

    /// <summary>Writes <paramref name="value"/> in the database's format.</summary>
    public static string ToText(DateTimeOffset value) => value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>Reads a value written by <see cref="ToText"/>.</summary>
    /// <exception cref="FormatException"><paramref name="text"/> is not in the database's format.</exception>
    public static DateTimeOffset FromText(string text) =>
        DateTimeOffset.ParseExact(
            text,
            Format,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
