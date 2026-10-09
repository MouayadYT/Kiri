using System.Text.Json.Nodes;
using Assistant.Tools.FakeMcpServer;
using Assistant.Tools.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Mcp;

/// <summary>A client over the HTTP transports to a fake server that is not on the network.</summary>
internal static class HttpHarness
{
    public static McpClient Client(
        McpTransportKind kind,
        FakeMcpHttpHandler handler,
        IReadOnlyDictionary<string, string>? headers = null,
        McpClientOptions? options = null,
        string endpoint = "https://mcp.example.com/mcp")
    {
        var address = new Uri(endpoint);
        var settings = options ?? new McpClientOptions();
        var sent = headers ?? new Dictionary<string, string>();
        return new McpClient(
            kind,
            (asked, _) => ValueTask.FromResult<IMcpTransport>(asked == McpTransportKind.LegacySse
                ? new LegacySseMcpTransport(address, sent, handler, settings)
                : new StreamableHttpMcpTransport(address, sent, handler, settings)),
            settings);
    }

    public static FakeMcpServerCore Server(FakeMcpEra era = FakeMcpEra.Modern, string legacyVersion = "2025-11-25") =>
        new(new FakeMcpOptions { Era = era, LegacyVersion = legacyVersion });
}

public sealed class McpHttpTests
{
    private static async Task<McpToolDescriptor> ToolAsync(IMcpClient client, string name) => (await client.ListToolsAsync()).Single(tool => tool.Name == name);

    [Fact]
    public async Task AModernServerIsFoundOverHttpAndEveryRequestCarriesTheHeadersTheProtocolRequires()
    {
        var core = HttpHarness.Server();
        var handler = new FakeMcpHttpHandler(core);
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);

        await client.ConnectAsync();
        var echo = await ToolAsync(client, "echo");
        var result = await client.CallToolAsync(echo, Sample.Json("""{"message":"hi"}"""));

        Assert.Equal("echo: hi", Assert.Single(result.Content).Text);
        Assert.Equal("2026-07-28", client.Server!.ProtocolVersion);
        Assert.Equal(McpTransportKind.StreamableHttp, client.TransportKind);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Post, request.Method));
        Assert.Equal(["server/discover", "tools/list", "tools/call"], handler.Requests.Select(request => request.RpcMethod));
        foreach (var request in handler.Requests)
        {
            Assert.Equal("2026-07-28", request.Header("MCP-Protocol-Version"));
            Assert.Equal(request.RpcMethod, request.Header("Mcp-Method"));
            Assert.Contains("application/json", request.Header("Accept"), StringComparison.Ordinal);
            Assert.Contains("text/event-stream", request.Header("Accept"), StringComparison.Ordinal);
            Assert.StartsWith("application/json", request.Header("Content-Type"), StringComparison.Ordinal);
            Assert.Null(request.Header("Mcp-Session-Id"));
            Assert.Equal("2026-07-28", (string?)JsonNode.Parse(request.Body)!["params"]!["_meta"]!["io.modelcontextprotocol/protocolVersion"]);
        }

        Assert.Equal("echo", handler.Requests[2].Header("Mcp-Name"));
        Assert.Null(handler.Requests[0].Header("Mcp-Name"));
    }

    [Fact]
    public async Task AnEventStreamAnswerGivesTheResultAfterItsNotifications()
    {
        var core = HttpHarness.Server();
        var handler = new FakeMcpHttpHandler(core, new FakeHttpOptions { Sse = true });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);

        await client.ConnectAsync();
        var echo = await ToolAsync(client, "echo");
        var result = await client.CallToolAsync(echo, Sample.Json("""{"message":"streamed"}"""));

        Assert.Equal("echo: streamed", Assert.Single(result.Content).Text);
    }

    [Fact]
    public async Task ValuesTheServerMarkedAreCopiedIntoHeaders()
    {
        var core = HttpHarness.Server();
        var handler = new FakeMcpHttpHandler(core);
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);
        await client.ConnectAsync();
        var regional = await ToolAsync(client, "regional");

        await client.CallToolAsync(regional, Sample.Json("""{"region":"us-west1","query":"x"}"""));
        await client.CallToolAsync(regional, Sample.Json("""{"region":"Zürich","query":"x"}"""));

        var calls = handler.Requests.Where(request => request.RpcMethod == "tools/call").ToList();
        Assert.Equal("us-west1", calls[0].Header("Mcp-Param-Region"));
        Assert.Equal("=?base64?WsO8cmljaA==?=", calls[1].Header("Mcp-Param-Region"));
    }

    [Fact]
    public async Task AToolWhoseHeaderMarksBreakTheRulesIsLeftOutOverHttp()
    {
        var core = HttpHarness.Server();
        core.Options.ExtraTools.Add(JsonNode.Parse("""{"name":"bad_marks","inputSchema":{"type":"object","properties":{"a":{"type":"number","x-mcp-header":"A"}}}}""")!.AsObject());
        var handler = new FakeMcpHttpHandler(core);
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);
        await client.ConnectAsync();

        var names = (await client.ListToolsAsync()).Select(tool => tool.Name).ToList();

        Assert.DoesNotContain("bad_marks", names);
        Assert.Contains("echo", names);
    }

    [Fact]
    public async Task AnEarlierServerThatAnswersTheModernQuestionWithAnErrorIsGreetedWithInitializeAndKeepsItsSession()
    {
        var core = HttpHarness.Server(FakeMcpEra.Legacy);
        var handler = new FakeMcpHttpHandler(core, new FakeHttpOptions { UseSessions = true });
        var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);

        await client.ConnectAsync();
        var echo = await ToolAsync(client, "echo");
        await client.CallToolAsync(echo, Sample.Json("""{"message":"hi"}"""));
        await client.DisposeAsync();

        Assert.Equal("2025-11-25", client.Server!.ProtocolVersion);
        var posts = handler.Requests.Where(request => request.Method == HttpMethod.Post).ToList();
        Assert.Equal(["server/discover", "initialize", "notifications/initialized", "tools/list", "tools/call"], posts.Select(request => request.RpcMethod));

        // The greeting has no session and no version yet; everything after it carries both.
        Assert.Null(posts[1].Header("Mcp-Session-Id"));
        Assert.Null(posts[1].Header("MCP-Protocol-Version"));
        Assert.All(posts.Skip(2), request =>
        {
            Assert.Equal("sess-1", request.Header("Mcp-Session-Id"));
            Assert.Equal("2025-11-25", request.Header("MCP-Protocol-Version"));
            Assert.Null(request.Header("Mcp-Method"));
        });

        // The session is ended when the client is.
        var end = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);
        Assert.Equal("sess-1", end.Header("Mcp-Session-Id"));
    }

    [Theory]
    [InlineData(ProbeReaction.JsonRpcError)]
    [InlineData(ProbeReaction.Http400Empty)]
    [InlineData(ProbeReaction.Http400Text)]
    [InlineData(ProbeReaction.Http404)]
    [InlineData(ProbeReaction.Http405)]
    public async Task AnEarlierServerIsRecognisedWhateverItAnswersTheModernQuestionWith(ProbeReaction probe)
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(FakeMcpEra.Legacy), new FakeHttpOptions { Probe = probe });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);

        await client.ConnectAsync();

        Assert.Equal("2025-11-25", client.Server!.ProtocolVersion);
        Assert.Equal(McpTransportKind.StreamableHttp, client.TransportKind);
        Assert.NotEmpty(await client.ListToolsAsync());
    }

    [Fact]
    public async Task ASessionTheServerForgotIsAFailureOfItsOwnKind()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(FakeMcpEra.Legacy), new FakeHttpOptions { UseSessions = true });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);
        await client.ConnectAsync();

        handler.ExpireSession();
        var exception = await Assert.ThrowsAsync<McpException>(() => client.ListToolsAsync());

        Assert.Equal(McpFailure.SessionExpired, exception.Failure);
        Assert.Equal(404, exception.HttpStatus);
    }

    [Fact]
    public async Task AServerOfTheDeprecatedTransportIsFoundByItsEventStream()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(FakeMcpEra.Legacy), new FakeHttpOptions { SseOnly = true });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);

        await client.ConnectAsync();
        var echo = await ToolAsync(client, "echo");
        var result = await client.CallToolAsync(echo, Sample.Json("""{"message":"over sse"}"""));

        Assert.Equal(McpTransportKind.LegacySse, client.TransportKind);
        Assert.Equal("echo: over sse", Assert.Single(result.Content).Text);
        Assert.Equal(HttpMethod.Get, handler.Requests.First(request => request.Method == HttpMethod.Get).Method);
        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath == "/messages" && request.RpcMethod == "initialize");
    }

    [Fact]
    public async Task AServerStoredAsTheDeprecatedTransportIsNotPostedTo()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(FakeMcpEra.Legacy), new FakeHttpOptions { SseOnly = true });
        await using var client = HttpHarness.Client(McpTransportKind.LegacySse, handler);

        await client.ConnectAsync();

        Assert.Equal(McpTransportKind.LegacySse, client.TransportKind);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath == "/mcp" && request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task AnEndpointOnAnotherOriginIsRefusedAndNothingIsSentThere()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(FakeMcpEra.Legacy), new FakeHttpOptions { SseOnly = true, EndpointEvent = "https://evil.example.com/steal" });
        await using var client = HttpHarness.Client(McpTransportKind.LegacySse, handler, new Dictionary<string, string> { ["Authorization"] = "Bearer secret-token" });

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.Protocol, exception.Failure);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.Host == "evil.example.com");
    }

    [Theory]
    [InlineData("//evil.example.com/steal")]
    [InlineData("http://mcp.example.com/messages")]
    [InlineData("https://mcp.example.com:8443/messages")]
    [InlineData("https://user:pw@mcp.example.com/messages")]
    public async Task AnEndpointThatIsNotOnTheSameOriginInAnyWayIsRefused(string endpoint)
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(FakeMcpEra.Legacy), new FakeHttpOptions { SseOnly = true, EndpointEvent = endpoint });
        await using var client = HttpHarness.Client(McpTransportKind.LegacySse, handler);

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.Protocol, exception.Failure);
    }

    [Fact]
    public async Task AnEventStreamThatEndsEndsTheConnection()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(FakeMcpEra.Legacy), new FakeHttpOptions { SseOnly = true });
        await using var client = HttpHarness.Client(McpTransportKind.LegacySse, handler);
        await client.ConnectAsync();
        var disconnected = new TaskCompletionSource();
        client.Disconnected += (_, _) => disconnected.TrySetResult();

        handler.EndStream();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(client.IsConnected);
        var exception = await Assert.ThrowsAsync<McpException>(() => client.ListToolsAsync());
        Assert.Equal(McpFailure.Closed, exception.Failure);
    }

    [Fact]
    public async Task ABearerTokenIsSentWithEveryRequestOfEveryKind()
    {
        const string Token = "very-secret-token-value";
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Token };

        var streamable = new FakeMcpHttpHandler(HttpHarness.Server(FakeMcpEra.Legacy), new FakeHttpOptions { BearerToken = Token, UseSessions = true });
        var client = HttpHarness.Client(McpTransportKind.StreamableHttp, streamable, headers);
        await client.ConnectAsync();
        await client.ListToolsAsync();
        await client.DisposeAsync();
        Assert.NotEmpty(streamable.Requests);
        Assert.All(streamable.Requests, request => Assert.Equal("Bearer " + Token, request.Header("Authorization")));
        Assert.Contains(streamable.Requests, request => request.Method == HttpMethod.Delete);

        var sse = new FakeMcpHttpHandler(HttpHarness.Server(FakeMcpEra.Legacy), new FakeHttpOptions { BearerToken = Token, SseOnly = true });
        await using var other = HttpHarness.Client(McpTransportKind.LegacySse, sse, headers);
        await other.ConnectAsync();
        await other.ListToolsAsync();
        Assert.All(sse.Requests, request => Assert.Equal("Bearer " + Token, request.Header("Authorization")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AServerThatWantsASignInIsAFailureThatSaysWhetherItOffersOne(bool offersSignIn)
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(), new FakeHttpOptions { BearerToken = "needed", OfferSignIn = offersSignIn });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.AuthRequired, exception.Failure);
        Assert.Equal(401, exception.HttpStatus);
        Assert.Equal(offersSignIn, exception.OffersSignIn);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ATokenThatIsRefusedIsAFailureThatDoesNotRepeatIt()
    {
        const string Token = "wrong-token-xyz";
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(), new FakeHttpOptions { BearerToken = "right" });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler, new Dictionary<string, string> { ["Authorization"] = "Bearer " + Token });

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.DoesNotContain(Token, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerThatRefusesTheAssistantIsAFailureOfItsOwnKind()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(), new FakeHttpOptions { ForceStatus = 403 });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);
        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());
        Assert.Equal(McpFailure.Forbidden, exception.Failure);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    public async Task AServerErrorIsAnHttpFailureWithItsStatus(int status)
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(), new FakeHttpOptions { ForceStatus = status });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);
        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());
        Assert.Equal(McpFailure.HttpError, exception.Failure);
        Assert.Equal(status, exception.HttpStatus);
    }

    [Fact]
    public async Task AnAddressThatNothingAnswersAtIsAnHttpFailureAfterEveryWayOfAskingWasTried()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(), new FakeHttpOptions { ForceStatus = 404 });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.HttpError, exception.Failure);
        Assert.Equal(404, exception.HttpStatus);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task ARedirectIsAFailureAndIsNotFollowed()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(), new FakeHttpOptions { RedirectTo = "https://evil.example.com/mcp" });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler, new Dictionary<string, string> { ["Authorization"] = "Bearer secret" });

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.HttpError, exception.Failure);
        Assert.Equal(307, exception.HttpStatus);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.Host == "evil.example.com");
    }

    [Fact]
    public void TheHandlerTheAppUsesFollowsNoRedirectAndKeepsNoCookies()
    {
        using var handler = (SocketsHttpHandler)McpHttp.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task ABodyLargerThanTheLimitIsRefused()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server());
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler, options: new McpClientOptions { MaxMessageBytes = 32 * 1024 });
        await client.ConnectAsync();
        var big = await ToolAsync(client, "big");

        var exception = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(big, Sample.Json("{}")));

        Assert.Equal(McpFailure.TooLarge, exception.Failure);
    }

    [Fact]
    public async Task AnEventLargerThanTheLimitIsRefused()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(), new FakeHttpOptions { Sse = true });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler, options: new McpClientOptions { MaxMessageBytes = 32 * 1024 });
        await client.ConnectAsync();
        var big = await ToolAsync(client, "big");

        var exception = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(big, Sample.Json("{}")));

        Assert.Equal(McpFailure.TooLarge, exception.Failure);
    }

    [Fact]
    public async Task AMessageToSendThatIsTooLargeIsNotSent()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server());
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler, options: new McpClientOptions { MaxMessageBytes = 4096 });
        await client.ConnectAsync();
        var echo = await ToolAsync(client, "echo");

        var exception = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(echo, Sample.Json("{\"message\":\"" + new string('x', 10_000) + "\"}")));

        Assert.Equal(McpFailure.TooLarge, exception.Failure);
        Assert.DoesNotContain(handler.Requests, request => request.Body.Length > 4096);
    }

    [Fact]
    public async Task ANetworkThatIsDownIsAConnectionFailure()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(), new FakeHttpOptions { Throw = () => new HttpRequestException("network down: secret.example.com") });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.ConnectFailed, exception.Failure);
        Assert.DoesNotContain("secret.example.com", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyThatIsNotJsonRpcIsAProtocolFailure()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(), new FakeHttpOptions { RawBody = "<html>not mcp</html>" });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);
        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());
        Assert.Equal(McpFailure.Protocol, exception.Failure);
    }

    [Fact]
    public async Task AServersOwnPingOnTheStreamIsAnsweredAndNothingElseIsServed()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(FakeMcpEra.Legacy), new FakeHttpOptions { Sse = true, PingOnStream = true });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);

        await client.ConnectAsync();

        // Both answers that came on a stream (the modern question's and the greeting's) carried a ping, and each is answered with a POST.
        var answers = handler.Requests.Where(request => request.Body.Contains("\"srv-1\"", StringComparison.Ordinal) && request.Body.Contains("\"result\"", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, answers.Count);
        Assert.All(answers, answer => Assert.Equal(HttpMethod.Post, answer.Method));
    }

    [Fact]
    public async Task ACallThatIsCancelledEndsAtOnce()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server());
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);
        await client.ConnectAsync();
        var echo = await ToolAsync(client, "echo");
        handler.Options.Delay = TimeSpan.FromSeconds(30);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CallToolAsync(echo, Sample.Json("""{"message":"x"}"""), cancel.Token));

        // Over modern HTTP, closing the response is the notice: no cancellation message is posted.
        Assert.DoesNotContain(handler.Requests, request => request.RpcMethod == "notifications/cancelled");
    }

    [Fact]
    public async Task ACallThatTakesTooLongFailsWithATimeout()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server());
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler, options: new McpClientOptions { CallTimeout = TimeSpan.FromMilliseconds(250) });
        await client.ConnectAsync();
        var echo = await ToolAsync(client, "echo");
        handler.Options.Delay = TimeSpan.FromSeconds(30);

        var exception = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(echo, Sample.Json("""{"message":"x"}""")));

        Assert.Equal(McpFailure.TimedOut, exception.Failure);
    }

    [Fact]
    public async Task ACallThatIsCancelledOverAnEarlierProtocolPostsTheNotice()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(FakeMcpEra.Legacy));
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);
        await client.ConnectAsync();
        var echo = await ToolAsync(client, "echo");
        handler.Options.Delay = TimeSpan.FromSeconds(30);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CallToolAsync(echo, Sample.Json("""{"message":"x"}"""), cancel.Token));

        var until = DateTime.UtcNow.AddSeconds(10);
        while (!handler.Requests.Any(request => request.RpcMethod == "notifications/cancelled") && DateTime.UtcNow < until)
        {
            await Task.Delay(25);
        }

        Assert.Contains(handler.Requests, request => request.RpcMethod == "notifications/cancelled");
    }

    [Fact]
    public async Task APaginatedListIsReadOverHttp()
    {
        var core = new FakeMcpServerCore(new FakeMcpOptions { Era = FakeMcpEra.Modern, PageSize = 3 });
        var handler = new FakeMcpHttpHandler(core);
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);
        await client.ConnectAsync();

        var tools = await client.ListToolsAsync();

        Assert.True(tools.Count > 3);
        Assert.True(handler.Requests.Count(request => request.RpcMethod == "tools/list") > 1);
    }

    [Fact]
    public async Task AToolsListChangedNoticeOnAResponseStreamIsPassedOn()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server(), new FakeHttpOptions { Sse = true, ListChangedOnStream = true });
        await using var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);
        var changed = new TaskCompletionSource();
        client.ToolsChanged += (_, _) => changed.TrySetResult();
        await client.ConnectAsync();

        await client.ListToolsAsync();

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ACallAfterDisposalFails()
    {
        var handler = new FakeMcpHttpHandler(HttpHarness.Server());
        var client = HttpHarness.Client(McpTransportKind.StreamableHttp, handler);
        await client.ConnectAsync();
        await client.DisposeAsync();

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ListToolsAsync());
        Assert.Equal(McpFailure.Closed, exception.Failure);
        Assert.False(client.IsConnected);
    }
}
