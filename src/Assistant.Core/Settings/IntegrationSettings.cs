namespace Assistant.Core.Settings;

/// <summary>Per-user integrations with other apps (PROJECT_SPEC §4.4, §4.5). All are off until the user turns them on.</summary>
public sealed record IntegrationSettings
{
    /// <summary>Whether the File Explorer context-menu entry is registered.</summary>
    public bool ExplorerContextMenuEnabled { get; init; }

    /// <summary>Whether the native-messaging host for the Edge and Chrome extension is registered.</summary>
    public bool BrowserBridgeEnabled { get; init; }

    /// <summary>
    /// Whether the Assistant may look, at most once a day and only while Settings > Integrations is open, for newer versions of the connected apps it
    /// installed (PROJECT_SPEC §4.8, step 109). It sends only the names of the packages, needs Local Only mode off and the External Web and Image Search
    /// permission on, and never installs anything: an update is only offered, and needs the user's approval.
    /// </summary>
    public bool CheckForIntegrationUpdates { get; init; }

    /// <summary>
    /// The application (client) ID the Assistant signs in to Microsoft with, for Microsoft To Do, when the user registered an app of their own in Microsoft Entra; <see langword="null"/>
    /// for the default one. It is an identifier and not a secret.
    /// </summary>
    public string? MicrosoftClientId { get; init; }
}
