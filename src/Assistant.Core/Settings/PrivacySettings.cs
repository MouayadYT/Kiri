namespace Assistant.Core.Settings;

/// <summary>History and search privacy (PROJECT_SPEC §3.5, §4.7).</summary>
public sealed record PrivacySettings
{
    /// <summary>
    /// Local Only mode (PROJECT_SPEC §3.4): nothing leaves this PC. On by default. While it is on, the one thing that sends something
    /// away, searching the web with a picture, is off; turning it off lets that be asked for, one picture at a time.
    /// </summary>
    public bool LocalOnly { get; init; } = true;

    /// <summary>Whether conversations are saved. When off, nothing from a conversation is written to disk.</summary>
    public bool HistoryEnabled { get; init; } = true;

    /// <summary>How long saved conversations are kept.</summary>
    public HistoryRetention HistoryRetention { get; init; } = HistoryRetention.UntilDeleted;

    /// <summary>Folders whose contents never appear in search results or grounded answers.</summary>
    public IReadOnlyList<string> ExcludedFolders { get; init; } = [];
}
