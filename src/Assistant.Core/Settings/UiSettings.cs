namespace Assistant.Core.Settings;

/// <summary>Appearance and behavior of the app's surfaces.</summary>
public sealed record UiSettings
{
    /// <summary>Whether the user has finished the first-run introduction (PROJECT_SPEC §4.9).</summary>
    public bool FirstRunCompleted { get; init; }

    /// <summary>Local results shown in each group (Apps, Files, Folders) of the Search or Ask bar.</summary>
    public int BarResultsPerGroup { get; init; } = 3;

    /// <summary>Whether the bar opens with the Files scope on, grounding asks in Windows Search results.</summary>
    public bool FilesScopeOnByDefault { get; init; }
}
