using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>What the game detector needs of user32.dll: to be told when the window in front changes, and to read a window's title and style.</summary>
internal static partial class User32
{
    /// <summary>The foreground window changed.</summary>
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    /// <summary>The callback runs in the hooking process, on the thread that set the hook, when that thread takes its messages.</summary>
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    /// <summary>Events of the hooking process's own windows are not reported.</summary>
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const uint WM_QUIT = 0x0012;

    /// <summary>A title bar (<c>WS_BORDER | WS_DLGFRAME</c>): a window that has both bits has one.</summary>
    public const uint WS_CAPTION = 0x00C00000;

    /// <summary>
    /// Has <paramref name="callback"/> called for the events from <paramref name="eventMin"/> to <paramref name="eventMax"/>. With
    /// <see cref="WINEVENT_OUTOFCONTEXT"/> nothing is loaded into other programs; the calling thread must take messages for the callback to run.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static unsafe partial nint SetWinEventHook(
        uint eventMin, uint eventMax, nint module, delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void> callback,
        uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hook);

    /// <summary>Puts a message in a thread's queue; it fails until the thread has made its queue.</summary>
    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static unsafe partial int GetWindowText(nint window, char* text, int maxCount);

    /// <summary>The next child of <paramref name="parent"/> after <paramref name="childAfter"/>; with no parent, the next top-level window.</summary>
    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);
}
