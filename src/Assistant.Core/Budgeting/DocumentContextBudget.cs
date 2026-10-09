using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Core.Settings;

namespace Assistant.Core.Budgeting;

/// <summary>
/// How much of a document goes into a question's prompt (PROJECT_SPEC §4.7, §5.5): the limits the passage selection works within,
/// worked out from the model's window and the user's limits, so the passages are chosen to fit before the budgeter has to cut
/// anything. The budgeter still fits whatever is sent, and tells the user when it cuts.
/// </summary>
public static class DocumentContextBudget
{
    /// <summary>The fewest characters of passages sent, however small the model's window.</summary>
    public const int MinCharacters = 2_000;

    /// <summary>The most characters of passages sent, however large the window: what is asked of a document is found, not read whole.</summary>
    public const int MaxCharacters = 16_000;

    /// <summary>The most passages sent.</summary>
    public const int MaxPassages = 12;

    // The share of the prompt the passages may take: the instructions, the question and the earlier turns need the rest.
    private const double ShareOfPrompt = 0.4;

    // Tokens are estimated by the heuristic, which for prose comes out a little above three characters to a token.
    private const int CharactersPerToken = 3;

    /// <summary>
    /// The limits for reading a file and selecting its passages for <paramref name="model"/> (the default window when it is not
    /// known) under the user's <paramref name="limits"/>: the largest file read is the user's limit, and the passages may take
    /// about two fifths of what is left of the heavy window after the answer's share.
    /// </summary>
    public static DocumentContextOptions For(ModelInfo? model, ContextLimitSettings limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var budget = ContextBudget.Resolve(model ?? new ModelInfo(string.Empty, 0), limits, ContextBudgetMode.Heavy);
        var characters = (int)Math.Clamp(budget.PromptTokens * ShareOfPrompt * CharactersPerToken, MinCharacters, MaxCharacters);
        return new DocumentContextOptions
        {
            Read = new DocumentReadOptions { MaxFileBytes = limits.MaxFileSizeBytes },
            Selection = new PassageSelectionOptions { MaxCharacters = characters, MaxPassages = MaxPassages },
        };
    }
}
