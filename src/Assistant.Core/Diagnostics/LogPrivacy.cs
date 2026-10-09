using System.Collections.Frozen;

namespace Assistant.Core.Diagnostics;

/// <summary>
/// The logging privacy convention (PROJECT_SPEC §3.3), applied to every logger once
/// <see cref="PrivacyLoggingBuilderExtensions.AddPrivacyFilter"/> is configured.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Message templates are kept as written, so they must be constant. CA2254 enforces this at build time.</item>
/// <item>Numbers, booleans, enums, GUIDs, dates, times, durations, versions and types are logged as they are.</item>
/// <item>
/// Strings are logged only when their placeholder name is in <see cref="SafeStringParameters"/>. Every other value,
/// including domain records and collections, is replaced with <see cref="Redacted"/>.
/// </item>
/// <item>Exception messages are dropped. Only type names, HRESULTs and stack frames are kept (<see cref="SanitizedException"/>).</item>
/// </list>
/// </remarks>
public static class LogPrivacy
{
    /// <summary>Text that replaces a value that may be private.</summary>
    public const string Redacted = "[redacted]";

    /// <summary>
    /// Placeholder names whose string values are never private content: the string categories that PROJECT_SPEC §3.3
    /// allows in logs. Matched case-insensitively.
    /// </summary>
    public static IReadOnlySet<string> SafeStringParameters { get; } = new[]
    {
        "Component",
        "EnvironmentName",
        "EventName",
        "ExceptionType",
        "FileExtension",
        "HotkeyId",
        "IntegrationId",
        "Outcome",
        "Shape",
        "ModelId",
        "ProcessName",
        "ToolName",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <paramref name="value"/> when it is safe to log under <paramref name="parameterName"/>, otherwise
    /// <see cref="Redacted"/>.
    /// </summary>
    public static object? Sanitize(string parameterName, object? value) => value switch
    {
        null => null,
        string text => SafeStringParameters.Contains(parameterName) ? text : Redacted,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        Enum or Guid or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan or Version or Type => value,
        _ => Redacted,
    };
}
