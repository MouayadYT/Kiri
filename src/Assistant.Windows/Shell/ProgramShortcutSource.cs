using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Assistant.Core.QuickSearch;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Shell;

/// <summary>
/// Finds the applications Start does not list, where PowerToys Run's Program plugin looks for them (PROJECT_SPEC §4.1), so that what it finds is
/// found here too: on the Desktop, the user's and everyone's, the shortcuts (<c>.lnk</c>), the internet shortcuts that start an application
/// (<c>.url</c>: a game a launcher installed, such as Epic's Fortnite or a Steam game, is only that), the ClickOnce applications
/// (<c>.appref-ms</c>) and the programs (<c>.exe</c>). Each is listed by its own file, which is what starts it and what its icon is read from,
/// under the name the user sees on the Desktop. A shortcut to a folder, a document or a web page is not an application and is left out, and so
/// is an uninstaller. It reads on a thread the shell can be used from, looks no deeper than one folder under the Desktop, and never logs a
/// name or a path.
/// </summary>
/// <remarks>
/// PowerToys Run also lists the programs registered under App Paths in the registry. They are left out on purpose: measured on a real PC, they
/// were helpers and duplicates ("Office XML Handler", eight entries called "Python") and nothing a person starts that Start does not list already.
/// </remarks>
public sealed partial class ProgramShortcutSource : IApplicationSource, IDisposable
{
    // The most files looked at under the Desktop, so that a Desktop that holds a whole disk's worth does not hold up the list.
    private const int MaxFiles = 4000;

    private static readonly HashSet<string> Listed = new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url", ".appref-ms", ".exe" };

    private readonly IReadOnlyList<string> _folders;
    private readonly StartMenuWatcher? _watcher;

    /// <summary>Creates the source over the Desktop, watching it for changes while it lives.</summary>
    public ProgramShortcutSource()
        : this(DesktopFolders(), watch: true)
    {
    }

    internal ProgramShortcutSource(IReadOnlyList<string> folders, bool watch)
    {
        _folders = folders ?? throw new ArgumentNullException(nameof(folders));
        if (watch)
        {
            _watcher = new StartMenuWatcher(() => Changed?.Invoke(this, EventArgs.Empty), folders);
        }
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public Task<IReadOnlyList<InstalledApplication>> GetApplicationsAsync(CancellationToken cancellationToken) =>
        StaThread.RunAsync(() => Read(cancellationToken), cancellationToken);

    /// <summary>Stops watching the Desktop.</summary>
    public void Dispose() => _watcher?.Dispose();

    /// <summary>The Desktop's folders: the user's own, and the one everyone on this PC shares.</summary>
    internal static IReadOnlyList<string> DesktopFolders() =>
    [
        .. new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        }.Where(folder => !string.IsNullOrEmpty(folder)).Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    private IReadOnlyList<InstalledApplication> Read(CancellationToken cancellationToken)
    {
        var applications = new List<InstalledApplication>();

        // One of a name: the user's own Desktop is read first, so its shortcut stands for one of the same name on everyone's.
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var budget = MaxFiles;
        foreach (var folder in _folders)
        {
            foreach (var file in FilesOf(folder, ref budget))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Describe(file) is { } application && names.Add(application.DisplayName))
                {
                    applications.Add(application);
                }
            }
        }

        return applications;
    }

    // The files in a folder and in the folders directly under it that could be applications. What cannot be read is passed over.
    private static List<string> FilesOf(string folder, ref int budget)
    {
        var files = new List<string>();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return files;
        }

        var folders = new List<string> { folder };
        try
        {
            folders.AddRange(Directory.EnumerateDirectories(folder).Where(IsOrdinary));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only the folder itself, then.
        }

        foreach (var directory in folders)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (--budget < 0)
                    {
                        return files;
                    }

                    if (Listed.Contains(Path.GetExtension(file)) && IsOrdinary(file))
                    {
                        files.Add(file);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be read lists nothing.
            }
        }

        return files;
    }

    // Not hidden and not the system's: what the user sees on the Desktop.
    private static bool IsOrdinary(string path)
    {
        try
        {
            return (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // One file of the Desktop as an application, or null for one that is not: a shortcut to a folder, a document or a page, an uninstaller.
    private static InstalledApplication? Describe(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file).Trim();
        if (name.Length == 0 || IsUninstaller(name))
        {
            return null;
        }

        switch (Path.GetExtension(file).ToLowerInvariant())
        {
            case ".url":
                return InternetShortcut.StartsAnApplication(file) ? new InstalledApplication(file, name) : null;
            case ".lnk":
                var target = LinkTarget(file);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    // A shortcut whose target the shell cannot name as a file (an advertised program, a packaged app) is kept: it starts something.
                    if (Directory.Exists(target) || ApplicationLaunchId.IsDocument(target) || IsUninstaller(Path.GetFileNameWithoutExtension(target)))
                    {
                        return null;
                    }

                    if (Path.IsPathFullyQualified(target) && !ApplicationLaunchId.IsProgram(target) && Path.GetExtension(target).Length > 0
                        && !IsScript(target))
                    {
                        // A shortcut to a file of some other kind (a picture, a spreadsheet) opens a document.
                        return null;
                    }
                }

                return new InstalledApplication(file, name)
                {
                    ExecutablePath = ApplicationLaunchId.IsProgram(target) ? Path.GetFullPath(target!) : null,
                };
            case ".appref-ms":
                return new InstalledApplication(file, name);
            default:
                return ApplicationLaunchId.IsProgram(file)
                    ? new InstalledApplication(Path.GetFullPath(file), name) { ExecutablePath = Path.GetFullPath(file) }
                    : null;
        }
    }

    // The file a shortcut points to, as the shell says it, or null.
    private static string? LinkTarget(string shortcut)
    {
        IShellItem? item = null;
        try
        {
            item = Shell32.CreateItem(shortcut);
            return item is null ? null : Shell32.StringProperty(item, Shell32.LinkTargetParsingPath);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (item is not null)
            {
                Marshal.ReleaseComObject(item);
            }
        }
    }

    private static bool IsScript(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".bat" or ".cmd" or ".msc" or ".cpl" or ".ps1" or ".vbs" or ".com" or ".appref-ms" or ".msi";

    private static bool IsUninstaller(string? name) => !string.IsNullOrEmpty(name) && Uninstaller().IsMatch(name);

    [GeneratedRegex(@"uninstall|^unins\d*$|^uninst$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Uninstaller();
}
