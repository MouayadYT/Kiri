using Assistant.Core.Contracts;
using Assistant.Core.Settings;
using Assistant.Core.Storage;

namespace Assistant.Core.ModelProfiles;

/// <summary>
/// Resolves model profiles against the models folder: the files come from the profile, the context from the settings or
/// else the profile held to what the hardware preset allows, and the engine options from the preset then the profile
/// (the profile's win).
/// </summary>
/// <remarks>
/// <para>
/// A model is looked for in the user's models folder first, and then in the folder of the models that were packaged with the Assistant
/// (step 123), so a model the user adds themselves is used instead of the packaged one of the same profile.
/// </para>
/// <para>
/// When the settings choose no profile and a <see cref="IModelRecommender"/> is given (step 124), the choice is the recommended profile for this
/// PC, or the nearest installed one when that is not installed: a profile that is a size smaller is preferred to one that is larger. The same goes
/// for the context window, unless the settings or an explicitly chosen hardware preset say otherwise. Whatever the settings choose is used as it is.
/// </para>
/// </remarks>
public sealed class ModelProfileResolver : IModelProfileResolver
{
    // Machines report a little less memory than they are sold with (about 15.7 GiB for 16), so a profile that
    // recommends 16 GiB is not flagged on one.
    private const double MemoryToleranceGiB = 0.5;

    private readonly IModelProfileCatalog _catalog;
    private readonly IHardwareInfoProvider _hardware;
    private readonly AppPaths _paths;
    private readonly Func<string, bool> _fileExists;
    private readonly IModelRecommender? _recommender;
    private readonly string? _packagedModelsDirectory;

    /// <summary>Creates a resolver over <paramref name="catalog"/>, finding models under <paramref name="paths"/>.</summary>
    /// <param name="catalog">The profiles.</param>
    /// <param name="hardware">Tells the memory that picks the hardware preset.</param>
    /// <param name="paths">Where the models folder is, unless the settings name another.</param>
    /// <param name="fileExists">Tells whether a file exists; <see cref="File.Exists(string?)"/> when omitted.</param>
    /// <param name="recommender">
    /// Recommends the profile and context window used when the settings choose none; without it the default profile and the preset's window are.
    /// </param>
    /// <param name="packagedModelsDirectory">
    /// The folder of the models packaged with the Assistant, looked in when a profile's files are not in the models folder; none when omitted.
    /// </param>
    public ModelProfileResolver(
        IModelProfileCatalog catalog,
        IHardwareInfoProvider hardware,
        AppPaths paths,
        Func<string, bool>? fileExists = null,
        IModelRecommender? recommender = null,
        string? packagedModelsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(paths);
        _catalog = catalog;
        _hardware = hardware;
        _paths = paths;
        _fileExists = fileExists ?? File.Exists;
        _recommender = recommender;
        _packagedModelsDirectory = packagedModelsDirectory;
    }

    /// <inheritdoc/>
    public ResolvedModel? Resolve(ModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var automatic = settings.ProfileId is null && _recommender is not null;
        var directory = ModelsDirectory(settings);
        var profile = settings.ProfileId is null
            ? automatic ? ChooseAutomatically(directory) : _catalog.Default
            : _catalog.Find(settings.ProfileId);
        if (profile is null)
        {
            return null;
        }

        var hardware = _hardware.Get();
        var preset = settings.HardwarePresetId is null
            ? HardwarePresets.Select(hardware)
            : HardwarePresets.Find(settings.HardwarePresetId);
        if (preset is null)
        {
            return null;
        }

        var root = RootFor(profile, directory);
        var modelPath = Locate(root, profile.ModelPath);
        var templatePath = profile.ChatTemplatePath is null ? null : Locate(root, profile.ChatTemplatePath);
        var projectorPath = profile.ProjectorPath is null ? null : Locate(root, profile.ProjectorPath);

        // The window the settings give; else, for a profile and a preset nobody chose, the one that suits this PC; else the profile's, held to the preset.
        var window = settings.ContextLength
            ?? (_recommender is not null && settings.HardwarePresetId is null
                ? _recommender.RecommendContext(profile)
                : Math.Min(profile.Context.LoadTokens, preset.MaxContextTokens));
        var files = new ModelFiles(modelPath)
        {
            ModelId = profile.Id,
            ProjectorPath = projectorPath is not null && _fileExists(projectorPath) ? projectorPath : null,
            ChatTemplatePath = templatePath,
            ContextLength = window,
            RuntimeArguments = EngineArguments.Merge(preset.RuntimeArguments, profile.RuntimeArguments),
        };
        var installed = _fileExists(modelPath) && (templatePath is null || _fileExists(templatePath));
        return new ResolvedModel(profile, preset, files, installed)
        {
            BelowRecommendedMemory = hardware.IsMemoryKnown
                && hardware.TotalMemoryGiB + MemoryToleranceGiB < profile.RecommendedMemoryGiB,
            IsPackaged = IsPackagedRoot(root, directory),
            IsAutomatic = automatic,
            Recommendation = automatic ? _recommender!.Recommend() : null,
        };
    }

    // The profile for a PC whose settings choose none: the recommended one if it is installed, else the nearest installed one (smaller ones first,
    // the largest of them, then larger ones, the smallest of them), else the recommended one, which is then reported as not installed.
    private ModelProfile ChooseAutomatically(string directory)
    {
        var recommended = _recommender!.Recommend().Profile;
        var smaller = _catalog.Profiles
            .Where(profile => profile != recommended && profile.ParametersBillions < recommended.ParametersBillions)
            .OrderByDescending(profile => profile.ParametersBillions);
        var larger = _catalog.Profiles
            .Where(profile => profile != recommended && profile.ParametersBillions >= recommended.ParametersBillions)
            .OrderBy(profile => profile.ParametersBillions);
        return new[] { recommended }.Concat(smaller).Concat(larger).FirstOrDefault(profile => IsInstalled(profile, directory)) ?? recommended;
    }

    private bool IsInstalled(ModelProfile profile, string directory)
    {
        var root = RootFor(profile, directory);
        return _fileExists(Locate(root, profile.ModelPath))
            && (profile.ChatTemplatePath is null || _fileExists(Locate(root, profile.ChatTemplatePath)));
    }

    // The folder a profile's relative paths are under: the user's models folder, unless the model is only in the packaged one.
    private string RootFor(ModelProfile profile, string directory) =>
        _packagedModelsDirectory is not null
            && !_fileExists(Locate(directory, profile.ModelPath))
            && _fileExists(Locate(_packagedModelsDirectory, profile.ModelPath))
            ? _packagedModelsDirectory
            : directory;

    private bool IsPackagedRoot(string root, string directory) =>
        _packagedModelsDirectory is not null && !string.Equals(root, directory, StringComparison.OrdinalIgnoreCase)
        && string.Equals(root, _packagedModelsDirectory, StringComparison.OrdinalIgnoreCase);

    // The folder the user picked for models, if it is a full path, else the default.
    private string ModelsDirectory(ModelSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.ModelsDirectory) && Path.IsPathFullyQualified(settings.ModelsDirectory)
            ? settings.ModelsDirectory
            : _paths.ModelsDirectory;

    private static string Locate(string directory, string path) =>
        Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(directory, path));
}
