using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Assistant.Core.Tools;

/// <summary>What the calculator's tool returned: the sum and its value, as they are shown.</summary>
/// <param name="Expression">What was calculated, such as <c>9 + 10</c>.</param>
/// <param name="Result">Its value as it is read, such as <c>19</c>.</param>
/// <param name="Secondary">Anything worth adding under the value, such as a rounding note; <see langword="null"/> when there is none.</param>
public sealed record CalculationOutput(string Expression, string Result, string? Secondary = null);

/// <summary>
/// The shape of the calculator's tool, <c>calculate</c> (PROJECT_SPEC §4.1, §4.8): it takes one argument, the sum, and returns the sum
/// and its value as JSON, which the conversation draws as a calculation card (<c>CalculationResult</c>). The tool itself is not part
/// of this step; this is the contract it is to be built to, so that the route for straightforward arithmetic, the registry and the card
/// agree on it. Arithmetic is routed to a calculation only while a tool of this name is registered.
/// </summary>
public static class CalculationToolResults
{
    /// <summary>The name of the calculator's tool.</summary>
    public const string Calculate = "calculate";

    // A sum is written as it is read ("9 + 10"), not with its plus escaped, in what the model and the log see.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The name of its one argument: the sum, written with <c>+ - * / ^ %</c> and parentheses.</summary>
    public const string ExpressionArgument = "expression";

    /// <summary>The arguments of a call that works out <paramref name="expression"/>, as JSON.</summary>
    public static string Arguments(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString(ExpressionArgument, expression);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The JSON a successful calculation returns.</summary>
    public static string Result(CalculationOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("expression", output.Expression);
            writer.WriteString("result", output.Result);
            if (!string.IsNullOrWhiteSpace(output.Secondary))
            {
                writer.WriteString("secondary", output.Secondary);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reads what a calculation returned; <see langword="false"/> for anything that is not a result with a sum and a value.</summary>
    public static bool TryRead(string? outputJson, out CalculationOutput output)
    {
        output = new CalculationOutput("", "");
        if (string.IsNullOrWhiteSpace(outputJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(outputJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("expression", out var expression) || expression.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(expression.GetString()) || string.IsNullOrWhiteSpace(result.GetString()))
            {
                return false;
            }

            var secondary = root.TryGetProperty("secondary", out var note) && note.ValueKind == JsonValueKind.String ? note.GetString() : null;
            output = new CalculationOutput(expression.GetString()!, result.GetString()!, string.IsNullOrWhiteSpace(secondary) ? null : secondary);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
