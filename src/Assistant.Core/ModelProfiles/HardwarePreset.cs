using Assistant.Core.Contracts;

namespace Assistant.Core.ModelProfiles;

/// <summary>
/// How local models are run on one class of machine (PROJECT_SPEC §5.6, R6): the most context they are given and the
/// engine options that suit it. A preset only ever holds a model back, so a machine is never asked for more than a
/// model's profile would use, and a profile's own engine options win over the preset's.
/// </summary>
/// <param name="Id">A stable identifier, as for <see cref="ModelProfile.Id"/>: what the settings store to choose it.</param>
/// <param name="DisplayName">What the user is shown.</param>
/// <param name="MinimumMemoryGiB">
/// The least physical memory a machine has to be picked automatically for this preset, in GiB. The preset with the
/// highest minimum a machine reaches is picked.
/// </param>
/// <param name="MaxContextTokens">The most context, in tokens, a model is loaded with on this class of machine.</param>
public sealed record HardwarePreset(string Id, string DisplayName, int MinimumMemoryGiB, int MaxContextTokens)
{
    private readonly IReadOnlyList<string> _runtimeArguments = [];

    /// <summary>Engine options for this class of machine, as command-line tokens (<see cref="EngineArguments"/> says which are allowed).</summary>
    public IReadOnlyList<string> RuntimeArguments
    {
        get => _runtimeArguments;
        init => _runtimeArguments = value ?? [];
    }

    /// <summary>Describes the first problem with this preset, or returns <see langword="null"/> when it is usable.</summary>
    public string? Validate()
    {
        if (!ModelProfile.IsValidId(Id) || string.IsNullOrWhiteSpace(DisplayName))
        {
            return "The id must be lower case letters, digits and hyphens, and the display name is required.";
        }

        if (MinimumMemoryGiB < 0 || MaxContextTokens is < ModelFiles.MinContextLength or > ModelFiles.MaxContextLength)
        {
            return "The memory minimum or the context cap is out of range.";
        }

        return EngineArguments.Validate(RuntimeArguments);
    }
}
