using Assistant.Core.Contracts;
using Assistant.Core.Settings;

namespace Assistant.Core.Budgeting;

/// <summary>How the model's context window is shared between the prompt and the answer for one request.</summary>
/// <param name="WindowTokens">
/// The tokens the prompt and the answer may take together: the limit for the request's mode, or the model's own window
/// when that is smaller.
/// </param>
/// <param name="ReservedOutputTokens">The tokens kept back for the answer, which is also the most it is asked to write.</param>
public readonly record struct ContextBudget(int WindowTokens, int ReservedOutputTokens)
{
    /// <summary>The tokens left for the prompt.</summary>
    public int PromptTokens => WindowTokens - ReservedOutputTokens;

    /// <summary>
    /// Works out the budget of a request of <paramref name="mode"/> to <paramref name="model"/> under
    /// <paramref name="limits"/>.
    /// </summary>
    /// <remarks>
    /// The window is the mode's limit, cut down to the model's context length. A limit of zero or less does not apply,
    /// and a model with no known context length has <see cref="ModelFiles.DefaultContextLength"/>. The answer is
    /// reserved <see cref="ContextLimitSettings.ReservedOutputTokens"/>, the default when that is zero or less, and never
    /// more than half the window, so a small window still leaves room for the prompt.
    /// </remarks>
    public static ContextBudget Resolve(ModelInfo model, ContextLimitSettings limits, ContextBudgetMode mode)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(limits);

        var window = model.ContextLength > 0 ? model.ContextLength : ModelFiles.DefaultContextLength;
        var limit = mode == ContextBudgetMode.Heavy ? limits.HeavyContextTokens : limits.NormalContextTokens;
        if (limit > 0)
        {
            window = Math.Min(window, limit);
        }

        var wanted = limits.ReservedOutputTokens > 0
            ? limits.ReservedOutputTokens
            : new ContextLimitSettings().ReservedOutputTokens;
        return new ContextBudget(window, Math.Clamp(wanted, 1, Math.Max(1, window / 2)));
    }
}
