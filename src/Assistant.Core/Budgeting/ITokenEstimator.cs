namespace Assistant.Core.Budgeting;

/// <summary>
/// Estimates how many tokens a text takes in the model's context window, without the model's tokenizer, which lives in
/// the model host's engine.
/// </summary>
/// <remarks>
/// Trimming relies on two properties every implementation must keep: the estimate is never negative, and it never
/// falls when text is added to either end, so a longer piece of a text never costs less than a shorter one inside it.
/// An estimate should err on the high side: too low a guess sends the engine a prompt it refuses.
/// </remarks>
public interface ITokenEstimator
{
    /// <summary>Estimates the tokens <paramref name="text"/> takes. An empty text takes none.</summary>
    int Estimate(ReadOnlySpan<char> text);
}
