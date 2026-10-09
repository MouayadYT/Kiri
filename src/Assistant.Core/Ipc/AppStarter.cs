using System.Diagnostics;

namespace Assistant.Core.Ipc;

/// <summary>Finds out whether the app is running, and starts it when it is not (PROJECT_SPEC §4.4: starting the app if needed).</summary>
public interface IAppStarter
{
    /// <summary>Whether an Assistant app process is running, ready or still starting.</summary>
    bool IsRunning();

    /// <summary>Starts the app. Returns <see langword="false"/> when it cannot be found or started.</summary>
    bool Start();
}

/// <summary>Starts <c>Assistant.UI.exe</c> from the folder this entry point is in, where the app ships it.</summary>
public sealed class ProcessAppStarter(string directory) : IAppStarter
{
    /// <summary>The app's executable.</summary>
    public const string ExecutableName = "Assistant.UI.exe";

    private string Executable => Path.Combine(directory, ExecutableName);

    /// <inheritdoc/>
    public bool IsRunning()
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName));
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    /// <inheritdoc/>
    public bool Start()
    {
        if (!File.Exists(Executable))
        {
            return false;
        }

        try
        {
            // Started in the background: what it is being started for (the files, the selection) is what it shows, and not its own window as well, which is
            // what it shows when the user opens it themselves.
            var start = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false,
                WorkingDirectory = directory,
            };
            start.ArgumentList.Add(Startup.LaunchOptions.BackgroundArgument);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
