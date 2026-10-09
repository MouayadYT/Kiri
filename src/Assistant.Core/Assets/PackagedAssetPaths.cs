namespace Assistant.Core.Assets;

/// <summary>Which kind of packaged asset a folder or a group holds.</summary>
public enum AssetKind
{
    /// <summary>Local language-model files (GGUF models and their projectors).</summary>
    Model,

    /// <summary>Text-to-speech engines and their voices.</summary>
    Voice,
}

/// <summary>
/// Where the assets that ship with an installed copy of the Assistant are (PROJECT_SPEC §3.5, step 123): a folder of their own beside the program
/// files, so they can be put there, replaced or removed without rebuilding or reinstalling anything else.
/// </summary>
/// <remarks>
/// <code>
/// &lt;install folder&gt;\assets\models\manifest.json
/// &lt;install folder&gt;\assets\models\&lt;profile id&gt;\model.gguf, mmproj.gguf
/// &lt;install folder&gt;\assets\voices\manifest.json
/// &lt;install folder&gt;\assets\voices\&lt;engine id&gt;\...
/// </code>
/// The folder belongs to the installation, not to the user's data (<see cref="Storage.AppPaths"/>): the models folder in the user's data stays the place
/// for models the user adds themselves, and is used first.
/// </remarks>
public sealed class PackagedAssetPaths
{
    /// <summary>The name of the folder, inside the install folder, that holds every packaged asset.</summary>
    public const string DirectoryName = "assets";

    /// <summary>The name of the manifest in each kind's folder.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>Creates the locations under <paramref name="installDirectory"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="installDirectory"/> is not a fully qualified path.</exception>
    public PackagedAssetPaths(string installDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        if (!Path.IsPathFullyQualified(installDirectory))
        {
            throw new ArgumentException("The install directory must be a fully qualified path.", nameof(installDirectory));
        }

        InstallDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        AssetsDirectory = Path.Combine(InstallDirectory, DirectoryName);
        ModelsDirectory = Path.Combine(AssetsDirectory, "models");
        VoicesDirectory = Path.Combine(AssetsDirectory, "voices");
    }

    /// <summary>The folder the program files are in.</summary>
    public string InstallDirectory { get; }

    /// <summary>The folder that holds every packaged asset.</summary>
    public string AssetsDirectory { get; }

    /// <summary>The folder of the packaged language models, one folder inside it for each model profile.</summary>
    public string ModelsDirectory { get; }

    /// <summary>The folder of the packaged text-to-speech engines, one folder inside it for each engine.</summary>
    public string VoicesDirectory { get; }

    /// <summary>The paths for the copy of the Assistant that is running.</summary>
    public static PackagedAssetPaths ForCurrentProcess() => new(AppContext.BaseDirectory);

    /// <summary>The folder of <paramref name="kind"/>.</summary>
    public string DirectoryOf(AssetKind kind) => kind == AssetKind.Model ? ModelsDirectory : VoicesDirectory;

    /// <summary>The manifest of <paramref name="kind"/>.</summary>
    public string ManifestOf(AssetKind kind) => Path.Combine(DirectoryOf(kind), ManifestFileName);
}
