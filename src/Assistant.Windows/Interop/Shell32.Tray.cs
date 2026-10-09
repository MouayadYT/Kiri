using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>The notification area's function from shell32.dll (PROJECT_SPEC §4.9, step 121).</summary>
internal static partial class Shell32
{
    public const uint NIM_ADD = 0;
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;
    public const uint NIM_SETVERSION = 4;

    public const uint NIF_MESSAGE = 0x01;
    public const uint NIF_ICON = 0x02;
    public const uint NIF_TIP = 0x04;
    public const uint NIF_SHOWTIP = 0x80;

    /// <summary>The behavior of Windows Vista and later: the callback carries the event and the icon in one value, and the anchor point.</summary>
    public const uint NOTIFYICON_VERSION_4 = 4;

    /// <summary>The user chose the icon with the mouse or the keyboard (version 4).</summary>
    public const uint NIN_SELECT = 0x0400;

    /// <summary>The user chose the icon with the Enter or Space key (version 4).</summary>
    public const uint NIN_KEYSELECT = 0x0401;

    /// <summary>NOTIFYICONDATAW, with the members from the tip to the version, which is all the icon uses; the rest of the structure stays zero.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool NotifyIcon(uint message, in NotifyIconData data);
}
