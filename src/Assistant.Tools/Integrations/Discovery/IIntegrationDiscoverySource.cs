using System.Text.Json;

namespace Assistant.Tools.Integrations;

/// <summary>When a source is asked.</summary>
internal enum DiscoveryStage
{
    /// <summary>With the first search: the maker's own places and the registries and directories that list integrations.</summary>
    First = 0,

    /// <summary>Only when the first search found nothing about the app that fits: the package registries.</summary>
    WhenNeeded = 1,
}

/// <summary>
/// One place that lists MCP integrations (PROJECT_SPEC §4.8, step 106). A source turns one focused search (the app, what is wanted, the word MCP) into
/// candidates. It fetches only through <see cref="IDiscoveryHttp"/>, reads only metadata, and downloads and runs nothing.
/// </summary>
internal interface IIntegrationDiscoverySource
{
    /// <summary>The source's name as shown in a result: <c>mcp-registry</c>, <c>github</c>, <c>npm</c>, <c>pypi</c>.</summary>
    string Id { get; }

    /// <summary>When it is asked.</summary>
    DiscoveryStage Stage { get; }

    /// <summary>The candidates the source lists for the query; none when nothing fits.</summary>
    /// <exception cref="DiscoveryException">The source could not be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IReadOnlyList<IntegrationCandidate>> SearchAsync(DiscoveryQuery query, CancellationToken cancellationToken);
}

/// <summary>Reads the members of JSON that came from the web, which may be anything: a member that is missing or of another type is nothing.</summary>
internal static class WebJson
{
    public static JsonElement? Member(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    public static string? Text(JsonElement element, string name) =>
        Member(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static bool? Flag(JsonElement element, string name) =>
        Member(element, name) is { } value && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    public static int? Whole(JsonElement element, string name) =>
        Member(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    public static DateTimeOffset? Date(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;

    public static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        Member(element, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    /// <summary>The root of <paramref name="body"/>, or an exception that says the answer was not JSON.</summary>
    public static JsonDocument Parse(string body)
    {
        try
        {
            return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            throw new DiscoveryException(DiscoveryFailure.Malformed);
        }
    }
}
