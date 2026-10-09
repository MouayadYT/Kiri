using System.Text;

namespace Assistant.Core.Contracts;

/// <summary>
/// The files that make up one local model (PROJECT_SPEC §5.6): a GGUF model file, and for a model that reads images a
/// multimodal projector file that belongs to it. Every path is fully qualified.
/// </summary>
/// <param name="ModelPath">The GGUF model file.</param>
/// <remarks>Files come from a model profile (<c>ModelProfiles</c>) or, until one is chosen, from the settings' paths.</remarks>
public sealed record ModelFiles(string ModelPath)
{
    private readonly IReadOnlyList<string> _runtimeArguments = [];

    /// <summary>The smallest context the host accepts, in tokens.</summary>
    public const int MinContextLength = 256;

    /// <summary>The largest context the host accepts, in tokens.</summary>
    public const int MaxContextLength = 1_048_576;

    /// <summary>
    /// The context the host loads a model with when <see cref="ContextLength"/> is not set, in tokens, which is also the window an
    /// ordinary conversation is given by default. The engine may start with less, for a model trained on a shorter context.
    /// </summary>
    public const int DefaultContextLength = 8000;

    /// <summary>
    /// The window a conversation that carries files is given by default, in tokens: the model is loaded again with it while the user asks
    /// about files, and with <see cref="DefaultContextLength"/> again afterwards, so that the memory it takes is only taken while it is needed.
    /// </summary>
    public const int DocumentContextLength = 32000;

    /// <summary>
    /// The multimodal projector (mmproj) GGUF file that goes with the model, or <see langword="null"/> for a model
    /// that only reads text. With it the model accepts images.
    /// </summary>
    public string? ProjectorPath { get; init; }

    /// <summary>
    /// A Jinja chat template file that replaces the template inside the model, or <see langword="null"/> to use the
    /// model's own.
    /// </summary>
    public string? ChatTemplatePath { get; init; }

    /// <summary>
    /// The context window to load the model with, in tokens, or <see langword="null"/> for the host's default. The
    /// window uses memory in proportion to its size.
    /// </summary>
    public int? ContextLength { get; init; }

    /// <summary>
    /// Whether the model runs on the processor alone (the user's "Use the graphics card" setting is off). It is what crosses to the host, as the field's absence
    /// has to mean "as before", and a field left out of a message reads as <see langword="false"/>.
    /// </summary>
    public bool CpuOnly { get; init; }

    /// <summary>A specific engine device, or null for automatic selection.</summary>
    public string? GpuDeviceId { get; init; }

    /// <summary>Whether the engine may offload the model to a graphics card when there is one. The opposite of <see cref="CpuOnly"/>, and on by default.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UseGpu => !CpuOnly;

    /// <summary>
    /// The model's identifier, or <see langword="null"/> to derive one from the model file's name
    /// (<see cref="DeriveModelId"/>). A model profile sets it to the profile's id.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Options for the model engine beyond the files, as command-line tokens such as <c>["--threads", "6"]</c>. Only
    /// what <see cref="EngineArguments"/> allows is accepted.
    /// </summary>
    public IReadOnlyList<string> RuntimeArguments
    {
        get => _runtimeArguments;
        init => _runtimeArguments = value ?? [];
    }

    /// <summary>
    /// The model's identifier: <see cref="ModelId"/> when it is set, else one from the model file's name, which the host
    /// and the status report use for a model that has no profile.
    /// </summary>
    public string DeriveModelId()
    {
        if (!string.IsNullOrWhiteSpace(ModelId))
        {
            return ModelId;
        }

        var name = Path.GetFileNameWithoutExtension(ModelPath);
        return string.IsNullOrWhiteSpace(name) ? "model" : name;
    }

    internal bool IsWellFormed() =>
        IsFullyQualified(ModelPath)
        && (ProjectorPath is null || IsFullyQualified(ProjectorPath))
        && (ChatTemplatePath is null || IsFullyQualified(ChatTemplatePath))
        && (ContextLength is null || ContextLength is >= MinContextLength and <= MaxContextLength)
        && (ModelId is null || !string.IsNullOrWhiteSpace(ModelId))
        && (GpuDeviceId is null || System.Text.RegularExpressions.Regex.IsMatch(GpuDeviceId, "^[A-Za-z][A-Za-z0-9_]{0,63}$"))
        && EngineArguments.IsValid(RuntimeArguments);

    private static bool IsFullyQualified(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    // Keeps the user's paths (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            $"HasProjector = {ProjectorPath is not null}, HasChatTemplate = {ChatTemplatePath is not null}, " +
            $"ContextLength = {ContextLength}, UseGpu = {UseGpu}, RuntimeArguments = {RuntimeArguments.Count}");
        return true;
    }
}
