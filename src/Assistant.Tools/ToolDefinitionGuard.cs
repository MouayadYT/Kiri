using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Core.Domain;

namespace Assistant.Tools;

/// <summary>
/// The rules every tool must meet to be registered (PROJECT_SPEC §4.8), checked when the app starts and not when the model calls: a
/// stable snake_case name, a description, an input schema that is an object of typed properties, a time limit, and a permission that
/// can be asked about. And no tool that runs what it is given: the model may call only what is registered, and nothing registered
/// takes a command line, a script or a shell to run (P7), whatever it is named. A name or an argument that reads as one is refused, so
/// that adding such a tool later is a change to this guard, which is seen, and not a side effect of adding a tool.
/// </summary>
internal static partial class ToolDefinitionGuard
{
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 2000;

    // Words that, as a part of a tool's name, say it runs what it is given.
    private static readonly HashSet<string> ExecutionWords = new(StringComparer.Ordinal)
    {
        "shell", "powershell", "pwsh", "cmd", "bash", "zsh", "terminal", "console", "exec", "execute", "executes", "eval", "evaluate",
        "command", "commands", "script", "scripts", "cli", "subprocess", "spawn", "invoke",
    };

    // Arguments (with their underscores taken out) that carry something to run.
    private static readonly HashSet<string> ExecutionArguments = new(StringComparer.Ordinal)
    {
        "command", "commands", "commandline", "cmd", "cmdline", "script", "scriptblock", "scripttext", "shell", "shellcommand",
        "powershell", "pwsh", "bash", "executable", "program", "processname", "arguments", "args", "argv",
    };

    private static readonly HashSet<string> SchemaTypes = new(StringComparer.Ordinal)
    {
        "string", "integer", "number", "boolean", "array", "object",
    };

    [GeneratedRegex("^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>Whether <paramref name="word"/>, as a whole word of a tool's name, says that the tool runs what it is given.</summary>
    public static bool ReadsAsExecution(string word) => ExecutionWords.Contains(word);

    /// <summary>What is wrong with <paramref name="definition"/> for a tool that runs for <paramref name="timeout"/>, or <see langword="null"/> when it may be registered.</summary>
    public static string? Problem(ToolDefinition definition, TimeSpan timeout)
    {
        if (definition is null)
        {
            return "A tool has no definition.";
        }

        var name = definition.Name ?? string.Empty;
        if (name.Length > MaxNameLength || !NamePattern().IsMatch(name))
        {
            return "A tool is named in lower snake_case, and at most 64 characters.";
        }

        if (name.Split('_').Any(ExecutionWords.Contains))
        {
            return $"The tool {name} reads as one that runs a command or a script, and no such tool is registered.";
        }

        if (string.IsNullOrWhiteSpace(definition.Description) || definition.Description.Length > MaxDescriptionLength)
        {
            return $"The tool {name} needs a description of at most {MaxDescriptionLength} characters.";
        }

        if (!Enum.IsDefined(definition.RiskLevel))
        {
            return $"The tool {name} has no side-effect category.";
        }

        if (definition.RequiredPermission is { } permission
            && (!Enum.IsDefined(permission) || permission == PermissionCapability.DestructiveActions))
        {
            return $"The tool {name} names a permission that cannot be required of a tool.";
        }

        if (timeout <= TimeSpan.Zero || timeout > ToolDefinition.MaxTimeout)
        {
            return $"The tool {name} needs a time limit of more than nothing and at most {ToolDefinition.MaxTimeout.TotalMinutes:0} minutes.";
        }

        return SchemaProblem(name, definition.InputSchemaJson);
    }

    private static string? SchemaProblem(string name, string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return $"The tool {name} has no input schema.";
        }

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var schema = document.RootElement;
            if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) || type.GetString() != "object")
            {
                return $"The input schema of {name} is an object.";
            }

            var declared = new HashSet<string>(StringComparer.Ordinal);
            if (schema.TryGetProperty("properties", out var properties))
            {
                if (properties.ValueKind != JsonValueKind.Object)
                {
                    return $"The properties of {name} are an object.";
                }

                foreach (var property in properties.EnumerateObject())
                {
                    declared.Add(property.Name);
                    if (!NamePattern().IsMatch(property.Name))
                    {
                        return $"The argument {property.Name} of {name} is named in lower snake_case.";
                    }

                    if (ExecutionArguments.Contains(property.Name.Replace("_", "", StringComparison.Ordinal)))
                    {
                        return $"The argument {property.Name} of {name} reads as something to run, and no registered tool takes one.";
                    }

                    if (property.Value.ValueKind != JsonValueKind.Object
                        || !property.Value.TryGetProperty("type", out var kind) || !SchemaTypes.Contains(kind.GetString() ?? string.Empty))
                    {
                        return $"The argument {property.Name} of {name} has no known type.";
                    }
                }
            }

            if (schema.TryGetProperty("required", out var required))
            {
                if (required.ValueKind != JsonValueKind.Array
                    || required.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || !declared.Contains(item.GetString()!)))
                {
                    return $"The input schema of {name} requires an argument it does not have.";
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return $"The input schema of {name} is not valid JSON.";
        }
    }
}
