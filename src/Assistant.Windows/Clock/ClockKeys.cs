using System.Runtime.InteropServices;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Clock;

/// <summary>
/// The few things <see cref="WindowsClockApp"/> does to the Clock app's window with Windows' own calls and not through UI Automation: pressing Space on
/// the button that has the keyboard (the one button that does not work when it is invoked), and finding and naming the app's frame window, which is the
/// only part of its small keep-on-top view another program can reach.
/// </summary>
internal static unsafe partial class ClockKeys
{
    public const uint WM_CLOSE = User32.WM_CLOSE;

    private const ushort VK_SPACE = 0x20;
    private const string FrameClass = "ApplicationFrameWindow";

    /// <summary>Presses and lets go of Space, as the keyboard would, for whichever control has the keyboard. Says whether both were sent.</summary>
    public static bool TapSpace()
    {
        var scan = (ushort)User32.MapVirtualKey(VK_SPACE, User32.MAPVK_VK_TO_VSC);
        var keys = stackalloc User32.Input[2];
        keys[0] = Key(scan, 0);
        keys[1] = Key(scan, User32.KEYEVENTF_KEYUP);
        return User32.SendInput(2, keys, sizeof(User32.Input)) == 2;
    }

    /// <summary>Whether the window has a title: the Clock app's frame has the app's name while the app shows in it, and none once the app has let go of it.</summary>
    public static bool HasTitle(nint window)
    {
        var text = stackalloc char[8];
        return User32.GetWindowText(window, text, 8) > 0;
    }

    /// <summary>The frame window called <paramref name="title"/>, which finds the Clock app's small keep-on-top view by the app's name; 0 when there is none.</summary>
    public static nint FindFrame(string title) => string.IsNullOrEmpty(title) ? 0 : FindWindow(FrameClass, title);

    private static User32.Input Key(ushort scan, uint flags) => new()
    {
        Type = User32.INPUT_KEYBOARD,
        Data = new User32.InputUnion { Keyboard = new User32.KeyboardInput { VirtualKey = VK_SPACE, Scan = scan, Flags = flags } },
    };

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string className, string windowName);
}
