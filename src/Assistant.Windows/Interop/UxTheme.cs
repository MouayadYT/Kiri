using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>
/// The two functions of uxtheme.dll that let a menu follow the system's dark mode. They are known by number only and not documented, but
/// every application with a dark menu uses them (Windows 10 version 1903 and later); where they are missing, the menu stays light.
/// </summary>
internal static unsafe partial class UxTheme
{
    private const int SetPreferredAppModeOrdinal = 135;
    private const int FlushMenuThemesOrdinal = 136;
    private const int AllowDark = 1;

    private static bool _applied;

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress")]
    private static partial nint GetProcAddress(nint module, nint nameOrOrdinal);

    /// <summary>Makes the menus of this process follow the user's light or dark choice for apps, once.</summary>
    public static void AllowDarkMenus()
    {
        if (_applied)
        {
            return;
        }

        _applied = true;
        try
        {
            if (!NativeLibrary.TryLoad("uxtheme.dll", out var library))
            {
                return;
            }

            // GetProcAddress takes an ordinal as a number in place of the name.
            var setMode = GetProcAddress(library, SetPreferredAppModeOrdinal);
            var flush = GetProcAddress(library, FlushMenuThemesOrdinal);
            if (setMode != 0 && flush != 0)
            {
                ((delegate* unmanaged[Stdcall]<int, int>)setMode)(AllowDark);
                ((delegate* unmanaged[Stdcall]<void>)flush)();
            }
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            // A menu that stays light is the only loss.
        }
    }
}
