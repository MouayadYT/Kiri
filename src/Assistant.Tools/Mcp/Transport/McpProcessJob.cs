using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Assistant.Tools.Mcp;

/// <summary>
/// A Windows job object that ends every process in it when it is closed. Closing is also what Windows does when the app ends in any way,
/// crashes included, so a program the Assistant started for an integration (and any program that one started in turn) never outlives the app.
/// </summary>
internal sealed partial class McpProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private readonly SafeJobHandle _handle;

    private McpProcessJob(SafeJobHandle handle) => _handle = handle;

    /// <summary>Creates an empty job that ends its processes when it is closed, or <see langword="null"/> when that is not possible here.</summary>
    public static McpProcessJob? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var limits = new ExtendedLimitInformation { BasicLimitInformation = { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, in limits, Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            handle.Dispose();
            return null;
        }

        return new McpProcessJob(handle);
    }

    /// <summary>Puts <paramref name="process"/> in the job.</summary>
    /// <returns><see langword="false"/> when it could not be (for example because it already ended).</returns>
    public bool TryAssign(Process process)
    {
        try
        {
            return AssignProcessToJobObject(_handle, process.SafeHandle);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Closes the job, which ends every process still in it.</summary>
    public void Dispose() => _handle.Dispose();

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobHandle job, int informationClass, in ExtendedLimitInformation information, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    // JOBOBJECT_BASIC_LIMIT_INFORMATION
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    // IO_COUNTERS
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
