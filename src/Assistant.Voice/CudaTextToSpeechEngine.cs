using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Assistant.Core.Models;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Assistant.Core.Voice;

namespace Assistant.Voice;

/// <summary>Streams speech from an isolated CUDA worker, keeping CPU and CUDA native runtimes separate.</summary>
public sealed class CudaTextToSpeechEngine(TextToSpeechModel model, AppPaths paths, string device, string? workerPath = null, string? modelFolder = null) : ITextToSpeechEngine
{
    private Process? _process;
    private Stream? _input, _output;
    public TextToSpeechModel Model => model;
    public int SampleRate { get; private set; }
    internal int? WorkerProcessId => _process?.Id;

    public void Load()
    {
        if (_process is not null) return;
        var runtime = Path.Combine(paths.RuntimesDirectory, DownloadCatalog.CudaVoiceRuntime.Id);
        var worker = workerPath ?? Path.Combine(AppContext.BaseDirectory, "Assistant.VoiceHost.exe");
        if (!File.Exists(worker) || !File.Exists(Path.Combine(runtime, "bin", "onnxruntime.dll"))) throw new VoiceEngineException(VoiceEngineFailure.NotInstalled, "Download the NVIDIA voice runtime in Voice setup first.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(device, "^cuda:GPU-[a-fA-F0-9-]{36}$")) throw new VoiceEngineException(VoiceEngineFailure.LoadFailed, "Select a connected NVIDIA GPU.");
        var info = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.Combine(runtime, "bin") };
        info.ArgumentList.Add(model.Id); info.ArgumentList.Add(paths.RootDirectory); info.ArgumentList.Add(runtime);
        info.ArgumentList.Add(modelFolder ?? Path.Combine(paths.VoicesDirectory, model.Id));
        info.Environment["CUDA_VISIBLE_DEVICES"] = device[5..];
        info.Environment["PATH"] = string.Join(Path.PathSeparator, DownloadCatalog.CudaPackages.Select(package => Path.Combine(paths.RuntimesDirectory, package.Id, "bin"))) + Path.PathSeparator + info.Environment["PATH"];
        try
        {
            _process = Process.Start(info) ?? throw new InvalidOperationException();
            _input = _process.StandardInput.BaseStream; _output = _process.StandardOutput.BaseStream;
            _ = DrainAsync(_process.StandardError.BaseStream);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            SampleRate = ReadInt(timeout.Token);
            if (SampleRate is < 8000 or > 192000) throw new IOException();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { Dispose(); throw new VoiceEngineException(VoiceEngineFailure.LoadFailed, "The GPU voice could not load. Check your NVIDIA driver, or choose CPU."); }
    }

    public void Synthesize(string text, SpeechAudioHandler onAudio, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_process is null) Load();
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 65536) throw new VoiceEngineException(VoiceEngineFailure.Failed, "This speech segment is too long.");
        var stopped = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(90));
            _input!.Write(BitConverter.GetBytes(bytes.Length)); _input.Write(bytes); _input.Flush();
            while (true)
            {
                var count = ReadInt(timeout.Token);
                if (count == 0) break;
                if (count is < 0 or > 1_000_000) throw new IOException();
                var data = new byte[count * sizeof(float)]; _output!.ReadExactlyAsync(data, timeout.Token).AsTask().GetAwaiter().GetResult();
                if (!onAudio(MemoryMarshal.Cast<byte, float>(data))) { stopped = true; throw new OperationCanceledException(cancellationToken); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || stopped) { Dispose(); throw; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        { Dispose(); throw new VoiceEngineException(VoiceEngineFailure.Failed, "GPU speech stopped. Try again, or choose CPU."); }
    }

    private int ReadInt(CancellationToken token)
    {
        var bytes = new byte[4]; _output!.ReadExactlyAsync(bytes, token).AsTask().GetAwaiter().GetResult();
        return BitConverter.ToInt32(bytes);
    }
    private static async Task DrainAsync(Stream errors)
    {
        var buffer = new byte[4096];
        try { while (await errors.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    public void Dispose()
    {
        var process = _process; _process = null;
        if (process is null) return;
        try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process.Dispose(); _input = _output = null;
    }
}
