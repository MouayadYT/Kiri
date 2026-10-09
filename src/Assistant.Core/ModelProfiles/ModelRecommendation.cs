namespace Assistant.Core.ModelProfiles;

/// <summary>What decided a <see cref="ModelRecommendation"/>.</summary>
public enum RecommendationBasis
{
    /// <summary>The hardware could not be read well enough, so the smallest profile was recommended.</summary>
    Unknown,

    /// <summary>The model is expected to run on the processor, because no graphics card has memory of its own that fits it.</summary>
    Processor,

    /// <summary>A graphics card has memory of its own that the model fits in.</summary>
    Graphics,
}

/// <summary>
/// The model profile and context window that suit this PC (PROJECT_SPEC §5.6, step 124). It is a recommendation only: the user's own choice
/// of profile, preset or window (<see cref="Settings.ModelSettings"/>) always wins over it.
/// </summary>
/// <param name="Profile">The profile that suits the PC.</param>
/// <param name="ContextTokens">The context window, in tokens, that suits the PC with that profile.</param>
/// <param name="Basis">What decided the profile.</param>
/// <param name="Reasons">One or two sentences that say why, in plain words, for the Settings window.</param>
public sealed record ModelRecommendation(
    ModelProfile Profile, int ContextTokens, RecommendationBasis Basis, IReadOnlyList<string> Reasons);
