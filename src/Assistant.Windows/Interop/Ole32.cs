using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>COM activation functions from ole32.dll.</summary>
internal static partial class Ole32
{
    public const uint CLSCTX_INPROC_SERVER = 0x1;
    public const uint CLSCTX_ALL = 0x17;
    public const uint COINIT_MULTITHREADED = 0x0;

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);
}
