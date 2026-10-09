using System.Text;
using Assistant.Core.Contracts;

namespace Assistant.ModelHost.Processes;

/// <summary>What the model engine runs.</summary>
/// <param name="ModelPath">The fully qualified path of the GGUF model file the engine loads.</param>
internal sealed record ModelProcessLaunch(string ModelPath)
{
    /// <summary>The context the engine is given when a launch names none, in tokens.</summary>
    /// <remarks>
    /// The engine's own default is the model's full training context, which for current models needs far more memory
    /// than the minimum hardware has (PROJECT_SPEC §2, R6).
    /// </remarks>
    public const int DefaultContextLength = ModelFiles.DefaultContextLength;

    /// <summary>
    /// The fully qualified path of the multimodal projector (mmproj) GGUF file that goes with the model, or
    /// <see langword="null"/> for a model that only reads text.
    /// </summary>
    public string? ProjectorPath { get; init; }

    /// <summary>The fully qualified path of a Jinja chat template file that replaces the model's own, or <see langword="null"/>.</summary>
    public string? ChatTemplatePath { get; init; }

    /// <summary>The context window in tokens, or <see langword="null"/> for <see cref="DefaultContextLength"/>.</summary>
    public int? ContextLength { get; init; }

    /// <summary>
    /// The engine's names for the devices to offload the model to (<c>--device</c>): none listed runs on the CPU alone,
    /// and <see langword="null"/> leaves the choice to the engine, which then uses every device it finds.
    /// </summary>
    public IReadOnlyList<string>? Devices { get; init; }

    /// <summary>
    /// More engine options, as command-line tokens, from the model's profile: only what <see cref="EngineArguments"/>
    /// allows, which <see cref="LlamaServerCommand"/> checks.
    /// </summary>
    public IReadOnlyList<string> RuntimeArguments { get; init; } = [];

    // Keeps the paths (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            $"HasProjector = {ProjectorPath is not null}, HasChatTemplate = {ChatTemplatePath is not null}, " +
            $"ContextLength = {ContextLength}, Devices = {Devices?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "engine's choice"}, " +
            $"RuntimeArguments = {RuntimeArguments.Count}");
        return true;
    }
}
