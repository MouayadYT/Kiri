using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>Menu, icon and message-number functions from user32.dll, for the notification-area icon (PROJECT_SPEC §4.9, step 121).</summary>
internal static partial class User32
{
    /// <summary>The first message number an application may use for its own messages.</summary>
    public const uint WM_APP = 0x8000;

    public const uint WM_NULL = 0x0000;
    public const uint WM_CONTEXTMENU = 0x007B;

    // Menu items (AppendMenu).
    public const uint MF_STRING = 0x0000;
    public const uint MF_GRAYED = 0x0001;
    public const uint MF_CHECKED = 0x0008;
    public const uint MF_SEPARATOR = 0x0800;

    // Showing a menu (TrackPopupMenuEx).
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_BOTTOMALIGN = 0x0020;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_WORKAREA = 0x10000;

    // Icons.
    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x0010;
    public const int IDI_APPLICATION = 32512;
    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(nint menu, uint flags, nuint idOrSubmenu, string? text);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetMenuDefaultItem(nint menu, uint item, uint byPosition);

    /// <summary>Shows the menu and, with <see cref="TPM_RETURNCMD"/>, waits for the user's choice and returns its id (0 for none).</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);

    /// <summary>A stock icon of the system (<paramref name="name"/> is one of the IDI_ numbers), shared, so it is never destroyed.</summary>
    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    public static partial nint LoadIcon(nint instance, nint name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
}
