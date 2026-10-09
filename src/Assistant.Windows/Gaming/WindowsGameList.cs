using Microsoft.Win32;

namespace Assistant.Windows.Gaming;

/// <summary>
/// The programs Windows itself has taken for games: the list its own Game Mode and Game Bar keep for the signed-in user
/// (<c>HKCU\System\GameConfigStore\Children</c>), which has the store games, the emulators and the copies kept in any folder alike. Only the
/// paths are read. Windows adds to it now and then, so a program that is not on it is looked for again, but not more than once a minute.
/// </summary>
internal sealed class WindowsGameList
{
    private const string ChildrenKey = @"System\GameConfigStore\Children";
    private static readonly TimeSpan RereadAfter = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public WindowsGameList(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Whether Windows lists the program at <paramref name="executablePath"/> as a game.</summary>
    public bool Contains(string executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        lock (_gate)
        {
            if (_paths.Contains(executablePath))
            {
                return true;
            }

            var now = _clock.GetUtcNow();
            if (now - _readAt < RereadAfter)
            {
                return false;
            }

            _readAt = now;
            _paths = Read();
            return _paths.Contains(executablePath);
        }
    }

    private static HashSet<string> Read()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(ChildrenKey);
            if (children is null)
            {
                return paths;
            }

            foreach (var name in children.GetSubKeyNames())
            {
                using var child = children.OpenSubKey(name);
                if (child?.GetValue("MatchedExeFullPath") is string path && path.Length > 0)
                {
                    paths.Add(path);
                }
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Without the list the other signs still decide.
        }

        return paths;
    }
}
