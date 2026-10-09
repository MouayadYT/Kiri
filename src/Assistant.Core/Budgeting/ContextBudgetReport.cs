namespace Assistant.Core.Budgeting;

/// <summary>What fitting a conversation into the context window did, in counts only, safe to log (PROJECT_SPEC §3.3).</summary>
/// <param name="Mode">The limit the request was held to.</param>
/// <param name="Budget">How the window was shared between the prompt and the answer.</param>
/// <param name="EstimatedPromptTokens">The estimated size of the prompt that fits was sent.</param>
/// <param name="TurnsKept">Earlier turns of the conversation kept, not counting the message being answered.</param>
/// <param name="TurnsLeftOut">Earlier turns left out, oldest first.</param>
/// <param name="ContextItemsKept">Context items sent whole.</param>
/// <param name="ContextItemsShortened">Context items cut short.</param>
/// <param name="ContextItemsLeftOut">Context items left out.</param>
/// <param name="QuestionShortened">Whether the user's own message was too long and lost its middle.</param>
public sealed record ContextBudgetReport(
    ContextBudgetMode Mode,
    ContextBudget Budget,
    int EstimatedPromptTokens,
    int TurnsKept,
    int TurnsLeftOut,
    int ContextItemsKept,
    int ContextItemsShortened,
    int ContextItemsLeftOut,
    bool QuestionShortened)
{
    /// <summary>
    /// What became of each piece of context that would have been sent, in the order it was served (highest rank first),
    /// and then the context of the turns that were left out. Holds no content.
    /// </summary>
    public IReadOnlyList<ContextItemFit> Items { get; init; } = [];

    /// <summary>Whether <paramref name="other"/> is the same report: the same counts, and the same fate for the same items in the same order.</summary>
    public bool Equals(ContextBudgetReport? other) =>
        other is not null
        && Mode == other.Mode
        && Budget == other.Budget
        && EstimatedPromptTokens == other.EstimatedPromptTokens
        && TurnsKept == other.TurnsKept
        && TurnsLeftOut == other.TurnsLeftOut
        && ContextItemsKept == other.ContextItemsKept
        && ContextItemsShortened == other.ContextItemsShortened
        && ContextItemsLeftOut == other.ContextItemsLeftOut
        && QuestionShortened == other.QuestionShortened
        && Items.SequenceEqual(other.Items);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(Mode, Budget, EstimatedPromptTokens, TurnsKept, TurnsLeftOut, ContextItemsLeftOut, Items.Count);

    /// <summary>
    /// Whether the estimated prompt is within the budget. It is not only when the instructions and the smallest useful
    /// piece of the user's message do not fit the window at all, which the engine then refuses.
    /// </summary>
    public bool Fits => EstimatedPromptTokens <= Budget.PromptTokens;

    /// <summary>Whether anything of what the user or the conversation supplied was cut short or left out.</summary>
    public bool AnythingTrimmed =>
        TurnsLeftOut > 0 || ContextItemsShortened > 0 || ContextItemsLeftOut > 0 || QuestionShortened;
}
