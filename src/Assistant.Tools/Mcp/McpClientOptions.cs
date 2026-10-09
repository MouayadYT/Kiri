namespace Assistant.Tools.Mcp;

/// <summary>How long and how much a client waits for and accepts. The defaults suit a person waiting for an answer.</summary>
public sealed record McpClientOptions
{
    /// <summary>The most time <see cref="IMcpClient.ConnectAsync"/> takes, from starting the program or reaching the server to being ready.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a program started by the Assistant has to answer the first question of the modern protocol (<c>server/discover</c>) before it is
    /// taken to speak the earlier protocol, which many servers do by saying nothing at all.
    /// </summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The most time a request other than a tool call takes (listing tools, a ping).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The most time a tool call takes. The tool executor's own limit for the tool is the one that normally ends a call first.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The most tools read from one server. A server with more is read as far as this and the rest is ignored.</summary>
    public int MaxTools { get; init; } = 500;

    /// <summary>The most pages of tools read from one server.</summary>
    public int MaxPages { get; init; } = 20;

    /// <summary>The most bytes of one message from a server that are accepted; a longer one ends the connection.</summary>
    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// The protocol version agreed with this server the last time it was connected to, when known. It saves asking a server that speaks the earlier
    /// protocol the modern question first, which a program that ignores it answers only after <see cref="ProbeTimeout"/>.
    /// </summary>
    public string? KnownProtocolVersion { get; init; }
}
