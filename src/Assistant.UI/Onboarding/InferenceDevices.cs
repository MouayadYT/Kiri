using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Assistant.UI.Onboarding;

public sealed record ComputeDevice(string? Id, string Name, bool Available = true, string? Note = null);

public static partial class InferenceDevices
{
    public static async Task<IReadOnlyList<ComputeDevice>> ListAsync(CancellationToken token = default)
    {
        List<ComputeDevice> devices = [new(null, "Automatic · best available GPU"), new("cpu", "CPU")];
        var server = Path.Combine(AppContext.BaseDirectory, "llama.cpp", "llama-server.exe");
        if (!File.Exists(server)) return devices;
        var info = new ProcessStartInfo(server) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(server)! };
        info.ArgumentList.Add("--list-devices");
        foreach (var name in info.Environment.Keys.Where(name => name.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("HF_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(name);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var process = Process.Start(info);
            if (process is null) return devices;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                foreach (Match match in DeviceLine().Matches(await output.ConfigureAwait(false) + "\n" + await errors.ConfigureAwait(false)))
                    devices.Add(new(match.Groups["id"].Value, match.Groups["name"].Value.Trim()));
            }
            finally { if (!process.HasExited) process.Kill(true); }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException) { }
        return devices;
    }

    [GeneratedRegex(@"^\s*(?<id>[A-Za-z][A-Za-z0-9_]*\d+):\s*(?<name>.+?)\s*\(\d+ MiB, \d+ MiB free\)\s*$", RegexOptions.Multiline)]
    private static partial Regex DeviceLine();

    public static async Task<IReadOnlyList<ComputeDevice>> ListNvidiaAsync()
    {
        List<ComputeDevice> devices = [];
        var smi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (!File.Exists(smi)) return devices;
        var info = new ProcessStartInfo(smi) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--query-gpu=uuid,name"); info.ArgumentList.Add("--format=csv,noheader");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var process = Process.Start(info);
            if (process is null) return devices;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                foreach (var line in (await output.ConfigureAwait(false)).Split('\n'))
                {
                    var parts = line.Split(',', 2);
                    if (parts.Length == 2 && Regex.IsMatch(parts[0].Trim(), "^GPU-[a-fA-F0-9-]{36}$")) devices.Add(new("cuda:" + parts[0].Trim(), parts[1].Trim() + " · GPU", true, "NVIDIA CUDA. First use downloads 2.35 GB of speech support files. Keep about 8 GB free for setup."));
                }
            }
            finally { if (!process.HasExited) process.Kill(true); }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException) { }
        return devices;
    }
}
