namespace Assistant.Tools.Mcp;

/// <summary>
/// What gives the tools of an installed connected app (step 104): the connection manager. Behind an interface so that what decides whether an
/// installed app can do a thing (step 105) can be tested without a server.
/// </summary>
internal interface IMcpCatalogProvider
{
    /// <summary>
    /// The tools of the integration <paramref name="integrationId"/>, connecting and listing them if they are not at hand; waits at most
    /// <paramref name="budget"/>. <see langword="null"/> when it is not installed or not enabled, may not be connected to now, could not be reached
    /// or was too slow.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<McpToolCatalog?> GetCatalogAsync(string integrationId, TimeSpan budget, CancellationToken cancellationToken);
}
