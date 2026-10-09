using Assistant.Core.Contracts;

namespace Assistant.Core.Ipc;

/// <summary>Why a file named in an invocation request is not used (PROJECT_SPEC §4.4: reported per file).</summary>
public enum InvokedFileProblem
{
    /// <summary>The file is used.</summary>
    None = 0,

    /// <summary>The text is not a full path to a file on a drive or a network share, such as a relative or device path.</summary>
    NotAPath = 1,

    /// <summary>No file is there (it was moved or deleted).</summary>
    NotFound = 2,

    /// <summary>The Assistant does not read files of its type (<see cref="ExplorerFileTypes"/>).</summary>
    NotSupported = 3,

    /// <summary>The same file was named before; it is used once.</summary>
    Repeated = 4,

    /// <summary>More usable files were named than one invocation takes (<see cref="InvocationProtocol.MaxFiles"/>).</summary>
    OverLimit = 5,

    /// <summary>The path is a folder: the Assistant attaches files.</summary>
    Folder = 6,
}

/// <summary>One file named in an invocation request, and whether it can be used.</summary>
/// <param name="Path">The full path, or the text as it came when it was not a path.</param>
/// <param name="Problem">Why it is not used, or <see cref="InvokedFileProblem.None"/>.</param>
public sealed record InvokedFile(string Path, InvokedFileProblem Problem)
{
    /// <summary>The file's name, for telling the user about it.</summary>
    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;

    /// <summary>Whether the file is used.</summary>
    public bool IsUsable => Problem == InvokedFileProblem.None;
}

/// <summary>
/// Checks the paths another process sent before the app uses any of them (PROJECT_SPEC §5.7: validation, path existence):
/// each must be a full path to an existing file of a supported type, each file counts once, and at most
/// <see cref="InvocationProtocol.MaxFiles"/> are used. Nothing is opened or read; only whether the file exists is asked.
/// </summary>
public static class InvokedFiles
{
    /// <summary>Checks <paramref name="paths"/>, in order: the first usable files up to the limit are the ones used.</summary>
    /// <param name="paths">The paths as they were sent.</param>
    /// <param name="fileExists">Whether a file (not a folder) is at a full path.</param>
    /// <param name="maxFiles">How many usable files are taken.</param>
    /// <param name="directoryExists">Whether a folder is at a full path; when given, a folder is reported as one instead of as a file that is missing or of a type not read.</param>
    /// <returns>One entry for each path, in the same order.</returns>
    public static IReadOnlyList<InvokedFile> Check(
        IEnumerable<string> paths, Func<string, bool> fileExists, int maxFiles = InvocationProtocol.MaxFiles,
        Func<string, bool>? directoryExists = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFiles);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var used = 0;
        var checkedFiles = new List<InvokedFile>();
        foreach (var path in paths)
        {
            if (InvokedPaths.Normalize(path) is not { } full)
            {
                checkedFiles.Add(new InvokedFile(path ?? "", InvokedFileProblem.NotAPath));
                continue;
            }

            var problem = !seen.Add(full) ? InvokedFileProblem.Repeated
                : directoryExists?.Invoke(full) == true ? InvokedFileProblem.Folder
                : !ExplorerFileTypes.IsSupported(full) ? InvokedFileProblem.NotSupported
                : !fileExists(full) ? InvokedFileProblem.NotFound
                : used == maxFiles ? InvokedFileProblem.OverLimit
                : InvokedFileProblem.None;
            if (problem == InvokedFileProblem.None)
            {
                used++;
            }

            checkedFiles.Add(new InvokedFile(full, problem));
        }

        return checkedFiles;
    }
}
