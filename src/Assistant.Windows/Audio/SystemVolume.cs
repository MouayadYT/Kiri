using System.Runtime.InteropServices;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Audio;

/// <summary>The volume and the mute switch of the default speakers.</summary>
internal interface ISystemVolume
{
    /// <summary>Reads the volume (0 to 100) and whether the sound is off; <see langword="false"/> when there is no output device.</summary>
    bool TryGetState(out int percent, out bool muted);

    /// <summary>Turns the sound off or on.</summary>
    bool SetMuted(bool muted);

    /// <summary>Sets the volume, 0 to 100.</summary>
    bool SetPercent(int percent);
}

/// <summary>
/// The default speakers' master volume through Windows Core Audio (<c>IAudioEndpointVolume</c> of the default render device), the
/// same control the volume keys and the taskbar's slider move. Like the microphone, its COM interfaces are called through their tables,
/// so no built-in COM interop is needed. It reads and sets the volume and the mute switch of the device and nothing else: no audio is
/// played, recorded or listened to.
/// </summary>
internal sealed unsafe class SystemVolume : ISystemVolume
{
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    private const int ERender = 0;
    private const int EConsole = 0;

    private static readonly Guid MMDeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IMMDeviceEnumeratorId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IAudioEndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    /// <inheritdoc/>
    public bool TryGetState(out int percent, out bool muted)
    {
        var read = Use<(int, bool)>(volume =>
        {
            float scalar;
            int mute;
            if (GetMasterVolumeLevelScalar(volume, &scalar) < 0 || GetMute(volume, &mute) < 0)
            {
                return null;
            }

            return (Math.Clamp((int)Math.Round(scalar * 100), 0, 100), mute != 0);
        });
        (percent, muted) = read ?? (0, false);
        return read is not null;
    }

    /// <inheritdoc/>
    public bool SetMuted(bool muted) => Use<bool>(volume => SetMute(volume, muted ? 1 : 0) >= 0 ? true : null) == true;

    /// <inheritdoc/>
    public bool SetPercent(int percent) =>
        Use<bool>(volume => SetMasterVolumeLevelScalar(volume, Math.Clamp(percent, 0, 100) / 100f) >= 0 ? true : null) == true;

    // Runs work against the default speakers' volume control, on a thread that has COM, and lets everything go again.
    private static T? Use<T>(Func<nint, T?> work)
        where T : struct
    {
        var comResult = Ole32.CoInitializeEx(0, Ole32.COINIT_MULTITHREADED);
        if (comResult < 0 && comResult != RPC_E_CHANGED_MODE)
        {
            return null;
        }

        nint enumerator = 0, device = 0, volume = 0;
        try
        {
            if (Ole32.CoCreateInstance(MMDeviceEnumeratorClass, 0, Ole32.CLSCTX_INPROC_SERVER, IMMDeviceEnumeratorId, out enumerator) < 0
                || GetDefaultAudioEndpoint(enumerator, ERender, EConsole, &device) < 0
                || Activate(device, IAudioEndpointVolumeId, Ole32.CLSCTX_ALL, &volume) < 0)
            {
                return null;
            }

            return work(volume);
        }
        finally
        {
            Release(volume);
            Release(device);
            Release(enumerator);
            if (comResult >= 0)
            {
                Ole32.CoUninitialize();
            }
        }
    }

    private static void Release(nint unknown)
    {
        if (unknown != 0)
        {
            Marshal.Release(unknown);
        }
    }

    // A COM method: the slot of the object's table.
    private static nint Method(nint unknown, int slot) => (*(nint**)unknown)[slot];

    // IMMDeviceEnumerator::GetDefaultAudioEndpoint
    private static int GetDefaultAudioEndpoint(nint enumerator, int dataFlow, int role, nint* device) =>
        ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Method(enumerator, 4))(enumerator, dataFlow, role, device);

    // IMMDevice::Activate
    private static int Activate(nint device, in Guid interfaceId, uint context, nint* instance)
    {
        fixed (Guid* id = &interfaceId)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Method(device, 3))(device, id, context, 0, instance);
        }
    }

    // IAudioEndpointVolume::SetMasterVolumeLevelScalar, GetMasterVolumeLevelScalar, SetMute and GetMute, with no event context
    private static int SetMasterVolumeLevelScalar(nint volume, float level) =>
        ((delegate* unmanaged[Stdcall]<nint, float, Guid*, int>)Method(volume, 7))(volume, level, null);

    private static int GetMasterVolumeLevelScalar(nint volume, float* level) =>
        ((delegate* unmanaged[Stdcall]<nint, float*, int>)Method(volume, 9))(volume, level);

    private static int SetMute(nint volume, int mute) =>
        ((delegate* unmanaged[Stdcall]<nint, int, Guid*, int>)Method(volume, 14))(volume, mute, null);

    private static int GetMute(nint volume, int* mute) =>
        ((delegate* unmanaged[Stdcall]<nint, int*, int>)Method(volume, 15))(volume, mute);
}
