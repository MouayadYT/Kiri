using System.ComponentModel;
using System.Diagnostics;
using Assistant.Core.QuickSearch;

namespace Assistant.Windows.Shell;

/// <summary>
/// Starts an application the user chose from a result (PROJECT_SPEC §4.1), the way Start's own tiles do: the shell is given the
/// application's entry in the apps folder (<c>shell:AppsFolder\identity</c>), which starts a packaged app, a desktop program and a
/// shortcut alike, with whatever the entry says to start it with. An application that is listed by its own file, as the ones found
/// the way PowerToys Run finds them are (a shortcut or a program on the Desktop, a game launcher's link), is
/// started by opening that file. It is a direct user action, never a tool call. It is given the
/// identity of an application from the list Windows gave, and nothing else: an identity that is empty, holds a control character or
/// a quote, or is not a single entry (it names another folder by <c>..</c>) is refused. An identity is never logged, and a failure is
/// only <see langword="false"/>.
/// </summary>
public sealed class ShellApplicationLauncher : IApplicationLauncher
{
    private readonly Func<ProcessStartInfo, bool> _start;

    /// <summary>Creates the launcher over the real shell.</summary>
    public ShellApplicationLauncher()
        : this(StartWithShell)
    {
    }

    internal ShellApplicationLauncher(Func<ProcessStartInfo, bool> start) =>
        _start = start ?? throw new ArgumentNullException(nameof(start));

    /// <inheritdoc/>
    public bool Launch(string applicationId)
    {
        if (!IsLaunchable(applicationId))
        {
            return false;
        }

        try
        {
            // A shortcut on the Desktop, a game launcher's link or a program that is not in Start is opened itself, as PowerToys Run does (a
            // program in its own folder, as a shortcut to it would start it); everything else through its entry in the apps folder.
            if (ApplicationLaunchId.IsStartableFile(applicationId))
            {
                var info = new ProcessStartInfo(applicationId) { UseShellExecute = true };
                if (ApplicationLaunchId.IsProgram(applicationId) && Path.GetDirectoryName(applicationId) is { Length: > 0 } folder)
                {
                    info.WorkingDirectory = folder;
                }

                return _start(info);
            }

            return _start(new ProcessStartInfo(AppsFolderEntry(applicationId)) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The name the shell knows the application's entry by.</summary>
    internal static string AppsFolderEntry(string applicationId) => "shell:AppsFolder\\" + applicationId;

    /// <summary>Whether <paramref name="applicationId"/> may be handed to the shell.</summary>
    internal static bool IsLaunchable(string? applicationId) =>
        !string.IsNullOrWhiteSpace(applicationId)
        && applicationId.Length <= 1024
        && !applicationId.Any(char.IsControl)
        && !applicationId.Contains('"', StringComparison.Ordinal)
        && !applicationId.Contains("..\\", StringComparison.Ordinal)
        && !applicationId.Contains("\\..", StringComparison.Ordinal)
        && !applicationId.StartsWith("..", StringComparison.Ordinal);

    private static bool StartWithShell(ProcessStartInfo info)
    {
        // A shell verb that reuses a running program gives no process back; that is still a success.
        using var process = Process.Start(info);
        return true;
    }
}
