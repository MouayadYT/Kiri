namespace Assistant.Core.Contracts;

/// <summary>How the settings were found when they were last read from the disk.</summary>
public enum SettingsLoadOutcome
{
    /// <summary>The settings have not been read yet.</summary>
    NotLoaded,

    /// <summary>No settings were saved yet, so the defaults are in use.</summary>
    NoFile,

    /// <summary>The settings were read as they were saved.</summary>
    Loaded,

    /// <summary>The settings were read, but some of them held values that were not allowed and are back to their defaults.</summary>
    Repaired,

    /// <summary>The settings file was damaged, so the last good copy of it was used.</summary>
    RestoredFromBackup,

    /// <summary>The settings file was damaged and there was no good copy, so the defaults are in use.</summary>
    ResetToDefaults,
}

/// <summary>
/// Says whether the settings service had to fall back on defaults, so the Settings window can tell the user that their
/// settings were not read as they left them (PROJECT_SPEC §5.10).
/// </summary>
public interface ISettingsLoadReport
{
    /// <summary>How the settings were found the last time they were read.</summary>
    SettingsLoadOutcome Outcome { get; }
}
