using System.Windows.Input;

namespace Assistant.UI.Messages;

/// <summary>
/// The result of a calculation, shown in a <see cref="RichAnswerCard"/> as in the floating conversation reference:
/// "9 + 10 =" above a large "19". The calculator's tool produces it; the conversation only draws it.
/// </summary>
public sealed class CalculationResult : RichAnswerCard
{
    /// <summary>The label a calculation's card is known by.</summary>
    public const string CalculationLabel = "Calculation";

    /// <summary>Creates the result of <paramref name="expression"/>, both as they should be read.</summary>
    /// <param name="expression">What was calculated, such as "9 + 10".</param>
    /// <param name="result">Its value, such as "19".</param>
    /// <param name="copyCommand">Copies the value; without one the card has no copy button.</param>
    /// <param name="secondary">Anything worth adding under the value, such as a rounding note.</param>
    public CalculationResult(string expression, string result, ICommand? copyCommand = null, string? secondary = null)
        : base(CalculationLabel, result, Required(expression), secondary, copyCommand)
    {
    }

    private static string Required(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return expression;
    }
}
