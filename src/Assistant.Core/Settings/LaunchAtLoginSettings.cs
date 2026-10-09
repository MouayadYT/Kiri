namespace Assistant.Core.Settings;

/// <summary>Starting the app when the user signs in to Windows.</summary>
public sealed record LaunchAtLoginSettings
{
    /// <summary>Whether the app starts at sign-in. Off by default (PROJECT_SPEC §4.9).</summary>
    public bool Enabled { get; init; }
}
