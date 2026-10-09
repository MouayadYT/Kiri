using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;

namespace Assistant.Tools.Mcp;

/// <summary>
/// The older MCP HTTP+SSE transport (protocol version 2024-11-05), which the protocol has deprecated but some servers still speak: the
/// Assistant opens an event stream with a GET, the server's first event (<c>endpoint</c>) names where to POST, messages are POSTed there
/// and the server's answers come back on the stream. The address the server names must be on the same origin as the stream (a server
/// cannot send the Assistant's messages, and its sign-in, to another place). Nothing is followed on redirect.
/// </summary>
internal sealed class LegacySseMcpTransport : IMcpTransport
{
    private readonly Uri _streamAddress;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly HttpClient _http;
    private readonly int _maxMessageBytes;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcMessage>> _pending = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<Uri> _endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private HttpResponseMessage? _stream;
    private Task _reading = Task.CompletedTask;
    private volatile bool _closed;
    private int _disposed;
    private int _closedRaised;

    /// <summary>Creates the transport for the stream at <paramref name="streamAddress"/>; <paramref name="handler"/> sends the requests and is disposed with it.</summary>
    public LegacySseMcpTransport(
        Uri streamAddress, IReadOnlyDictionary<string, string> headers, HttpMessageHandler handler, McpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(streamAddress);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        _streamAddress = streamAddress;
        _headers = headers;
        _maxMessageBytes = options.MaxMessageBytes;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <inheritdoc/>
    public McpTransportKind Kind => McpTransportKind.LegacySse;

    /// <inheritdoc/>
    public string? ProtocolVersion { get; set; }

    /// <inheritdoc/>
    public event Action<JsonRpcMessage>? NotificationReceived;

    /// <inheritdoc/>
    public event Action? Closed;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var open = McpHttp.Request(HttpMethod.Get, _streamAddress, _headers);
        open.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(open, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            throw new McpException(McpFailure.ConnectFailed, inner: exception);
        }

        if (!response.IsSuccessStatusCode || !McpHttp.IsMediaType(response, "text/event-stream"))
        {
            var failure = response.IsSuccessStatusCode
                ? new McpException(McpFailure.Protocol, httpStatus: (int)response.StatusCode)
                : McpHttp.FailureFor(response);
            response.Dispose();
            throw failure;
        }

        _stream = response;
        _reading = Task.Run(() => ReadAsync(response));

        // The server says where to POST before anything else; one that does not is not speaking this transport (the caller's time limit ends the wait).
        await _endpoint.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<JsonRpcMessage> RequestAsync(OutgoingMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Id is not { } id)
        {
            throw new ArgumentException("A request has an id.", nameof(request));
        }

        ThrowIfClosed();
        var key = id.ToString(CultureInfo.InvariantCulture);
        var answer = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, answer))
        {
            throw new McpException(McpFailure.Protocol);
        }

        try
        {
            using var registration = cancellationToken.Register(static state => ((TaskCompletionSource<JsonRpcMessage>)state!).TrySetCanceled(), answer);
            await PostAsync(request.Json, cancellationToken).ConfigureAwait(false);
            return await answer.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <inheritdoc/>
    public async Task NotifyAsync(OutgoingMessage notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ThrowIfClosed();
        await PostAsync(notification.Json, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _closed = true;
        FailPending();
        await _stop.CancelAsync().ConfigureAwait(false);
        _stream?.Dispose();
        try
        {
            await _reading.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or McpException or IOException or HttpRequestException or ObjectDisposedException)
        {
            // The stream was closed under it.
        }

        _http.Dispose();
        _stop.Dispose();
    }

    // The server's stream: where to POST first, then its answers and its own messages.
    private async Task ReadAsync(HttpResponseMessage response)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(_stop.Token).ConfigureAwait(false);
            await foreach (var sse in SseReader.ReadAsync(stream, _maxMessageBytes, _stop.Token).ConfigureAwait(false))
            {
                if (sse.Type == "endpoint")
                {
                    ResolveEndpoint(sse.Data);
                }
                else if (sse.Type == "message")
                {
                    foreach (var read in JsonRpcMessage.Parse(Encoding.UTF8.GetBytes(sse.Data)))
                    {
                        await DispatchAsync(read).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is McpException or IOException or HttpRequestException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // The stream ended, or sent more than a message may hold: either way nothing more is read.
        }
        finally
        {
            _endpoint.TrySetException(new McpException(McpFailure.Closed));
            Ended();
        }
    }

    // The address the server named, accepted only when it is on the origin the stream came from.
    private void ResolveEndpoint(string named)
    {
        if (Uri.TryCreate(_streamAddress, named.Trim(), out var address)
            && address.Scheme == _streamAddress.Scheme
            && string.Equals(address.Host, _streamAddress.Host, StringComparison.OrdinalIgnoreCase)
            && address.Port == _streamAddress.Port
            && string.IsNullOrEmpty(address.UserInfo))
        {
            _endpoint.TrySetResult(address);
        }
        else
        {
            _endpoint.TrySetException(new McpException(McpFailure.Protocol));
        }
    }

    private async Task DispatchAsync(JsonRpcMessage message)
    {
        switch (message.Kind)
        {
            case JsonRpcKind.Response or JsonRpcKind.Error:
                if (message.IdKey is { } key && _pending.TryRemove(key, out var waiting))
                {
                    waiting.TrySetResult(message);
                }

                break;
            case JsonRpcKind.Request:
                if (JsonRpc.AnswerServerRequest(message) is { } answer)
                {
                    try
                    {
                        await PostAsync(answer, _stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is McpException or OperationCanceledException)
                    {
                        // The connection is ending.
                    }
                }

                break;
            default:
                try
                {
                    NotificationReceived?.Invoke(message);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // What listens cannot disturb the connection.
                }

                break;
        }
    }

    private async Task PostAsync(byte[] json, CancellationToken cancellationToken)
    {
        if (json.Length > _maxMessageBytes)
        {
            throw new McpException(McpFailure.TooLarge);
        }

        var address = await _endpoint.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var post = McpHttp.Request(HttpMethod.Post, address, _headers);
        post.Content = McpHttp.JsonContent(json);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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

    private void Ended()
    {
        _closed = true;
        FailPending();
        if (Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _closedRaised, 1) == 0)
        {
            try
            {
                Closed?.Invoke();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // What listens cannot disturb the connection.
            }
        }
    }

    private void FailPending()
    {
        foreach (var (key, waiting) in _pending)
        {
            if (_pending.TryRemove(key, out _))
            {
                waiting.TrySetException(new McpException(McpFailure.Closed));
            }
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new McpException(McpFailure.Closed);
        }
    }
}
