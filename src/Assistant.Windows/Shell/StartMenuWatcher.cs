namespace Assistant.Windows.Shell;

/// <summary>
/// Notices that something was added to or removed from the folders applications are listed from, so the list read earlier can be read
/// again (PROJECT_SPEC §4.1): the Start menu's folders, or the folders it is given, such as the Desktop's. It only notices that something
/// changed, never what, and says so once for a burst of changes, as an installer writes many files. A packaged app does not touch these
/// folders; the list's age covers it.
/// </summary>
internal sealed class StartMenuWatcher : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Action _changed;
    private readonly Timer _timer;

    /// <summary>Watches the Start menu's folders, the user's and everyone's.</summary>
    public StartMenuWatcher(Action changed)
        : this(changed,
        [
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        ])
    {
    }

    /// <summary>Watches <paramref name="folders"/> and everything under them; one that does not exist is left out.</summary>
    public StartMenuWatcher(Action changed, IEnumerable<string> folders)
    {
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        ArgumentNullException.ThrowIfNull(folders);
        _timer = new Timer(_ => _changed(), null, Timeout.Infinite, Timeout.Infinite);
        foreach (var root in folders)
        {
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                Watch(root);
            }
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
        _timer.Dispose();
    }

    private void Watch(string folder)
    {
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A folder that cannot be watched is only not noticed; the list's age still brings the change in.
        }
    }

    // A change starts a short quiet period; another change before it ends starts it again, so a burst is one notice.
    private void OnChanged(object sender, EventArgs e)
    {
        try
        {
            _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // The watcher was disposed while the change arrived.
        }
    }
}
