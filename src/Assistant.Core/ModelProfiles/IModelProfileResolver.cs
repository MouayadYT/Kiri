using Assistant.Core.Settings;

namespace Assistant.Core.ModelProfiles;

/// <summary>Turns the settings' choice of model profile into the files and options to load.</summary>
public interface IModelProfileResolver
{
    /// <summary>
    /// Resolves the profile <paramref name="settings"/> choose (the default profile when they choose none) for the
    /// hardware preset they choose (the one that suits this machine when they choose none). Reads no file's contents.
    /// </summary>
    /// <returns>
    /// The resolved model, or <see langword="null"/> when the settings name a profile or preset that does not exist.
    /// A profile whose files are not there is returned with <see cref="ResolvedModel.IsInstalled"/> false.
    /// </returns>
    ResolvedModel? Resolve(ModelSettings settings);
}
