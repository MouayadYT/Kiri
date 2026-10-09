namespace Assistant.Core.Settings;

/// <summary>
/// What the update check keeps between runs (<see cref="Assistant.Core.Updates.GitHubUpdateChecker"/>): when GitHub was last asked, so that it is asked
/// about once a month at most, and the newer version the user has already been told about and chose to ignore, so that they are not told again until
/// an even newer one is released.
/// </summary>
public sealed record UpdateSettings
{
    /// <summary>When GitHub was last asked for the latest release, or <see langword="null"/> when it never was.</summary>
    public DateTimeOffset? LastCheckedAt { get; init; }

    /// <summary>The release tag the user chose to ignore ("v0.2.0"), or <see langword="null"/>.</summary>
    public string? DismissedVersion { get; init; }
}
