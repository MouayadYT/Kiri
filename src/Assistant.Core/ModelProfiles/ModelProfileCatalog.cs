namespace Assistant.Core.ModelProfiles;

/// <summary>
/// The catalog of model profiles: the two the Assistant ships with, and any others its owner adds. Every profile is
/// checked when the catalog is made, so a bad one fails at start rather than on the user's first question.
/// </summary>
/// <remarks>
/// The built-in profiles are classes of model, not particular models: each names a folder under the models folder for
/// its files, so whichever model of that size the user (or a later install step) puts there is the one loaded. Nothing
/// else in the app refers to a model by name.
/// </remarks>
public sealed class ModelProfileCatalog : IModelProfileCatalog
{
    /// <summary>The identifier of <see cref="Standard"/>, the default profile.</summary>
    public const string DefaultProfileId = "chat-4b";

    /// <summary>The identifier of <see cref="Large"/>.</summary>
    public const string LargeProfileId = "chat-9b";

    /// <summary>
    /// The default profile, a 4B-class model that reads text and images: small enough for the minimum hardware
    /// (PROJECT_SPEC §2), quick on the CPU alone.
    /// </summary>
    public static ModelProfile Standard { get; } = new(
        DefaultProfileId,
        "Standard (4B class)",
        Path.Combine(DefaultProfileId, "model.gguf"),
        "Q4_K_M",
        new ModelContextDefaults(32768) { NormalTokens = 32768, HeavyTokens = 32768, ReservedOutputTokens = 4096 })
    {
        ParametersBillions = 4,
        RecommendedMemoryGiB = 8,
        RecommendedVideoMemoryGiB = 6,
        SupportsVision = true,
        ProjectorPath = Path.Combine(DefaultProfileId, "mmproj.gguf"),
    };

    /// <summary>
    /// The optional larger profile, an 8B to 9B-class model that reads text and images: better answers for a machine with
    /// 16 GB or a graphics card, slower on the CPU alone.
    /// </summary>
    public static ModelProfile Large { get; } = new(
        LargeProfileId,
        "Large (8-9B class)",
        Path.Combine(LargeProfileId, "model.gguf"),
        "Q4_K_M",
        new ModelContextDefaults(32768) { NormalTokens = 32768, HeavyTokens = 32768, ReservedOutputTokens = 4096 })
    {
        ParametersBillions = 9,
        RecommendedMemoryGiB = 16,
        RecommendedVideoMemoryGiB = 10,
        SupportsVision = true,
        ProjectorPath = Path.Combine(LargeProfileId, "mmproj.gguf"),
    };

    private readonly ModelProfile[] _profiles;

    /// <summary>Creates the catalog of the built-in profiles.</summary>
    public ModelProfileCatalog()
        : this([])
    {
    }

    /// <summary>
    /// Creates the catalog of the built-in profiles and <paramref name="additional"/> ones. A profile with the id of a
    /// built-in one replaces it, keeping its place; the default stays the default.
    /// </summary>
    /// <exception cref="ArgumentException">A profile is not valid (<see cref="ModelProfile.Validate"/>) or two share an id.</exception>
    public ModelProfileCatalog(IEnumerable<ModelProfile> additional)
    {
        ArgumentNullException.ThrowIfNull(additional);
        var profiles = new List<ModelProfile> { Standard, Large };
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in additional)
        {
            if (profile is null || profile.Validate() is not null)
            {
                throw new ArgumentException("A model profile is not valid.", nameof(additional));
            }

            if (!added.Add(profile.Id))
            {
                throw new ArgumentException("Two model profiles have the same id.", nameof(additional));
            }

            var existing = profiles.FindIndex(known => known.Id == profile.Id);
            if (existing >= 0)
            {
                profiles[existing] = profile;
            }
            else
            {
                profiles.Add(profile);
            }
        }

        _profiles = [.. profiles];
    }

    /// <inheritdoc/>
    public IReadOnlyList<ModelProfile> Profiles => _profiles;

    /// <inheritdoc/>
    public ModelProfile Default => _profiles[0];

    /// <inheritdoc/>
    public ModelProfile? Find(string? id) =>
        Array.Find(_profiles, profile => string.Equals(profile.Id, id, StringComparison.Ordinal));
}
