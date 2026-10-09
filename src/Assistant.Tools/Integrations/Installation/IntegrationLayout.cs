using Assistant.Core.Storage;

namespace Assistant.Tools.Integrations;

/// <summary>
/// Where the Assistant keeps what it installs for integrations (PROJECT_SPEC §3.5, §4.8, step 108): <c>Integrations\&lt;id&gt;\&lt;version&gt;</c> for each version of
/// each integration, <c>Integrations\.staging</c> for what is being installed, and <c>Runtimes\&lt;kind&gt;\&lt;version&gt;</c> for the runtimes. The locations are worked out
/// here from an id and a version that have been checked; a path is never taken from a record, a package or a download.
/// </summary>
public sealed class IntegrationLayout
{
    private const string StagingName = ".staging";

    private readonly string _root;
    private readonly string _runtimes;

    /// <summary>Creates the layout under the app's folders.</summary>
    public IntegrationLayout(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.IntegrationsDirectory));
        _runtimes = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.RuntimesDirectory));
    }

    /// <summary>The folder every integration is in.</summary>
    public string Root => _root;

    /// <summary>The folder every runtime is in.</summary>
    public string RuntimesRoot => _runtimes;

    /// <summary>The folder of the integration <paramref name="id"/>, all its versions.</summary>
    /// <exception cref="ArgumentException">The id is not a valid integration id.</exception>
    public string IntegrationDirectory(string id)
    {
        if (!IntegrationRules.IsValidId(id))
        {
            throw new ArgumentException("Not an integration id.", nameof(id));
        }

        return Path.Combine(_root, id);
    }

    /// <summary>The folder of one version of the integration <paramref name="id"/>.</summary>
    /// <exception cref="ArgumentException">The id or the version is not valid.</exception>
    public string VersionDirectory(string id, string version)
    {
        if (!IntegrationRules.IsValidVersion(version) || version.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Not a version.", nameof(version));
        }

        return Path.Combine(IntegrationDirectory(id), version);
    }

    /// <summary>Where things are put while they are being installed, so that an install that fails or is cancelled leaves nothing in an integration's own folder.</summary>
    public string StagingRoot => Path.Combine(_root, StagingName);

    /// <summary>A new, empty place to install into.</summary>
    public string NewStagingDirectory() => Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));

    /// <summary>The folder of the runtime <paramref name="kind"/> at <paramref name="version"/>.</summary>
    public string RuntimeDirectory(RuntimeKind kind, string version)
    {
        if (!IntegrationRules.IsValidVersion(version) || version.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Not a version.", nameof(version));
        }

        return Path.Combine(_runtimes, kind == RuntimeKind.NodeJs ? "node" : "python", version);
    }

    /// <summary>Whether <paramref name="path"/> is inside the integrations folder (and not the folder itself).</summary>
    public bool IsInsideIntegrations(string path) => IsInside(_root, path);

    /// <summary>Whether <paramref name="path"/> is inside the runtimes folder (and not the folder itself).</summary>
    public bool IsInsideRuntimes(string path) => IsInside(_runtimes, path);

    private static bool IsInside(string root, string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Deleting folders the installer made.</summary>
internal static class InstallFiles
{
    /// <summary>
    /// Deletes <paramref name="path"/> and everything in it, trying a few times because a program that was just ended may still hold a file for a moment.
    /// Returns whether it is gone. It never follows a link out of the folder: a link is removed, not what it points to.
    /// </summary>
    public static bool DeleteDirectory(string path, int attempts = 5)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return true;
                }

                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ClearReadOnly(path);
                Thread.Sleep(150 * (attempt + 1));
            }
        }

        return !Directory.Exists(path);
    }

    // A read-only file stops its folder being deleted. Links are not looked through, so nothing outside the folder is touched.
    private static void ClearReadOnly(string path)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // What cannot be changed is tried again.
        }
    }

    /// <summary>Deletes <paramref name="path"/> if it is a file; a file that cannot be deleted is left.</summary>
    public static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the next clean-up.
        }
    }
}
