namespace Assistant.Core.Settings;

/// <summary>All user settings, loaded and saved as one versioned document. A new instance holds the defaults.</summary>
public sealed record AppSettings
{
    /// <summary>
    /// Schema version this build writes. Increment it with every change that needs a migration; adding a section or a
    /// property that has a default does not need one, since a file without it simply gets the default.
    /// </summary>
    public const int CurrentSchemaVersion = 4;

    /// <summary>Schema version of this document, used to migrate settings saved by older builds.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Appearance and behavior of the app's surfaces.</summary>
    public UiSettings Ui { get; init; } = new();

    /// <summary>Local model selection and inference.</summary>
    public ModelSettings Model { get; init; } = new();

    /// <summary>History and search privacy.</summary>
    public PrivacySettings Privacy { get; init; } = new();

    /// <summary>What the user allows the Assistant to use: files, the screen, selected text and more.</summary>
    public PermissionSettings Permissions { get; init; } = new();

    /// <summary>Global keyboard shortcuts.</summary>
    public HotkeySettings Hotkeys { get; init; } = new();

    /// <summary>How much context one request may include.</summary>
    public ContextLimitSettings ContextLimits { get; init; } = ContextLimitSettings.Roomy;

    /// <summary>Starting the app when the user signs in.</summary>
    public LaunchAtLoginSettings LaunchAtLogin { get; init; } = new();

    /// <summary>File Explorer and browser integrations.</summary>
    public IntegrationSettings Integrations { get; init; } = new();

    /// <summary>Optional hosted web search; disabled until the user opts in.</summary>
    public WebSearchSettings WebSearch { get; init; } = new();

    /// <summary>Spoken answers and the wake word, which the Assistant cannot do yet.</summary>
    public VoiceSettings Voice { get; init; } = new();

    /// <summary>Releasing the AI models while a game runs.</summary>
    public GameModeSettings GameMode { get; init; } = new();

    /// <summary>Chats that are deleted by themselves after a while.</summary>
    public CleanupSettings Cleanup { get; init; } = new();

    /// <summary>What the update check keeps: when it last looked, and the version the user chose to ignore.</summary>
    public UpdateSettings Updates { get; init; } = new();
}
