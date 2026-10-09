using System.Runtime.InteropServices;
using Assistant.Core.Hardware;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Hardware;

/// <summary>Reads the physical memory with <c>GlobalMemoryStatusEx</c>: what Windows can use and what is free of it.</summary>
internal sealed class NativeMemoryInfoSource : IMemoryInfoSource
{
    /// <inheritdoc/>
    public SystemMemoryInfo Read()
    {
        var status = new Kernel32.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Kernel32.MemoryStatusEx>() };
        if (!Kernel32.GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException("Windows would not report the memory.");
        }

        return new SystemMemoryInfo(
            (long)Math.Min(status.TotalPhysical, long.MaxValue), (long)Math.Min(status.AvailablePhysical, long.MaxValue));
    }
}
