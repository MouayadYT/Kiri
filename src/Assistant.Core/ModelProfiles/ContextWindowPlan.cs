using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Assistant.Core.Tools;

namespace Assistant.Core.ModelProfiles;

/// <summary>
/// Which context window the model is loaded with (PROJECT_SPEC §5.5, §5.6). A window costs memory in proportion to its size for as long as the model is
/// loaded, whether a conversation fills it or not, so the model is not kept loaded with the largest one: it has the ordinary conversations' window
/// (<see cref="ContextLimitSettings.NormalContextTokens"/>, 8,000 tokens by default), and is loaded again with the larger one
/// (<see cref="ContextLimitSettings.HeavyContextTokens"/>, 32,000 by default) while the conversation being answered carries files, and with the ordinary
/// one again once a conversation without them is answered. A window the user set themselves (<see cref="ModelSettings.ContextLength"/>) is used for
/// everything, as it is.
/// </summary>
public static class ContextWindowPlan
{
    /// <summary>The window to load the model with.</summary>
    /// <param name="pinned">The window the user set, or <see langword="null"/> when it follows the conversation.</param>
    /// <param name="limits">The user's limits, which are the two windows.</param>
    /// <param name="documents">Whether the conversation being answered carries files.</param>
    /// <param name="fallback">The window used when the limit that applies is "no limit": the one that suits this PC, or <see langword="null"/> for the default.</param>
    /// <param name="profile">The model's profile, for what a window costs; <see langword="null"/> for a model file the user picked.</param>
    /// <param name="hardware">What the PC has, or <see langword="null"/> when it is not known: a files window the PC's memory would not hold is made smaller.</param>
    /// <param name="ceiling">
    /// The most context the hardware preset the user chose themselves allows (<see cref="HardwarePreset.MaxContextTokens"/>), which holds both windows; or
    /// <see langword="null"/> when no preset was chosen.
    /// </param>
    /// <param name="modelFileBytes">
    /// For a model file the user picked, which has no profile: how long its files are (the weights, and the image projector when there is one), so that
    /// what a window costs is worked out from the model itself and not from a model of an assumed size. <see langword="null"/> when it is not known.
    /// </param>
    public static int Window(
        int? pinned, ContextLimitSettings limits, bool documents, int? fallback = null, ModelProfile? profile = null, HardwareInfo? hardware = null,
        int? ceiling = null, long? modelFileBytes = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (pinned is { } set)
        {
            return set;
        }

        if (ceiling is { } most)
        {
            // A preset is the user's own word on what the PC should be asked for, so neither window goes past it.
            return Math.Min(Window(null, limits, documents, fallback, profile, hardware, ceiling: null, modelFileBytes), Hold(most));
        }

        var normal = limits.NormalContextTokens > 0 ? limits.NormalContextTokens : fallback ?? ModelFiles.DefaultContextLength;
        if (!documents)
        {
            return Hold(normal);
        }

        // Never less than the ordinary window: a conversation with files is not given less room than one without.
        var wanted = Math.Max(limits.HeavyContextTokens > 0 ? limits.HeavyContextTokens : fallback ?? normal, normal);
        if (hardware is { IsMemoryKnown: true })
        {
            wanted = Math.Max(normal, Math.Min(wanted, LargestThatFits(profile, hardware, modelFileBytes)));
        }

        return Hold(wanted);
    }

    /// <summary>
    /// The largest window whose estimated memory (<see cref="ContextAdvisor.EstimateMemoryBytes"/>) stays under the share of the PC's memory beyond which a
    /// model may not load (<see cref="ContextAdvisor.ExceedsMemory"/>). It is an estimate, and only ever makes the files window smaller.
    /// </summary>
    public static int LargestThatFits(ModelProfile? profile, HardwareInfo hardware, long? modelFileBytes = null)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        var room = (ContextAdvisor.ExceedsMemory * hardware.TotalMemoryBytes) - EstimateMemoryBytes(profile, modelFileBytes, 0);
        var perToken = EstimateMemoryBytes(profile, modelFileBytes, 1) - EstimateMemoryBytes(profile, modelFileBytes, 0);
        return room <= 0 || perToken <= 0 ? ModelFiles.MinContextLength : (int)Math.Min(ModelFiles.MaxContextLength, room / perToken);
    }

    /// <summary>
    /// About how many bytes of memory the model takes with a window of <paramref name="contextTokens"/> tokens: by its profile, or, for a model file the
    /// user picked whose size is known, by its files as they are; and by a model of an assumed size when neither is known.
    /// </summary>
    public static long EstimateMemoryBytes(ModelProfile? profile, long? modelFileBytes, int contextTokens) =>
        profile is null && modelFileBytes is > 0
            ? ContextAdvisor.EstimateMemoryBytes(modelFileBytes.Value, ContextAdvisor.EstimateBillions(modelFileBytes.Value), contextTokens)
            : ContextAdvisor.EstimateMemoryBytes(profile, contextTokens);

    /// <summary>How long the files at <paramref name="paths"/> are together; <see langword="null"/> when none of them is there to measure.</summary>
    public static long? FileBytes(params string?[] paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && new FileInfo(path) is { Exists: true } file)
                {
                    total += file.Length;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                // A file that cannot be looked at is not counted.
            }
        }

        return total > 0 ? total : null;
    }

    /// <summary>
    /// Whether a conversation carries files, so that it is given the larger window: a file is attached to one of its messages or to the question being asked
    /// (<paramref name="arriving"/>), its answers are grounded in files that were searched for, or the model looked for or read a file in it. Text selected
    /// on the screen, a picture or a web page is not a file.
    /// </summary>
    public static bool CarriesDocuments(IReadOnlyList<Message> conversation, IReadOnlyList<ContextItem>? arriving = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (arriving?.Any(IsDocument) == true)
        {
            return true;
        }

        foreach (var message in conversation)
        {
            if (message.ContextItems.Any(IsDocument) || message.ToolCalls.Any(call => IsFileTool(call.ToolName))
                || (message.Role == MessageRole.Tool && IsFileTool(message.ToolResult?.ToolName)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="item"/> is a file, notes on one, or the files a search grounds an answer in.</summary>
    public static bool IsDocument(ContextItem item) =>
        item is { Type: ContextItemType.File or ContextItemType.FileNotes or ContextItemType.SearchResults };

    /// <summary>Whether <paramref name="toolName"/> is a tool that looks for files or reads one.</summary>
    public static bool IsFileTool(string? toolName) => toolName is FileToolResults.SearchFiles or FileToolResults.ReadFileText;

    private static int Hold(int tokens) => Math.Clamp(tokens, ModelFiles.MinContextLength, ModelFiles.MaxContextLength);
}
