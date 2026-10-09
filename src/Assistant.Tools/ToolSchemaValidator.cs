using System.Globalization;
using System.Text.Json;

namespace Assistant.Tools;

/// <summary>
/// Checks a tool call's arguments against the tool's input schema (PROJECT_SPEC §4.8), for the part of JSON Schema the tools use: an
/// object with typed properties (<c>string</c>, <c>integer</c>, <c>number</c>, <c>boolean</c>), the ones that are required, <c>enum</c>,
/// <c>minimum</c> and <c>maximum</c> of a number, <c>minLength</c> and <c>maxLength</c> of text, and <c>additionalProperties: false</c>.
/// Every problem is reported (a few at most), not only the first, so that the model can correct a call in one go. Text is never longer
/// than <see cref="DefaultMaxStringLength"/> unless its schema says how long it may be.
/// </summary>
/// <remarks>
/// An argument the schema does not name is refused when the tool says so (<c>additionalProperties: false</c>, which every typed tool's
/// schema does) or when the caller says unknown arguments are not to be ignored (a tool with a side effect, whose confirmation shows
/// the user the arguments: an argument that is shown and not used would mislead). Otherwise it is ignored, since a model adds some to
/// a tool that only reads.
/// </remarks>
internal static class ToolSchemaValidator
{
    /// <summary>The most characters a text argument may have when its schema does not give a limit.</summary>
    public const int DefaultMaxStringLength = 4_000;

    private const int MaxProblems = 4;

    /// <summary>Checks <paramref name="arguments"/>, a JSON object, against <paramref name="schemaJson"/>.</summary>
    /// <param name="schemaJson">The tool's input schema.</param>
    /// <param name="arguments">The arguments as the parser read them.</param>
    /// <param name="rejectUnknown">Whether an argument the schema does not name is refused whatever the schema says.</param>
    /// <returns>What is wrong with the arguments, in words the model can act on, or <see langword="null"/> when they are valid.</returns>
    public static string? Validate(string schemaJson, JsonElement arguments, bool rejectUnknown)
    {
        using var schemaDocument = JsonDocument.Parse(schemaJson);
        var schema = schemaDocument.RootElement;
        var properties = schema.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object
            ? declared
            : default;
        var strict = rejectUnknown
            || (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False);

        var problems = new List<string>();
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Select(item => item.GetString()).OfType<string>())
            {
                if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                {
                    problems.Add($"The argument \"{name}\" is required.");
                }
            }
        }

        foreach (var property in arguments.EnumerateObject())
        {
            if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(property.Name, out var rule))
            {
                if (strict)
                {
                    problems.Add($"The argument \"{Shown(property.Name)}\" is not one this tool takes. {Takes(properties)}");
                }

                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (Problem(property.Name, property.Value, rule) is { } problem)
            {
                problems.Add(problem);
            }
        }

        return problems.Count == 0 ? null : string.Join(' ', problems.Take(MaxProblems));
    }

    // What is wrong with one argument's value, or null.
    private static string? Problem(string name, JsonElement value, JsonElement rule)
    {
        if (rule.TryGetProperty("type", out var type) && type.GetString() is { } kind && !IsOfType(value, kind))
        {
            return $"The argument \"{name}\" must be {Describe(kind)}.";
        }

        if (rule.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array
            && !allowed.EnumerateArray().Any(option => option.ToString() == value.ToString()))
        {
            var options = string.Join(", ", allowed.EnumerateArray().Select(option => option.ToString()));
            return $"The argument \"{name}\" must be one of: {options}.";
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            var minimum = Limit(rule, "minimum");
            var maximum = Limit(rule, "maximum");
            if ((minimum is { } low && number < low) || (maximum is { } high && number > high))
            {
                return (minimum, maximum) switch
                {
                    ({ } least, { } most) => $"The argument \"{name}\" must be from {Format(least)} to {Format(most)}.",
                    ({ } least, null) => $"The argument \"{name}\" must be at least {Format(least)}.",
                    (null, { } most) => $"The argument \"{name}\" must be at most {Format(most)}.",
                    _ => null,
                };
            }
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var length = value.GetString()!.Length;
            var most = Limit(rule, "maxLength") is { } declared ? (int)declared : DefaultMaxStringLength;
            if (length > most)
            {
                return $"The argument \"{name}\" is too long: at most {most} characters.";
            }

            if (Limit(rule, "minLength") is { } least && length < least)
            {
                return $"The argument \"{name}\" is too short: at least {Format(least)} characters.";
            }
        }

        return null;
    }

    private static double? Limit(JsonElement rule, string name) =>
        rule.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static string Format(double number) => number.ToString("0.##", CultureInfo.InvariantCulture);

    // What a tool takes, in a sentence, for a call that gave something else.
    private static string Takes(JsonElement properties)
    {
        if (properties.ValueKind != JsonValueKind.Object)
        {
            return "It takes no arguments.";
        }

        var names = properties.EnumerateObject().Select(property => property.Name).ToList();
        return names.Count == 0 ? "It takes no arguments." : "It takes: " + string.Join(", ", names) + ".";
    }

    // A name the model gave, shown back short and on one line.
    private static string Shown(string name)
    {
        var text = name.Length > 40 ? name[..40] + "..." : name;
        return new string([.. text.Select(character => char.IsControl(character) ? ' ' : character)]);
    }

    private static bool IsOfType(JsonElement value, string type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        _ => true,
    };

    private static string Describe(string type) => type switch
    {
        "string" => "text",
        "integer" => "a whole number",
        "number" => "a number",
        "boolean" => "true or false",
        _ => "a " + type,
    };
}
