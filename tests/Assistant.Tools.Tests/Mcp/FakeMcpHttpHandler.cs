using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Assistant.Tools.FakeMcpServer;

namespace Assistant.Tools.Tests.Mcp;

/// <summary>How a server of the earlier protocol answers the modern question that a client asks first.</summary>
public enum ProbeReaction
{
    /// <summary>With an error in JSON-RPC (method not found), as a server that has never heard of it does.</summary>
    JsonRpcError,

    /// <summary>With HTTP 400 and nothing else.</summary>
    Http400Empty,

    /// <summary>With HTTP 400 and a plain text body.</summary>
    Http400Text,

    /// <summary>With HTTP 404.</summary>
    Http404,

    /// <summary>With HTTP 405.</summary>
    Http405,
}

/// <summary>How the fake HTTP server behaves.</summary>
internal sealed class FakeHttpOptions
{
    /// <summary>Whether it answers a POST with an event stream (with a progress notification before the result) rather than a JSON body.</summary>
    public bool Sse { get; set; }

    /// <summary>Whether it says on the stream that its list of tools changed.</summary>
    public bool ListChangedOnStream { get; set; }

    /// <summary>Whether it sends a request of its own (<c>ping</c>) on the stream before the answer, as servers of the earlier protocol may.</summary>
    public bool PingOnStream { get; set; }

    /// <summary>Whether it gives sessions (<c>Mcp-Session-Id</c>) as servers of the earlier protocol do.</summary>
    public bool UseSessions { get; set; }

    /// <summary>The bearer token it asks for, or <see langword="null"/> for none.</summary>
    public string? BearerToken { get; set; }

    /// <summary>Whether a 401 says where its sign-in information is.</summary>
    public bool OfferSignIn { get; set; } = true;

    /// <summary>How a server of the earlier protocol answers the modern question.</summary>
    public ProbeReaction Probe { get; set; } = ProbeReaction.JsonRpcError;

    /// <summary>Whether it is a server of the deprecated HTTP+SSE transport: POST to its address is refused, a GET opens its event stream.</summary>
    public bool SseOnly { get; set; }

    /// <summary>The address its <c>endpoint</c> event names (a path, or a whole address).</summary>
    public string EndpointEvent { get; set; } = "/messages?session=1";

    /// <summary>Whether a modern server refuses a request that lacks the headers the protocol requires.</summary>
    public bool ValidateModernHeaders { get; set; } = true;

    /// <summary>When set, every request is answered with this status.</summary>
    public int? ForceStatus { get; set; }

    /// <summary>When set, every request is answered with a redirect to this address.</summary>
    public string? RedirectTo { get; set; }

    /// <summary>How long a POST takes to be answered.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>When set, what a request raises instead of being answered (the network is down).</summary>
    public Func<Exception>? Throw { get; set; }

    /// <summary>When set, the body of every answer to a POST is this (not JSON-RPC at all).</summary>
    public string? RawBody { get; set; }

    /// <summary>The session id it gives.</summary>
    public string SessionId { get; set; } = "sess-1";
}

/// <summary>What the fake server was sent.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public string? RpcMethod => Body.Length > 0 && Body[0] == '{' ? (string?)JsonNode.Parse(Body)?["method"] : null;
}

/// <summary>
/// An HTTP server for the HTTP transports' tests that is not on the network: it is the handler the HTTP client sends its requests through. It
/// serves a <see cref="FakeMcpServerCore"/> in the way <see cref="FakeHttpOptions"/> says, and keeps what it was sent.
/// </summary>
internal sealed class FakeMcpHttpHandler(FakeMcpServerCore core, FakeHttpOptions? options = null) : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private PushStream? _stream;
    private bool _expired;

    public FakeHttpOptions Options { get; } = options ?? new FakeHttpOptions();

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Makes the server forget the session: requests that carry it are answered 404.</summary>
    public void ExpireSession() => _expired = true;

    /// <summary>Ends the event stream of the HTTP+SSE transport.</summary>
    public void EndStream() => _stream?.Complete();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        lock (_gate)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body));
        }

        if (Options.Throw is { } throwing)
        {
            throw throwing();
        }

        if (Options.Delay > TimeSpan.Zero)
        {
            await Task.Delay(Options.Delay, cancellationToken).ConfigureAwait(false);
        }

        if (Options.RedirectTo is { } target)
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            redirect.Headers.Location = new Uri(target);
            return redirect;
        }

        if (Options.ForceStatus is { } status)
        {
            return new HttpResponseMessage((HttpStatusCode)status);
        }

        if (Options.BearerToken is { } token && headers.GetValueOrDefault("Authorization") != "Bearer " + token)
        {
            var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            unauthorized.Headers.TryAddWithoutValidation(
                "WWW-Authenticate",
                Options.OfferSignIn ? "Bearer resource_metadata=\"https://mcp.example.com/.well-known/oauth-protected-resource\"" : "Bearer realm=\"mcp\"");
            return unauthorized;
        }

        if (request.Method == HttpMethod.Get)
        {
            return Options.SseOnly ? OpenStream() : new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        if (request.Method == HttpMethod.Delete)
        {
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        if (Options.SseOnly)
        {
            return await HandleSseOnlyPostAsync(request, body, cancellationToken).ConfigureAwait(false);
        }

        return await HandlePostAsync(headers, body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> HandlePostAsync(Dictionary<string, string> headers, string body, CancellationToken cancellationToken)
    {
        var message = JsonNode.Parse(body)!.AsObject();
        var method = (string?)message["method"] ?? string.Empty;
        var modern = core.Options.Era == FakeMcpEra.Modern;
        if (Options.RawBody is { } raw)
        {
            return Reply(HttpStatusCode.OK, "application/json", raw);
        }

        if (modern && Options.ValidateModernHeaders && message["id"] is not null && HeaderMismatch(headers, message, method) is { } mismatch)
        {
            return Reply(HttpStatusCode.BadRequest, "application/json", mismatch);
        }

        if (!modern && method == "server/discover" && Options.Probe != ProbeReaction.JsonRpcError)
        {
            return Options.Probe switch
            {
                ProbeReaction.Http400Empty => new HttpResponseMessage(HttpStatusCode.BadRequest),
                ProbeReaction.Http400Text => Reply(HttpStatusCode.BadRequest, "text/plain", "Bad request"),
                ProbeReaction.Http404 => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed),
            };
        }

        if (Options.UseSessions && !modern && method != "initialize")
        {
            var carried = headers.GetValueOrDefault("Mcp-Session-Id");
            if (_expired || carried != Options.SessionId)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        var answer = await core.HandleAsync(body, cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        var response = Options.Sse ? StreamAnswer(answer) : Reply(HttpStatusCode.OK, "application/json", answer);
        if (Options.UseSessions && !modern && method == "initialize")
        {
            response.Headers.TryAddWithoutValidation("Mcp-Session-Id", Options.SessionId);
        }

        return response;
    }

    // The 400 a modern server gives a request whose headers do not say what its body does.
    private static string? HeaderMismatch(Dictionary<string, string> headers, JsonObject message, string method)
    {
        string? problem = null;
        if (headers.GetValueOrDefault("MCP-Protocol-Version") != "2026-07-28")
        {
            problem = "MCP-Protocol-Version is missing or wrong";
        }
        else if (headers.GetValueOrDefault("Mcp-Method") != method)
        {
            problem = "Mcp-Method does not match";
        }
        else if (method == "tools/call")
        {
            var parameters = message["params"]!.AsObject();
            var name = (string?)parameters["name"];
            if (Decode(headers.GetValueOrDefault("Mcp-Name")) != name)
            {
                problem = "Mcp-Name does not match";
            }
            else if (name == "regional" && Decode(headers.GetValueOrDefault("Mcp-Param-Region")) != (string?)parameters["arguments"]?["region"])
            {
                problem = "Mcp-Param-Region does not match";
            }
        }

        return problem is null
            ? null
            : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["error"] = new JsonObject { ["code"] = -32020, ["message"] = problem } }.ToJsonString();
    }

    private static string? Decode(string? value) =>
        value is not null && value.StartsWith("=?base64?", StringComparison.Ordinal) && value.EndsWith("?=", StringComparison.Ordinal)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(value["=?base64?".Length..^2]))
            : value;

    // A request on the deprecated transport: POST to the address named in the endpoint event is answered 202 and the answer goes down the stream.
    private async Task<HttpResponseMessage> HandleSseOnlyPostAsync(HttpRequestMessage request, string body, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath != "/messages" || _stream is null)
        {
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        var answer = await core.HandleAsync(body, cancellationToken).ConfigureAwait(false);
        if (answer is not null)
        {
            _stream.Push("event: message\ndata: " + answer + "\n\n");
        }

        return new HttpResponseMessage(HttpStatusCode.Accepted);
    }

    private HttpResponseMessage OpenStream()
    {
        _stream = new PushStream();
        _stream.Push("event: endpoint\ndata: " + Options.EndpointEvent + "\n\n");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(_stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private HttpResponseMessage StreamAnswer(string answer)
    {
        var text = new StringBuilder(": keep-alive\n\n");
        if (Options.PingOnStream)
        {
            text.Append("event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":\"srv-1\",\"method\":\"ping\"}\n\n");
        }

        text.Append("event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progress\":1}}\n\n");
        if (Options.ListChangedOnStream)
        {
            text.Append("event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}\n\n");
        }

        text.Append("event: message\ndata: ").Append(answer).Append("\n\n");
        return Reply(HttpStatusCode.OK, "text/event-stream", text.ToString());
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string contentType, string text)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    // A stream that gives what the server pushes into it, and waits for more, until it is ended.
    private sealed class PushStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[] _current = [];
        private int _offset;

        public void Push(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public void Complete() => _chunks.Writer.TryComplete();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_offset >= _current.Length)
            {
                try
                {
                    if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return 0;
                    }
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }

                if (_chunks.Reader.TryRead(out var chunk))
                {
                    _current = chunk;
                    _offset = 0;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _chunks.Writer.TryComplete();
            }

            base.Dispose(disposing);
        }
    }
}
