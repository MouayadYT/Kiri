using System.Runtime.InteropServices;
using Assistant.Core.Voice;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Audio;

/// <summary>
/// The default speakers through WASAPI in shared mode (PROJECT_SPEC §4.2, step 125): the Assistant's voice plays beside whatever else the user is hearing,
/// at the volume Windows is set to, in the format the voice makes it (the audio engine converts it to the speakers' own). A stream's COM interfaces are
/// called through their tables on a thread of the stream's own, as the microphone's are, so no built-in COM interop is needed. Nothing here records
/// or logs what is played.
/// </summary>
public sealed class WasapiAudioOutput : IAudioOutput
{
    /// <inheritdoc/>
    public IAudioOutputStream Open(int sampleRate)
    {
        if (sampleRate is < 8_000 or > 192_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        return new RenderStream(sampleRate, () => new WasapiRenderDevice(sampleRate));
    }
}

/// <summary>One WASAPI shared-mode render stream, mono 16-bit, with an event that says when its buffer has room.</summary>
internal sealed unsafe class WasapiRenderDevice : IRenderDevice
{
    // The device's buffer, in 100-ns units: the engine asks for more every period (about 10 ms); this is the headroom.
    private const long BufferDuration = 1_000_000;

    private const int eRender = 0;
    private const int eConsole = 0;
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const ushort WAVE_FORMAT_PCM = 1;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    private static readonly Guid MMDeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IMMDeviceEnumeratorId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IAudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IAudioRenderClientId = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    private readonly bool _comInitialized;
    private readonly AutoResetEvent _ready = new(false);
    private nint _client;
    private nint _render;
    private uint _bufferFrames;

    public WasapiRenderDevice(int sampleRate)
    {
        var comResult = Ole32.CoInitializeEx(0, Ole32.COINIT_MULTITHREADED);
        if (comResult < 0 && comResult != RPC_E_CHANGED_MODE)
        {
            throw new InvalidOperationException("COM could not be started for the speakers.");
        }

        _comInitialized = comResult >= 0;
        nint enumerator = 0, device = 0;
        try
        {
            Check(Ole32.CoCreateInstance(MMDeviceEnumeratorClass, 0, Ole32.CLSCTX_INPROC_SERVER, IMMDeviceEnumeratorId, out enumerator));
            Check(GetDefaultAudioEndpoint(enumerator, eRender, eConsole, &device));
            nint client = 0;
            Check(Activate(device, IAudioClientId, Ole32.CLSCTX_ALL, &client));
            _client = client;

            var format = new WaveFormat
            {
                FormatTag = WAVE_FORMAT_PCM,
                Channels = 1,
                SamplesPerSec = (uint)sampleRate,
                AvgBytesPerSec = (uint)(sampleRate * sizeof(short)),
                BlockAlign = sizeof(short),
                BitsPerSample = 16,
            };
            const uint flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            Check(Initialize(_client, AUDCLNT_SHAREMODE_SHARED, flags, BufferDuration, &format));
            Check(SetEventHandle(_client, _ready.SafeWaitHandle.DangerousGetHandle()));
            uint frames;
            Check(GetBufferSize(_client, &frames));
            _bufferFrames = frames;
            nint render = 0;
            Check(GetService(_client, IAudioRenderClientId, &render));
            _render = render;
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            Release(device);
            Release(enumerator);
        }
    }

    public int BufferFrames => checked((int)_bufferFrames);

    public WaitHandle Ready => _ready;

    public int Padding
    {
        get
        {
            uint padding;
            return GetCurrentPadding(_client, &padding) >= 0 ? checked((int)padding) : 0;
        }
    }

    public void Write(ReadOnlySpan<short> frames)
    {
        if (frames.IsEmpty)
        {
            return;
        }

        byte* data;
        Check(GetBuffer(_render, (uint)frames.Length, &data));
        frames.CopyTo(new Span<short>(data, frames.Length));
        Check(ReleaseBuffer(_render, (uint)frames.Length, 0));
    }

    public void Start() => Check(Start(_client));

    public void Stop() => _ = Stop(_client);

    public void Reset() => _ = Reset(_client);

    public void Dispose()
    {
        if (_client != 0)
        {
            _ = Stop(_client);
        }

        Release(_render);
        Release(_client);
        _render = 0;
        _client = 0;
        _ready.Dispose();
        if (_comInitialized)
        {
            Ole32.CoUninitialize();
        }
    }

    private static void Check(int hresult)
    {
        if (hresult < 0)
        {
            throw new InvalidOperationException($"The speakers failed (0x{hresult:X8}).");
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

    // IAudioClient::GetBufferSize, GetCurrentPadding, Start, Stop, Reset, SetEventHandle and GetService
    private static int GetBufferSize(nint client, uint* frames) => ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Method(client, 4))(client, frames);

    private static int GetCurrentPadding(nint client, uint* frames) => ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Method(client, 6))(client, frames);

    private static int Start(nint client) => ((delegate* unmanaged[Stdcall]<nint, int>)Method(client, 10))(client);

    private static int Stop(nint client) => ((delegate* unmanaged[Stdcall]<nint, int>)Method(client, 11))(client);

    private static int Reset(nint client) => ((delegate* unmanaged[Stdcall]<nint, int>)Method(client, 12))(client);

    private static int SetEventHandle(nint client, nint handle) => ((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(client, 13))(client, handle);

    private static int GetService(nint client, in Guid interfaceId, nint* service)
    {
        fixed (Guid* id = &interfaceId)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(client, 14))(client, id, service);
        }
    }

    // IAudioRenderClient::GetBuffer and ReleaseBuffer
    private static int GetBuffer(nint render, uint frames, byte** data) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, byte**, int>)Method(render, 3))(render, frames, data);

    private static int ReleaseBuffer(nint render, uint frames, uint flags) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Method(render, 4))(render, frames, flags);

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
