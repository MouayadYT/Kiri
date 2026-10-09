using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Assistant.Core.Tools;

/// <summary>The kinds of value a tool's argument can be: the part of JSON Schema the tools use (PROJECT_SPEC §4.8).</summary>
public enum ToolParameterType
{
    /// <summary>Text.</summary>
    String = 0,

    /// <summary>A whole number.</summary>
    Integer = 1,

    /// <summary>Any number.</summary>
    Number = 2,

    /// <summary>True or false.</summary>
    Boolean = 3,
}

/// <summary>
/// One argument of a tool, as a typed description rather than a hand-written schema (PROJECT_SPEC §4.8). A list of these makes the
/// tool's input schema (<see cref="ToolSchema.Build"/>), which is what the model is told and what a call's arguments are checked
/// against before the tool runs.
/// </summary>
/// <param name="Name">The argument's name, lower snake_case as the model writes it.</param>
/// <param name="Type">What kind of value it is.</param>
/// <param name="Description">What it is for, in words the model acts on.</param>
/// <param name="Required">Whether a call must give it.</param>
/// <param name="Choices">For text, the only values allowed; <see langword="null"/> for any text.</param>
/// <param name="Minimum">For a number, the least it may be; <see langword="null"/> for no least.</param>
/// <param name="Maximum">For a number, the most it may be; <see langword="null"/> for no most.</param>
/// <param name="MaxLength">For text, the most characters it may have; <see langword="null"/> for the usual limit of the checker.</param>
public sealed record ToolParameter(
    string Name,
    ToolParameterType Type,
    string Description,
    bool Required = true,
    IReadOnlyList<string>? Choices = null,
    double? Minimum = null,
    double? Maximum = null,
    int? MaxLength = null);

/// <summary>Makes a tool's input schema from its typed <see cref="ToolParameter"/>s.</summary>
public static partial class ToolSchema
{
    [GeneratedRegex("^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>
    /// The input schema of a tool that takes <paramref name="parameters"/>, as JSON. It says <c>additionalProperties: false</c>, so a call
    /// that gives an argument the tool does not take is refused and not quietly run without it.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A name is not lower snake_case or is given twice, a description is empty, choices go with a value that is not text, a range goes
    /// with a value that is not a number or runs backwards, or a length goes with a value that is not text or is not more than nothing.
    /// </exception>
    public static string Build(IEnumerable<ToolParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var list = parameters.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in list)
        {
            if (parameter is null || parameter.Name is not { } parameterName || !NamePattern().IsMatch(parameterName))
            {
                throw new ArgumentException("An argument is named in lower snake_case.", nameof(parameters));
            }

            if (!seen.Add(parameterName))
            {
                throw new ArgumentException($"Two arguments are named {parameter.Name}.", nameof(parameters));
            }

            if (string.IsNullOrWhiteSpace(parameter.Description))
            {
                throw new ArgumentException($"The argument {parameter.Name} has no description.", nameof(parameters));
            }

            if (parameter.Choices is { Count: > 0 } && parameter.Type != ToolParameterType.String)
            {
                throw new ArgumentException($"The argument {parameter.Name} lists choices but is not text.", nameof(parameters));
            }

            if ((parameter.Minimum is not null || parameter.Maximum is not null)
                && (parameter.Type is not (ToolParameterType.Integer or ToolParameterType.Number)
                    || parameter.Minimum > parameter.Maximum
                    || parameter.Minimum is { } low && !double.IsFinite(low)
                    || parameter.Maximum is { } high && !double.IsFinite(high)))
            {
                throw new ArgumentException($"The argument {parameter.Name} has a range that is not for a number or is not valid.", nameof(parameters));
            }

            if (parameter.MaxLength is not null && (parameter.Type != ToolParameterType.String || parameter.MaxLength <= 0))
            {
                throw new ArgumentException($"The argument {parameter.Name} has a length that is not for text or is not more than nothing.", nameof(parameters));
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            foreach (var parameter in list)
            {
                writer.WriteStartObject(parameter.Name);
                writer.WriteString("type", TypeName(parameter.Type));
                writer.WriteString("description", parameter.Description);
                if (parameter.Choices is { Count: > 0 } choices)
                {
                    writer.WriteStartArray("enum");
                    foreach (var choice in choices)
                    {
                        writer.WriteStringValue(choice);
                    }

                    writer.WriteEndArray();
                }

                if (parameter.Minimum is { } minimum)
                {
                    writer.WriteNumber("minimum", minimum);
                }

                if (parameter.Maximum is { } maximum)
                {
                    writer.WriteNumber("maximum", maximum);
                }

                if (parameter.MaxLength is { } maxLength)
                {
                    writer.WriteNumber("maxLength", maxLength);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            var required = list.Where(parameter => parameter.Required).ToList();
            if (required.Count > 0)
            {
                writer.WriteStartArray("required");
                foreach (var parameter in required)
                {
                    writer.WriteStringValue(parameter.Name);
                }

                writer.WriteEndArray();
            }

            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string TypeName(ToolParameterType type) => type switch
    {
        ToolParameterType.String => "string",
        ToolParameterType.Integer => "integer",
        ToolParameterType.Number => "number",
        ToolParameterType.Boolean => "boolean",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
