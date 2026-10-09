namespace Assistant.Core.Hardware;

/// <summary>Whether a graphics adapter is a real device.</summary>
public enum GpuAdapterKind
{
    /// <summary>A graphics card, or the graphics built into the processor.</summary>
    Hardware,

    /// <summary>A software renderer such as Microsoft's Basic Render Driver. It is not a graphics card and never runs a model.</summary>
    Software,
}

/// <summary>One graphics adapter, as Windows reports it (PROJECT_SPEC §5.6, step 124).</summary>
/// <param name="Name">The adapter's name, such as "NVIDIA GeForce RTX 5070".</param>
/// <param name="VendorId">The PCI vendor id (0x10DE is NVIDIA, 0x1002 AMD, 0x8086 Intel), or 0 when it is not known.</param>
/// <param name="Kind">Whether it is a real device.</param>
/// <param name="DedicatedVideoMemoryBytes">
/// The memory that belongs to the adapter alone, in bytes: a card's own. It is small or 0 for graphics that share the PC's memory.
/// </param>
/// <param name="SharedSystemMemoryBytes">The system memory the adapter may share with the processor, in bytes.</param>
/// <param name="AvailableDedicatedVideoMemoryBytes">
/// The part of <paramref name="DedicatedVideoMemoryBytes"/> that Windows says programs may use right now (its budget less what is in
/// use), or <see langword="null"/> when Windows would not say. It changes as other programs use the card.
/// </param>
public sealed record GpuAdapterInfo(
    string Name,
    int VendorId,
    GpuAdapterKind Kind,
    long DedicatedVideoMemoryBytes,
    long SharedSystemMemoryBytes,
    long? AvailableDedicatedVideoMemoryBytes)
{
    private const double BytesPerGiB = 1024d * 1024 * 1024;

    /// <summary>The dedicated video memory in GiB.</summary>
    public double DedicatedVideoMemoryGiB => DedicatedVideoMemoryBytes / BytesPerGiB;

    /// <summary>The dedicated video memory that is free now in GiB, or <see langword="null"/> when it is not known.</summary>
    public double? AvailableDedicatedVideoMemoryGiB => AvailableDedicatedVideoMemoryBytes / BytesPerGiB;

    /// <summary>Whether a model could be put in the adapter's own memory: a real device that has some.</summary>
    public bool HasDedicatedMemory => Kind == GpuAdapterKind.Hardware && DedicatedVideoMemoryBytes > 0;
}
