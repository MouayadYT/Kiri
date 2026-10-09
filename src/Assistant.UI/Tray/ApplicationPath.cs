using System.IO;

namespace Assistant.UI.Tray;

/// <summary>Where the app's executable is, for the entry Windows starts at sign-in.</summary>
internal static class ApplicationPath
{
    private const string ExecutableName = "Assistant.UI.exe";

    /// <summary>
    /// The running executable. When the app was started through the <c>dotnet</c> host, which cannot be what Windows starts, it is the app's own
    /// executable in the same folder.
    /// </summary>
    public static string Executable() => Choose(Environment.ProcessPath, AppContext.BaseDirectory);

    /// <summary>The choice, from the process's path (which may be missing) and the folder the app's files are in.</summary>
    internal static string Choose(string? processPath, string baseDirectory) =>
        !string.IsNullOrWhiteSpace(processPath)
        && !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? processPath
            : Path.Combine(baseDirectory, ExecutableName);
}
