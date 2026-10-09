using System.Runtime.InteropServices;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Audio;

/// <summary>A microphone Windows lists, as Settings shows it.</summary>
/// <param name="Id">Windows own id for the device, which is what is kept in the settings.</param>
/// <param name="Name">The name Windows shows for it ("Microphone (USB Audio)").</param>
/// <param name="IsDefault">Whether it is the one Windows uses when none is chosen.</param>
public sealed record MicrophoneDevice(string Id, string Name, bool IsDefault);

/// <summary>The microphones that are plugged in and switched on.</summary>
public interface IMicrophoneDevices
{
    /// <summary>The microphones now, the default first; none when Windows lists none or could not be asked.</summary>
    IReadOnlyList<MicrophoneDevice> List();
}

/// <summary>
/// Which microphone the Assistant listens to: the id of one device, or <see langword="null"/> for the one Windows uses. It is set from the saved settings and read each time a
/// microphone is opened, so a choice made in Settings is used the next time one is. A device that is no longer there is not an error: the default is used.
/// </summary>
public sealed class MicrophoneChoice
{
    private volatile string? _deviceId;

    /// <summary>The chosen device id, or <see langword="null"/> for the default.</summary>
    public string? DeviceId
    {
        get => _deviceId;
        set => _deviceId = string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

/// <summary>The real list, from Windows core audio.</summary>
public sealed unsafe class WindowsMicrophoneDevices : IMicrophoneDevices
{
    private const int eCapture = 1;
    private const int eConsole = 0;
    private const uint DEVICE_STATE_ACTIVE = 0x1;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    private const ushort VT_LPWSTR = 31;

    private static readonly Guid MMDeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IMMDeviceEnumeratorId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    // PKEY_Device_FriendlyName
    private static readonly Guid FriendlyNameSet = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private const uint FriendlyNamePid = 14;

    /// <inheritdoc/>
    public IReadOnlyList<MicrophoneDevice> List()
    {
        var comResult = Ole32.CoInitializeEx(0, Ole32.COINIT_MULTITHREADED);
        if (comResult < 0 && comResult != RPC_E_CHANGED_MODE)
        {
            return [];
        }

        nint enumerator = 0, collection = 0, defaultDevice = 0;
        var devices = new List<MicrophoneDevice>();
        try
        {
            if (Ole32.CoCreateInstance(MMDeviceEnumeratorClass, 0, Ole32.CLSCTX_INPROC_SERVER, IMMDeviceEnumeratorId, out enumerator) < 0)
            {
                return [];
            }

            var defaultId = string.Empty;
            if (GetDefaultAudioEndpoint(enumerator, eCapture, eConsole, &defaultDevice) >= 0)
            {
                defaultId = IdOf(defaultDevice);
            }

            if (EnumAudioEndpoints(enumerator, eCapture, DEVICE_STATE_ACTIVE, &collection) < 0)
            {
                return [];
            }

            uint count;
            if (GetCount(collection, &count) < 0)
            {
                return [];
            }

            for (uint index = 0; index < count && index < 64; index++)
            {
                nint device = 0;
                if (Item(collection, index, &device) < 0)
                {
                    continue;
                }

                try
                {
                    var id = IdOf(device);
                    if (id.Length > 0)
                    {
                        var name = NameOf(device);
                        devices.Add(new MicrophoneDevice(id, name.Length > 0 ? name : "Microphone", string.Equals(id, defaultId, StringComparison.Ordinal)));
                    }
                }
                finally
                {
                    Release(device);
                }
            }
        }
        finally
        {
            Release(defaultDevice);
            Release(collection);
            Release(enumerator);
            if (comResult >= 0)
            {
                Ole32.CoUninitialize();
            }
        }

        return [.. devices.OrderByDescending(device => device.IsDefault).ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    // IMMDevice::GetId
    private static string IdOf(nint device)
    {
        char* id = null;
        if (((delegate* unmanaged[Stdcall]<nint, char**, int>)Method(device, 5))(device, &id) < 0 || id is null)
        {
            return string.Empty;
        }

        try
        {
            return new string(id);
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)id);
        }
    }

    // IMMDevice::OpenPropertyStore(STGM_READ), then IPropertyStore::GetValue(PKEY_Device_FriendlyName)
    private static string NameOf(nint device)
    {
        nint store = 0;
        if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(device, 4))(device, 0, &store) < 0)
        {
            return string.Empty;
        }

        try
        {
            var key = new PropertyKey { FormatId = FriendlyNameSet, PropertyId = FriendlyNamePid };
            var value = new PropVariant();
            if (((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)Method(store, 5))(store, &key, &value) < 0)
            {
                return string.Empty;
            }

            try
            {
                return value.Type == VT_LPWSTR && value.Pointer != 0 ? new string((char*)value.Pointer) : string.Empty;
            }
            finally
            {
                PropVariantClear(&value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    private static void Release(nint unknown)
    {
        if (unknown != 0)
        {
            Marshal.Release(unknown);
        }
    }

    private static nint Method(nint unknown, int slot) => (*(nint**)unknown)[slot];

    private static int GetDefaultAudioEndpoint(nint enumerator, int dataFlow, int role, nint* device) =>
        ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Method(enumerator, 4))(enumerator, dataFlow, role, device);

    private static int EnumAudioEndpoints(nint enumerator, int dataFlow, uint stateMask, nint* collection) =>
        ((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)Method(enumerator, 3))(enumerator, dataFlow, stateMask, collection);

    private static int GetCount(nint collection, uint* count) => ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Method(collection, 3))(collection, count);

    private static int Item(nint collection, uint index, nint* device) => ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(collection, 4))(collection, index, device);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(PropVariant* value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // PROPVARIANT on 64-bit Windows: the type first and the value at offset 8.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }
}
