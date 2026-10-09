using System.IO;
using System.Runtime.CompilerServices;

namespace Assistant.SmokeTests.Support;

/// <summary>Where the repository is, for the checks that use its own files (the project's documents, the packaging scripts).</summary>
internal static class Repo
{
    /// <summary>The repository's root folder, found from where this file was compiled (the same way the packaging tests find the scripts).</summary>
    public static string Root { get; } = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))))!;

    public static string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string SourcePath([CallerFilePath] string path = "") => path;
}
