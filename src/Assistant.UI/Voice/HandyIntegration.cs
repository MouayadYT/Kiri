using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Assistant.UI.Voice;

public sealed record HandyInstallation(string? Executable, string ModelName, bool ModelInstalled, string Device, bool PasteCompatible = true)
{
    public bool Installed => Executable is not null;
}

/// <summary>Reads only Handy's public ASR selection. Never copies its models, history, credentials, or settings.</summary>
public class HandyIntegration
{
    public virtual HandyInstallation Detect()
    {
        string? executable = null;
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Handy", "handy.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Handy", "handy.exe"),
        };
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key is null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                using var entry = key.OpenSubKey(name);
                if (!string.Equals(entry?.GetValue("DisplayName") as string, "Handy", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry?.GetValue("InstallLocation") is string location && Path.IsPathFullyQualified(location)) candidates.Add(Path.Combine(location, "handy.exe"));
            }
        }
        executable = candidates.FirstOrDefault(File.Exists);
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.pais.handy");
        var selection = ""; var device = "Managed in Handy"; var installed = false; var pasteCompatible = true;
        try
        {
            var store = Path.Combine(data, "settings_store.json");
            if (File.Exists(store) && new FileInfo(store).Length <= 2_000_000)
            {
                using var json = JsonDocument.Parse(File.ReadAllText(store));
                if (json.RootElement.TryGetProperty("settings", out var settings))
                {
                    if (settings.TryGetProperty("paste_method", out var paste) && paste.ValueKind == JsonValueKind.String) pasteCompatible = paste.GetString() == "ctrl_v";
                    if (settings.TryGetProperty("auto_submit", out var submit) && submit.ValueKind == JsonValueKind.True) pasteCompatible = false;
                    if (settings.TryGetProperty("selected_model", out var model) && model.ValueKind == JsonValueKind.String) selection = model.GetString() ?? "";
                    if (settings.TryGetProperty("transcribe_accelerator", out var accelerator) && accelerator.ValueKind == JsonValueKind.String)
                        device = "Handy's device: " + (accelerator.GetString() is "cpu" ? "CPU" : accelerator.GetString() is "gpu" ? "GPU" : "Automatic");
                }
            }
            installed = IsSelectedModelPresent(data, selection);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        var label = selection.Length == 0 ? "Choose and download a model in Handy" : selection.Split('/').Last();
        return new(executable, label, installed, device, pasteCompatible);
    }
    internal static bool IsSelectedModelPresent(string data, string selection)
    {
        if (selection.Length == 0 || selection.Any(char.IsControl) || selection.Contains("..") || selection.Contains('\\') || selection.Contains(':')) return false;
        var legacy = selection switch
        {
            "small" => "ggml-small.bin", "medium" => "whisper-medium-q4_1.bin", "turbo" => "ggml-large-v3-turbo.bin", "large" => "ggml-large-v3-q5_0.bin",
            "breeze-asr" => "breeze-asr-q5_k.bin", "parakeet-tdt-0.6b-v2" => "parakeet-tdt-0.6b-v2-int8", "parakeet-tdt-0.6b-v3" => "parakeet-tdt-0.6b-v3-int8", _ => selection.Split('/').Last(),
        };
        var local = Path.Combine(data, "models", legacy);
        if (File.Exists(local)) return new FileInfo(local).Length > 0;
        if (Directory.Exists(local)) return Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories).Any();
        var parts = selection.Split('/');
        if (parts.Length < 3) return false;
        var cache = Path.Combine(Environment.GetEnvironmentVariable("HF_HUB_CACHE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface", "hub"), "models--" + parts[0] + "--" + parts[1], "snapshots");
        return Directory.Exists(cache) && Directory.EnumerateDirectories(cache).Any(snapshot => File.Exists(Path.Combine(snapshot, Path.Combine(parts.Skip(2).ToArray()))));
    }
    public virtual void Open()
    {
        var installation = Detect();
        if (installation.Executable is { } executable) Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        else Process.Start(new ProcessStartInfo("https://handy.computer/") { UseShellExecute = true });
    }
    // A loaded model is what makes Handy large: on the graphics card it shows as graphics memory, on the processor as memory of its own. Without one
    // Handy holds a fraction of either.
    private const long ModelGraphicsBytes = 150L << 20;
    private const long ModelPrivateBytes = 700L << 20;

    /// <summary>
    /// Whether Handy is running and holds a recognizer model now, on the graphics card or in memory. Handy lets go of its model by itself after it has
    /// been idle for the time set in it, so this is often false while Handy runs.
    /// </summary>
    public virtual bool IsHoldingModel()
    {
        foreach (var process in Running())
        {
            using (process)
            {
                try
                {
                    if (process.PrivateMemorySize64 >= ModelPrivateBytes
                        || Assistant.Windows.Gaming.ProcessGraphicsMemory.DedicatedBytes(process.Id) >= ModelGraphicsBytes)
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }

        return false;
    }

    /// <summary>Whether Handy is running.</summary>
    public virtual bool IsRunning()
    {
        var running = Running();
        foreach (var process in running) process.Dispose();
        return running.Count > 0;
    }

    /// <summary>
    /// Ends Handy, which is the only way to make it let go of its model: it has no command that unloads one. Whether a running Handy was ended.
    /// Nothing of Handy's is touched besides: its settings, models and history stay as they are, and <see cref="StartHidden"/> brings it back.
    /// </summary>
    public virtual bool Close()
    {
        var closed = false;
        foreach (var process in Running())
        {
            using (process)
            {
                try
                {
                    // With the processes it started for its window, which would otherwise be left behind.
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                    closed = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }

        return closed;
    }

    /// <summary>Starts Handy in the notification area, without its window, as it starts with Windows. Whether it was started.</summary>
    public virtual bool StartHidden()
    {
        if (Detect().Executable is not { } executable) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, "--start-hidden") { UseShellExecute = true });
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return false; }
    }

    // How long a Handy that has just been started is given before it is sent a command: it takes commands from a second launch only once it has set
    // itself up, and one that comes sooner is lost.
    private static readonly TimeSpan StartupSettle = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Makes sure Handy is running, without showing its window, and returns whether it had to be started. A Handy that is already running is left
    /// exactly as it is. It must never be sent <c>--start-hidden</c>: Handy shows its main window for any command line that is not one of its remote
    /// controls, so that would open the window instead of hiding anything, and take the keyboard from the prompt the transcript is pasted into.
    /// </summary>
    public virtual async Task<bool> EnsureRunningAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (IsRunning()) return false;
        if (!StartHidden()) throw new IOException("Handy could not start.");
        for (var waited = 0; waited < 5000 && !IsRunning(); waited += 100) await Task.Delay(100, token).ConfigureAwait(false);
        await Task.Delay(StartupSettle, token).ConfigureAwait(false);
        return true;
    }

    // The running processes that are the Handy this PC has installed, and not another program of the same name.
    private List<Process> Running()
    {
        var running = new List<Process>();
        if (Detect().Executable is not { } executable) return running;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) { running.Add(process); continue; }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }

        return running;
    }

    public virtual async Task CommandAsync(string argument, CancellationToken token)
    {
        var executable = Detect().Executable ?? throw new IOException("Install Handy first, then click Refresh.");
        // The single-instance CLI forwards to Handy's existing process. Never use --transcribe-file, which starts a second model.
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Handy could not start.");
        // A first launch stays running; forwarded commands normally exit immediately.
        await Task.WhenAny(process.WaitForExitAsync(token), Task.Delay(1000, token)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
}
