using System.Buffers.Binary;
using System.IO;
using Assistant.Core.Voice;

namespace Assistant.UI.Voice;

/// <summary>Loads the short speech sample shipped with the app for the local ASR speed test.</summary>
internal static class AsrSpeedTestAudio
{
    private const int HeaderSize = 12;

    public static short[] Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "asr-speed-test.wav");
        if (!File.Exists(path)) throw new IOException("The ASR speed-test sample is missing from this installation.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderSize || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F' ||
            bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
            throw new IOException("The ASR speed-test sample is not a WAV file.");

        var offset = HeaderSize;
        short channels = 0, bits = 0;
        int sampleRate = 0, dataOffset = -1, dataLength = 0;
        while (offset + 8 <= bytes.Length)
        {
            var chunk = new string(new[] { (char)bytes[offset], (char)bytes[offset + 1], (char)bytes[offset + 2], (char)bytes[offset + 3] });
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            offset += 8;
            if (length < 0 || offset + length > bytes.Length) break;
            if (chunk == "fmt " && length >= 16)
            {
                var format = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset, 2));
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 14, 2));
                if (format != 1) throw new IOException("The ASR speed-test sample is not uncompressed PCM.");
            }
            else if (chunk == "data") { dataOffset = offset; dataLength = length; }
            offset += length + (length & 1);
        }
        if (channels != 1 || sampleRate != VoiceAudio.SampleRate || bits != 16 || dataOffset < 0 || dataLength < 2)
            throw new IOException("The ASR speed-test sample must be 16 kHz, mono, 16-bit PCM.");
        var sampleCount = dataLength / 2;
        var audio = new short[sampleCount];
        for (var i = 0; i < sampleCount; i++) audio[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataOffset + i * 2, 2));
        return audio;
    }
}
