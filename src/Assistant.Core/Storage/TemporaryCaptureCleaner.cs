using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Core.Storage;

/// <summary>
/// Empties <see cref="AppPaths.TemporaryCapturesDirectory"/> (PROJECT_SPEC §3.5). Nothing in the app puts a screenshot or a crop there:
/// they stay in memory. The folder is for the one file an API may need to read from disk, and that file is deleted as soon as it is
/// used. Should the application stop in the middle of that, such as in a crash or when the PC loses power, the file is still there;
/// the application therefore empties the folder when it starts, and again when it stops, so that no capture outlives the run that took it.
/// </summary>
/// <remarks>It deletes what it can and logs how many it did, never a name: a file that is in use, or cannot be deleted, is left for the next time.</remarks>
public sealed partial class TemporaryCaptureCleaner(AppPaths paths, ILogger<TemporaryCaptureCleaner>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<TemporaryCaptureCleaner>.Instance;

    /// <summary>Deletes every file and folder in the folder. A folder that does not exist has nothing to delete.</summary>
    /// <returns>How many files and folders were deleted at the top of the folder (a folder counts once, with what was in it).</returns>
    public int DeleteAll()
    {
        var deleted = 0;
        var left = 0;
        try
        {
            var folder = new DirectoryInfo(paths.TemporaryCapturesDirectory);
            if (!folder.Exists)
            {
                return 0;
            }

            foreach (var entry in folder.EnumerateFileSystemInfos())
            {
                if (TryDelete(entry))
                {
                    deleted++;
                }
                else
                {
                    left++;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The folder itself could not be read: nothing more can be done now.
            LogNotCleaned(_logger, exception.GetType().Name);
            return deleted;
        }

        if (deleted > 0 || left > 0)
        {
            LogCleaned(_logger, deleted, left);
        }

        return deleted;
    }

    private static bool TryDelete(FileSystemInfo entry)
    {
        try
        {
            // A link is removed and what it points to is left alone: the folder is ours, what a link in it leads to may not be.
            if (entry is DirectoryInfo directory && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                directory.Delete(recursive: true);
            }
            else
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                }

                entry.Delete();
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 2630, Level = LogLevel.Information, Message = "Temporary captures were cleaned up: {Deleted} deleted, {Left} left")]
    private static partial void LogCleaned(ILogger logger, int deleted, int left);

    [LoggerMessage(EventId = 2631, Level = LogLevel.Warning, Message = "The temporary captures folder could not be cleaned up ({ExceptionType})")]
    private static partial void LogNotCleaned(ILogger logger, string exceptionType);
}
