namespace Assistant.Core.ModelProfiles;

/// <summary>The hardware presets, and which one suits a machine.</summary>
public static class HardwarePresets
{
    /// <summary>
    /// The minimum hardware (PROJECT_SPEC §2): a small context and small batches keep the model's memory low. It is also
    /// the preset for a machine whose memory could not be read, since a machine that is not known is not assumed roomy.
    /// </summary>
    public static HardwarePreset Compact { get; } = new("compact", "Compact (8 GB of memory)", 0, 4096)
    {
        RuntimeArguments = ["--batch-size", "512", "--ubatch-size", "256"],
    };

    /// <summary>The recommended hardware, 12 to 16 GB: the engine's usual settings and the default context.</summary>
    public static HardwarePreset Balanced { get; } = new("balanced", "Balanced (12 to 16 GB of memory)", 11, 16384);

    /// <summary>A roomy machine, 24 GB or more: a larger context than the default is worth its memory.</summary>
    public static HardwarePreset Performance { get; } = new("performance", "Performance (24 GB of memory or more)", 22, 32768);

    /// <summary>Every preset, by growing memory minimum.</summary>
    public static IReadOnlyList<HardwarePreset> All { get; } = [Compact, Balanced, Performance];

    /// <summary>The preset with identifier <paramref name="id"/>, or <see langword="null"/> when there is none.</summary>
    public static HardwarePreset? Find(string? id) =>
        All.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.Ordinal));

    /// <summary>The preset that suits <paramref name="hardware"/>: the highest one its memory reaches.</summary>
    public static HardwarePreset Select(HardwareInfo hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        if (!hardware.IsMemoryKnown)
        {
            return Compact;
        }

        // A machine sold as "16 GB" reports a little less to the runtime (about 15.7), so the minimums are a little lower
        // than the sizes in the names.
        return All.Where(preset => hardware.TotalMemoryGiB >= preset.MinimumMemoryGiB).MaxBy(preset => preset.MinimumMemoryGiB)
            ?? Compact;
    }
}
