using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assistant.Tools.Mcp;

/// <summary>A connected app's tool schema as the tool registry takes it, and how its arguments are named on the server.</summary>
/// <param name="Json">The input schema: an object of typed properties named in lower snake_case, with <c>additionalProperties: false</c>.</param>
/// <param name="ServerNames">For each argument, the name the server gave it, to put back when the tool is called.</param>
internal sealed record AdaptedSchema(string Json, IReadOnlyDictionary<string, string> ServerNames);

/// <summary>
/// Makes a connected app's tool schema fit the typed tools of the registry (PROJECT_SPEC §4.8, step 104). An MCP server writes its input schema as
/// it pleases: camel-case names, <c>$ref</c>, <c>anyOf</c> with <c>null</c>, no <c>type</c> at all. The registry wants an object of snake_case properties of
/// a known type (<c>ToolDefinitionGuard</c>), which is also what a small local model can fill in. What this keeps: the type, a short cleaned
/// description, <c>enum</c>, the numeric and length limits, an array's item type and an object's own (simplified) properties. What it does not: a schema that
/// refers outside itself, composes types (other than "this or null"), or holds too many properties is not adapted, and an optional property that cannot
/// be simplified is left out (a required one makes the tool unusable). The result is only what the model is told and what a call is checked against; the
/// server still checks its own input.
/// </summary>
internal static class McpSchemaAdapter
{
    /// <summary>The most arguments a tool may have.</summary>
    public const int MaxProperties = 24;

    /// <summary>The longest schema, in characters.</summary>
    public const int MaxJsonLength = 6000;

    private const int MaxDescriptionLength = 300;
    private const int MaxDepth = 3;
    private const int MaxNestedProperties = 16;
    private const int MaxEnumValues = 50;
    private const int MaxReferenceHops = 5;
    private const int MaxArgumentNameLength = 40;

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal) { "string", "integer", "number", "boolean", "array", "object" };

    /// <summary>Adapts <paramref name="inputSchema"/>; <see langword="null"/> when the tool cannot be offered with it.</summary>
    public static AdaptedSchema? Adapt(JsonElement inputSchema)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // A schema that is a choice between whole argument lists cannot be said as one list of arguments.
        if (inputSchema.TryGetProperty("anyOf", out _) || inputSchema.TryGetProperty("oneOf", out _) || inputSchema.TryGetProperty("allOf", out _))
        {
            return null;
        }

        var root = inputSchema;
        if (Resolve(inputSchema, root) is not { } schema || TypeOf(schema, root, out var effective) is not "object")
        {
            // A tool that takes no arguments may be written as {} or as {"type":"object"}.
            if (inputSchema.EnumerateObject().Any(member => member.Name is "properties" or "required" or "type"))
            {
                return null;
            }

            effective = inputSchema;
        }

        var properties = effective.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object ? declared : default;
        var count = properties.ValueKind == JsonValueKind.Object ? properties.EnumerateObject().Count() : 0;
        if (count > MaxProperties)
        {
            return null;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (effective.TryGetProperty("required", out var requiredList) && requiredList.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in requiredList.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String))
            {
                required.Add(name.GetString()!);
            }
        }

        var outputProperties = new JsonObject();
        var outputRequired = new JsonArray();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        if (properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (Simplify(property.Value, root, 0) is not { } simplified)
                {
                    if (required.Contains(property.Name))
                    {
                        return null;
                    }

                    continue;
                }

                var ours = ArgumentName(property.Name, taken);
                names[ours] = property.Name;
                outputProperties[ours] = simplified;
                if (required.Contains(property.Name))
                {
                    outputRequired.Add(ours);
                }
            }
        }

        // A required argument that the schema does not declare cannot be named, and so cannot be given.
        if (required.Any(name => properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(name, out _)))
        {
            return null;
        }

        var schemaObject = new JsonObject { ["type"] = "object", ["properties"] = outputProperties };
        if (outputRequired.Count > 0)
        {
            schemaObject["required"] = outputRequired;
        }

        schemaObject["additionalProperties"] = false;
        var json = schemaObject.ToJsonString();
        return json.Length > MaxJsonLength ? null : new AdaptedSchema(json, names);
    }

    // An argument's name in lower snake_case, starting with a letter and not one that another argument of the tool has.
    private static string ArgumentName(string serverName, HashSet<string> taken)
    {
        var name = McpText.Snake(serverName);
        if (name.Length == 0)
        {
            name = "arg";
        }
        else if (!char.IsAsciiLetterLower(name[0]))
        {
            name = "p_" + name;
        }

        if (name.Length > MaxArgumentNameLength)
        {
            name = name[..MaxArgumentNameLength].TrimEnd('_');
        }

        var candidate = name;
        for (var number = 2; !taken.Add(candidate); number++)
        {
            candidate = name + "_" + number;
        }

        return candidate;
    }

    // One property's schema, simplified; null when it cannot be.
    private static JsonObject? Simplify(JsonElement schema, JsonElement root, int depth)
    {
        if (Resolve(schema, root) is not { } resolved || TypeOf(resolved, root, out var effective) is not { } type)
        {
            return null;
        }

        var result = new JsonObject { ["type"] = type };
        if (McpText.Clean(Description(effective) ?? Description(resolved) ?? Description(schema), MaxDescriptionLength) is { } description)
        {
            result["description"] = description;
        }

        switch (type)
        {
            case "string":
                AddEnum(result, effective, JsonValueKind.String);
                AddNumber(result, effective, "minLength");
                AddNumber(result, effective, "maxLength");
                break;
            case "integer" or "number":
                AddEnum(result, effective, JsonValueKind.Number);
                AddNumber(result, effective, "minimum");
                AddNumber(result, effective, "maximum");
                break;
            case "array":
                if (effective.TryGetProperty("items", out var items) && depth < MaxDepth && Simplify(items, root, depth + 1) is { } itemSchema)
                {
                    result["items"] = itemSchema;
                }

                break;
            case "object":
                if (depth < MaxDepth - 1 && effective.TryGetProperty("properties", out var nested) && nested.ValueKind == JsonValueKind.Object)
                {
                    AddNested(result, effective, nested, root, depth);
                }

                break;
        }

        return result;
    }

    // An object's own properties, as far as they can be simplified; an optional one that cannot is left out.
    private static void AddNested(JsonObject result, JsonElement effective, JsonElement nested, JsonElement root, int depth)
    {
        var properties = new JsonObject();
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (effective.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String))
            {
                required.Add(item.GetString()!);
            }
        }

        foreach (var property in nested.EnumerateObject().Take(MaxNestedProperties))
        {
            if (Simplify(property.Value, root, depth + 1) is { } simplified)
            {
                properties[property.Name] = simplified;
            }
        }

        if (properties.Count == 0)
        {
            return;
        }

        result["properties"] = properties;
        var kept = new JsonArray();
        foreach (var name in required.Where(name => properties.ContainsKey(name)))
        {
            kept.Add(name);
        }

        if (kept.Count > 0)
        {
            result["required"] = kept;
        }
    }

    // The schema a $ref points to, when it points inside this schema; the schema itself when it holds no $ref; null otherwise.
    private static JsonElement? Resolve(JsonElement schema, JsonElement root)
    {
        var current = schema;
        for (var hop = 0; hop <= MaxReferenceHops; hop++)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!current.TryGetProperty("$ref", out var reference))
            {
                return current;
            }

            if (reference.ValueKind != JsonValueKind.String || reference.GetString() is not { } pointer || !pointer.StartsWith("#/", StringComparison.Ordinal))
            {
                return null;
            }

            var target = root;
            foreach (var segment in pointer[2..].Split('/'))
            {
                var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (target.ValueKind != JsonValueKind.Object || !target.TryGetProperty(name, out target))
                {
                    return null;
                }
            }

            current = target;
        }

        return null;
    }

    // The simple type a schema stands for, or null when it stands for none: a type, a type or null, an enum or constant, a union of one type, or an
    // object or array by what it has. effective is the schema that says it (the one alternative that is not null).
    private static string? TypeOf(JsonElement schema, JsonElement root, out JsonElement effective)
    {
        effective = schema;
        if (schema.ValueKind != JsonValueKind.Object || schema.TryGetProperty("allOf", out _) || schema.TryGetProperty("not", out _))
        {
            return null;
        }

        foreach (var keyword in new[] { "anyOf", "oneOf" })
        {
            if (!schema.TryGetProperty(keyword, out var alternatives))
            {
                continue;
            }

            if (alternatives.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var options = new List<(JsonElement Schema, string Type, JsonElement Effective)>();
            foreach (var alternative in alternatives.EnumerateArray())
            {
                if (Resolve(alternative, root) is not { } resolved)
                {
                    return null;
                }

                if (resolved.TryGetProperty("type", out var nullable) && nullable.ValueKind == JsonValueKind.String && nullable.GetString() == "null")
                {
                    continue;
                }

                if (TypeOf(resolved, root, out var inner) is not { } innerType)
                {
                    return null;
                }

                options.Add((resolved, innerType, inner));
            }

            // "This or null" is this; several alternatives of the same plain type are that type, without what tells them apart.
            if (options.Count == 0 || options.Select(option => option.Type).Distinct().Count() != 1)
            {
                return null;
            }

            effective = options.Count == 1 ? options[0].Effective : schema;
            return options[0].Type;
        }

        if (schema.TryGetProperty("type", out var declared))
        {
            string? name = null;
            if (declared.ValueKind == JsonValueKind.String)
            {
                name = declared.GetString();
            }
            else if (declared.ValueKind == JsonValueKind.Array)
            {
                var kinds = declared.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(item => item != "null").Distinct().ToList();
                name = kinds.Count == 1 ? kinds[0] : null;
            }

            return name is not null && Types.Contains(name) ? name : null;
        }

        foreach (var keyword in new[] { "enum", "const" })
        {
            if (!schema.TryGetProperty(keyword, out var values))
            {
                continue;
            }

            var list = values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().ToList() : [values];
            if (list.Count == 0)
            {
                return null;
            }

            if (list.All(value => value.ValueKind == JsonValueKind.String))
            {
                return "string";
            }

            if (list.All(value => value.ValueKind == JsonValueKind.Number))
            {
                return list.All(value => value.TryGetInt64(out _)) ? "integer" : "number";
            }

            return list.All(value => value.ValueKind is JsonValueKind.True or JsonValueKind.False) ? "boolean" : null;
        }

        if (schema.TryGetProperty("properties", out _))
        {
            return "object";
        }

        return schema.TryGetProperty("items", out _) ? "array" : null;
    }

    private static string? Description(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("description", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // The values an argument may take, when they are all of the kind the type needs and few.
    private static void AddEnum(JsonObject result, JsonElement schema, JsonValueKind kind)
    {
        if (!schema.TryGetProperty("enum", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var list = values.EnumerateArray().ToList();
        if (list.Count == 0 || list.Count > MaxEnumValues || list.Any(value => value.ValueKind != kind)
            || kind == JsonValueKind.String && list.Any(value => value.GetString()!.Length > 100))
        {
            return;
        }

        var array = new JsonArray();
        foreach (var value in list)
        {
            array.Add(JsonNode.Parse(value.GetRawText()));
        }

        result["enum"] = array;
    }

    private static void AddNumber(JsonObject result, JsonElement schema, string name)
    {
        if (schema.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
        {
            result[name] = JsonNode.Parse(value.GetRawText());
        }
    }
}
