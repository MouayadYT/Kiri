namespace Assistant.Core.Hardware;

/// <summary>The PC's physical memory (PROJECT_SPEC §5.6, step 124).</summary>
/// <param name="TotalBytes">The physical memory Windows can use, in bytes, or 0 when it could not be read.</param>
/// <param name="AvailableBytes">The part of it that is free now, in bytes, or 0 when it could not be read.</param>
public sealed record SystemMemoryInfo(long TotalBytes, long AvailableBytes)
{
    private const double BytesPerGiB = 1024d * 1024 * 1024;

    /// <summary>The physical memory in GiB.</summary>
    public double TotalGiB => TotalBytes / BytesPerGiB;

    /// <summary>The free memory in GiB.</summary>
    public double AvailableGiB => AvailableBytes / BytesPerGiB;

    /// <summary>Whether the total is known.</summary>
    public bool IsKnown => TotalBytes > 0;
}
