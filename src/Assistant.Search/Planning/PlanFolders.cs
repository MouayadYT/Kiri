using Microsoft.Win32;

namespace Assistant.Search.Planning;

/// <summary>
/// The folders a plan may name, by a fixed name each. The model never writes a path: it picks one of these names, and the
/// planner turns it into the folder of this user's PC, so a plan can only look where the user's own libraries are.
/// </summary>
internal interface IPlanFolders
{
    /// <summary>The names a plan may use, as they are written in it.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>The full path of the folder <paramref name="name"/> stands for, or <see langword="null"/> when there is none.</summary>
    string? Resolve(string name);
}

/// <summary>The user's own libraries, as Windows has them set up (they may be moved, for example to OneDrive).</summary>
internal sealed class KnownPlanFolders : IPlanFolders
{
    // The Downloads folder has no Environment.SpecialFolder; Windows records where it is for this user.
    private const string UserShellFolders = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
    private const string DownloadsId = "{374DE290-123F-4565-9164-39C4925E467B}";

    /// <inheritdoc/>
    public IReadOnlyList<string> Names { get; } =
        ["documents", "downloads", "desktop", "pictures", "screenshots", "music", "videos"];

    /// <inheritdoc/>
    public string? Resolve(string name) => name switch
    {
        "documents" => Special(Environment.SpecialFolder.MyDocuments),
        "desktop" => Special(Environment.SpecialFolder.DesktopDirectory),
        "pictures" => Special(Environment.SpecialFolder.MyPictures),
        "music" => Special(Environment.SpecialFolder.MyMusic),
        "videos" => Special(Environment.SpecialFolder.MyVideos),
        "downloads" => Downloads(),
        "screenshots" => Special(Environment.SpecialFolder.MyPictures) is { } pictures ? Path.Combine(pictures, "Screenshots") : null,
        _ => null,
    };

    private static string? Special(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        return path.Length > 0 && Path.IsPathFullyQualified(path) ? path : null;
    }

    private static string? Downloads()
    {
        try
        {
            if (Registry.GetValue(UserShellFolders, DownloadsId, null) is string recorded
                && Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(recorded)))
            {
                return Environment.ExpandEnvironmentVariables(recorded);
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Not readable: the usual place is used.
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 ? Path.Combine(profile, "Downloads") : null;
    }
}
