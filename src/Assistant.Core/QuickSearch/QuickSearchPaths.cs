namespace Assistant.Core.QuickSearch;

/// <summary>How quick search writes where something is.</summary>
public static class QuickSearchPaths
{
    /// <summary>
    /// The folder the item at <paramref name="path"/> is in, with the user's own folder written <c>~</c> so the path is short enough
    /// to read at a glance (<c>~\Documents\Taxes</c>). A path with no folder is returned as it is.
    /// </summary>
    /// <param name="path">A full path.</param>
    /// <param name="userProfile">The user's profile folder; the current user's by default.</param>
    public static string Location(string path, string? userProfile = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var folder = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(folder))
        {
            return path;
        }

        var profile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0
            && folder.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
            && (folder.Length == profile.Length || folder[profile.Length] is '\\' or '/')
            ? "~" + folder[profile.Length..]
            : folder;
    }
}
