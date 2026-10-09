using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>What the game detector asks of shell32.dll: whether Windows sees a Direct3D program in exclusive full screen.</summary>
internal static partial class Shell32
{
    /// <summary><c>QUNS_RUNNING_D3D_FULL_SCREEN</c>: a Direct3D program has the screen to itself, which is how Windows itself knows not to show notifications over a game.</summary>
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    /// <summary>Says what state the PC is in for showing notifications; an HRESULT.</summary>
    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(out int state);
}
