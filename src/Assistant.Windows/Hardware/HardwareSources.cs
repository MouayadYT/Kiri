using Assistant.Core.Hardware;

namespace Assistant.Windows.Hardware;

/// <summary>Reads the processor. May throw; the service that asks catches it.</summary>
internal interface ICpuInfoSource
{
    /// <summary>Reads the processor.</summary>
    CpuInfo Read();
}

/// <summary>Reads the physical memory. May throw; the service that asks catches it.</summary>
internal interface IMemoryInfoSource
{
    /// <summary>Reads the memory.</summary>
    SystemMemoryInfo Read();
}

/// <summary>Lists the graphics adapters. May throw; the service that asks catches it.</summary>
internal interface IGpuInfoSource
{
    /// <summary>Lists the adapters, software renderers included.</summary>
    IReadOnlyList<GpuAdapterInfo> Read();
}
