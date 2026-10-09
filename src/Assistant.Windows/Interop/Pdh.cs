using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>
/// Performance counters from pdh.dll, for the game detector: how much one process uses the graphics card. They are the numbers Task Manager
/// shows, read from Windows itself, so they work for any graphics card and for processes that are elevated or protected.
/// </summary>
internal static partial class Pdh
{
    public const int ERROR_SUCCESS = 0;

    /// <summary><c>PDH_MORE_DATA</c>: the buffer is too small; the size needed was written back.</summary>
    public const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    /// <summary><c>PDH_FMT_DOUBLE</c>: the value as a double.</summary>
    public const uint PDH_FMT_DOUBLE = 0x00000200;

    /// <summary><c>PDH_FMT_LARGE</c>: the value as a 64-bit integer.</summary>
    public const uint PDH_FMT_LARGE = 0x00000400;

    /// <summary><c>PDH_FMT_COUNTERVALUE_ITEM_W</c> on 64-bit Windows: the instance's name, the value's status and the value.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct CounterValueItem
    {
        [FieldOffset(0)]
        public nint Name;

        [FieldOffset(8)]
        public uint Status;

        [FieldOffset(16)]
        public double DoubleValue;

        [FieldOffset(16)]
        public long LargeValue;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW")]
    public static partial int OpenQuery(nint dataSource, nint userData, out nint query);

    /// <summary>Adds a counter by its English path, which is the same on every display language; the instance may hold a wildcard.</summary>
    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int AddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    public static partial int CollectQueryData(nint query);

    /// <summary>Writes every instance of a wildcard counter with its value; <paramref name="bufferSize"/> is the buffer's size in, the size needed out.</summary>
    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static unsafe partial int GetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, byte* buffer);

    [LibraryImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    public static partial int CloseQuery(nint query);
}
