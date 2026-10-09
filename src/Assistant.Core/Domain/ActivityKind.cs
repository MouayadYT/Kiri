namespace Assistant.Core.Domain;

/// <summary>What the Assistant is busy doing while it shows its transient activity state (the Searching chip).</summary>
public enum ActivityKind
{
    /// <summary>Waiting on the Windows Search index.</summary>
    WindowsSearch,

    /// <summary>Looking through local files in some way other than the Windows Search index.</summary>
    FileSearch,

    /// <summary>Searching the web.</summary>
    WebSearch,

    /// <summary>Waiting on the local model, loading it or generating.</summary>
    Model,

    /// <summary>Running a tool call.</summary>
    Tool,
}
