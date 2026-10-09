using System.Runtime.InteropServices;
using Assistant.Core.QuickSearch;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Shell;

/// <summary>
/// Reads the applications Start lists (PROJECT_SPEC §4.1) from the shell's apps folder (<c>shell:AppsFolder</c>), which is where the
/// Start menu itself gets them: desktop programs from the Start menu folders and packaged apps alike, each with the identity Windows
/// launches it by. It reads on a thread of its own that the shell can be used from, and it never looks at the disk beyond the shell's
/// own answer and, for a desktop program, whether its file exists. Start menu entries that are documents (a help file, a web page)
/// are left out, and so is a second entry with an identity already listed. Names and identities are never logged.
/// </summary>
public sealed class ShellApplicationSource : IApplicationSource, IDisposable
{
    private readonly StartMenuWatcher? _watcher;

    /// <summary>Creates the source, watching the Start menu folders for changes while it lives.</summary>
    public ShellApplicationSource()
        : this(watchStartMenu: true)
    {
    }

    internal ShellApplicationSource(bool watchStartMenu)
    {
        if (watchStartMenu)
        {
            _watcher = new StartMenuWatcher(() => Changed?.Invoke(this, EventArgs.Empty));
        }
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public Task<IReadOnlyList<InstalledApplication>> GetApplicationsAsync(CancellationToken cancellationToken) =>
        StaThread.RunAsync(() => Read(cancellationToken), cancellationToken);

    /// <summary>Stops watching the Start menu.</summary>
    public void Dispose() => _watcher?.Dispose();

    private static IReadOnlyList<InstalledApplication> Read(CancellationToken cancellationToken)
    {
        var folder = Shell32.CreateItem("shell:AppsFolder") ?? throw new IOException("The shell's apps folder could not be opened.");
        var applications = new List<InstalledApplication>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object? enumerator = null;
        try
        {
            var bound = folder.BindToHandler(0, Shell32.EnumItemsHandler, Shell32.EnumShellItemsId, out var pointer);
            if (bound < 0 || pointer == 0)
            {
                throw new IOException("The shell's apps folder could not be listed.");
            }

            try
            {
                enumerator = Marshal.GetObjectForIUnknown(pointer);
            }
            finally
            {
                Marshal.Release(pointer);
            }

            var items = (IEnumShellItems)enumerator;
            while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (Describe(item) is { } application && seen.Add(application.Id))
                    {
                        applications.Add(application);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }

            Marshal.ReleaseComObject(folder);
        }

        applications.Sort((first, second) => string.Compare(first.DisplayName, second.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return applications;
    }

    // One entry of the apps folder, or null for one that is not an application or has no name.
    private static InstalledApplication? Describe(IShellItem item)
    {
        var id = Shell32.NameOf(item, ShellItemNameKind.ParentRelativeParsing);
        var name = Shell32.NameOf(item, ShellItemNameKind.NormalDisplay);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || ApplicationLaunchId.IsDocument(id))
        {
            return null;
        }

        return new InstalledApplication(id, name.Trim()) { ExecutablePath = ProgramFile(item, id) };
    }

    // The program a shortcut points to, if the shell says so and it is a program that exists, else what the identity itself names.
    private static string? ProgramFile(IShellItem item, string id)
    {
        var target = Shell32.StringProperty(item, Shell32.LinkTargetParsingPath);
        if (ApplicationLaunchId.IsProgram(target))
        {
            return Path.GetFullPath(target!);
        }

        return ApplicationLaunchId.ExecutablePath(id);
    }
}
