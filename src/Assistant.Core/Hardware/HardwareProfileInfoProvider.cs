using Assistant.Core.ModelProfiles;

namespace Assistant.Core.Hardware;

/// <summary>
/// Gives the hardware preset the memory and the processor count that the hardware profile read from Windows (PROJECT_SPEC §5.6, step 124), so that
/// one reading of the PC decides both the preset and the recommendation. When the profile could not read the memory, the runtime's own figure is used.
/// </summary>
public sealed class HardwareProfileInfoProvider : IHardwareInfoProvider
{
    private readonly IHardwareProfileService _profile;
    private readonly IHardwareInfoProvider _fallback;

    /// <summary>Creates the provider over <paramref name="profile"/>, falling back on <paramref name="fallback"/> when the memory is not known.</summary>
    public HardwareProfileInfoProvider(IHardwareProfileService profile, IHardwareInfoProvider fallback)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(fallback);
        _profile = profile;
        _fallback = fallback;
    }

    /// <inheritdoc/>
    public HardwareInfo Get()
    {
        var profile = _profile.Current;
        return profile.Memory.IsKnown
            ? new HardwareInfo(profile.Memory.TotalBytes, Math.Max(1, profile.Cpu.LogicalProcessors))
            : _fallback.Get();
    }
}
