using System.Text.Json;
using Assistant.Tools.Integrations;

namespace Assistant.Tools.Mcp;

/// <summary>
/// A connection to one MCP server (PROJECT_SPEC §4.8, step 104): the one abstraction the rest of the app sees, whatever carries the messages
/// (a local program's standard streams, a Streamable HTTP endpoint, the older HTTP+SSE transport) and whichever era of the protocol the
/// server speaks. It is a client of tools only. Calls are not made concurrently on one client by the app (the tool loop runs calls one after
/// another), though a client does tolerate it. A client is used by one caller at a time to connect; disposing it ends the connection and, for a
/// local program, the program.
/// </summary>
public interface IMcpClient : IAsyncDisposable
{
    /// <summary>The transport in use. It can differ from the one asked for: a server found to speak only the older HTTP+SSE transport is reached over it.</summary>
    McpTransportKind TransportKind { get; }

    /// <summary>What the server is and can do; <see langword="null"/> until <see cref="ConnectAsync"/> has succeeded.</summary>
    McpServerInfo? Server { get; }

    /// <summary>Whether the connection is made and has not ended.</summary>
    bool IsConnected { get; }

    /// <summary>Raised when the server says its list of tools changed. The list a caller holds is then out of date.</summary>
    event EventHandler? ToolsChanged;

    /// <summary>Raised once when a connection that was made ends (the program exited, the stream closed).</summary>
    event EventHandler? Disconnected;

    /// <summary>
    /// Connects: starts the program or reaches the server, finds out which era of the protocol it speaks and agrees a version.
    /// Gives up after the client's connect time.
    /// </summary>
    /// <exception cref="McpException">The connection could not be made.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the server's tools (all pages, up to a limit), leaving out any that are not valid.</summary>
    /// <exception cref="McpException">The list could not be read.</exception>
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Calls <paramref name="tool"/> with <paramref name="arguments"/> (an object, by the names the server gave its arguments).</summary>
    /// <exception cref="McpException">The call could not be made or the server answered with an error; a tool that ran and failed is a result with <see cref="McpToolResult.IsError"/>.</exception>
    Task<McpToolResult> CallToolAsync(McpToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken = default);

    /// <summary>Asks the server whether it is still there.</summary>
    /// <exception cref="McpException">It is not.</exception>
    Task PingAsync(CancellationToken cancellationToken = default);
}

/// <summary>Makes the connection to an installed integration.</summary>
public interface IMcpClientFactory
{
    /// <summary>
    /// Makes a client for <paramref name="integration"/> that is not yet connected. Its secrets are read from the secret store as it connects, never
    /// before and never kept in the record.
    /// </summary>
    /// <exception cref="McpException">The integration cannot be reached as it is stored (<see cref="McpFailure.NotConfigured"/>).</exception>
    IMcpClient Create(InstalledIntegration integration);
}
