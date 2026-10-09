namespace Assistant.Core.ModelProfiles;

/// <summary>
/// Reads the hardware from the .NET runtime: the physical memory the runtime sees (which is the machine's, unless the
/// process runs under a memory limit) and the processor count. It reads once, since neither changes while the app runs.
/// </summary>
public sealed class EnvironmentHardwareInfoProvider : IHardwareInfoProvider
{
    private readonly Lazy<HardwareInfo> _info = new(Read);

    /// <inheritdoc/>
    public HardwareInfo Get() => _info.Value;

    private static HardwareInfo Read() =>
        new(Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes), Environment.ProcessorCount);
}
