using System.Runtime.InteropServices;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Audio;

/// <summary>
/// Captures the default recording device through WASAPI in shared mode, converted by the audio engine to 16 kHz mono
/// 16-bit PCM. The COM interfaces are called through their vtables, so no built-in COM interop is needed.
/// </summary>
internal sealed unsafe class WasapiMicrophoneCapture(Func<string?>? deviceId = null) : IMicrophoneCapture
{
    private const int SampleRate = 16_000;

    // The engine's buffer, in 100-ns units. Packets arrive about every 10 ms; the rest is headroom.
    private const long BufferDuration = 2_000_000;

    // How long to wait for a packet before checking the device again.
    private const int PacketTimeoutMs = 500;

    private const int eCapture = 1;
    private const int eConsole = 0;
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private const ushort WAVE_FORMAT_PCM = 1;

    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int E_NOTFOUND = unchecked((int)0x80070490);
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    private static readonly Guid MMDeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IMMDeviceEnumeratorId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IAudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IAudioCaptureClientId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    public void Run(MicrophoneSamplesHandler onSamples, WaitHandle stop)
    {
        // WASAPI needs COM on this thread; any apartment will do.
        var comResult = Ole32.CoInitializeEx(0, Ole32.COINIT_MULTITHREADED);
        if (comResult < 0 && comResult != RPC_E_CHANGED_MODE)
        {
            throw new MicrophoneException(MicrophoneFailure.Unavailable, comResult);
        }

        nint enumerator = 0, device = 0, client = 0, capture = 0;
        try
        {
            Check(Ole32.CoCreateInstance(MMDeviceEnumeratorClass, 0, Ole32.CLSCTX_INPROC_SERVER, IMMDeviceEnumeratorId, out enumerator));

            // The device the user chose in Settings; one that is no longer there (unplugged) gives way to the one Windows uses.
            var chosen = deviceId?.Invoke();
            if (string.IsNullOrEmpty(chosen) || GetDevice(enumerator, chosen, &device) < 0 || device == 0)
            {
                Check(GetDefaultAudioEndpoint(enumerator, eCapture, eConsole, &device));
            }

            Check(Activate(device, IAudioClientId, Ole32.CLSCTX_ALL, &client));

            var format = new WaveFormat
            {
                FormatTag = WAVE_FORMAT_PCM,
                Channels = 1,
                SamplesPerSec = SampleRate,
                AvgBytesPerSec = SampleRate * sizeof(short),
                BlockAlign = sizeof(short),
                BitsPerSample = 16,
            };
            const uint flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                               AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            Check(Initialize(client, AUDCLNT_SHAREMODE_SHARED, flags, BufferDuration, &format));

            using var packetReady = new AutoResetEvent(false);
            Check(SetEventHandle(client, packetReady.SafeWaitHandle.DangerousGetHandle()));
            Check(GetService(client, IAudioCaptureClientId, &capture));
            Check(Start(client));
            try
            {
                WaitHandle[] handles = [stop, packetReady];
                while (WaitHandle.WaitAny(handles, PacketTimeoutMs) != 0)
                {
                    Drain(capture, onSamples);
                }
            }
            finally
            {
                _ = Stop(client);
            }
        }
        finally
        {
            Release(capture);
            Release(client);
            Release(device);
            Release(enumerator);
            if (comResult >= 0)
            {
                Ole32.CoUninitialize();
            }
        }
    }

    // Hands over every packet that is waiting, and gives each buffer straight back to the engine.
    private static void Drain(nint capture, MicrophoneSamplesHandler onSamples)
    {
        uint packetFrames;
        Check(GetNextPacketSize(capture, &packetFrames));
        while (packetFrames > 0)
        {
            byte* data;
            uint frames, bufferFlags;
            Check(GetBuffer(capture, &data, &frames, &bufferFlags));
            try
            {
                onSamples((bufferFlags & AUDCLNT_BUFFERFLAGS_SILENT) != 0
                    ? ReadOnlySpan<short>.Empty
                    : new ReadOnlySpan<short>(data, checked((int)frames)));
            }
            finally
            {
                _ = ReleaseBuffer(capture, frames);
            }

            Check(GetNextPacketSize(capture, &packetFrames));
        }
    }

    private static void Check(int hresult)
    {
        if (hresult < 0)
        {
            throw new MicrophoneException(hresult switch
            {
                E_NOTFOUND => MicrophoneFailure.NoMicrophone,
                E_ACCESSDENIED => MicrophoneFailure.AccessDenied,
                AUDCLNT_E_DEVICE_INVALIDATED => MicrophoneFailure.Disconnected,
                _ => MicrophoneFailure.Unavailable,
            }, hresult);
        }
    }

    private static void Release(nint unknown)
    {
        if (unknown != 0)
        {
            Marshal.Release(unknown);
        }
    }

    // A COM method: the slot of the object's vtable.
    private static nint Method(nint unknown, int slot) => (*(nint**)unknown)[slot];

    // IMMDeviceEnumerator::GetDefaultAudioEndpoint
    private static int GetDefaultAudioEndpoint(nint enumerator, int dataFlow, int role, nint* device) =>
        ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Method(enumerator, 4))(enumerator, dataFlow, role, device);

    // IMMDeviceEnumerator::GetDevice
    private static int GetDevice(nint enumerator, string id, nint* device)
    {
        fixed (char* text = id)
        {
            return ((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Method(enumerator, 5))(enumerator, text, device);
        }
    }

    // IMMDevice::Activate
    private static int Activate(nint device, in Guid interfaceId, uint context, nint* instance)
    {
        fixed (Guid* id = &interfaceId)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Method(device, 3))(device, id, context, 0, instance);
        }
    }

    // IAudioClient::Initialize, with no session GUID
    private static int Initialize(nint client, int shareMode, uint streamFlags, long bufferDuration, WaveFormat* format) =>
        ((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, WaveFormat*, Guid*, int>)Method(client, 3))(
            client, shareMode, streamFlags, bufferDuration, 0, format, null);

    // IAudioClient::Start, Stop, SetEventHandle and GetService
    private static int Start(nint client) => ((delegate* unmanaged[Stdcall]<nint, int>)Method(client, 10))(client);

    private static int Stop(nint client) => ((delegate* unmanaged[Stdcall]<nint, int>)Method(client, 11))(client);

    private static int SetEventHandle(nint client, nint handle) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(client, 13))(client, handle);

    private static int GetService(nint client, in Guid interfaceId, nint* service)
    {
        fixed (Guid* id = &interfaceId)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(client, 14))(client, id, service);
        }
    }

    // IAudioCaptureClient::GetBuffer (without the device and performance-counter positions), ReleaseBuffer and
    // GetNextPacketSize
    private static int GetBuffer(nint capture, byte** data, uint* frames, uint* flags) =>
        ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, ulong*, ulong*, int>)Method(capture, 3))(
            capture, data, frames, flags, null, null);

    private static int ReleaseBuffer(nint capture, uint frames) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Method(capture, 4))(capture, frames);

    private static int GetNextPacketSize(nint capture, uint* frames) =>
        ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Method(capture, 5))(capture, frames);

    // WAVEFORMATEX
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }
}
