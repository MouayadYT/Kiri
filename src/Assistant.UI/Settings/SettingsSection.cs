namespace Assistant.UI.Settings;

/// <summary>The sections of the Settings window. The order its sidebar lists them in is the order of <c>SettingsViewModel</c>'s pages, not of these values.</summary>
public enum SettingsSection
{
    /// <summary>Starting the Assistant and the Search or Ask bar.</summary>
    General,

    /// <summary>The local model: which one, where it is, its status and how much context it is given.</summary>
    Model,

    /// <summary>How much a request may include.</summary>
    Context,

    /// <summary>History and what is kept out of search.</summary>
    Privacy,

    /// <summary>Chats that are deleted by themselves after a while.</summary>
    Cleanup,

    /// <summary>What the Assistant may use: files, the screen, selected text and more.</summary>
    Permissions,

    /// <summary>The people the user tells the Assistant about, for "message my brother".</summary>
    People,

    /// <summary>What the Assistant remembers for the user between conversations.</summary>
    Memory,

    /// <summary>Global keyboard shortcuts.</summary>
    Hotkeys,

    /// <summary>Spoken answers and the wake word (planned).</summary>
    Voice,

    /// <summary>Speech recognition models, devices and the optional wake word.</summary>
    Asr,

    /// <summary>File Explorer and the browser.</summary>
    Integrations,

    /// <summary>What the Assistant did on the user's behalf: its multi-step tasks, and the integrations it looked for, offered, installed and removed.</summary>
    Activity,

    /// <summary>The Assistant itself.</summary>
    About,
}

/// <summary>An entry of the Settings window's sidebar.</summary>
/// <param name="Section">The section it opens.</param>
/// <param name="Title">What the sidebar shows.</param>
public sealed record SettingsSectionItem(SettingsSection Section, string Title);
