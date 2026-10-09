using System.Text;

namespace Assistant.Tools.Mcp;

/// <summary>
/// What carries messages between the Assistant and one MCP server: it sends a request and gives back the server's answer to it, sends a
/// notification, and reports what the server says unasked. It knows nothing of what the messages mean, nor of which era of the protocol
/// is spoken; <see cref="McpClient"/> does. Whatever the server asks of the client (<c>ping</c>) is answered by the transport itself.
/// </summary>
internal interface IMcpTransport : IAsyncDisposable
{
    /// <summary>The transport.</summary>
    McpTransportKind Kind { get; }

    /// <summary>
    /// The protocol version to send with every request from now on (the <c>MCP-Protocol-Version</c> header of an HTTP transport); a transport
    /// that has no such header ignores it. <see langword="null"/> sends none.
    /// </summary>
    string? ProtocolVersion { get; set; }

    /// <summary>Raised for a notification the server sent. It runs on the transport's own thread and must not block.</summary>
    event Action<JsonRpcMessage>? NotificationReceived;

    /// <summary>Raised once when the connection ends without the transport having been disposed (the program exited, a stream closed).</summary>
    event Action? Closed;

    /// <summary>Starts the transport: starts the program, or opens the stream the older HTTP+SSE transport needs. Nothing to do for Streamable HTTP.</summary>
    /// <exception cref="McpException">It could not be started.</exception>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Sends <paramref name="request"/> and returns the server's answer to it, a response or an error. Waits as long as <paramref name="cancellationToken"/> lets it.</summary>
    /// <exception cref="McpException">No answer could be had (the connection is closed, the server answered with an HTTP error that is not a JSON-RPC error, a message was too large).</exception>
    Task<JsonRpcMessage> RequestAsync(OutgoingMessage request, CancellationToken cancellationToken);

    /// <summary>Sends <paramref name="notification"/>.</summary>
    /// <exception cref="McpException">It could not be sent.</exception>
    Task NotifyAsync(OutgoingMessage notification, CancellationToken cancellationToken);
}

/// <summary>How a program is started for a local MCP server, with the secrets it was given. Its text form shows none of it.</summary>
/// <param name="Command">The full path of the program.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="WorkingDirectory">The folder it starts in, or <see langword="null"/>.</param>
/// <param name="Environment">What it is given on top of the few variables Windows needs, secrets included.</param>
internal sealed record McpLaunch(
    string Command, IReadOnlyList<string> Arguments, string? WorkingDirectory, IReadOnlyDictionary<string, string> Environment)
{
    // The command is a path, the arguments and the environment may hold a secret: none of it reaches a log through ToString.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("McpLaunch");
        return true;
    }
}
