namespace Assistant.Core.Contracts;

/// <summary>
/// Opens a file the user chose, or shows it in File Explorer (PROJECT_SPEC §4.1): what a result row does when it is activated.
/// It is the user's own action, never a tool call, and it is never given a path the user did not pick from a result.
/// </summary>
public interface IFileLauncher
{
    /// <summary>Opens the file or folder at <paramref name="path"/> with its default handler, as a double click would.</summary>
    /// <returns><see langword="false"/> when it does not exist or cannot be opened.</returns>
    bool Open(string path);

    /// <summary>Shows the file or folder at <paramref name="path"/> selected in a File Explorer window.</summary>
    /// <returns><see langword="false"/> when it does not exist or Explorer could not be started.</returns>
    bool Reveal(string path);
}
