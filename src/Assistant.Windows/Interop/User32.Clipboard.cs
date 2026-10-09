using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>Clipboard and keyboard-input functions from user32.dll, for the copy fallback of the selection (PROJECT_SPEC §4.5, step 89).</summary>
internal static partial class User32
{
    public const uint CF_BITMAP = 2;
    public const uint CF_METAFILEPICT = 3;
    public const uint CF_DIB = 8;
    public const uint CF_PALETTE = 9;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_ENHMETAFILE = 14;
    public const uint CF_DIBV5 = 17;
    public const uint CF_OWNERDISPLAY = 0x0080;
    public const uint CF_DSPFIRST = 0x0081;
    public const uint CF_DSPLAST = 0x008E;
    public const uint CF_PRIVATEFIRST = 0x0200;
    public const uint CF_GDIOBJLAST = 0x03FF;

    /// <summary>The parent of a message-only window.</summary>
    public const nint HWND_MESSAGE = -3;

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_C = 0x43;
    public const uint MAPVK_VK_TO_VSC = 0;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint newOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    /// <summary>The next format on the clipboard after <paramref name="format"/> (0 for the first), or 0 at the end.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint EnumClipboardFormats(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    /// <summary>A number that changes each time the clipboard's contents change.</summary>
    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial uint RegisterClipboardFormat(string name);

    /// <summary>Whether the key is down right now, in the high bit of the result, whatever thread has the input.</summary>
    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    public static partial uint MapVirtualKey(uint code, uint mapType);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static unsafe partial uint SendInput(uint count, Input* inputs, int size);

    /// <summary>One synthesized input event. Only keyboard events are made, so the union holds the largest member for its size and the keyboard's fields.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    /// <summary>The union of the mouse and keyboard parts of an <see cref="Input"/>; the mouse part is the larger, so it sets the size.</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
}
