namespace Assistant.Core.ModelProfiles;

/// <summary>The model profiles the Assistant knows.</summary>
public interface IModelProfileCatalog
{
    /// <summary>Every profile, the default first.</summary>
    IReadOnlyList<ModelProfile> Profiles { get; }

    /// <summary>The profile used when the settings choose none.</summary>
    ModelProfile Default { get; }

    /// <summary>The profile with identifier <paramref name="id"/>, or <see langword="null"/> when there is none.</summary>
    ModelProfile? Find(string? id);
}
