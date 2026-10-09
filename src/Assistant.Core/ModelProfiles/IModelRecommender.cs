using Assistant.Core.Hardware;

namespace Assistant.Core.ModelProfiles;

/// <summary>Recommends a model profile and a context window for a PC (PROJECT_SPEC §5.6, step 124), by fixed, documented thresholds.</summary>
public interface IModelRecommender
{
    /// <summary>The recommendation for this PC, from the hardware profile as it is now.</summary>
    ModelRecommendation Recommend();

    /// <summary>The recommendation for the PC that <paramref name="hardware"/> describes.</summary>
    ModelRecommendation Recommend(HardwareProfile hardware);

    /// <summary>The context window, in tokens, that suits this PC with <paramref name="profile"/>, whichever profile is in use.</summary>
    int RecommendContext(ModelProfile profile);
}
