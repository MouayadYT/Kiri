using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Assistant.Tools.Integrations;

/// <summary>The runtimes the Assistant can set up for an integration by itself.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeKind>))]
public enum RuntimeKind
{
    /// <summary>Node.js, for npm packages and bundles that run on it.</summary>
    NodeJs = 1,

    /// <summary>Python, for PyPI packages.</summary>
    Python = 2,
}

/// <summary>How a runtime's download is packed.</summary>
public enum ArchiveKind
{
    /// <summary>A zip file.</summary>
    Zip = 0,

    /// <summary>A gzip-compressed tar file.</summary>
    TarGz = 1,
}

/// <summary>
/// One runtime the Assistant knows how to set up (PROJECT_SPEC §4.8, step 108): exactly which file to download, the hash it must have, and where
/// the program is inside it. All of this is fixed in the Assistant's code, so nothing a candidate, a registry or the web says can change which
/// runtime is downloaded or from where.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="Version">Its version, such as <c>24.21.0</c>.</param>
/// <param name="DisplayName">What the user is told it is called.</param>
/// <param name="DownloadUrl">Where it is downloaded from (<c>https</c>).</param>
/// <param name="Hash">What the download must hash to (published by its maker next to the file).</param>
/// <param name="SizeBytes">How large the download is.</param>
/// <param name="Archive">How it is packed.</param>
/// <param name="TopFolder">The one folder everything in the archive is inside, which is dropped when it is unpacked; empty when there is none.</param>
/// <param name="Executable">The program's path inside the unpacked runtime.</param>
public sealed record RuntimeRelease(
    RuntimeKind Kind,
    string Version,
    string DisplayName,
    Uri DownloadUrl,
    ContentHash Hash,
    long SizeBytes,
    ArchiveKind Archive,
    string TopFolder,
    string Executable)
{
    /// <summary>About how many megabytes the download is, for telling the user.</summary>
    public int ApproximateMegabytes => (int)Math.Max(1, Math.Round(SizeBytes / 1_048_576.0));
}

/// <summary>The runtimes the Assistant sets up, one of each kind for the processor of this PC.</summary>
public static class RuntimeCatalog
{
    // Node.js 24 "Krypton" (long-term support). Its hash is the one in nodejs.org's SHASUMS256.txt for this release.
    private static readonly RuntimeRelease NodeWindowsX64 = new(
        RuntimeKind.NodeJs,
        "24.21.0",
        "Node.js 24",
        new Uri("https://nodejs.org/dist/v24.21.0/node-v24.21.0-win-x64.zip"),
        new ContentHash("sha256", "158f7685b44de51f6c0df1d153526cbcd3e1bc739a8dfc607721cef75de9e541"),
        37_618_919,
        ArchiveKind.Zip,
        "node-v24.21.0-win-x64",
        "node.exe");

    // CPython 3.12 as built by the python-build-standalone project (it includes pip). Its hash is the one in the release's SHA256SUMS.
    private static readonly RuntimeRelease PythonWindowsX64 = new(
        RuntimeKind.Python,
        "3.12.15",
        "Python 3.12",
        new Uri("https://github.com/astral-sh/python-build-standalone/releases/download/20261001/cpython-3.12.15%2B20261001-x86_64-pc-windows-msvc-install_only_stripped.tar.gz"),
        new ContentHash("sha256", "52124cee54126f3f360eaa378288f6f64c402c983a3c14c95eff67f4af986aaa"),
        22_013_771,
        ArchiveKind.TarGz,
        "python",
        "python.exe");

    /// <summary>The release of <paramref name="kind"/> for this PC's processor, or <see langword="null"/> when there is none for it.</summary>
    public static RuntimeRelease? For(RuntimeKind kind) => For(kind, RuntimeInformation.OSArchitecture);

    /// <summary>The release of <paramref name="kind"/> for <paramref name="architecture"/>, or <see langword="null"/> when there is none.</summary>
    public static RuntimeRelease? For(RuntimeKind kind, Architecture architecture)
    {
        if (architecture != Architecture.X64)
        {
            return null;
        }

        return kind switch
        {
            RuntimeKind.NodeJs => NodeWindowsX64,
            RuntimeKind.Python => PythonWindowsX64,
            _ => null,
        };
    }

    /// <summary>The kind of runtime a candidate's requirement means, or <see langword="null"/> when it needs none or one the Assistant does not set up.</summary>
    public static RuntimeKind? KindOf(CandidateRuntime runtime) => runtime switch
    {
        CandidateRuntime.NodeJs => RuntimeKind.NodeJs,
        CandidateRuntime.Python => RuntimeKind.Python,
        _ => null,
    };
}
