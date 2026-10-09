namespace Assistant.Core.Tools;

/// <summary>
/// Works out a sum deterministically, on this PC: the calculator tool's own arithmetic, for what needs it without a tool call, such as the answer the
/// Search or Ask bar shows under a sum while it is typed (PROJECT_SPEC §4.1). The sum is private content and is never logged.
/// </summary>
public interface ICalculator
{
    /// <summary>Works out <paramref name="expression"/>.</summary>
    /// <param name="expression">The sum, with numbers, <c>+ - * / ^ %</c> and brackets.</param>
    /// <param name="output">The sum as it is read, its value, and a note when the value was rounded.</param>
    /// <returns>Whether it was worked out; a sum that is not one, or has no value (a division by zero), is not.</returns>
    bool TryEvaluate(string expression, out CalculationOutput output);
}
