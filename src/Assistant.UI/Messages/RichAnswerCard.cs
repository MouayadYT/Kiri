using System.Windows.Input;

namespace Assistant.UI.Messages;

/// <summary>
/// A result presented on its own in a black card, as in the floating conversation reference: a small, dim caption
/// above the result in large type, an optional line under it, and a round copy button on the right. A tool whose result
/// is one value, such as a calculation, a conversion or a date, shows it with this card or a kind derived from it
/// (<see cref="CalculationResult"/>); the card never holds prose.
/// </summary>
public class RichAnswerCard : MessageCard
{
    /// <summary>Creates a card for <paramref name="result"/>.</summary>
    /// <param name="label">What the result is, such as "Calculation".</param>
    /// <param name="result">The result, as it should be read.</param>
    /// <param name="expression">What was worked out to get the result, if anything, such as "9 + 10".</param>
    /// <param name="secondary">Anything worth adding under the result, such as a rounding note or another unit.</param>
    /// <param name="copyCommand">Copies the result; without one the card has no copy button.</param>
    public RichAnswerCard(
        string label, string result, string? expression = null, string? secondary = null, ICommand? copyCommand = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(result);
        Label = label;
        Result = result;
        Expression = string.IsNullOrWhiteSpace(expression) ? null : expression;
        Secondary = string.IsNullOrWhiteSpace(secondary) ? null : secondary;
        CopyCommand = copyCommand;
    }

    /// <summary>What the result is, such as "Calculation". It names the card for assistive technology, and is its
    /// caption when there is no <see cref="Expression"/>.</summary>
    public string Label { get; }

    /// <summary>What was worked out, such as "9 + 10", or <see langword="null"/>.</summary>
    public string? Expression { get; }

    /// <summary>The result, drawn large.</summary>
    public string Result { get; }

    /// <summary>A smaller line under the result, or <see langword="null"/>.</summary>
    public string? Secondary { get; }

    /// <summary>Copies the result, from the card's copy button; <see langword="null"/> when it cannot be copied.</summary>
    public ICommand? CopyCommand { get; }

    /// <summary>
    /// The dim line above the result: the expression and an equals sign, as in "9 + 10 =", or else the label.
    /// </summary>
    public string Caption => Expression is null ? Label : Expression + " =";
}
