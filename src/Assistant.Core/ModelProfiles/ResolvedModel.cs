using Assistant.Core.Contracts;

namespace Assistant.Core.ModelProfiles;

/// <summary>
/// A model profile made concrete for this machine and these settings (<see cref="IModelProfileResolver"/>): the files
/// to load, with the context and the engine options the profile and the hardware preset give them.
/// </summary>
/// <param name="Profile">The profile.</param>
/// <param name="Preset">The hardware preset it runs with.</param>
/// <param name="Files">
/// What to load, with every path fully qualified. The projector is set only when its file exists, so a model whose
/// projector is missing still loads and answers text.
/// </param>
/// <param name="IsInstalled">Whether the model file (and the chat template, if the profile names one) exist.</param>
public sealed record ResolvedModel(ModelProfile Profile, HardwarePreset Preset, ModelFiles Files, bool IsInstalled)
{
    /// <summary>Whether the model will read images: its profile says it can and the projector file exists.</summary>
    public bool ReadsImages => Files.ProjectorPath is not null;

    /// <summary>Whether the profile reads images but its projector file is missing, so images are not accepted.</summary>
    public bool ProjectorMissing => Profile.SupportsVision && Files.ProjectorPath is null;

    /// <summary>
    /// Whether the machine's memory is known and below what the profile recommends, so the model may be slow or fail
    /// to load. It is only a warning: the user's choice stands.
    /// </summary>
    public bool BelowRecommendedMemory { get; init; }

    /// <summary>Whether the model's files are the ones packaged with the Assistant (step 123), not ones in the user's own models folder.</summary>
    public bool IsPackaged { get; init; }

    /// <summary>
    /// Whether the settings chose no profile and this one was chosen for this PC (step 124): the recommended one, or the nearest installed one
    /// when that is not installed. <see langword="false"/> when the user chose the profile.
    /// </summary>
    public bool IsAutomatic { get; init; }

    /// <summary>What was recommended for this PC when <see cref="IsAutomatic"/>; otherwise <see langword="null"/>.</summary>
    public ModelRecommendation? Recommendation { get; init; }
}
