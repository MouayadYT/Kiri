using System.Text;
using Assistant.Core.Contracts;

namespace Assistant.Core.ModelProfiles;

/// <summary>
/// Everything the Assistant needs to know about one kind of local model (PROJECT_SPEC §5.6): where its files are, what
/// it is, what context to give it, whether it reads images, and how the engine should run it. Nothing else in the app
/// names a model; it asks the <see cref="IModelProfileCatalog"/> for profiles and the <see cref="IModelProfileResolver"/>
/// for the files to load.
/// </summary>
/// <param name="Id">
/// A stable identifier, lower case letters, digits and hyphens: what the settings store to choose the profile, and the
/// id the model has in the host and its status.
/// </param>
/// <param name="DisplayName">What the user is shown.</param>
/// <param name="ModelPath">
/// The GGUF model file, relative to the models folder or fully qualified. The default profiles name a file inside a
/// folder of their own, so installing a model (by download or import, a later step) is only putting files there.
/// </param>
/// <param name="Quantization">
/// The quantization the file is expected to have, as a label such as <c>Q4_K_M</c>. It is shown, never used to load.
/// </param>
/// <param name="Context">The context the model is given by default.</param>
public sealed record ModelProfile(
    string Id,
    string DisplayName,
    string ModelPath,
    string Quantization,
    ModelContextDefaults Context)
{
    private readonly IReadOnlyList<string> _runtimeArguments = [];

    /// <summary>The model's size class, in billions of parameters: 4 for a "4B" model. Only for describing and ordering.</summary>
    public double ParametersBillions { get; init; }

    /// <summary>
    /// The memory the machine needs to run the model comfortably, in GiB (a guide: the model file, its context and the
    /// rest of the system). A profile chosen on a machine with less still loads, and the app says it may be slow.
    /// </summary>
    public int RecommendedMemoryGiB { get; init; }

    /// <summary>
    /// The memory of its own, in GiB, a graphics card needs to hold the model, its projector and a window of 8,192 tokens with room left for the
    /// desktop (a guide, as <see cref="RecommendedMemoryGiB"/> is). <see cref="ModelRecommender"/> recommends a profile beyond the default only
    /// when a card has this much. 0 means the profile is never recommended for the sake of a card.
    /// </summary>
    public int RecommendedVideoMemoryGiB { get; init; }

    /// <summary>Whether the model reads images. It does only when <see cref="ProjectorPath"/> names a file that exists.</summary>
    public bool SupportsVision { get; init; }

    /// <summary>
    /// The multimodal projector (mmproj) file that goes with the model, relative to the models folder or fully
    /// qualified. Set exactly when <see cref="SupportsVision"/> is.
    /// </summary>
    public string? ProjectorPath { get; init; }

    /// <summary>A Jinja chat template file that replaces the template inside the model, or <see langword="null"/>.</summary>
    public string? ChatTemplatePath { get; init; }

    /// <summary>
    /// Engine options for this model, as command-line tokens (<see cref="EngineArguments"/> says which are allowed).
    /// They take precedence over those of the hardware preset.
    /// </summary>
    public IReadOnlyList<string> RuntimeArguments
    {
        get => _runtimeArguments;
        init => _runtimeArguments = value ?? [];
    }

    /// <summary>Describes the first problem with this profile, or returns <see langword="null"/> when it is usable.</summary>
    public string? Validate()
    {
        if (!IsValidId(Id))
        {
            return "The id must be lower case letters, digits and hyphens.";
        }

        if (string.IsNullOrWhiteSpace(DisplayName) || string.IsNullOrWhiteSpace(ModelPath) || string.IsNullOrWhiteSpace(Quantization))
        {
            return "The display name, model file and quantization label are required.";
        }

        if (Context is null)
        {
            return "The context defaults are required.";
        }

        if (ParametersBillions <= 0 || RecommendedMemoryGiB <= 0)
        {
            return "The size class and the recommended memory must be positive.";
        }

        if (RecommendedVideoMemoryGiB < 0)
        {
            return "The recommended video memory may not be negative.";
        }

        if (SupportsVision != !string.IsNullOrWhiteSpace(ProjectorPath))
        {
            return "A model that reads images needs a projector file, and one that does not may not have one.";
        }

        return Context.Validate() ?? EngineArguments.Validate(RuntimeArguments);
    }

    /// <summary>Whether <paramref name="id"/> can be a profile id.</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.All(letter => letter is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
        && id[0] != '-' && id[^1] != '-';

    // Keeps the user's paths (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            $"Id = {Id}, ParametersBillions = {ParametersBillions}, Quantization = {Quantization}, " +
            $"SupportsVision = {SupportsVision}, RuntimeArguments = {RuntimeArguments.Count}");
        return true;
    }
}
