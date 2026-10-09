namespace Assistant.Core.ModelProfiles;

/// <summary>What the machine offers a local model. Graphics cards are not listed: the model host asks the engine for its devices before each start (PROJECT_SPEC §5.6).</summary>
/// <param name="TotalMemoryBytes">
/// The machine's physical memory in bytes, or 0 when it could not be read.
/// </param>
/// <param name="LogicalProcessors">The number of logical processors.</param>
public sealed record HardwareInfo(long TotalMemoryBytes, int LogicalProcessors)
{
    private const double BytesPerGiB = 1024d * 1024 * 1024;

    /// <summary>The physical memory in GiB, or 0 when it is not known.</summary>
    public double TotalMemoryGiB => TotalMemoryBytes / BytesPerGiB;

    /// <summary>Whether the physical memory is known.</summary>
    public bool IsMemoryKnown => TotalMemoryBytes > 0;
}
