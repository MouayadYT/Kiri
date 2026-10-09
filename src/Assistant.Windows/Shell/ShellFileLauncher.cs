using System.ComponentModel;
using System.Diagnostics;
using Assistant.Core.Contracts;

namespace Assistant.Windows.Shell;

/// <summary>
/// Opens a file the user chose from a result, or shows it in File Explorer (PROJECT_SPEC §4.1): a direct user action, handed to
/// the shell as a double click or Explorer's own "Show in folder" would be, never a tool call. Only a full path that exists is
/// handed on, so a result that has since been moved or deleted is refused rather than searched for, and nothing is ever run from
/// a name alone. A path is never logged, and a failure is only <see langword="false"/>.
/// </summary>
public sealed class ShellFileLauncher : IFileLauncher
{
    private readonly IProcessStarter _processes;

    /// <summary>Creates the launcher over the real shell.</summary>
    public ShellFileLauncher()
        : this(new SystemProcessStarter())
    {
    }

    internal ShellFileLauncher(IProcessStarter processes)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
    }

    /// <inheritdoc/>
    public bool Open(string path)
    {
        if (!Exists(path))
        {
            return false;
        }

        // The shell opens it with whatever the user has chosen to open that kind of file: a folder in Explorer, a document in
        // its app. The working directory is the item's own folder, as a double click in Explorer has it.
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? "";
        return Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = folder });
    }

    /// <inheritdoc/>
    public bool Reveal(string path)
    {
        if (!Exists(path))
        {
            return false;
        }

        // Explorer reads its arguments as one string: the path is quoted, which a Windows path can never contain itself.
        return Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, Arguments = RevealArguments(path) });
    }

    /// <summary>The arguments that make Explorer show <paramref name="path"/> selected.</summary>
    internal static string RevealArguments(string path) => $"/select,\"{path}\"";

    private static bool Exists(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && !path.Contains('"', StringComparison.Ordinal)
        && (File.Exists(path) || Directory.Exists(path));

    private bool Start(ProcessStartInfo info)
    {
        try
        {
            return _processes.Start(info);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException
            or FileNotFoundException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts a process; the seam the tests replace.</summary>
    internal interface IProcessStarter
    {
        /// <summary>Starts the process described by <paramref name="info"/>.</summary>
        /// <returns>Whether it was started.</returns>
        bool Start(ProcessStartInfo info);
    }

    private sealed class SystemProcessStarter : IProcessStarter
    {
        public bool Start(ProcessStartInfo info)
        {
            // A shell verb that reuses a running program gives no process back; that is still a success.
            using var process = Process.Start(info);
            return true;
        }
    }
}
