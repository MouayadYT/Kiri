using System.Runtime.InteropServices;
using Assistant.Core.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Windows.Hardware;

/// <summary>
/// Reads what this PC offers a local model from Windows (<see cref="IHardwareProfileService"/>, PROJECT_SPEC §5.6, step 124): the processor, the
/// memory and the graphics adapters with their video memory. Each part is read on its own, so a part Windows will not give leaves the others intact
/// and is named in <see cref="HardwareProfile.Unreadable"/>. Nothing is sent anywhere or written to disk, and the log holds counts and sizes, never
/// a name.
/// </summary>
public sealed partial class WindowsHardwareProfileService : IHardwareProfileService
{
    private readonly ICpuInfoSource _cpu;
    private readonly IMemoryInfoSource _memory;
    private readonly IGpuInfoSource _gpu;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private HardwareProfile? _current;

    /// <summary>Creates the service over the real Windows readers.</summary>
    /// <param name="logger">Where the outcome is logged; nowhere when omitted.</param>
    public WindowsHardwareProfileService(ILogger<WindowsHardwareProfileService>? logger = null)
        : this(new NativeCpuInfoSource(), new NativeMemoryInfoSource(), new DxgiGpuInfoSource(), logger)
    {
    }

    internal WindowsHardwareProfileService(
        ICpuInfoSource cpu, IMemoryInfoSource memory, IGpuInfoSource gpu, ILogger? logger = null)
    {
        _cpu = cpu;
        _memory = memory;
        _gpu = gpu;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public HardwareProfile Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= Read();
            }
        }
    }

    /// <inheritdoc/>
    public HardwareProfile Refresh()
    {
        lock (_gate)
        {
            return _current = Read();
        }
    }

    private HardwareProfile Read()
    {
        var unreadable = HardwareParts.None;

        var cpu = HardwareProfile.Unknown.Cpu;
        try
        {
            cpu = _cpu.Read();
            if (cpu.PhysicalCores == 0 && cpu.Name.Length == 0)
            {
                unreadable |= HardwareParts.Processor;
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            unreadable |= HardwareParts.Processor;
        }

        var memory = new SystemMemoryInfo(0, 0);
        try
        {
            memory = _memory.Read();
            if (!memory.IsKnown)
            {
                unreadable |= HardwareParts.Memory;
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            unreadable |= HardwareParts.Memory;
        }

        IReadOnlyList<GpuAdapterInfo> gpus = [];
        try
        {
            gpus = _gpu.Read();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            unreadable |= HardwareParts.Graphics;
        }

        var profile = new HardwareProfile(cpu, memory, gpus) { Unreadable = unreadable };
        LogRead(
            _logger, profile.Cpu.LogicalProcessors, (int)Math.Round(profile.Memory.TotalGiB), gpus.Count,
            (int)Math.Round(profile.BestGpu?.DedicatedVideoMemoryGiB ?? 0), unreadable);
        return profile;
    }

    // What Windows or its drivers throw when a part cannot be read: a failed call, a missing library, a driver that answers badly.
    private static bool IsExpected(Exception exception) =>
        exception is InvalidOperationException or System.ComponentModel.Win32Exception or COMException or DllNotFoundException
            or EntryPointNotFoundException or UnauthorizedAccessException or IOException or InvalidCastException;

    [LoggerMessage(
        EventId = 5100,
        Level = LogLevel.Information,
        Message = "Read the hardware: {LogicalProcessors} logical processors, {MemoryGiB} GiB of memory, {Adapters} graphics adapters, " +
                  "{BestVideoGiB} GiB in the largest, unreadable parts {Unreadable}")]
    private static partial void LogRead(ILogger logger, int logicalProcessors, int memoryGiB, int adapters, int bestVideoGiB, HardwareParts unreadable);
}
