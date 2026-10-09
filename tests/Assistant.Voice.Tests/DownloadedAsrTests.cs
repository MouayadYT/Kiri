using System.Diagnostics;
using System.Text.Json;
using Assistant.Core.Models;
using Assistant.Core.Storage;
using Assistant.Data.Models;
using Assistant.Voice;
using Xunit;

namespace Assistant.Voice.Tests;

public sealed class DownloadedAsrTests
{
    [Theory]
    [InlineData("asr-parakeet-v3", "cpu")]
    [InlineData("asr-sensevoice", "cpu")]
    [InlineData("asr-moonshine-tiny", "cpu")]
    [InlineData("asr-whisper-small", "cpu")]
    [InlineData("asr-whisper-small", "vulkan")]
    public async Task VerifiedAsrPackagesTranscribePublicFixtureAudio(string id, string device)
    {
        var root = Environment.GetEnvironmentVariable("ASSISTANT_ASR_TEST_ROOT");
        if (string.IsNullOrEmpty(root) || device == "vulkan" && Environment.GetEnvironmentVariable("ASSISTANT_TEST_ASR_GPU") != "1") return;
        var paths = new AppPaths(root);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var library = new ModelLibrary(paths, http); var model = DownloadCatalog.Find(id)!;
        await library.DownloadAsync(model, null);
        Assert.True(library.IsInstalled(model));
        var fixture = Path.Combine(library.FolderOf(DownloadCatalog.Find("asr-moonshine-tiny")!), "test_wavs", "0.wav");
        if (!File.Exists(fixture))
        {
            await library.DownloadAsync(DownloadCatalog.Find("asr-moonshine-tiny")!, null);
            Assert.True(File.Exists(fixture));
        }
        var samples = ReadPcm16(fixture);
        var timer = Stopwatch.StartNew();
        var text = model.ArchiveRoot is null
            ? await NativeAsrWorker.RecognizeAsync(model, library.FolderOf(model), device, samples, CancellationToken.None)
            : await SherpaOfflineAsr.RecognizeAsync(id, library.FolderOf(model), samples, CancellationToken.None);
        Assert.True(text.Length > 10, "Recognizer returned no useful transcript.");
        await File.WriteAllTextAsync(Path.Combine(root, id + "-" + device + ".json"), JsonSerializer.Serialize(new { id, device, text, seconds = timer.Elapsed.TotalSeconds, audioSeconds = samples.Length / 16000d }));
    }
    private static short[] ReadPcm16(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        Assert.Equal("RIFF", new string(reader.ReadChars(4))); reader.ReadInt32(); Assert.Equal("WAVE", new string(reader.ReadChars(4)));
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var kind = new string(reader.ReadChars(4)); var length = reader.ReadInt32();
            if (kind == "fmt ")
            {
                Assert.Equal(1, reader.ReadInt16()); Assert.Equal(1, reader.ReadInt16()); Assert.Equal(16000, reader.ReadInt32());
                reader.ReadInt32(); reader.ReadInt16(); Assert.Equal(16, reader.ReadInt16()); if (length > 16) reader.ReadBytes(length - 16);
            }
            else if (kind == "data")
            {
                var samples = new short[length / 2]; for (var i = 0; i < samples.Length; i++) samples[i] = reader.ReadInt16(); return samples;
            }
            else reader.ReadBytes(length);
            if ((length & 1) != 0) reader.ReadByte();
        }
        throw new IOException("Fixture has no audio.");
    }
}
