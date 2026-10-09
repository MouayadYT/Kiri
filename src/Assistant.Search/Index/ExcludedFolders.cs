namespace Assistant.Search.Index;

/// <summary>
/// The folders whose contents never appear in results (PROJECT_SPEC §4.7): the user's excluded-folders setting, made ready
/// to test a path against and to write into a query.
/// </summary>
internal sealed class ExcludedFolders
{
    private readonly string[] _folders;

    private ExcludedFolders(string[] folders) => _folders = folders;

    /// <summary>No folder is excluded.</summary>
    public static ExcludedFolders None { get; } = new([]);

    /// <summary>Whether no folder is excluded.</summary>
    public bool IsEmpty => _folders.Length == 0;

    /// <summary>
    /// Reads the folders as full paths without a trailing separator (a drive's root keeps it). A folder that is not a valid
    /// path is left out, and so is one that another folder holds.
    /// </summary>
    public static ExcludedFolders Create(IEnumerable<string>? folders)
    {
        if (folders is null)
        {
            return None;
        }

        var normalized = new List<string>();
        foreach (var folder in folders)
        {
            if (Normalize(folder) is { } full && !normalized.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(full);
            }
        }

        var kept = normalized.Where(folder => !normalized.Any(other => other != folder && Holds(other, folder))).ToArray();
        return kept.Length == 0 ? None : new ExcludedFolders(kept);
    }

    /// <summary>Whether <paramref name="path"/> is an excluded folder or is inside one.</summary>
    public bool Contains(string path)
    {
        if (IsEmpty || Normalize(path) is not { } full)
        {
            return false;
        }

        foreach (var folder in _folders)
        {
            if (Holds(folder, full))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The <c>file:</c> URLs, as Windows Search writes them in <c>System.ItemUrl</c>, of the excluded folders: each as it is
    /// (the folder itself) and as a prefix ending in a separator (what is inside it), at most <paramref name="max"/> folders.
    /// </summary>
    public IEnumerable<(string Folder, string Contents)> Urls(int max)
    {
        foreach (var folder in _folders.Take(max))
        {
            var url = "file:" + folder.Replace('\\', '/');
            yield return (url, url.EndsWith('/') ? url : url + "/");
        }
    }

    private static bool Holds(string folder, string path)
    {
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length == folder.Length
            || IsSeparator(folder[^1])
            || IsSeparator(path[folder.Length]);
    }

    private static string? Normalize(string? path)
    {
        // A relative folder would mean whatever the process's current folder is, which is never what the user meant.
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(full);
            return full.Length > (root?.Length ?? 0) ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsSeparator(char character) => character is '\\' or '/';
}
