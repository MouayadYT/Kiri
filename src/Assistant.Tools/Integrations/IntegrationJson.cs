using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assistant.Tools.Integrations;

/// <summary>How an <see cref="InstalledIntegration"/> is written to and read from JSON: camel case, enums as words, nothing written for what is absent.</summary>
internal static class IntegrationJson
{
    /// <summary>The options the integrations file is written with.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        // The file is for people to read: a path's backslashes and a version's plus sign are not escaped more than JSON needs.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    /// <summary>The JSON of <paramref name="integration"/> on one line, for comparing two of them.</summary>
    public static string Serialize(InstalledIntegration integration) => JsonSerializer.Serialize(integration, Compact);

    /// <summary>Whether the two integrations hold the same.</summary>
    public static bool Equivalent(InstalledIntegration first, InstalledIntegration second) =>
        string.Equals(Serialize(first), Serialize(second), StringComparison.Ordinal);
}
