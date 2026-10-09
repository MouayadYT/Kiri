using System.Globalization;
using System.Text;
using System.Text.Json;
using Assistant.Core.Domain;

namespace Assistant.Core.Tools;

/// <summary>
/// Says in a line how a tool is called, from its input schema: <c>set_volume(percent: a whole number from 0 to 100)</c>. It is what a
/// call that broke its tool's schema is answered with, so that the model's next call can be right without the whole schema again.
/// </summary>
public static class ToolUsage
{
    /// <summary>How <paramref name="definition"/> is called, in one line; just its name when its schema cannot be read.</summary>
    public static string Describe(ToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        try
        {
            using var document = JsonDocument.Parse(definition.InputSchemaJson);
            var schema = document.RootElement;
            if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object)
            {
                return definition.Name + "()";
            }

            var required = schema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(item => item.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : [];
            var arguments = properties.EnumerateObject()
                .Select(property => $"{property.Name}{(required.Contains(property.Name) ? "" : "?")}: {Describe(property.Value)}");
            return $"{definition.Name}({string.Join(", ", arguments)})";
        }
        catch (JsonException)
        {
            return definition.Name + "()";
        }
    }

    // What an argument is, in words: its kind, then the values or the range it is held to.
    private static string Describe(JsonElement rule)
    {
        var type = rule.TryGetProperty("type", out var kind) ? kind.GetString() : null;
        if (rule.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            return "one of " + string.Join(", ", choices.EnumerateArray().Select(choice => choice.ToString()));
        }

        var text = type switch
        {
            "string" => "text",
            "integer" => "a whole number",
            "number" => "a number",
            "boolean" => "true or false",
            "array" => "a list",
            "object" => "an object",
            _ => "a value",
        };

        var minimum = Number(rule, "minimum");
        var maximum = Number(rule, "maximum");
        var range = (minimum, maximum) switch
        {
            ({ } low, { } high) => $" from {low} to {high}",
            ({ } low, null) => $" of at least {low}",
            (null, { } high) => $" of at most {high}",
            _ => string.Empty,
        };
        return new StringBuilder(text).Append(range).ToString();
    }

    private static string? Number(JsonElement rule, string name) =>
        rule.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number.ToString("0.##", CultureInfo.InvariantCulture)
            : null;
}
