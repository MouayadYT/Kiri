using System.ComponentModel;
using System.Runtime.InteropServices;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Backdrop;

/// <summary>
/// The window class of backdrop windows: borderless popups that never activate, draw nothing themselves and show only
/// composition visuals.
/// </summary>
internal sealed unsafe class BackdropWindowClass : IDisposable
{
    private const string Name = "Assistant.Backdrop";

    private const uint Style = User32.WS_POPUP;
    private const uint ExtendedStyle =
        User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE | User32.WS_EX_NOREDIRECTIONBITMAP;

    private readonly nint _instance;

    private BackdropWindowClass(nint instance) => _instance = instance;

    public static BackdropWindowClass Register()
    {
        var instance = Kernel32.GetModuleHandle(null);
        fixed (char* name = Name)
        {
            var windowClass = new User32.WindowClass
            {
                Size = (uint)sizeof(User32.WindowClass),
                WindowProc = NativeLibrary.GetExport(NativeLibrary.Load("user32.dll"), "DefWindowProcW"),
                Instance = instance,
                ClassName = (nint)name,
            };

            if (User32.RegisterClass(in windowClass) == 0
                && Marshal.GetLastPInvokeError() != User32.ERROR_CLASS_ALREADY_EXISTS)
            {
                throw new Win32Exception();
            }
        }

        return new BackdropWindowClass(instance);
    }

    /// <summary>Creates a hidden backdrop window.</summary>
    public nint CreateWindow(bool topmost)
    {
        var extendedStyle = ExtendedStyle | (topmost ? User32.WS_EX_TOPMOST : 0);
        var window = User32.CreateWindow(extendedStyle, Name, null, Style, 0, 0, 0, 0, 0, 0, _instance, 0);
        return window != 0 ? window : throw new Win32Exception();
    }

    public void Dispose() => User32.UnregisterClass(Name, _instance);
}
