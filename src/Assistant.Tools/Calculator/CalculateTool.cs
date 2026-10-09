using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;

namespace Assistant.Tools.Calculator;

/// <summary>
/// <c>calculate</c> (PROJECT_SPEC §4.1, §4.8): works out a sum, deterministically and on this PC, with a parser that knows numbers,
/// <c>+ - * / ^ %</c> and brackets and nothing else (<see cref="ArithmeticEvaluator"/>): never code, never a model's guess at the
/// answer. It returns a <see cref="CalculationOutput"/> as <see cref="CalculationToolResults"/> writes it: the sum as it was read, its
/// value, and a note when the value was rounded. The conversation draws that as a calculation card, and the bar's arithmetic is
/// answered with it without asking a model.
/// </summary>
public static class CalculateTool
{
    /// <summary>Creates the tool.</summary>
    public static ITool Create() => new HandlerTool(Definition, RunAsync);

    /// <summary>What the model is told of the tool.</summary>
    public static ToolDefinition Definition { get; } = ToolDefinition.Create(
        CalculationToolResults.Calculate,
        "Work out a sum exactly. Use it for any arithmetic with numbers, and never work a sum out yourself. Write the sum with numbers, " +
        "+ - * / ^ and brackets: 9 + 10, (3 + 4) * 5, 2 ^ 10. A % between two numbers is the remainder, as 10 % 3 is 1; for a percentage " +
        "write it as a product (15% of 200 is 0.15 * 200). It returns the sum and its value.",
        [
            new ToolParameter(
                CalculationToolResults.ExpressionArgument,
                ToolParameterType.String,
                "The sum, such as (3 + 4) * 5. Only numbers, + - * / ^ % and brackets.",
                MaxLength: ArithmeticEvaluator.MaxLength),
        ],
        RiskLevel.ReadOnly,
        timeout: TimeSpan.FromSeconds(5));

    private static Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var expression = arguments.GetProperty(CalculationToolResults.ExpressionArgument).GetString();
        if (!ArithmeticEvaluator.TryEvaluate(expression, out var output, out var problem))
        {
            return Task.FromResult(ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, problem ?? "That could not be worked out."));
        }

        return Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, CalculationToolResults.Result(output)));
    }
}
