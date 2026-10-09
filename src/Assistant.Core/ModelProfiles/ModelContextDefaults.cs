using Assistant.Core.Contracts;
using Assistant.Core.Settings;

namespace Assistant.Core.ModelProfiles;

/// <summary>
/// The context a model is given by default (PROJECT_SPEC §5.5, §5.6): the window it is loaded with, and how much of it
/// a conversation may use.
/// </summary>
/// <param name="LoadTokens">
/// The context window to load the model with, in tokens, which uses memory in proportion to its size. A hardware preset
/// may give the model less (<see cref="HardwarePreset.MaxContextTokens"/>).
/// </param>
public sealed record ModelContextDefaults(int LoadTokens)
{
    /// <summary>
    /// The most tokens of the window (prompt and answer together) an ordinary conversation should use; see
    /// <see cref="ContextLimitSettings.NormalContextTokens"/>.
    /// </summary>
    public int NormalTokens { get; init; } = 4096;

    /// <summary>
    /// The most tokens of the window (prompt and answer together) a conversation with attached or retrieved context
    /// should use; see <see cref="ContextLimitSettings.HeavyContextTokens"/>.
    /// </summary>
    public int HeavyTokens { get; init; } = 8192;

    /// <summary>The tokens reserved for the answer; see <see cref="ContextLimitSettings.ReservedOutputTokens"/>.</summary>
    public int ReservedOutputTokens { get; init; } = 1024;

    /// <summary>
    /// Describes the first problem with these defaults, or returns <see langword="null"/> when they are usable. The
    /// limits are what the budgeter reads, so they may not exceed the window.
    /// </summary>
    public string? Validate()
    {
        if (LoadTokens is < ModelFiles.MinContextLength or > ModelFiles.MaxContextLength)
        {
            return "The context window to load is out of range.";
        }

        if (NormalTokens < 1 || HeavyTokens < NormalTokens || HeavyTokens > LoadTokens)
        {
            return "The normal and heavy context limits must be in order and fit the window.";
        }

        return ReservedOutputTokens < 1 || ReservedOutputTokens > NormalTokens / 2
            ? "The tokens reserved for the answer must be positive and at most half the normal limit."
            : null;
    }

    /// <summary>
    /// The context limits these defaults give: <paramref name="other"/> with the normal and heavy limits and the answer's
    /// reserve replaced, each held to <paramref name="windowTokens"/>, the window the model was actually loaded with.
    /// </summary>
    public ContextLimitSettings ApplyTo(ContextLimitSettings other, int windowTokens)
    {
        ArgumentNullException.ThrowIfNull(other);
        var normal = Math.Min(NormalTokens, windowTokens);
        return other with
        {
            NormalContextTokens = normal,
            HeavyContextTokens = Math.Min(HeavyTokens, windowTokens),
            ReservedOutputTokens = Math.Min(ReservedOutputTokens, Math.Max(1, normal / 2)),
        };
    }
}
