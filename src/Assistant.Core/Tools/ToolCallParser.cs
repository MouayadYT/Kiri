using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Assistant.Core.Domain;

namespace Assistant.Core.Tools;

/// <summary>What was wrong with a tool call, as the model is told (<see cref="ToolErrors"/>).</summary>
/// <param name="Code">The kind of failure, one of the <see cref="ToolErrors"/> codes.</param>
/// <param name="Message">What is wrong, in words the model can act on; it never repeats what the model wrote.</param>
public sealed record ToolCallProblem(string Code, string Message);

/// <summary>
/// A tool call as the model wrote it, read and made tidy by <see cref="ToolCallParser"/>.
/// </summary>
/// <param name="Call">
/// The call with its name trimmed, its id kept in bounds and, when the arguments could be read, its arguments as one compact JSON
/// object (<c>{}</c> for none). When they could not, the call is as the model wrote it, apart from arguments that were too long to keep.
/// </param>
/// <param name="Arguments">The arguments as a JSON object when they could be read; otherwise <c>default</c>.</param>
/// <param name="Problem">What is wrong with the call, or <see langword="null"/> when it is well formed.</param>
public sealed record ParsedToolCall(ToolCall Call, JsonElement Arguments, ToolCallProblem? Problem)
{
    /// <summary>Whether the call's name and arguments are well formed. It says nothing of whether the tool exists or its schema is met.</summary>
    public bool IsValid => Problem is null;
}

/// <summary>
/// Reads a tool call as the model produced it (PROJECT_SPEC §4.8, §5.5) before anything is looked up or run: the name, the id and the
/// arguments, which are text the model wrote and may be anything. It is strict about what is dangerous or ambiguous and forgiving
/// only about slips that cannot change what is meant: the arguments must be one JSON object of a bounded size and depth with no
/// comments, no trailing commas and no argument given twice; an empty text, or <c>null</c>, is no arguments; an object that the model
/// wrote as a JSON string (a common slip) or inside a Markdown code fence is read as the object; the name is trimmed and may carry the
/// <c>functions.</c> prefix some models write. Nothing here looks at a schema (the executor does that, against the tool the name
/// finds) and nothing here is run or kept: the result is a tidy copy of the call and, if it is not well formed, why not.
/// </summary>
public static class ToolCallParser
{
    /// <summary>The longest a tool's name may be (<c>ToolDefinitionGuard</c>); a longer one is cut to this in what the app keeps.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest an id is kept.</summary>
    public const int MaxIdLength = 64;

    /// <summary>The most characters the arguments of one call may be; more are refused, which keeps a runaway answer out of the conversation.</summary>
    public const int MaxArgumentsLength = 16_384;

    /// <summary>How deeply the arguments may nest.</summary>
    public const int MaxArgumentsDepth = 8;

    private static readonly string[] NamePrefixes = ["functions.", "tools.", "tool."];

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        MaxDepth = MaxArgumentsDepth,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    // The arguments are written back as the model would read them: a plus or an accent as it is, not escaped.
    private static readonly JsonWriterOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string NotJson =
        "The arguments are not valid JSON. Write them as one JSON object, such as {\"name\": \"value\"}, with nothing before or after it.";

    /// <summary>Reads <paramref name="call"/>.</summary>
    public static ParsedToolCall Parse(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var name = CleanName(call.ToolName);
        var id = call.Id is { Length: > MaxIdLength } ? call.Id[..MaxIdLength] : call.Id ?? string.Empty;
        var tidy = call with { ToolName = name, Id = id };

        if (name.Length == 0)
        {
            return Refused(tidy, ToolErrors.UnknownTool, "The call names no tool. Name one of the tools you were given.");
        }

        var text = call.ArgumentsJson ?? string.Empty;
        if (text.Length > MaxArgumentsLength)
        {
            // What is not kept is not repeated into the conversation: the model is told, and the call goes on as an empty one.
            return Refused(
                tidy with { ArgumentsJson = "{}" },
                ToolErrors.InvalidArguments,
                $"The arguments are too long: at most {MaxArgumentsLength} characters.");
        }

        if (!TryReadObject(text, out var arguments, out var problem))
        {
            return Refused(tidy, ToolErrors.InvalidArguments, problem);
        }

        var duplicate = FirstDuplicate(arguments);
        if (duplicate is not null)
        {
            return Refused(tidy, ToolErrors.InvalidArguments, $"The argument \"{Cut(duplicate)}\" is given more than once.");
        }

        var compact = Compact(arguments);
        using var document = JsonDocument.Parse(compact, ReadOptions);
        return new ParsedToolCall(tidy with { ArgumentsJson = compact }, document.RootElement.Clone(), null);
    }

    // The name as the registry knows it: trimmed, without a "functions." prefix, and no longer than a tool's name may be.
    private static string CleanName(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        foreach (var prefix in NamePrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && text.Length > prefix.Length)
            {
                text = text[prefix.Length..];
                break;
            }
        }

        return text.Length > MaxNameLength ? text[..MaxNameLength] : text;
    }

    // The arguments as one JSON object, or why they cannot be.
    private static bool TryReadObject(string text, out JsonElement arguments, out string problem)
    {
        arguments = default;
        problem = NotJson;
        var trimmed = StripFence(text.Trim());
        if (trimmed.Length == 0)
        {
            return Empty(out arguments);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed, ReadOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            // An object written as a string of JSON ("{\"query\":\"a\"}") is the object; once, and no deeper.
            if (root.ValueKind == JsonValueKind.String && root.GetString() is { } inner)
            {
                var again = StripFence(inner.Trim());
                if (again.Length > MaxArgumentsLength || !again.StartsWith('{'))
                {
                    problem = "The arguments must be a JSON object, not a string.";
                    return false;
                }

                try
                {
                    using var nested = JsonDocument.Parse(again, ReadOptions);
                    return Accept(nested.RootElement, out arguments, out problem);
                }
                catch (JsonException)
                {
                    return false;
                }
            }

            return Accept(root, out arguments, out problem);
        }
    }

    private static bool Accept(JsonElement root, out JsonElement arguments, out string problem)
    {
        arguments = default;
        problem = "The arguments must be a JSON object, such as {\"name\": \"value\"}.";
        switch (root.ValueKind)
        {
            case JsonValueKind.Object:
                arguments = root.Clone();
                return true;
            case JsonValueKind.Null:
                return Empty(out arguments);
            default:
                return false;
        }
    }

    private static bool Empty(out JsonElement arguments)
    {
        using var empty = JsonDocument.Parse("{}");
        arguments = empty.RootElement.Clone();
        return true;
    }

    // ```json ... ``` around the arguments, which a model that is used to answering in Markdown sometimes writes.
    private static string StripFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstLine = text.IndexOf('\n', StringComparison.Ordinal);
        if (firstLine < 0)
        {
            return text;
        }

        var body = text[(firstLine + 1)..].TrimEnd();
        return body.EndsWith("```", StringComparison.Ordinal) ? body[..^3].Trim() : body;
    }

    // The name of an argument that the object gives twice (which one would count is anyone's guess), or null.
    private static string? FirstDuplicate(JsonElement arguments)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return property.Name;
            }
        }

        return null;
    }

    private static string Compact(JsonElement arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriteOptions))
        {
            arguments.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // A name the model gave is shown back only as far as it is safe to: short, with no line breaks.
    private static string Cut(string text)
    {
        var shown = text.Length > 40 ? text[..40] + "..." : text;
        return new string([.. shown.Select(character => char.IsControl(character) ? ' ' : character)]);
    }

    private static ParsedToolCall Refused(ToolCall call, string code, string message) =>
        new(call, default, new ToolCallProblem(code, message));
}
