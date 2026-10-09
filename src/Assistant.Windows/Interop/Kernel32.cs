using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>Module and process functions from kernel32.dll.</summary>
internal static partial class Kernel32
{
    /// <summary>The right to ask for a process's image path, which Windows gives even for processes that are elevated or protected.</summary>
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>The right to wait for a process to end.</summary>
    public const uint SYNCHRONIZE = 0x00100000;

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? moduleName);

    /// <summary>The operating system's id of the calling thread, which is not the managed thread id.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    /// <summary>Writes the process's executable path into <paramref name="path"/>; <paramref name="size"/> is its length in, the path's out.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* path, ref uint size);
}
