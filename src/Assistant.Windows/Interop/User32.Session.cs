using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

internal static partial class User32
{
    /// <summary>Locks the workstation's display, as Win+L does. Returns at once; the session is locked a moment later.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LockWorkStation();
}
