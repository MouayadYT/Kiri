using System.Runtime.InteropServices;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Hotkeys;

internal interface IHotkeyNativeMethods
{
    int Register(nint window, int id, uint modifiers, uint virtualKey);
    int Unregister(nint window, int id);
}

internal sealed class HotkeyNativeMethods : IHotkeyNativeMethods
{
    public int Register(nint window, int id, uint modifiers, uint virtualKey) =>
        User32.RegisterHotKey(window, id, modifiers, virtualKey) ? 0 : Marshal.GetLastPInvokeError();

    public int Unregister(nint window, int id) =>
        User32.UnregisterHotKey(window, id) ? 0 : Marshal.GetLastPInvokeError();
}
