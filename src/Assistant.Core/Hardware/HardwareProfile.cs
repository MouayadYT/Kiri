namespace Assistant.Core.Hardware;

/// <summary>The parts of the hardware profile that could not be read.</summary>
[Flags]
public enum HardwareParts
{
    /// <summary>Everything was read.</summary>
    None = 0,

    /// <summary>The processor's name or core count could not be read.</summary>
    Processor = 1,

    /// <summary>The memory could not be read.</summary>
    Memory = 2,

    /// <summary>The graphics adapters could not be listed.</summary>
    Graphics = 4,
}

/// <summary>
/// What this PC offers a local model (PROJECT_SPEC §5.6, step 124): the processor, the memory and the graphics adapters, read from
/// Windows on this PC and never sent anywhere. A part that could not be read is empty and named in <see cref="Unreadable"/>, so a PC that
/// hides something is described by what is known and never assumed to be roomy.
/// </summary>
/// <param name="Cpu">The processor.</param>
/// <param name="Memory">The physical memory.</param>
/// <param name="Gpus">Every graphics adapter Windows lists, software renderers included.</param>
public sealed record HardwareProfile(CpuInfo Cpu, SystemMemoryInfo Memory, IReadOnlyList<GpuAdapterInfo> Gpus)
{
    /// <summary>The parts that could not be read.</summary>
    public HardwareParts Unreadable { get; init; }

    /// <summary>The adapter with the most memory of its own among those that could hold a model, or <see langword="null"/> when none could.</summary>
    public GpuAdapterInfo? BestGpu => Gpus.Where(gpu => gpu.HasDedicatedMemory).MaxBy(gpu => gpu.DedicatedVideoMemoryBytes);

    /// <summary>A profile of a PC that could not be read at all.</summary>
    public static HardwareProfile Unknown { get; } = new(
        new CpuInfo(string.Empty, 0, Environment.ProcessorCount, string.Empty, 0), new SystemMemoryInfo(0, 0), [])
    {
        Unreadable = HardwareParts.Processor | HardwareParts.Memory | HardwareParts.Graphics,
    };
}
