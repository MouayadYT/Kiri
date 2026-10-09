using Assistant.Core.Contracts;

namespace Assistant.Core.ModelHosting;

/// <summary>Answers a <see cref="HealthRequest"/>: the host is up, and this is its state.</summary>
/// <param name="HostVersion">The host executable's version.</param>
/// <param name="ProcessId">The host's process id, which lets the app check it reached the process it started.</param>
/// <param name="Uptime">How long the host has been running.</param>
public sealed record HealthReport(Version HostVersion, int ProcessId, TimeSpan Uptime) : ModelHostReply
{
    /// <summary>The host's working set, in bytes.</summary>
    public long WorkingSetBytes { get; init; }

    /// <summary>The loaded model's identifier, or <see langword="null"/> when no model is loaded.</summary>
    public string? LoadedModelId { get; init; }

    /// <summary>The model's status, or <see langword="null"/> from a host that does not say.</summary>
    public ModelStatus? ModelStatus { get; init; }

    /// <summary>
    /// Whether the bundled inference runtime can run, or <see langword="null"/> from a host that does not say.
    /// </summary>
    public ModelRuntimeState? Runtime { get; init; }

    internal override bool IsWellFormed() => HostVersion is not null && ProcessId > 0;
}
