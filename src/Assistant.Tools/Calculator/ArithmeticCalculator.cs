using Assistant.Core.Tools;

namespace Assistant.Tools.Calculator;

/// <summary>The app's <see cref="ICalculator"/>: the arithmetic of the <c>calculate</c> tool (<see cref="ArithmeticEvaluator"/>), with no tool call.</summary>
public sealed class ArithmeticCalculator : ICalculator
{
    /// <inheritdoc/>
    public bool TryEvaluate(string expression, out CalculationOutput output) =>
        ArithmeticEvaluator.TryEvaluate(expression, out output, out _);
}
