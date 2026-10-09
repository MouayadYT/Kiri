using System.Diagnostics;
using System.IO;

namespace Assistant.UI.Integrations;

/// <summary>
/// Runs one of the entry points shipped beside the app (File Explorer's, the browser's) with its <c>register</c> or <c>unregister</c>
/// command: an entry point owns its registration (PROJECT_SPEC §5.2), and the app only asks for it.
/// </summary>
internal static class EntryPointRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Runs <paramref name="executableName"/> from <paramref name="directory"/> with <paramref name="command"/>, hidden, and waits for it.
    /// </summary>
    /// <returns>Whether it ran and ended with exit code 0; <see langword="false"/> when it is not there, did not start, or did not end in time.</returns>
    public static async Task<bool> RunAsync(
        string directory, string executableName, string command, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(directory, executableName);
        if (!File.Exists(executable))
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(executable, command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory,
            });
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            // One that does not finish is ended, so it cannot change the registry after the answer was given.
            try
            {
                process?.Kill();
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            return false;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }
}
