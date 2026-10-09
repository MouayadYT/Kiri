namespace Assistant.Data;

/// <summary>
/// Removes the files the Data layer sets aside: the database's backups, damaged settings files and unfinished writes.
/// Only files a caller names are touched, and a file that cannot be deleted now (another program holds it) is left for
/// the next time, never a reason to fail what the caller was doing.
/// </summary>
internal static class FileCleanup
{
    /// <summary>Deletes <paramref name="path"/> if it can. Returns whether the file is gone.</summary>
    public static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps the newest <paramref name="keep"/> of <paramref name="files"/>, whose names sort in the order they were made,
    /// and deletes the others. Returns how many of those could not be deleted.
    /// </summary>
    public static int DeleteAllButNewest(IEnumerable<string> files, int keep) =>
        files
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(keep)
            .Count(file => !TryDelete(file));
}
