using System.Globalization;
using Assistant.Core.Settings;

namespace Assistant.Core.ModelProfiles;

/// <summary>How serious a piece of <see cref="ContextAdvice"/> is.</summary>
public enum ContextAdviceLevel
{
    /// <summary>Nothing to say.</summary>
    None,

    /// <summary>Something worth knowing about the value, which is fine as it is.</summary>
    Note,

    /// <summary>The value may use a lot of memory or be slow. The user may still choose it.</summary>
    Warning,

    /// <summary>The value is outside what the app can work with (<see cref="SettingsLimits"/>). It cannot be chosen.</summary>
    Error,
}

/// <summary>What the Settings window tells the user about a context value they entered.</summary>
/// <param name="Level">How serious it is.</param>
/// <param name="Message">One or two sentences for the user, or empty when the level is <see cref="ContextAdviceLevel.None"/>.</param>
public sealed record ContextAdvice(ContextAdviceLevel Level, string Message)
{
    /// <summary>No advice.</summary>
    public static ContextAdvice None { get; } = new(ContextAdviceLevel.None, string.Empty);

    /// <summary>Whether the value may not be chosen.</summary>
    public bool IsError => Level == ContextAdviceLevel.Error;
}

/// <summary>
/// Judges context values the user enters in the Settings window (PROJECT_SPEC §5.6, §5.10). Only a value outside
/// <see cref="SettingsLimits"/> is an error; a value that may need a lot of memory gets a warning that says roughly how
/// much, and stays choosable, because an expert with a large machine, a graphics card or a small model is the best judge.
/// </summary>
/// <remarks>
/// The memory figures are estimates from the model's size class alone: a model's real needs depend on its layers and
/// on how the engine stores its cache, so they say "about" and never decide anything.
/// </remarks>
public static class ContextAdvisor
{
    /// <summary>The size class assumed for a model the user picked by file, which no profile describes.</summary>
    public const double AssumedBillions = 8;

    /// <summary>The share of the machine's memory above which the user is told the model will take a lot of it.</summary>
    public const double LargeShareOfMemory = 0.6;

    /// <summary>The share of the machine's memory above which the user is told the model may not fit.</summary>
    public const double ExceedsMemory = 0.85;

    /// <summary>The size of the context's cache, in bytes, that is worth a warning when the machine's memory is not known.</summary>
    public const long LargeCacheBytes = 4L * GiB;

    /// <summary>The context beyond which most models of the sizes offered were not trained, in tokens.</summary>
    public const int UsualTrainingLimit = 32_768;

    private const long GiB = 1024L * 1024 * 1024;
    private const double WeightBytesPerBillion = 0.6 * GiB;
    private const long ProjectorBytes = (long)(0.8 * GiB);
    private const long EngineOverheadBytes = GiB;

    /// <summary>
    /// About how many bytes the engine's cache of a context of <paramref name="contextTokens"/> tokens takes, for a model
    /// of <paramref name="parametersBillions"/> billion parameters: some 136 KiB a token for a 4B model, growing a little
    /// with size (an uncompressed cache; the real figure depends on the model).
    /// </summary>
    public static long EstimateCacheBytes(double parametersBillions, int contextTokens)
    {
        var perTokenKiB = 128 + (2 * parametersBillions);
        return (long)(perTokenKiB * 1024 * Math.Max(0, contextTokens));
    }

    /// <summary>
    /// About how many bytes of memory running a model with a context of <paramref name="contextTokens"/> tokens takes:
    /// its weights (a 4-bit file), its image projector when it has one, the context's cache and the engine's own buffers.
    /// <paramref name="profile"/> is <see langword="null"/> for a model the user picked by file.
    /// </summary>
    public static long EstimateMemoryBytes(ModelProfile? profile, int contextTokens)
    {
        var billions = profile?.ParametersBillions ?? AssumedBillions;
        var projector = profile is null || profile.SupportsVision ? ProjectorBytes : 0;
        return (long)(billions * WeightBytesPerBillion) + projector + EstimateCacheBytes(billions, contextTokens) + EngineOverheadBytes;
    }

    /// <summary>
    /// About how many bytes of memory running a model whose files are <paramref name="modelFileBytes"/> long takes with a context of
    /// <paramref name="contextTokens"/> tokens: the files as they are (the weights, and the image projector when there is one), the context's cache
    /// for a model of <paramref name="parametersBillions"/> billion parameters, and the engine's own buffers. For a model whose files are known,
    /// such as one of the downloads the setup offers.
    /// </summary>
    public static long EstimateMemoryBytes(long modelFileBytes, double parametersBillions, int contextTokens) =>
        Math.Max(0, modelFileBytes) + EstimateCacheBytes(parametersBillions > 0 ? parametersBillions : AssumedBillions, contextTokens) + EngineOverheadBytes;

    /// <summary>
    /// About how many billion parameters a model whose weights file is <paramref name="modelFileBytes"/> long has, taking it for a 4-bit file as
    /// <see cref="EstimateMemoryBytes(ModelProfile?, int)"/> does: what a model file the user picked, which no profile describes, is sized by. Never less
    /// than one, and <see cref="AssumedBillions"/> for a file whose size is not known.
    /// </summary>
    public static double EstimateBillions(long modelFileBytes) =>
        modelFileBytes > 0 ? Math.Clamp(modelFileBytes / WeightBytesPerBillion, 1, 200) : AssumedBillions;

    /// <summary>
    /// Judges a context window to load the model with, in tokens: the value the user types in the advanced field.
    /// </summary>
    /// <param name="contextTokens">The window.</param>
    /// <param name="profile">The model profile that will be loaded, or <see langword="null"/> for a model picked by file.</param>
    /// <param name="hardware">What the machine offers.</param>
    /// <param name="preset">The hardware preset that will run the model, or <see langword="null"/> when there is none to compare with.</param>
    public static ContextAdvice AssessWindow(int contextTokens, ModelProfile? profile, HardwareInfo hardware, HardwarePreset? preset)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        if (contextTokens is < SettingsLimits.MinContextTokens or > SettingsLimits.MaxContextTokens)
        {
            return new ContextAdvice(
                ContextAdviceLevel.Error, $"Enter a number from {Tokens(SettingsLimits.MinContextTokens)} to {Tokens(SettingsLimits.MaxContextTokens)}.");
        }

        var billions = profile?.ParametersBillions ?? AssumedBillions;
        var cache = EstimateCacheBytes(billions, contextTokens);
        var total = EstimateMemoryBytes(profile, contextTokens);
        var memory = hardware.TotalMemoryBytes;
        var about = profile is null ? " (for a model of about 8 billion parameters)" : string.Empty;

        if (hardware.IsMemoryKnown && total > ExceedsMemory * memory)
        {
            return new ContextAdvice(
                ContextAdviceLevel.Warning,
                $"This may need about {Size(total)}{about}, and this PC has {Size(memory)}. The model may fail to load or make the PC very slow. You can still use it.");
        }

        if (hardware.IsMemoryKnown && total > LargeShareOfMemory * memory)
        {
            return new ContextAdvice(
                ContextAdviceLevel.Warning,
                $"This may need about {Size(total)}{about} of this PC's {Size(memory)}, so other apps may slow down while the model is loaded.");
        }

        if (!hardware.IsMemoryKnown && cache >= LargeCacheBytes)
        {
            return new ContextAdvice(
                ContextAdviceLevel.Warning,
                $"The context alone may need about {Size(cache)} of memory{about}, and this PC's memory is not known. You can still use it.");
        }

        if (preset is not null && contextTokens > preset.MaxContextTokens)
        {
            return new ContextAdvice(
                ContextAdviceLevel.Note,
                $"That is more than the {Tokens(preset.MaxContextTokens)} tokens the {PresetName(preset)} preset gives; it is used as you enter it and may need about {Size(total)} in all{about}.");
        }

        if (contextTokens > UsualTrainingLimit)
        {
            return new ContextAdvice(
                ContextAdviceLevel.Note,
                $"Most models of this size were trained for up to {Tokens(UsualTrainingLimit)} tokens or a few times that; past a model's own limit its answers get worse.");
        }

        return contextTokens < 1024
            ? new ContextAdvice(ContextAdviceLevel.Note, "A window this small forgets earlier messages almost at once.")
            : ContextAdvice.None;
    }

    /// <summary>
    /// Judges a limit on how much of the window a conversation may use, in tokens, or zero for no limit.
    /// </summary>
    /// <param name="limitTokens">The limit.</param>
    /// <param name="windowTokens">The window the model is loaded with, which is the most any limit can use.</param>
    public static ContextAdvice AssessLimit(int limitTokens, int windowTokens)
    {
        if (limitTokens == 0)
        {
            return new ContextAdvice(ContextAdviceLevel.Note, "No limit: a conversation may use the model's whole context window.");
        }

        if (limitTokens is < SettingsLimits.MinContextTokens or > SettingsLimits.MaxContextTokens)
        {
            return new ContextAdvice(
                ContextAdviceLevel.Error,
                $"Enter 0 for no limit, or a number from {Tokens(SettingsLimits.MinContextTokens)} to {Tokens(SettingsLimits.MaxContextTokens)}.");
        }

        return windowTokens > 0 && limitTokens > windowTokens
            ? new ContextAdvice(
                ContextAdviceLevel.Note,
                $"The model's context window is {Tokens(windowTokens)} tokens, so this is held to that. Raise the window under Advanced to use more.")
            : ContextAdvice.None;
    }

    /// <summary>
    /// Judges the heavy limit, the one for conversations that carry attached or retrieved context, which should not be
    /// smaller than the normal one.
    /// </summary>
    public static ContextAdvice AssessHeavyLimit(int normalTokens, int heavyTokens, int windowTokens)
    {
        var advice = AssessLimit(heavyTokens, windowTokens);
        if (advice.Level is ContextAdviceLevel.Error)
        {
            return advice;
        }

        return normalTokens > 0 && heavyTokens > 0 && heavyTokens < normalTokens
            ? new ContextAdvice(
                ContextAdviceLevel.Warning,
                "This is smaller than the normal limit, so a conversation with attached files would get less room than an ordinary one.")
            : advice;
    }

    private static string Tokens(int tokens) => tokens.ToString("N0", CultureInfo.CurrentCulture);

    private static string Size(long bytes) => (bytes / (double)GiB).ToString("F1", CultureInfo.CurrentCulture) + " GB";

    // "Compact (8 GB of memory)" -> "Compact".
    private static string PresetName(HardwarePreset preset)
    {
        var name = preset.DisplayName;
        var open = name.IndexOf(" (", StringComparison.Ordinal);
        return open > 0 ? name[..open] : name;
    }
}
