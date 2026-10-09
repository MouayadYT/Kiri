using System.Diagnostics;
using Assistant.Core.Models;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Assistant.Data.Models;
using Xunit;
using Xunit.Abstractions;

namespace Assistant.Voice.Tests;

/// <summary>Opt-in verification of the exact downloadable archives with the shipped speech runtime.</summary>
public sealed class DownloadedVoiceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("speech-recognition")]
    [InlineData("wake-word")]
    public async Task DownloadedSpeechInputArchiveLoadsWithTheShippedRuntime(string id)
    {
        var root = Environment.GetEnvironmentVariable("ASSISTANT_DOWNLOAD_TEST_ROOT");
        if (string.IsNullOrEmpty(root)) return;
        var paths = new AppPaths(root);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var library = new ModelLibrary(paths, http);
        await library.DownloadAsync(DownloadCatalog.Find(id)!, null);
        Assert.True(library.IsInstalled(DownloadCatalog.Find(id)!));
        var folders = new VoiceModelFolders(paths);
        if (id == "speech-recognition")
        {
            using var recognizer = new SherpaSpeechToTextService(folders);
            await recognizer.WarmUpAsync();
            Assert.True(recognizer.Status.IsReady, recognizer.Status.Message);
        }
        else
        {
            using var listener = new SherpaWakeWordService(folders, paths);
            await listener.WarmUpAsync();
            Assert.True(listener.Status.IsReady, listener.Status.Message);
        }
    }

    [Theory]
    [InlineData("kokoro-82m-onnx")]
    [InlineData("kitten-tts-mini")]
    [InlineData("piper")]
    public async Task DownloadedArchiveLoadsAndSynthesizesRealAudio(string id)
    {
        var root = Environment.GetEnvironmentVariable("ASSISTANT_DOWNLOAD_TEST_ROOT");
        if (string.IsNullOrEmpty(root)) { output.WriteLine("Opt-in: set ASSISTANT_DOWNLOAD_TEST_ROOT to download and test the actual voice."); return; }
        var paths = new AppPaths(root);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var library = new ModelLibrary(paths, http);
        var model = DownloadCatalog.Find(id)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await library.DownloadAsync(model, null, timeout.Token);
        Assert.True(library.IsInstalled(model));
        using var engine = new TextToSpeechEngineFactory(new VoiceModelFolders(paths)).Create(TextToSpeechModels.Find(id)!);
        var load = Stopwatch.StartNew(); engine.Load();
        output.WriteLine($"{id}: load {load.ElapsedMilliseconds} ms");
        var timer = Stopwatch.StartNew(); var samples = 0;
        engine.Synthesize("Hello, I'm Kiri. You're all set.", block => { samples += block.Length; Assert.All(block.ToArray(), sample => Assert.InRange(sample, -1.1f, 1.1f)); return true; }, timeout.Token);
        Assert.True(samples > engine.SampleRate);
        output.WriteLine($"{samples / (double)engine.SampleRate:0.00} seconds of audio in {timer.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("kokoro-82m-onnx")]
    [InlineData("kitten-tts-mini")]
    [InlineData("piper")]
    public async Task IsolatedCudaWorkerSynthesizesAndStopsRealAudio(string id)
    {
        var root = Environment.GetEnvironmentVariable("ASSISTANT_DOWNLOAD_TEST_ROOT");
        var device = Environment.GetEnvironmentVariable("ASSISTANT_CUDA_TEST_DEVICE");
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(device)) { output.WriteLine("Opt-in CUDA test: set ASSISTANT_DOWNLOAD_TEST_ROOT and ASSISTANT_CUDA_TEST_DEVICE."); return; }
        var paths = new AppPaths(root);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var library = new ModelLibrary(paths, http);
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        foreach (var package in DownloadCatalog.CudaPackages) await library.DownloadAsync(package, null, limit.Token);
        await library.DownloadAsync(DownloadCatalog.Find(id)!, null, limit.Token);
        using var engine = new CudaTextToSpeechEngine(TextToSpeechModels.Find(id)!, paths, device, Environment.GetEnvironmentVariable("ASSISTANT_CUDA_TEST_WORKER"));
        var load = Stopwatch.StartNew(); engine.Load();
        output.WriteLine($"{id} GPU: load {load.ElapsedMilliseconds} ms");
        Assert.NotNull(engine.WorkerProcessId);
        var timer = Stopwatch.StartNew(); var samples = 0;
        engine.Synthesize("Hello, I'm Kiri. You're all set.", block => { samples += block.Length; Assert.All(block.ToArray(), sample => Assert.InRange(sample, -1.1f, 1.1f)); return true; }, limit.Token);
        Assert.True(samples > engine.SampleRate);
        output.WriteLine($"GPU: {samples / (double)engine.SampleRate:0.00} seconds of audio in {timer.ElapsedMilliseconds} ms");
        Assert.ThrowsAny<OperationCanceledException>(() => engine.Synthesize("Stop this sample.", _ => false, limit.Token));
        var restarted = 0;
        engine.Synthesize("I can speak again.", block => { restarted += block.Length; return true; }, limit.Token);
        Assert.True(restarted > engine.SampleRate);
    }
}
