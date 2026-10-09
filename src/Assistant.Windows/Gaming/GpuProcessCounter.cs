using System.Runtime.InteropServices;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Gaming;

/// <summary>Reads how much one process uses the graphics card, a reading at a time.</summary>
internal interface IGpuSampler : IDisposable
{
    /// <summary>
    /// The process's use of the graphics card since the reading before (or since this was opened), or <see langword="null"/> when Windows has
    /// nothing to say yet.
    /// </summary>
    GpuUsage? Sample();
}

/// <summary>
/// One process's use of the graphics card, from the performance counters Windows keeps for every process and every graphics card (the
/// numbers Task Manager shows): its share of the 3D and video-decode engines and the graphics memory it holds. It is opened for one process
/// only while that process is being considered, asks for that process's counters alone, and costs nothing between readings.
/// </summary>
internal sealed unsafe class GpuProcessCounter : IGpuSampler
{
    private nint _query;
    private readonly nint _engines;
    private readonly nint _memory;
    private readonly nint _shared;

    private GpuProcessCounter(nint query, nint engines, nint memory, nint shared)
    {
        _query = query;
        _engines = engines;
        _memory = memory;
        _shared = shared;
    }

    /// <summary>Opens the counters of process <paramref name="processId"/>, or returns <see langword="null"/> when this PC has none (no graphics driver that reports them).</summary>
    public static GpuProcessCounter? Open(int processId)
    {
        if (Pdh.OpenQuery(0, 0, out var query) != Pdh.ERROR_SUCCESS)
        {
            return null;
        }

        // An instance is named pid_<id>_luid_..._engtype_<kind>: one for each engine of each graphics card the process has used.
        var engines = Pdh.AddEnglishCounter(query, $@"\GPU Engine(pid_{processId}_*)\Utilization Percentage", 0, out var engineCounter);
        var memory = Pdh.AddEnglishCounter(query, $@"\GPU Process Memory(pid_{processId}_*)\Dedicated Usage", 0, out var memoryCounter);
        var shared = Pdh.AddEnglishCounter(query, $@"\GPU Process Memory(pid_{processId}_*)\Shared Usage", 0, out var sharedCounter);
        if (engines != Pdh.ERROR_SUCCESS && memory != Pdh.ERROR_SUCCESS)
        {
            Pdh.CloseQuery(query);
            return null;
        }

        // The first reading: a share of time is the difference between two.
        Pdh.CollectQueryData(query);
        return new GpuProcessCounter(
            query, engines == Pdh.ERROR_SUCCESS ? engineCounter : 0, memory == Pdh.ERROR_SUCCESS ? memoryCounter : 0,
            shared == Pdh.ERROR_SUCCESS ? sharedCounter : 0);
    }

    /// <inheritdoc/>
    public GpuUsage? Sample()
    {
        if (_query == 0 || Pdh.CollectQueryData(_query) != Pdh.ERROR_SUCCESS)
        {
            return null;
        }

        double render = 0;
        double decode = 0;
        long dedicated = 0;
        long sharedBytes = 0;
        var any = false;
        if (_engines != 0)
        {
            any |= Read(_engines, Pdh.PDH_FMT_DOUBLE, (name, item) =>
            {
                if (name.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                {
                    render += item.DoubleValue;
                }
                else if (name.Contains("engtype_VideoDecode", StringComparison.OrdinalIgnoreCase))
                {
                    decode += item.DoubleValue;
                }
            });
        }

        if (_memory != 0)
        {
            any |= Read(_memory, Pdh.PDH_FMT_LARGE, (_, item) => dedicated += item.LargeValue);
        }

        if (_shared != 0)
        {
            any |= Read(_shared, Pdh.PDH_FMT_LARGE, (_, item) => sharedBytes += item.LargeValue);
        }

        return any ? new GpuUsage(Math.Min(render, 100), Math.Min(decode, 100), dedicated, sharedBytes) : null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        var query = Interlocked.Exchange(ref _query, 0);
        if (query != 0)
        {
            Pdh.CloseQuery(query);
        }
    }

    private delegate void Item(string name, Pdh.CounterValueItem item);

    // Every instance of a counter with its value. Whether there was at least one.
    private static bool Read(nint counter, uint format, Item each)
    {
        uint size = 0;
        if (Pdh.GetFormattedCounterArray(counter, format, ref size, out _, null) != Pdh.PDH_MORE_DATA || size == 0)
        {
            return false;
        }

        var buffer = new byte[size];
        fixed (byte* start = buffer)
        {
            if (Pdh.GetFormattedCounterArray(counter, format, ref size, out var count, start) != Pdh.ERROR_SUCCESS)
            {
                return false;
            }

            var items = (Pdh.CounterValueItem*)start;
            for (var index = 0; index < count; index++)
            {
                // A status other than the two that mean "valid" is an instance that came or went between the readings.
                if (items[index].Status is 0 or 1)
                {
                    each(Marshal.PtrToStringUni(items[index].Name) ?? "", items[index]);
                }
            }

            return count > 0;
        }
    }
}
