using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Assistant.Core.Models;
using Assistant.Core.Voice;

namespace Assistant.Voice;

/// <summary>One bounded request in an isolated CPU/Vulkan worker, released immediately afterwards.</summary>
public static class NativeAsrWorker
{
    public static async Task<string> RecognizeAsync(DownloadableModel model, string folder, string device, short[] audio, CancellationToken token)
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "Assistant.VoiceHost.exe");
        var file = Path.Combine(folder, "model.bin");
        if (!File.Exists(worker) || !File.Exists(file)) throw new VoiceEngineException(VoiceEngineFailure.NotInstalled, "Download this speech model first.");
        var info = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--asr", file, model.Id == "asr-parakeet-v3" ? "parakeet" : "whisper", device }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new VoiceEngineException(VoiceEngineFailure.LoadFailed, "The speech worker could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var stop = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
        var drain = DrainAsync(process.StandardError.BaseStream);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(BitConverter.GetBytes(audio.Length), timeout.Token).ConfigureAwait(false);
            if (audio.Length > 0) await process.StandardInput.BaseStream.WriteAsync(MemoryMarshal.AsBytes(audio.AsSpan()).ToArray(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            var header = new byte[4];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            var size = BitConverter.ToInt32(header);
            if (size is < 0 or > 65536) throw new IOException("Speech worker failed.");
            var result = new byte[size];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(result, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Speech worker failed.");
            return Encoding.UTF8.GetString(result);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            throw new VoiceEngineException(VoiceEngineFailure.LoadFailed, device == "vulkan" ? "GPU recognition could not run. Update the graphics driver or choose CPU." : "This speech model could not run. Check its download and choose a compatible model.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await drain.ConfigureAwait(false);
        }
    }
    private static async Task DrainAsync(Stream stream)
    {
        try { var buffer = new byte[4096]; while (await stream.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (IOException) { }
    }
}
