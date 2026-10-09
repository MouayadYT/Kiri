namespace Assistant.Core.Settings;

/// <summary>
/// What the settings may hold (PROJECT_SPEC §5.10). A value outside these ranges is not something the app can work
/// with at all, so it is refused when settings are saved and replaced by the default when a settings file holds it. A
/// value inside them may still be unwise (a context window larger than the memory can hold); that is the user's to
/// choose, and the Settings window warns about it (<c>ContextAdvisor</c>) without preventing it.
/// </summary>
public static class SettingsLimits
{
    /// <summary>The fewest local results shown in each group of the Search or Ask bar.</summary>
    public const int MinBarResultsPerGroup = 1;

    /// <summary>The most local results shown in each group of the Search or Ask bar.</summary>
    public const int MaxBarResultsPerGroup = 10;

    /// <summary>The fewest files one request may have attached.</summary>
    public const int MinAttachedFiles = 1;

    /// <summary>The most files one request may have attached.</summary>
    public const int MaxAttachedFiles = 100;

    /// <summary>The fewest files retrieved from Windows Search to ground one answer.</summary>
    public const int MinRetrievedFiles = 1;

    /// <summary>The most files retrieved from Windows Search to ground one answer.</summary>
    public const int MaxRetrievedFiles = 50;

    /// <summary>The smallest largest-file size, in bytes.</summary>
    public const long MinFileSizeBytes = 1024L * 1024;

    /// <summary>The largest largest-file size, in bytes.</summary>
    public const long MaxFileSizeBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// The smallest context window or limit, in tokens, other than zero (which leaves a limit to the model). It is the
    /// engine's own smallest (<c>ModelFiles.MinContextLength</c>), written out so that the settings depend on nothing
    /// else in Core; a test keeps the two equal.
    /// </summary>
    public const int MinContextTokens = 256;

    /// <summary>The largest context window or limit, in tokens (<c>ModelFiles.MaxContextLength</c>; a test keeps them equal).</summary>
    public const int MaxContextTokens = 1_048_576;

    /// <summary>The fewest tokens reserved for an answer, other than zero (which means the default).</summary>
    public const int MinReservedOutputTokens = 32;

    /// <summary>The most tokens reserved for an answer.</summary>
    public const int MaxReservedOutputTokens = 65_536;

    /// <summary>The shortest time a loaded model may stay idle before it is unloaded.</summary>
    public static readonly TimeSpan MinIdleUnloadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest time a loaded model may stay idle before it is unloaded.</summary>
    public static readonly TimeSpan MaxIdleUnloadTimeout = TimeSpan.FromHours(24);

    /// <summary>The most folders that may be excluded from search.</summary>
    public const int MaxExcludedFolders = 200;

    /// <summary>The fewest hours a chat from the bar is kept before Cleanup deletes it.</summary>
    public const int MinCleanupHours = 1;

    /// <summary>The most hours a chat from the bar is kept before Cleanup deletes it: a year.</summary>
    public const int MaxCleanupHours = 8760;

    /// <summary>The fewest days a chat from the full window is kept before Cleanup deletes it.</summary>
    public const int MinCleanupDays = 1;

    /// <summary>The most days a chat from the full window is kept before Cleanup deletes it: ten years.</summary>
    public const int MaxCleanupDays = 3650;

    /// <summary>The longest path a setting may hold, in characters (Windows' own limit for a path is 32,767).</summary>
    public const int MaxPathLength = 32_000;

    /// <summary>
    /// Whether <paramref name="id"/> can be the identifier of a model profile or hardware preset: lower case letters,
    /// digits and hyphens, not starting or ending with a hyphen (the rule of <c>ModelProfile.IsValidId</c>, written out
    /// here so that the settings depend on nothing else in Core; a test keeps the two the same).
    /// </summary>
    public static bool IsValidIdentifier(string? id) =>
        !string.IsNullOrEmpty(id) && id.All(letter => letter is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
        && id[0] != '-' && id[^1] != '-';

    /// <summary>
    /// Whether <paramref name="path"/> can be a file or folder setting: fully qualified, no longer than
    /// <see cref="MaxPathLength"/>, without characters Windows forbids in a path. It need not exist.
    /// </summary>
    public static bool IsValidPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.Length <= MaxPathLength
        && path.IndexOfAny(Path.GetInvalidPathChars()) < 0
        && Path.IsPathFullyQualified(path);

    /// <summary>
    /// Whether <paramref name="hotkey"/> can be registered as a global shortcut: a key <see cref="HotkeyNames"/> knows
    /// and at least one of Alt, Ctrl and Windows (Shift alone would take a capital letter from every app), and no
    /// modifier this build does not know.
    /// </summary>
    public static bool IsValidHotkey(Hotkey? hotkey)
    {
        const HotkeyModifiers Known = HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Windows;
        const HotkeyModifiers Required = HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Windows;
        return hotkey is not null
            && (hotkey.Modifiers & ~Known) == 0
            && (hotkey.Modifiers & Required) != 0
            && HotkeyNames.IsValid(hotkey.Key);
    }
}
