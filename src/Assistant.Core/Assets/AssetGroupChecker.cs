using System.Security.Cryptography;

namespace Assistant.Core.Assets;

/// <summary>
/// Checks one asset's files against a manifest (PROJECT_SPEC §3.5, step 123): that each is there, is the size the manifest says and, unless this PC
/// already checked that very file, hashes to the SHA-256 the manifest says. It reads files only to hash them, never keeps their contents and never
/// reports more than the manifest's own names for the files.
/// </summary>
public static class AssetGroupChecker
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>Checks <paramref name="group"/>, whose files are in <paramref name="groupDirectory"/>.</summary>
    /// <param name="groupDirectory">The asset's folder: the kind's folder with the group's id inside it.</param>
    /// <param name="group">What the folder should hold.</param>
    /// <param name="mode">How thoroughly to check.</param>
    /// <param name="cache">Remembers the files checked before.</param>
    /// <param name="cancellationToken">Stops the check; the cache is left as it was.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<AssetGroupState> CheckAsync(
        string groupDirectory, AssetGroup group, AssetCheckMode mode, IAssetCheckCache cache, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(cache);
        var root = Path.GetFullPath(groupDirectory);
        var missing = new List<string>();
        var damaged = new List<string>();
        var unreadable = new List<string>();
        var unverified = false;
        var present = 0;

        foreach (var file in group.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(root, path))
            {
                damaged.Add(file.Path);
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                {
                    missing.Add(file.Path);
                    continue;
                }

                present++;
                if (info.Length != file.Size)
                {
                    damaged.Add(file.Path);
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                unreadable.Add(file.Path);
                continue;
            }

            var modified = info.LastWriteTimeUtc;
            if (mode != AssetCheckMode.Reverify && cache.Vouches(path, info.Length, modified, file.Sha256))
            {
                continue;
            }

            if (mode == AssetCheckMode.Peek)
            {
                unverified = true;
                continue;
            }

            string actual;
            try
            {
                actual = await HashAsync(path, cancellationToken).ConfigureAwait(false);

                // A file that changed while it was read is neither proven nor disproven: it is looked at again next time.
                info.Refresh();
                if (info.Length != file.Size || info.LastWriteTimeUtc != modified)
                {
                    unreadable.Add(file.Path);
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(file.Path);
                continue;
            }

            if (string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                cache.Record(path, info.Length, modified, actual);
            }
            else
            {
                cache.Forget(path);
                damaged.Add(file.Path);
            }
        }

        cache.Save();
        if (damaged.Count > 0)
        {
            return new AssetGroupState(group.Id, AssetGroupStatus.Damaged, damaged);
        }

        if (unreadable.Count > 0)
        {
            return new AssetGroupState(group.Id, AssetGroupStatus.Unreadable, unreadable);
        }

        if (missing.Count > 0)
        {
            return new AssetGroupState(
                group.Id, present == 0 ? AssetGroupStatus.Missing : AssetGroupStatus.Incomplete, missing);
        }

        return AssetGroupState.Of(group.Id, unverified ? AssetGroupStatus.Unverified : AssetGroupStatus.Verified);
    }

    /// <summary>
    /// What the files of <paramref name="group"/> look like from the outside: each one's size and last-write time, or that it is not there. Two
    /// calls give the same text exactly when no file was added, removed, resized or written to between them, which is when a result that was found
    /// for the files still stands.
    /// </summary>
    public static string Fingerprint(string groupDirectory, AssetGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var root = Path.GetFullPath(groupDirectory);
        var text = new System.Text.StringBuilder();
        foreach (var file in group.Files)
        {
            try
            {
                var info = new FileInfo(Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar))));
                text.Append(info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                text.Append('?');
            }

            text.Append(';');
        }

        return text.ToString();
    }

    /// <summary>The SHA-256 of the file at <paramref name="path"/>, as lower-case hexadecimal.</summary>
    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static bool IsInside(string root, string path) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
