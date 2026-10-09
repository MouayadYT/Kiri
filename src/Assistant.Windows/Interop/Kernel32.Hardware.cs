using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>The memory and processor queries of kernel32.dll that the hardware profile reads (PROJECT_SPEC §5.6, step 124).</summary>
internal static partial class Kernel32
{
    /// <summary>The processor group number that stands for every group: <c>ALL_PROCESSOR_GROUPS</c>.</summary>
    public const ushort AllProcessorGroups = 0xFFFF;

    /// <summary><c>RelationProcessorCore</c>: one record for each physical core.</summary>
    public const int RelationProcessorCore = 0;

    /// <summary>The native <c>MEMORYSTATUSEX</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>
    /// Fills <paramref name="buffer"/> with variable-length records of the relationship asked for; called with a null buffer, it sets
    /// <paramref name="length"/> to the size needed and fails with <c>ERROR_INSUFFICIENT_BUFFER</c>.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);

    /// <summary>The number of logical processors in a processor group, or in all of them for <see cref="AllProcessorGroups"/>.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial uint GetActiveProcessorCount(ushort groupNumber);
}
