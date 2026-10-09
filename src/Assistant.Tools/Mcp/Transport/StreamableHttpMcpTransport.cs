using System.Globalization;
using System.Net;
using System.Text;

namespace Assistant.Tools.Mcp;

/// <summary>
/// The MCP Streamable HTTP transport: every message is a POST to the one MCP endpoint, and the answer to a request is a JSON body or an
/// event stream that carries it. It serves both eras: the modern one adds the headers that mirror a request's method and name, the earlier
/// ones a session (<c>Mcp-Session-Id</c>, given by the server and ended with a DELETE). Servers do not need the older standalone stream that a
/// GET opened, since the Assistant asks for nothing that arrives unasked; a server that wants to send it a request on a stream gets
/// <c>ping</c> answered and nothing else served. The sign-in headers it is given are sent with every request and never logged. No redirect is followed.
/// </summary>
internal sealed class StreamableHttpMcpTransport : IMcpTransport
{
    private static readonly TimeSpan EndSessionTimeout = TimeSpan.FromSeconds(2);

    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly HttpClient _http;
    private readonly int _maxMessageBytes;
    private string? _sessionId;
    private int _disposed;

    /// <summary>Creates the transport for <paramref name="endpoint"/>; <paramref name="handler"/> is what sends the requests, and is disposed with it.</summary>
    public StreamableHttpMcpTransport(
        Uri endpoint, IReadOnlyDictionary<string, string> headers, HttpMessageHandler handler, McpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        _endpoint = endpoint;
        _headers = headers;
        _maxMessageBytes = options.MaxMessageBytes;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <inheritdoc/>
    public McpTransportKind Kind => McpTransportKind.StreamableHttp;

    /// <inheritdoc/>
    public string? ProtocolVersion { get; set; }

    /// <inheritdoc/>
    public event Action<JsonRpcMessage>? NotificationReceived;

    /// <inheritdoc/>
    public event Action? Closed
    {
        // Nothing here is a long-lived connection: each request stands alone, so the transport never ends by itself.
        add { }
        remove { }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task<JsonRpcMessage> RequestAsync(OutgoingMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Id is not { } id)
        {
            throw new ArgumentException("A request has an id.", nameof(request));
        }

        var key = id.ToString(CultureInfo.InvariantCulture);
        using var message = Post(request.Json, request.Headers);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            throw new McpException(McpFailure.ConnectFailed, inner: exception);
        }

        using (response)
        {
            RememberSession(response);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return await ErrorAnswerAsync(response, cancellationToken).ConfigureAwait(false);
            }

            if (McpHttp.IsMediaType(response, "text/event-stream"))
            {
                return await ReadStreamAsync(response, key, cancellationToken).ConfigureAwait(false);
            }

            if (!McpHttp.IsMediaType(response, "application/json"))
            {
                // A 202 with no body is for a notification, not a request; anything else that is not JSON is not an MCP answer.
                throw new McpException(McpFailure.Protocol, httpStatus: status);
            }

            var body = await McpHttp.ReadBodyAsync(response.Content, _maxMessageBytes, cancellationToken).ConfigureAwait(false);
            foreach (var read in JsonRpcMessage.Parse(body))
            {
                if (read.Kind is JsonRpcKind.Response or JsonRpcKind.Error && read.IdKey == key)
                {
                    return read;
                }

                await HandleOtherAsync(read, cancellationToken).ConfigureAwait(false);
            }

            throw new McpException(McpFailure.Protocol, httpStatus: status);
        }
    }

    /// <inheritdoc/>
    public async Task NotifyAsync(OutgoingMessage notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        using var message = Post(notification.Json, notification.Headers);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            throw new McpException(McpFailure.ConnectFailed, inner: exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw McpHttp.FailureFor(response);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // A server that gave a session is told it is over, if it cares to listen; whatever it answers, the session is ended here.
        if (_sessionId is not null)
        {
            try
            {
                using var end = McpHttp.Request(HttpMethod.Delete, _endpoint, _headers);
                end.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, _sessionId);
                if (ProtocolVersion is { } version)
                {
                    end.Headers.TryAddWithoutValidation(McpProtocol.ProtocolVersionHeader, version);
                }

                using var limit = new CancellationTokenSource(EndSessionTimeout);
                using var response = await _http.SendAsync(end, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
            {
                // The session ends with the connection anyway.
            }
        }

        _http.Dispose();
    }

    // A POST of one message with the headers every request carries.
    private HttpRequestMessage Post(byte[] json, IReadOnlyList<KeyValuePair<string, string>>? extra)
    {
        if (json.Length > _maxMessageBytes)
        {
            throw new McpException(McpFailure.TooLarge);
        }

        var message = McpHttp.Request(HttpMethod.Post, _endpoint, _headers);
        message.Content = McpHttp.JsonContent(json);
        message.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (ProtocolVersion is { } version)
        {
            message.Headers.TryAddWithoutValidation(McpProtocol.ProtocolVersionHeader, version);
        }

        if (_sessionId is { } session)
        {
            message.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, session);
        }

        if (extra is not null)
        {
            foreach (var (name, value) in extra)
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return message;
    }

    // A session the server gave is sent back with every later request.
    private void RememberSession(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues(McpProtocol.SessionIdHeader, out var values)
            && values.FirstOrDefault() is { } session && McpHttp.IsValidSessionId(session))
        {
            _sessionId = session;
        }
    }

    // What an unsuccessful answer says: a JSON-RPC error in its body is given back (with the status it came with, which tells the client which
    // era the server is of); anything else is a failure of the kind its status stands for.
    private async Task<JsonRpcMessage> ErrorAnswerAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw McpHttp.FailureFor(response);
        }

        if (response.StatusCode == HttpStatusCode.NotFound && _sessionId is not null)
        {
            // The server no longer knows the session this request carried; the request was not served.
            _sessionId = null;
            throw new McpException(McpFailure.SessionExpired, httpStatus: status);
        }

        if (McpHttp.IsMediaType(response, "application/json"))
        {
            try
            {
                var body = await McpHttp.ReadBodyAsync(response.Content, 64 * 1024, cancellationToken).ConfigureAwait(false);
                if (JsonRpcMessage.Parse(body).FirstOrDefault(read => read.Kind == JsonRpcKind.Error) is { } error)
                {
                    return error.WithHttpStatus(status);
                }
            }
            catch (McpException exception) when (exception.Failure == McpFailure.TooLarge)
            {
                // Too large to be an error message: it is an HTTP error.
            }
        }

        throw McpHttp.FailureFor(response);
    }

    // The messages of an event stream, until the answer to the request: what comes before it is a notification, or a request of the server's own.
    private async Task<JsonRpcMessage> ReadStreamAsync(HttpResponseMessage response, string key, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var sse in SseReader.ReadAsync(stream, _maxMessageBytes, cancellationToken).ConfigureAwait(false))
        {
            if (sse.Type != "message")
            {
                continue;
            }

            foreach (var read in JsonRpcMessage.Parse(Encoding.UTF8.GetBytes(sse.Data)))
            {
                if (read.Kind is JsonRpcKind.Response or JsonRpcKind.Error && read.IdKey == key)
                {
                    return read;
                }

                await HandleOtherAsync(read, cancellationToken).ConfigureAwait(false);
            }
        }

        // The stream ended and the answer never came.
        throw new McpException(McpFailure.Closed);
    }

    // A notification is reported; a request of the server's is answered if it is one that is served (ping) and refused otherwise.
    private async Task HandleOtherAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        if (message.Kind == JsonRpcKind.Notification)
        {
            try
            {
                NotificationReceived?.Invoke(message);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // What listens cannot disturb the connection.
            }
        }
        else if (JsonRpc.AnswerServerRequest(message) is { } answer)
        {
            try
            {
                using var post = Post(answer, null);
                using var response = await _http.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or McpException)
            {
                // The server asked something that did not need an answer to go on.
            }
        }
    }
}
