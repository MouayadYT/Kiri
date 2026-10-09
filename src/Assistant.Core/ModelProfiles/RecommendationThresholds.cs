namespace Assistant.Core.ModelProfiles;

/// <summary>
/// The numbers <see cref="ModelRecommender"/> decides by (PROJECT_SPEC §5.6, step 124). They are few and plain on purpose, so that anyone can see
/// why a PC was given the model it was, and they only recommend: the user's own choice always wins.
/// </summary>
/// <remarks>
/// <para>
/// <b>Profile.</b> The catalog's default profile (the 4B class) is the baseline, which every PC can run. A larger profile (the 8-9B class) is
/// recommended instead only when both hold: the graphics card with the most memory of its own has at least the profile's
/// <see cref="ModelProfile.RecommendedVideoMemoryGiB"/> (the model, its image projector and a window of 8,192 tokens fit in the card, with room
/// for the desktop), and the PC has at least the profile's <see cref="ModelProfile.RecommendedMemoryGiB"/> of memory. Without such a card the
/// larger model would run on the processor, which is too slow to talk with, so the baseline is recommended.
/// </para>
/// <para>
/// <b>Context window.</b> The window the PC's memory allows (<see cref="HardwarePresets"/>: 4,096 tokens below 11 GiB, 8,192 from 11 GiB, 16,384
/// from 22 GiB), held to the model's usual window (8,192) unless the card has at least <see cref="LargeContextVideoMemoryGiB"/> of memory of its own and
/// the model fits in it, in which case the window is the one the memory allows.
/// </para>
/// </remarks>
public static class RecommendationThresholds
{
    /// <summary>
    /// How much less than a threshold a PC may report and still meet it, in GiB. Memory and cards report a little under their marketed sizes
    /// (about 15.7 GiB for 16), as <see cref="HardwarePresets"/> and <see cref="ModelProfileResolver"/> already allow.
    /// </summary>
    public const double ToleranceGiB = 0.5;

    /// <summary>The memory of its own, in GiB, a graphics card needs for a window larger than the model's usual one to be recommended.</summary>
    public const double LargeContextVideoMemoryGiB = 16;
}
