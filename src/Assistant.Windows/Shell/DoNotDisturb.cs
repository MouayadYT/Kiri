using System.Runtime.InteropServices;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Shell;

/// <summary>
/// Windows' Do not disturb (the bell in the notification centre), read and switched through the shell's own quiet hours settings: the object the
/// notification centre itself uses. Windows publishes no documented call for it, so everything here is allowed to fail: an answer Windows does not
/// give is <see langword="null"/> or <see langword="false"/>, and what was switched is read back before it is said to be done. The work is done on
/// a thread of its own with a single-threaded apartment, which the shell's object expects.
/// </summary>
internal static class DoNotDisturb
{
    // "Priority only" is what the notification centre's bell turns on; "Unrestricted" is off.
    private const string On = "Microsoft.QuietHoursProfile.PriorityOnly";
    private const string Off = "Microsoft.QuietHoursProfile.Unrestricted";

    private static readonly Guid SettingsClass = new("f53321fa-34f8-4b7f-b9a3-361877cb94cf");
    private static readonly Guid SettingsInterface = new("6bff4732-81ec-4ffb-ae67-b6c1bc29631f");

    /// <summary>Whether Do not disturb is on, or <see langword="null"/> when Windows does not say.</summary>
    public static bool? Get() => OnItsOwnThread<bool?>(settings => Read(settings));

    /// <summary>Turns Do not disturb on or off. Returns whether Windows now shows it that way.</summary>
    public static bool Set(bool on) => OnItsOwnThread(settings =>
    {
        if (Read(settings) == on)
        {
            return true;
        }

        return settings.SetUserSelectedProfile(on ? On : Off) >= 0 && Read(settings) == on;
    });

    private static bool? Read(IQuietHoursSettings settings)
    {
        if (settings.GetUserSelectedProfile(out var profile) < 0 || profile == 0)
        {
            return null;
        }

        try
        {
            // Anything but "Unrestricted" holds notifications back: alarms only is Do not disturb too.
            return Marshal.PtrToStringUni(profile) is { Length: > 0 } name ? !string.Equals(name, Off, StringComparison.OrdinalIgnoreCase) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(profile);
        }
    }

    private static T? OnItsOwnThread<T>(Func<IQuietHoursSettings, T?> work)
    {
        T? result = default;
        var thread = new Thread(() =>
        {
            nint instance = 0;
            try
            {
                if (Ole32.CoCreateInstance(SettingsClass, 0, Ole32.CLSCTX_ALL, SettingsInterface, out instance) < 0 || instance == 0)
                {
                    return;
                }

                result = work((IQuietHoursSettings)Marshal.GetObjectForIUnknown(instance));
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException or EntryPointNotFoundException or InvalidComObjectException)
            {
                // A Windows without this object, or with another shape of it, is one where Do not disturb cannot be switched from here.
                result = default;
            }
            finally
            {
                if (instance != 0)
                {
                    Marshal.Release(instance);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Do not disturb",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(5)) ? result : default;
    }

    // The first two members of the shell's quiet hours settings, which are all that is used: which profile the user has chosen.
    [ComImport, Guid("6bff4732-81ec-4ffb-ae67-b6c1bc29631f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IQuietHoursSettings
    {
        [PreserveSig]
        int GetUserSelectedProfile(out nint profile);

        [PreserveSig]
        int SetUserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string profile);
    }
}
