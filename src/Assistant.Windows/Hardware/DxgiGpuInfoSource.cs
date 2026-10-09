using System.Runtime.InteropServices;
using Assistant.Core.Hardware;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Hardware;

/// <summary>
/// Lists the graphics adapters through DXGI (<see cref="Dxgi"/>): each one's name, its own video memory, the system memory it may share and, where
/// Windows can say, how much of its own memory is free now (the memory budget less what is in use). It creates no device and draws nothing.
/// </summary>
internal sealed class DxgiGpuInfoSource : IGpuInfoSource
{
    /// <inheritdoc/>
    public IReadOnlyList<GpuAdapterInfo> Read()
    {
        var interfaceId = Dxgi.FactoryIid;
        var result = Dxgi.CreateDXGIFactory1(in interfaceId, out var pointer);
        if (result < 0 || pointer == 0)
        {
            throw new InvalidOperationException("Windows would not list the graphics adapters.");
        }

        IDxgiFactory1 factory;
        try
        {
            factory = (IDxgiFactory1)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }

        var adapters = new List<GpuAdapterInfo>();

        // Windows can list one card more than once: a virtual display driver (for remote desktop or a virtual monitor) gets an adapter of its own that
        // reports the card it draws with, with the same device, subsystem, revision and memory but another locally unique id. Such an adapter is
        // the card again, not a second card, so it is listed once. (Two real cards of one model look the same and are listed once too: the engine
        // lists its own devices before every start, and the recommendation goes by the card with the most memory, which is unaffected.)
        var seen = new HashSet<(uint, uint, uint, uint, nuint, nuint)>();
        try
        {
            for (uint index = 0; ; index++)
            {
                if (factory.EnumAdapters1(index, out var adapter) < 0)
                {
                    break;
                }

                try
                {
                    if (Describe(adapter, seen) is { } info)
                    {
                        adapters.Add(info);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }

        return adapters;
    }

    private static GpuAdapterInfo? Describe(IDxgiAdapter1 adapter, HashSet<(uint, uint, uint, uint, nuint, nuint)> seen)
    {
        if (adapter.GetDesc1(out var description) < 0)
        {
            return null;
        }

        var software = (description.Flags & Dxgi.AdapterFlagSoftware) != 0;
        if (!software && !seen.Add((
                description.VendorId, description.DeviceId, description.SubSysId, description.Revision,
                description.DedicatedVideoMemory, description.SharedSystemMemory)))
        {
            return null;
        }

        return new GpuAdapterInfo(
            Tidy(description.Description),
            (int)description.VendorId,
            software ? GpuAdapterKind.Software : GpuAdapterKind.Hardware,
            Clamp(description.DedicatedVideoMemory),
            Clamp(description.SharedSystemMemory),
            software ? null : AvailableMemory(adapter));
    }

    // Windows 10 and later say what is free of the adapter's own memory; an adapter that cannot says nothing, and that is not a failure.
    private static long? AvailableMemory(IDxgiAdapter1 adapter)
    {
        if (adapter is not IDxgiAdapter3 adapter3
            || adapter3.QueryVideoMemoryInfo(0, Dxgi.SegmentGroupLocal, out var information) < 0)
        {
            return null;
        }

        return Clamp(information.Budget > information.CurrentUsage ? information.Budget - information.CurrentUsage : 0);
    }

    private static long Clamp(nuint value) => (long)Math.Min((ulong)value, long.MaxValue);

    private static long Clamp(ulong value) => (long)Math.Min(value, long.MaxValue);

    private static string Tidy(string? name) =>
        string.Join(' ', (name ?? string.Empty).Split([' ', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
