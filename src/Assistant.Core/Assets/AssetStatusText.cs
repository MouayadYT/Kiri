namespace Assistant.Core.Assets;

/// <summary>
/// What to tell the user about a packaged asset: one plain sentence for each state (PROJECT_SPEC §3.5, step 123). It names files only as the
/// manifest does, never by path, so the words are also safe in diagnostics.
/// </summary>
public static class AssetStatusText
{
    private const int MaxNamedFiles = 3;

    /// <summary>Describes <paramref name="state"/> for something called <paramref name="noun"/> ("model", "voice").</summary>
    public static string Describe(AssetGroupState state, string noun)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(noun);
        return state.Status switch
        {
            AssetGroupStatus.NotPackaged or AssetGroupStatus.Missing => "Not installed with this copy of the Assistant.",
            AssetGroupStatus.Incomplete => $"Incomplete: some of its {noun} files are missing {Names(state)}. Reinstall the Assistant to restore them.",
            AssetGroupStatus.Unverified => "Installed. Its files are checked before it is first used.",
            AssetGroupStatus.Checking => "Checking its files…",
            AssetGroupStatus.Verified => "Installed, and its files match what was packaged.",
            AssetGroupStatus.Damaged => $"Its {noun} files do not match what was packaged {Names(state)}. Reinstall the Assistant to restore them.",
            AssetGroupStatus.Unreadable => $"Its {noun} files could not be read to check them {Names(state)}. Close any program that has them open and check again.",
            _ => string.Empty,
        };
    }

    /// <summary>The words that say the files <paramref name="state"/> names are why, such as "(model.gguf, mmproj.gguf)", or nothing when none is named.</summary>
    private static string Names(AssetGroupState state)
    {
        if (state.ProblemFiles.Count == 0)
        {
            return string.Empty;
        }

        var shown = state.ProblemFiles.Take(MaxNamedFiles).ToArray();
        var more = state.ProblemFiles.Count - shown.Length;
        return $"({string.Join(", ", shown)}{(more > 0 ? $" and {more} more" : string.Empty)})";
    }

    /// <summary>What the model host's caller says when a packaged model failed its check (<see cref="AssetIntegrityException"/>).</summary>
    public const string ModelFilesFailedText =
        "The model's files don't match what was packaged with this Assistant, so it wasn't loaded. Reinstall the Assistant, or choose another model in Settings > Model.";
}
