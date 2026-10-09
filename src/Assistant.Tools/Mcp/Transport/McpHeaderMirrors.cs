using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Assistant.Tools.Mcp;

/// <summary>One argument of a tool that the server asked to have copied into an HTTP header (<c>x-mcp-header</c>).</summary>
/// <param name="Path">The argument's place in the arguments: the names of the properties down to it.</param>
/// <param name="HeaderName">The part of the header's name after <c>Mcp-Param-</c>.</param>
/// <param name="Type">The argument's type: <c>string</c>, <c>integer</c> or <c>boolean</c>.</param>
internal sealed record McpHeaderMirror(IReadOnlyList<string> Path, string HeaderName, string Type);

/// <summary>
/// The <c>x-mcp-header</c> rule of the modern MCP protocol over HTTP: a server may mark a tool's argument so that the client copies its value into
/// a header (<c>Mcp-Param-Name</c>), which lets a gateway route a request without reading it. A client must do so, and must leave out any tool whose
/// marks break the rules: a name that is not a header name, one used twice, a mark on anything but a string, an integer or a boolean, or on a
/// property that cannot be reached through <c>properties</c> alone. A value that is not plain printable ASCII is sent Base64-encoded in
/// <c>=?base64?...?=</c>, so that it cannot carry anything into the request.
/// </summary>
internal static class McpHeaderMirrors
{
    private const string Annotation = "x-mcp-header";
    private const string SentinelStart = "=?base64?";
    private const string SentinelEnd = "?=";
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private const int MaxDepth = 16;

    /// <summary>The arguments of a tool that are mirrored into headers, found in its input schema.</summary>
    /// <returns><see langword="false"/> when the marks break the rules: the tool is not to be used.</returns>
    public static bool TryExtract(JsonElement inputSchema, out IReadOnlyList<McpHeaderMirror> mirrors)
    {
        var found = new List<McpHeaderMirror>();
        mirrors = found;
        if (inputSchema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Walk(inputSchema, [], reachable: true, found, names, depth: 0))
        {
            mirrors = [];
            return false;
        }

        return true;
    }

    /// <summary>The headers that carry the values of <paramref name="mirrors"/> found in <paramref name="arguments"/>, encoded. An argument that is not given is left out.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> HeadersFor(IReadOnlyList<McpHeaderMirror> mirrors, JsonElement arguments)
    {
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var mirror in mirrors)
        {
            var value = arguments;
            var present = true;
            foreach (var name in mirror.Path)
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out value))
                {
                    present = false;
                    break;
                }
            }

            if (!present || Text(value, mirror.Type) is not { } text)
            {
                continue;
            }

            headers.Add(new KeyValuePair<string, string>(McpProtocol.ParamHeaderPrefix + mirror.HeaderName, Encode(text)));
        }

        return headers;
    }

    /// <summary>A value as a header carries it: itself when it is plain printable ASCII, otherwise <c>=?base64?(its UTF-8)?=</c>.</summary>
    public static string Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var plain = value.Length == 0
            || (value[0] is not (' ' or '\t') && value[^1] is not (' ' or '\t')
                && value.All(character => character is >= ' ' and <= '~' or '\t'));
        if (plain && value.StartsWith(SentinelStart, StringComparison.Ordinal) && value.EndsWith(SentinelEnd, StringComparison.Ordinal))
        {
            plain = false;
        }

        return plain ? value : SentinelStart + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + SentinelEnd;
    }

    // The value as text, when it is of the type the mark says; otherwise nothing is sent (the call is then the server's to reject).
    private static string? Text(JsonElement value, string type) => type switch
    {
        "string" when value.ValueKind == JsonValueKind.String => value.GetString(),
        "integer" when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && Math.Abs(number) <= MaxSafeInteger =>
            number.ToString(CultureInfo.InvariantCulture),
        "boolean" when value.ValueKind == JsonValueKind.True => "true",
        "boolean" when value.ValueKind == JsonValueKind.False => "false",
        _ => null,
    };

    // Looks at a schema and everything under it. A property reached through "properties" alone may carry the mark; the mark anywhere else breaks the rules.
    private static bool Walk(
        JsonElement schema, List<string> path, bool reachable, List<McpHeaderMirror> found, HashSet<string> names, int depth)
    {
        if (depth > MaxDepth)
        {
            return false;
        }

        if (schema.TryGetProperty(Annotation, out var mark))
        {
            if (!reachable || path.Count == 0 || mark.ValueKind != JsonValueKind.String || !IsToken(mark.GetString())
                || !names.Add(mark.GetString()!)
                || !schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() is not ("string" or "integer" or "boolean"))
            {
                return false;
            }

            found.Add(new McpHeaderMirror([.. path], mark.GetString()!, type.GetString()!));
        }

        foreach (var member in schema.EnumerateObject())
        {
            if (member.Name == Annotation)
            {
                continue;
            }

            if (member.Name == "properties" && member.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in member.Value.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    path.Add(property.Name);
                    var fine = Walk(property.Value, path, reachable, found, names, depth + 1);
                    path.RemoveAt(path.Count - 1);
                    if (!fine)
                    {
                        return false;
                    }
                }
            }
            else if (member.Value.ValueKind == JsonValueKind.Object)
            {
                if (!Walk(member.Value, path, reachable: false, found, names, depth + 1))
                {
                    return false;
                }
            }
            else if (member.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in member.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && !Walk(item, path, reachable: false, found, names, depth + 1))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    // An HTTP header name is a token: letters, digits and a few marks.
    private static bool IsToken(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(character => char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character, StringComparison.Ordinal));
}
