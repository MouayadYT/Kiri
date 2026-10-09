using System.Globalization;
using Assistant.Core.Contracts;
using Assistant.Core.Hardware;

namespace Assistant.Core.ModelProfiles;

/// <summary>
/// Recommends a model profile and a context window from the hardware profile, by the thresholds in <see cref="RecommendationThresholds"/>
/// (PROJECT_SPEC §5.6, step 124). It works from the catalog alone, so a profile added to it is considered with no change here.
/// </summary>
public sealed class ModelRecommender : IModelRecommender
{
    private readonly IModelProfileCatalog _catalog;
    private readonly IHardwareProfileService _hardware;

    /// <summary>Creates the recommender over <paramref name="catalog"/>, reading this PC from <paramref name="hardware"/>.</summary>
    public ModelRecommender(IModelProfileCatalog catalog, IHardwareProfileService hardware)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(hardware);
        _catalog = catalog;
        _hardware = hardware;
    }

    /// <inheritdoc/>
    public ModelRecommendation Recommend() => Recommend(_hardware.Current);

    /// <inheritdoc/>
    public ModelRecommendation Recommend(HardwareProfile hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        var baseline = _catalog.Default;
        var gpu = hardware.BestGpu;
        var larger = _catalog.Profiles.Where(profile => profile.ParametersBillions > baseline.ParametersBillions).ToArray();
        var chosen = larger
            .Where(profile => FitsGraphics(profile, gpu) && FitsMemory(profile, hardware.Memory))
            .MaxBy(profile => profile.ParametersBillions) ?? baseline;

        var basis = FitsGraphics(chosen, gpu)
            ? RecommendationBasis.Graphics
            : hardware.Memory.IsKnown ? RecommendationBasis.Processor : RecommendationBasis.Unknown;

        var (tokens, contextReason) = Context(hardware, chosen);
        var reasons = new List<string> { ProfileReason(hardware, gpu, baseline, chosen, larger) };
        if (contextReason is not null)
        {
            reasons.Add(contextReason);
        }

        return new ModelRecommendation(chosen, tokens, basis, reasons);
    }

    /// <inheritdoc/>
    public int RecommendContext(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Context(_hardware.Current, profile).Tokens;
    }

    private static bool FitsGraphics(ModelProfile profile, GpuAdapterInfo? gpu) =>
        gpu is not null && profile.RecommendedVideoMemoryGiB > 0
        && gpu.DedicatedVideoMemoryGiB + RecommendationThresholds.ToleranceGiB >= profile.RecommendedVideoMemoryGiB;

    private static bool FitsMemory(ModelProfile profile, SystemMemoryInfo memory) =>
        memory.IsKnown && memory.TotalGiB + RecommendationThresholds.ToleranceGiB >= profile.RecommendedMemoryGiB;

    // The window the PC's memory allows, held to the model's usual one unless the card has room for more.
    private static (int Tokens, string? Reason) Context(HardwareProfile hardware, ModelProfile profile)
    {
        var preset = HardwarePresets.Select(new HardwareInfo(hardware.Memory.TotalBytes, hardware.Cpu.LogicalProcessors));
        var gpu = hardware.BestGpu;
        var roomy = FitsGraphics(profile, gpu)
            && gpu!.DedicatedVideoMemoryGiB + RecommendationThresholds.ToleranceGiB >= RecommendationThresholds.LargeContextVideoMemoryGiB;
        var usual = profile.Context.LoadTokens;
        var tokens = Math.Max(ModelFiles.MinContextLength, roomy ? preset.MaxContextTokens : Math.Min(preset.MaxContextTokens, usual));
        var window = tokens.ToString("N0", CultureInfo.CurrentCulture);

        string? reason;
        if (!hardware.Memory.IsKnown)
        {
            reason = $"This PC's memory could not be read, so the window is kept small ({window} tokens).";
        }
        else if (roomy && tokens > usual)
        {
            reason = $"A window of {window} tokens: the graphics card has room for more than the usual {usual.ToString("N0", CultureInfo.CurrentCulture)}.";
        }
        else if (tokens < usual)
        {
            reason = $"A window of {window} tokens: this PC's {Gb(hardware.Memory.TotalGiB)} of memory holds it back from the usual {usual.ToString("N0", CultureInfo.CurrentCulture)}.";
        }
        else
        {
            reason = $"A window of {window} tokens, the model's usual one.";
        }

        return (tokens, reason);
    }

    private static string ProfileReason(
        HardwareProfile hardware, GpuAdapterInfo? gpu, ModelProfile baseline, ModelProfile chosen, ModelProfile[] larger)
    {
        if (chosen != baseline && gpu is not null)
        {
            return $"{gpu.Name} has {Gb(gpu.DedicatedVideoMemoryGiB)} of memory of its own, enough for the {chosen.DisplayName} model, " +
                   $"and this PC has {Gb(hardware.Memory.TotalGiB)} of memory.";
        }

        if (gpu is not null && FitsGraphics(baseline, gpu))
        {
            var next = larger.OrderBy(profile => profile.ParametersBillions).FirstOrDefault();
            var enough = $"{gpu.Name} has {Gb(gpu.DedicatedVideoMemoryGiB)} of memory of its own, enough for the {baseline.DisplayName} model";
            if (next is null)
            {
                return enough + ".";
            }

            return FitsGraphics(next, gpu)
                ? $"{enough}. The {next.DisplayName} model also needs about {next.RecommendedMemoryGiB} GB of the PC's memory, and this PC has {Gb(hardware.Memory.TotalGiB)}."
                : $"{enough}, but the {next.DisplayName} model needs about {next.RecommendedVideoMemoryGiB} GB.";
        }

        if (gpu is not null)
        {
            return $"{gpu.Name} has {Gb(gpu.DedicatedVideoMemoryGiB)} of memory of its own, less than the {baseline.RecommendedVideoMemoryGiB} GB " +
                   $"the {baseline.DisplayName} model likes, so it will run mostly on the processor.";
        }

        return hardware.Unreadable.HasFlag(HardwareParts.Graphics)
            ? $"This PC's graphics cards could not be read, so the {baseline.DisplayName} model, which runs on the processor, is recommended."
            : $"No graphics card with memory of its own was found, so the {baseline.DisplayName} model, which is quick on the processor alone, is recommended.";
    }

    private static string Gb(double gibibytes) => $"{gibibytes.ToString("0.#", CultureInfo.CurrentCulture)} GB";
}
