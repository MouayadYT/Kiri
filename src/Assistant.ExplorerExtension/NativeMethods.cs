using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Assistant.ExplorerExtension;

/// <summary>The few Windows functions the entry point calls.</summary>
internal static partial class NativeMethods
{
    /// <summary><c>SHCNE_ASSOCCHANGED</c>: a file type's association changed.</summary>
    public const int ShcneAssocChanged = 0x08000000;

    /// <summary><c>SHCNF_IDLIST</c>: the item arguments are ID lists (here, none).</summary>
    public const uint ShcnfIdList = 0;

    /// <summary><c>MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST</c>.</summary>
    public const uint MessageBoxInformation = 0x00000000 | 0x00000040 | 0x00010000 | 0x00040000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(nint owner, string text, string caption, uint type);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetLongPathName(string shortPath, Span<char> longPath, int bufferLength);

    [LibraryImport("shell32.dll")]
    public static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
