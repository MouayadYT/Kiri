namespace Assistant.Tools.Mcp;

/// <summary>
/// How connected apps' tools are loaded for a request (PROJECT_SPEC §4.8, step 104). A small local model has a small window, so the tools of
/// installed apps are never all offered: only those that the request seems to be about, and few of them.
/// </summary>
public sealed record McpLoadingOptions
{
    /// <summary>The most apps whose tools are offered for one request.</summary>
    public int MaxIntegrationsPerRequest { get; init; } = 2;

    /// <summary>The most tools of one app that are offered for one request.</summary>
    public int MaxToolsPerIntegration { get; init; } = 5;

    /// <summary>The most tools of all apps that are offered for one request.</summary>
    public int MaxToolsOffered { get; init; } = 8;

    /// <summary>
    /// The most characters of tool descriptions and schemas that are offered for one request, together: what the model reads and so what takes room
    /// in its context. Tools are added by relevance until the next would go over it.
    /// </summary>
    public int MaxDefinitionCharacters { get; init; } = 3000;

    /// <summary>
    /// The most time a request waits for the tools of an app to be loaded (starting its program or reaching it, and listing its tools). A request
    /// that cannot wait goes on without them, and the loading goes on in the background, so the next request finds them.
    /// </summary>
    public TimeSpan PrepareTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>How long a list of an app's tools is used before it is read again.</summary>
    public TimeSpan CatalogLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a connection that is not used is kept before it is closed (and a program started for it ended).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long an app that could not be connected to is left alone before another attempt, so that every request does not wait for it.</summary>
    public TimeSpan FailureBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often idle connections are looked for.</summary>
    public TimeSpan ReapInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long the tools of a local integration that were read before are used without starting its program (step 109). They are read for exactly the
    /// program, arguments and version that were installed, so a new version is never offered the old version's tools; a server that says its list changed
    /// makes them be read again.
    /// </summary>
    public TimeSpan CachedCatalogLifetime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How long a connection that is idle is left before it is asked whether it is still there (step 109).</summary>
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The most time Reconnect waits for a program to start and list its tools (step 109).</summary>
    public TimeSpan ReconnectTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>How long a server is given to answer that it is still there (step 109).</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long what was offered to a conversation can still be called.</summary>
    public TimeSpan OfferLifetime { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long after an app's tool was called the next requests of its conversation are taken to be about the app too, when they name no app themselves:
    /// the answer to "which list?" is "Tasks", which says nothing about Microsoft To Do.
    /// </summary>
    public TimeSpan FollowUpLifetime { get; init; } = TimeSpan.FromMinutes(10);
}
