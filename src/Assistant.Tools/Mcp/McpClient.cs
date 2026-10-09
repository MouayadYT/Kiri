using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Tools.Integrations;

namespace Assistant.Tools.Mcp;

/// <summary>
/// The app's <see cref="IMcpClient"/>: one session with one MCP server over any <see cref="IMcpTransport"/>. It speaks both eras of the
/// protocol. A connection first asks the modern question (<c>server/discover</c>, with the per-request <c>_meta</c> of revision 2026-07-28); a
/// server that answers it is modern, one that answers with another error (or, over stdio, says nothing for the probe time) speaks the earlier
/// protocol and is then greeted with <c>initialize</c> and <c>notifications/initialized</c>; an HTTP server that rejects both without a
/// JSON-RPC error is tried as the deprecated HTTP+SSE transport. A server that was connected to before is not asked the question again (the
/// version agreed is kept with the integration). The Assistant declares no client capabilities, so a server can ask it nothing but <c>ping</c>.
/// Nothing a server says is logged: failures are codes (<see cref="McpFailure"/>).
/// </summary>
internal sealed class McpClient : IMcpClient
{
    private static readonly TimeSpan CancelNoticeTimeout = TimeSpan.FromSeconds(2);

    private readonly McpClientOptions _options;
    private readonly McpTransportKind _initialKind;
    private readonly Func<McpTransportKind, CancellationToken, ValueTask<IMcpTransport>> _createTransport;
    private IMcpTransport? _transport;
    private McpEra _era;
    private McpServerInfo? _server;
    private long _nextId;
    private volatile bool _connected;
    private int _started;
    private int _disposed;
    private int _disconnectedRaised;

    /// <summary>
    /// Creates a client that reaches its server over a transport of <paramref name="kind"/> that <paramref name="createTransport"/> makes (it reads
    /// the secrets the transport needs, which is why it is asynchronous, and is asked again for the older HTTP+SSE transport if the server needs it).
    /// </summary>
    public McpClient(McpTransportKind kind, Func<McpTransportKind, CancellationToken, ValueTask<IMcpTransport>> createTransport, McpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(createTransport);
        ArgumentNullException.ThrowIfNull(options);
        _initialKind = kind;
        _createTransport = createTransport;
        _options = options;
    }

    /// <inheritdoc/>
    public McpTransportKind TransportKind => _transport?.Kind ?? _initialKind;

    /// <inheritdoc/>
    public McpServerInfo? Server => _server;

    /// <inheritdoc/>
    public bool IsConnected => _connected && Volatile.Read(ref _disposed) == 0;

    /// <inheritdoc/>
    public event EventHandler? ToolsChanged;

    /// <inheritdoc/>
    public event EventHandler? Disconnected;

    /// <inheritdoc/>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("A client connects once.");
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_options.ConnectTimeout);
        try
        {
            await ConnectCoreAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await CloseTransportAsync().ConfigureAwait(false);
            if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new McpException(McpFailure.TimedOut, inner: exception);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = new List<McpToolDescriptor>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < _options.MaxPages && tools.Count < _options.MaxTools; page++)
        {
            var parameters = new JsonObject();
            if (cursor is not null)
            {
                parameters["cursor"] = cursor;
            }

            var result = await SendAsync(McpProtocol.ListTools, parameters, _options.RequestTimeout, null, cancellationToken).ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("tools", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (tools.Count < _options.MaxTools && ReadTool(item) is { } tool && names.Add(tool.Name))
                    {
                        tools.Add(tool);
                    }
                }
            }

            cursor = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
            if (string.IsNullOrEmpty(cursor) || !cursors.Add(cursor))
            {
                break;
            }
        }

        return tools;
    }

    /// <inheritdoc/>
    public async Task<McpToolResult> CallToolAsync(McpToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The arguments are an object.", nameof(arguments));
        }

        var parameters = new JsonObject
        {
            ["name"] = tool.Name,
            ["arguments"] = JsonNode.Parse(arguments.GetRawText()),
        };

        // Over HTTP, the modern protocol has the name of the tool, and the values the server marked, copied into headers.
        List<KeyValuePair<string, string>>? headers = null;
        if (_era == McpEra.Modern && TransportKind != McpTransportKind.Stdio)
        {
            headers = [new KeyValuePair<string, string>(McpProtocol.NameHeader, McpHeaderMirrors.Encode(tool.Name))];
            if (McpHeaderMirrors.TryExtract(tool.InputSchema, out var mirrors))
            {
                headers.AddRange(McpHeaderMirrors.HeadersFor(mirrors, arguments));
            }
        }

        var result = await SendAsync(McpProtocol.CallTool, parameters, _options.CallTimeout, headers, cancellationToken).ConfigureAwait(false);
        return ReadResult(result);
    }

    /// <inheritdoc/>
    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        // The modern protocol asks a server what it is with a request every server must answer; the earlier one has ping.
        await SendAsync(
            _era == McpEra.Modern ? McpProtocol.Discover : McpProtocol.Ping, new JsonObject(), _options.RequestTimeout, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connected = false;
        await CloseTransportAsync().ConfigureAwait(false);
    }

    // ---- Connecting ----

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        var transport = await OpenAsync(_initialKind, cancellationToken).ConfigureAwait(false);
        await transport.StartAsync(cancellationToken).ConfigureAwait(false);
        if (_initialKind == McpTransportKind.LegacySse)
        {
            // A server stored as speaking the older HTTP+SSE transport speaks the earlier protocol with it.
            await InitializeAsync(transport, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A server connected to before is greeted the way it was then; if that no longer works, it may have been upgraded, so it is asked the modern question.
        if (McpProtocol.IsKnownLegacyVersion(_options.KnownProtocolVersion))
        {
            try
            {
                await InitializeAsync(transport, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (McpException exception) when (exception.Failure is McpFailure.Server or McpFailure.Protocol or McpFailure.HttpError or McpFailure.Unsupported)
            {
                if (!await TryDiscoverAsync(transport, cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }

                return;
            }
        }

        if (await TryDiscoverAsync(transport, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await InitializeAsync(transport, cancellationToken).ConfigureAwait(false);
        }
        catch (McpException exception) when (_initialKind == McpTransportKind.StreamableHttp && IsNotAnMcpEndpoint(exception))
        {
            // Neither the modern question nor the earlier greeting was understood at this address, and it did not answer in JSON-RPC: it may be a server
            // of the deprecated HTTP+SSE transport, which is found by opening its event stream.
            await CloseTransportAsync().ConfigureAwait(false);
            var legacy = await OpenAsync(McpTransportKind.LegacySse, cancellationToken).ConfigureAwait(false);
            await legacy.StartAsync(cancellationToken).ConfigureAwait(false);
            await InitializeAsync(legacy, cancellationToken).ConfigureAwait(false);
        }
    }

    // The transport, made and watched.
    private async Task<IMcpTransport> OpenAsync(McpTransportKind kind, CancellationToken cancellationToken)
    {
        var transport = await _createTransport(kind, cancellationToken).ConfigureAwait(false);
        transport.NotificationReceived += OnNotification;
        transport.Closed += OnClosed;
        _transport = transport;
        return transport;
    }

    // Asks the modern question. True: the server is modern and the connection is made. False: it speaks the earlier protocol.
    private async Task<bool> TryDiscoverAsync(IMcpTransport transport, CancellationToken cancellationToken)
    {
        transport.ProtocolVersion = McpProtocol.ModernVersion;
        var request = JsonRpc.Request(
            Interlocked.Increment(ref _nextId),
            McpProtocol.Discover,
            ModernParameters(new JsonObject()),
            [new KeyValuePair<string, string>(McpProtocol.MethodHeader, McpProtocol.Discover)]);

        // A program that does not know the modern protocol may never answer, so over stdio the question is given only a little time.
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (transport.Kind == McpTransportKind.Stdio)
        {
            probe.CancelAfter(_options.ProbeTimeout);
        }

        JsonRpcMessage answer;
        try
        {
            answer = await transport.RequestAsync(request, probe.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            NotifyCancelled(transport, request.Id!.Value);
            return false;
        }
        catch (McpException exception) when (IsNotAnMcpEndpoint(exception))
        {
            return false;
        }

        if (answer.Kind == JsonRpcKind.Error)
        {
            var error = answer.Error!;
            if (error.Code == McpProtocol.UnsupportedProtocolVersion)
            {
                // It is modern, but not of the version asked: it names the ones it speaks. The earlier protocol is used if it speaks one of those.
                if (SupportedVersions(error.Data).Any(McpProtocol.IsKnownLegacyVersion))
                {
                    return false;
                }

                throw new McpException(McpFailure.Unsupported, rpcCode: error.Code);
            }

            if (McpProtocol.IsModernErrorCode(error.Code))
            {
                throw new McpException(McpFailure.Protocol, answer.HttpStatus, error.Code);
            }

            // Any other error is what a server of the earlier protocol says to a method it does not know.
            return false;
        }

        var result = answer.Result;
        if (UnwrapResultType(result) is { } problem)
        {
            throw problem;
        }

        var versions = SupportedVersions(result);
        if (!versions.Contains(McpProtocol.ModernVersion, StringComparer.Ordinal))
        {
            if (versions.Any(McpProtocol.IsKnownLegacyVersion))
            {
                return false;
            }

            throw new McpException(McpFailure.Unsupported);
        }

        var meta = result.TryGetProperty("_meta", out var metaElement) && metaElement.ValueKind == JsonValueKind.Object ? metaElement : default;
        var identity = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("io.modelcontextprotocol/serverInfo", out var infoElement)
            ? infoElement
            : default;
        _server = new McpServerInfo(
            Cut(ServerInfoMember(identity, "name"), 100), Cut(ServerInfoMember(identity, "version"), 64), McpProtocol.ModernVersion, ReadCapabilities(result));
        _era = McpEra.Modern;
        _connected = true;
        return true;
    }

    // The earlier protocol's greeting: initialize, then the notice that the client is ready.
    private async Task InitializeAsync(IMcpTransport transport, CancellationToken cancellationToken)
    {
        transport.ProtocolVersion = null;
        var asked = McpProtocol.LatestLegacyVersion;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var parameters = new JsonObject
            {
                ["protocolVersion"] = asked,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = McpProtocol.ClientInfo(),
            };
            var answer = await transport.RequestAsync(
                JsonRpc.Request(Interlocked.Increment(ref _nextId), McpProtocol.Initialize, parameters), cancellationToken).ConfigureAwait(false);

            if (answer.Kind == JsonRpcKind.Error)
            {
                // A server that does not speak the version asked may name the ones it does; the best of those that the Assistant speaks is tried once.
                var error = answer.Error!;
                var offered = SupportedVersions(error.Data).FirstOrDefault(McpProtocol.IsKnownLegacyVersion);
                if (attempt == 0 && offered is not null && offered != asked)
                {
                    asked = offered;
                    continue;
                }

                throw new McpException(
                    McpProtocol.IsModernErrorCode(error.Code) ? McpFailure.Unsupported : McpFailure.Server, answer.HttpStatus, error.Code, error.Message);
            }

            var result = answer.Result;
            var agreed = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("protocolVersion", out var version)
                && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
            if (!McpProtocol.IsKnownLegacyVersion(agreed))
            {
                throw new McpException(McpFailure.Unsupported);
            }

            transport.ProtocolVersion = agreed;
            var info = result.TryGetProperty("serverInfo", out var serverInfo) ? serverInfo : default;
            _server = new McpServerInfo(Cut(ServerInfoMember(info, "name"), 100), Cut(ServerInfoMember(info, "version"), 64), agreed!, ReadCapabilities(result));
            _era = McpEra.Legacy;
            await transport.NotifyAsync(JsonRpc.Notification(McpProtocol.Initialized), cancellationToken).ConfigureAwait(false);
            _connected = true;
            return;
        }

        throw new McpException(McpFailure.Unsupported);
    }

    // What an HTTP server that does not host the endpoint, or that does not know the era, answers: a client takes it for the earlier protocol or transport.
    private static bool IsNotAnMcpEndpoint(McpException exception) =>
        exception.Failure == McpFailure.HttpError && exception.HttpStatus is 400 or 404 or 405 or 415;

    // ---- Requests ----

    // Sends a request and returns its result. The modern era's metadata (and its headers) are added to what is sent.
    private async Task<JsonElement> SendAsync(
        string method,
        JsonObject parameters,
        TimeSpan timeout,
        List<KeyValuePair<string, string>>? extraHeaders,
        CancellationToken cancellationToken)
    {
        var transport = _transport;
        if (!_connected || transport is null || Volatile.Read(ref _disposed) != 0)
        {
            throw new McpException(McpFailure.Closed);
        }

        var id = Interlocked.Increment(ref _nextId);
        IReadOnlyList<KeyValuePair<string, string>>? headers = extraHeaders;
        if (_era == McpEra.Modern)
        {
            parameters = ModernParameters(parameters);
            headers = [new KeyValuePair<string, string>(McpProtocol.MethodHeader, method), .. extraHeaders ?? []];
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        JsonRpcMessage answer;
        try
        {
            answer = await transport.RequestAsync(JsonRpc.Request(id, method, parameters, headers), limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            NotifyCancelled(transport, id);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new McpException(McpFailure.TimedOut, inner: exception);
        }

        if (answer.Kind == JsonRpcKind.Error)
        {
            var error = answer.Error!;
            throw new McpException(
                error.Code is McpProtocol.UnsupportedProtocolVersion or McpProtocol.MissingRequiredClientCapability ? McpFailure.Unsupported : McpFailure.Server,
                answer.HttpStatus, error.Code, error.Message);
        }

        if (UnwrapResultType(answer.Result) is { } problem)
        {
            throw problem;
        }

        return answer.Result;
    }

    // The per-request metadata of the modern era.
    private static JsonObject ModernParameters(JsonObject parameters)
    {
        parameters["_meta"] = McpProtocol.ModernMeta();
        return parameters;
    }

    // A server of the modern era says what kind of result it is. Only a finished one is understood: one that asks the client for more input
    // (sampling, elicitation) cannot be served, since the Assistant declares no capability to.
    private static McpException? UnwrapResultType(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return new McpException(McpFailure.Protocol);
        }

        if (result.TryGetProperty("resultType", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() is not "complete")
        {
            return new McpException(type.GetString() == "input_required" ? McpFailure.Unsupported : McpFailure.Protocol);
        }

        return null;
    }

    // A request that was given up on is told so, as the protocol asks of a client on stdio and of the earlier protocol; on modern HTTP closing the
    // response is the notice. It is sent in the background, briefly, and whatever becomes of it does not matter.
    private void NotifyCancelled(IMcpTransport transport, long id)
    {
        if (transport.Kind != McpTransportKind.Stdio && _era == McpEra.Modern)
        {
            return;
        }

        var notice = JsonRpc.Notification(
            McpProtocol.Cancelled, new JsonObject { ["requestId"] = id, ["reason"] = "The request was cancelled." });
        _ = Task.Run(async () =>
        {
            try
            {
                using var limit = new CancellationTokenSource(CancelNoticeTimeout);
                await transport.NotifyAsync(notice, limit.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is McpException or OperationCanceledException or ObjectDisposedException)
            {
                // The connection is gone or slow; the request is over either way.
            }
        });
    }

    // ---- Reading what servers send ----

    private McpToolDescriptor? ReadTool(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
            || !IntegrationRules.IsValidToolName(name.GetString())
            || !item.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // A tool whose header marks break the rules is left out, as the protocol requires of a client of the modern era over HTTP.
        if (_era == McpEra.Modern && TransportKind != McpTransportKind.Stdio && !McpHeaderMirrors.TryExtract(schema, out _))
        {
            return null;
        }

        var annotations = McpToolAnnotations.None;
        if (item.TryGetProperty("annotations", out var notes) && notes.ValueKind == JsonValueKind.Object)
        {
            annotations = new McpToolAnnotations(Flag(notes, "readOnlyHint"), Flag(notes, "destructiveHint"), Flag(notes, "idempotentHint"), Flag(notes, "openWorldHint"));
        }

        return new McpToolDescriptor(
            name.GetString()!,
            Cut(String(item, "title"), McpToolDescriptor.MaxTitleLength),
            Cut(String(item, "description"), McpToolDescriptor.MaxDescriptionLength),
            schema.Clone(),
            annotations);
    }

    private static McpToolResult ReadResult(JsonElement result)
    {
        var isError = result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
        var content = new List<McpContentBlock>();
        if (result.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in blocks.EnumerateArray().Take(100))
            {
                if (ReadBlock(block) is { } read)
                {
                    content.Add(read);
                }
            }
        }

        JsonElement? structured = result.TryGetProperty("structuredContent", out var data) && data.ValueKind != JsonValueKind.Null ? data.Clone() : null;
        return new McpToolResult(isError, content, structured);
    }

    private static McpContentBlock? ReadBlock(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        switch (String(block, "type"))
        {
            case "text":
                return new McpContentBlock(McpContentKind.Text, String(block, "text"), null, null, null);
            case "image":
                return new McpContentBlock(McpContentKind.Image, null, Cut(String(block, "mimeType"), 100), null, null);
            case "audio":
                return new McpContentBlock(McpContentKind.Audio, null, Cut(String(block, "mimeType"), 100), null, null);
            case "resource_link":
                return new McpContentBlock(
                    McpContentKind.ResourceLink, null, Cut(String(block, "mimeType"), 100), Cut(String(block, "uri"), 500), Cut(String(block, "name"), 200));
            case "resource":
                var resource = block.TryGetProperty("resource", out var embedded) && embedded.ValueKind == JsonValueKind.Object ? embedded : default;
                return resource.ValueKind == JsonValueKind.Object
                    ? new McpContentBlock(McpContentKind.Resource, String(resource, "text"), Cut(String(resource, "mimeType"), 100), Cut(String(resource, "uri"), 500), null)
                    : null;
            default:
                return null;
        }
    }

    private static McpServerCapabilities ReadCapabilities(JsonElement result)
    {
        if (!result.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object)
        {
            return McpServerCapabilities.None;
        }

        var tools = capabilities.TryGetProperty("tools", out var toolsElement) && toolsElement.ValueKind == JsonValueKind.Object ? toolsElement : default;
        return new McpServerCapabilities(
            Tools: tools.ValueKind == JsonValueKind.Object,
            ToolsListChanged: tools.ValueKind == JsonValueKind.Object && Flag(tools, "listChanged") == true,
            Resources: capabilities.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object,
            Prompts: capabilities.TryGetProperty("prompts", out var prompts) && prompts.ValueKind == JsonValueKind.Object);
    }

    // The versions an answer lists, whether it is a discover result (supportedVersions) or an error's data (supported).
    private static IReadOnlyList<string> SupportedVersions(JsonElement? source)
    {
        if (source is not { ValueKind: JsonValueKind.Object } element)
        {
            return [];
        }

        foreach (var name in new[] { "supportedVersions", "supported" })
        {
            if (element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                return [.. list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Take(32)];
            }
        }

        return [];
    }

    private static string? ServerInfoMember(JsonElement info, string name) => info.ValueKind == JsonValueKind.Object ? String(info, name) : null;

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.ValueKind == JsonValueKind.True : null;

    private static string? Cut(string? text, int length) => text is not null && text.Length > length ? text[..length] : text;

    // ---- Events ----

    private void OnNotification(JsonRpcMessage message)
    {
        if (message.Method == McpProtocol.ToolsListChanged)
        {
            ToolsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnClosed()
    {
        _connected = false;
        if (Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _disconnectedRaised, 1) == 0)
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task CloseTransportAsync()
    {
        _connected = false;
        var transport = Interlocked.Exchange(ref _transport, null);
        if (transport is not null)
        {
            transport.NotificationReceived -= OnNotification;
            transport.Closed -= OnClosed;
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
