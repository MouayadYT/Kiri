using System.Text;

namespace Assistant.Core.Settings;

/// <summary>Local model selection and inference (PROJECT_SPEC §5.6).</summary>
public sealed record ModelSettings
{
    /// <summary>Identifier of the chat model, or <see langword="null"/> until one is installed.</summary>
    public string? ChatModelId { get; init; }

    /// <summary>
    /// Identifier of the vision model, or <see langword="null"/> to use the chat model when it supports vision.
    /// </summary>
    public string? VisionModelId { get; init; }

    /// <summary>
    /// Folder the user picked for installed models, or <see langword="null"/> for the default,
    /// <c>AppPaths.ModelsDirectory</c>.
    /// </summary>
    public string? ModelsDirectory { get; init; }

    /// <summary>
    /// Identifier of the model profile to use (<c>ModelProfiles</c>), or <see langword="null"/> to let the Assistant choose: the profile
    /// recommended for this PC from its hardware (PROJECT_SPEC §5.6, step 124), or the default profile where nothing recommends one.
    /// It applies only while <see cref="ModelFilePath"/> is not set: a model the user pointed at by path is used as it is.
    /// </summary>
    public string? ProfileId { get; init; }

    /// <summary>
    /// Identifier of the hardware preset to run the profile's model with, or <see langword="null"/> to pick the one
    /// that suits this machine's memory (and, with <see cref="ContextLength"/> also <see langword="null"/>, the context window
    /// recommended for this PC).
    /// </summary>
    public string? HardwarePresetId { get; init; }

    /// <summary>
    /// The GGUF model file to load, or <see langword="null"/> to use the model profile's files. This is how the user
    /// points the Assistant at a model of their own, and it overrides the profile.
    /// </summary>
    public string? ModelFilePath { get; init; }

    /// <summary>The llama.cpp device id chosen by the user, or null for automatic selection.</summary>
    public string? GpuDeviceId { get; init; }

    /// <summary>
    /// The multimodal projector (mmproj) GGUF file that goes with <see cref="ModelFilePath"/>, or
    /// <see langword="null"/> for a model that only reads text. With it the model accepts images.
    /// </summary>
    public string? ProjectorFilePath { get; init; }

    /// <summary>
    /// Whether the model is loaded without its projector until a picture is asked about. The projector takes memory of its own while it is loaded
    /// (about twice its file's size); with this on, the model is loaded again with it for a question that carries a picture, which takes as long as
    /// loading the model does, and without it again a few seconds after the last such question is answered. Off by default: pictures are then read at once.
    /// </summary>
    public bool VisionOnDemand { get; init; }

    /// <summary>A Jinja chat template file that replaces the template inside the model, or <see langword="null"/>.</summary>
    public string? ChatTemplateFilePath { get; init; }

    /// <summary>
    /// The context window to load the model with, in tokens, or <see langword="null"/> for the default: the one recommended for this PC
    /// or, with a hardware preset chosen, the profile's held to what that preset allows. A value set here is used as it is.
    /// </summary>
    public int? ContextLength { get; init; }

    /// <summary>How long a loaded model may stay idle before it is unloaded.</summary>
    public TimeSpan IdleUnloadTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Whether to use GPU acceleration when available. CPU inference is always the fallback.</summary>
    public bool UseGpuAcceleration { get; init; } = true;

    // Keeps the user's folder and file paths (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            $"ChatModelId = {ChatModelId}, VisionModelId = {VisionModelId}, ProfileId = {ProfileId}, " +
            $"HardwarePresetId = {HardwarePresetId}, " +
            $"HasModelFile = {ModelFilePath is not null}, HasProjectorFile = {ProjectorFilePath is not null}, " +
            $"HasChatTemplateFile = {ChatTemplateFilePath is not null}, ContextLength = {ContextLength}, " +
            $"IdleUnloadTimeout = {IdleUnloadTimeout}, UseGpuAcceleration = {UseGpuAcceleration}");
        return true;
    }
}
