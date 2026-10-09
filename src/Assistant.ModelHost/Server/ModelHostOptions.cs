namespace Assistant.ModelHost.Server;

/// <summary>Where the host listens, whom it serves, and how long it waits for them.</summary>
/// <param name="PipeName">The pipe to create, named by the owner for this launch.</param>
/// <param name="OwnerProcessId">
/// The app process that started the host, or <see langword="null"/> for none (in tests). The host exits when it does.
/// </param>
internal sealed record ModelHostOptions(string PipeName, int? OwnerProcessId)
{
    /// <summary>How long the host waits for its owner to connect before it gives up and exits.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
