using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Tools;
using Assistant.Tools.Calculator;
using Assistant.Tools.FakeMcpServer;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Mcp;

/// <summary>The factory: the secrets an integration names are read from the secret store as it connects and go where they belong.</summary>
public sealed class McpClientFactoryTests
{
    [Fact]
    public async Task TavilyKeylessModeIsSentOnEveryHttpRequestWithoutReadingASecret()
    {
        var handler = Server();
        var integration = Sample.Remote() with { Transport = new() { Endpoint = "https://mcp.tavily.com/mcp/", Headers = new Dictionary<string, string> { ["X-Tavily-Access-Mode"] = "keyless" } } };
        await using var client = Factory(null, handler).Create(integration);
        await client.ConnectAsync();
        await client.ListToolsAsync();
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, request => { Assert.Equal("keyless", request.Header("X-Tavily-Access-Mode")); Assert.Null(request.Header("Authorization")); });
    }

    [Theory]
    [InlineData("Authorization", "Bearer secret")]
    [InlineData("X-Api-Key", "secret")]
    [InlineData("X-Tavily-Access-Mode", "keyless\r\nX-Api-Key: secret")]
    public void PublicHeadersCannotCarryCredentialsOrInjectHeaders(string name, string value)
    {
        var integration = Sample.Remote() with { Transport = new() { Endpoint = "https://example.com/mcp", Headers = new Dictionary<string, string> { [name] = value } } };
        Assert.Throws<McpException>(() => Factory(null, Server()).Create(integration));
    }
    private static InstalledIntegration WithAuth(IntegrationAuthKind kind, params IntegrationSecretBinding[] secrets) =>
        Sample.Remote() with { Authentication = new IntegrationAuthentication { Kind = kind, State = IntegrationAuthState.Unknown, Secrets = secrets } };

    private static McpClientFactory Factory(FakeSecretStore? secrets, FakeMcpHttpHandler handler) => new(secrets, null, () => handler);

    private static FakeMcpHttpHandler Server(string? token = null, FakeMcpEra era = FakeMcpEra.Modern, bool sessions = false) =>
        new(new FakeMcpServerCore(new FakeMcpOptions { Era = era }), new FakeHttpOptions { BearerToken = token, UseSessions = sessions });

    [Fact]
    public async Task ABearerTokenIsReadFromTheSecretStoreAndSentInTheAuthorizationHeader()
    {
        var secrets = new FakeSecretStore();
        secrets.Secrets["mcp.todoist.token"] = "tok-123";
        var handler = Server("tok-123");
        await using var client = Factory(secrets, handler).Create(WithAuth(IntegrationAuthKind.BearerToken, new IntegrationSecretBinding("Authorization", "mcp.todoist.token")));

        await client.ConnectAsync();

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer tok-123", request.Header("Authorization")));
    }

    [Fact]
    public async Task AKeyInAHeaderGoesInTheHeaderItIsBoundTo()
    {
        var secrets = new FakeSecretStore();
        secrets.Secrets["mcp.todoist.key"] = "key-456";
        var handler = Server();
        await using var client = Factory(secrets, handler).Create(WithAuth(IntegrationAuthKind.HeaderKey, new IntegrationSecretBinding("X-Api-Key", "mcp.todoist.key")));

        await client.ConnectAsync();

        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("key-456", request.Header("X-Api-Key"));
            Assert.Null(request.Header("Authorization"));
        });
    }

    [Fact]
    public async Task ASecretThatIsNotInTheStoreMeansTheIntegrationNeedsASignInAndNothingIsSent()
    {
        var handler = Server();
        await using var client = Factory(new FakeSecretStore(), handler).Create(WithAuth(IntegrationAuthKind.BearerToken, new IntegrationSecretBinding("Authorization", "mcp.todoist.token")));

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.AuthRequired, exception.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ASecretStoreThatCannotBeReadMeansTheSameAndSaysNothingOfTheSecret()
    {
        var handler = Server();
        await using var client = Factory(new FakeSecretStore { Fails = true }, handler).Create(WithAuth(IntegrationAuthKind.BearerToken, new IntegrationSecretBinding("Authorization", "mcp.todoist.token")));

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.AuthRequired, exception.Failure);
        Assert.DoesNotContain("mcp.todoist.token", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoSecretStoreAtAllAnIntegrationThatNeedsOneCannotBeSignedIn()
    {
        var handler = Server();
        await using var client = Factory(null, handler).Create(WithAuth(IntegrationAuthKind.BearerToken, new IntegrationSecretBinding("Authorization", "mcp.todoist.token")));

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.AuthRequired, exception.Failure);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("tok\r\nX-Injected: yes")]
    [InlineData("tok\nen")]
    [InlineData("tok\0en")]
    public async Task ASecretThatCouldInjectAHeaderIsNeverSent(string secret)
    {
        var secrets = new FakeSecretStore();
        secrets.Secrets["mcp.todoist.token"] = secret;
        var handler = Server();
        await using var client = Factory(secrets, handler).Create(WithAuth(IntegrationAuthKind.BearerToken, new IntegrationSecretBinding("Authorization", "mcp.todoist.token")));

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.NotConfigured, exception.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnIntegrationThatWillSignInInTheBrowserIsConnectedToWithoutACredentialAndSaysItNeedsOne()
    {
        var handler = Server(token: "needed");
        await using var client = Factory(new FakeSecretStore(), handler).Create(WithAuth(IntegrationAuthKind.OAuth));

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.AuthRequired, exception.Failure);
        Assert.True(exception.OffersSignIn);
        Assert.All(handler.Requests, request => Assert.Null(request.Header("Authorization")));
    }

    [Fact]
    public void ARecordThatBreaksARuleIsNeverConnectedTo()
    {
        var factory = new McpClientFactory();

        var shell = Assert.Throws<McpException>(() => factory.Create(Sample.Program(command: @"C:\Windows\System32\cmd.exe")));
        var insecure = Assert.Throws<McpException>(() => factory.Create(Sample.Remote(endpoint: "http://mcp.example.com/mcp")));

        Assert.Equal(McpFailure.NotConfigured, shell.Failure);
        Assert.Equal(McpFailure.NotConfigured, insecure.Failure);
    }

    [Fact]
    public async Task AVersionRememberedFromTheLastConnectionSavesAskingTheModernQuestion()
    {
        var handler = Server(era: FakeMcpEra.Legacy);
        var integration = Sample.Remote() with { Capabilities = new IntegrationCapabilities { ProtocolVersion = "2025-06-18" } };
        await using var client = Factory(null, handler).Create(integration);

        await client.ConnectAsync();

        Assert.DoesNotContain(handler.Requests, request => request.RpcMethod == "server/discover");
        Assert.Equal("initialize", handler.Requests[0].RpcMethod);
    }

    [Fact]
    public async Task ASecretForAProgramIsGivenAsAnEnvironmentVariableAndNeverAsAnArgument()
    {
        using var program = new FakeServerProgram();
        var secrets = new FakeSecretStore();
        secrets.Secrets["mcp.local.key"] = "environment-secret-value";
        var integration = Sample.Program("local", "Local App", FakeServerProgram.ExePath, "--era", "modern") with
        {
            Authentication = new IntegrationAuthentication
            {
                Kind = IntegrationAuthKind.EnvironmentSecret,
                Secrets = [new IntegrationSecretBinding("LOCAL_APP_API_KEY", "mcp.local.key")],
            },
        };
        await using var client = new McpClientFactory(secrets).Create(integration);
        await client.ConnectAsync();
        var env = (await client.ListToolsAsync()).Single(tool => tool.Name == "env");

        var names = (await client.CallToolAsync(env, Sample.Json("{}"))).Content[0].Text!.Split(',');

        Assert.Contains("LOCAL_APP_API_KEY", names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(integration.Transport.Arguments, argument => argument.Contains("environment-secret-value", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLaunchDescriptionNeverPrintsWhatItHolds()
    {
        var launch = new McpLaunch(@"C:\Tools\server.exe", ["--token", "super-secret"], @"C:\Tools", new Dictionary<string, string> { ["KEY"] = "super-secret" });
        Assert.DoesNotContain("super-secret", launch.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Tools", launch.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>The whole stack with a real MCP server program, in both eras: what the model is offered, what is called and what comes back.</summary>
public sealed class ConnectedAppEndToEndTests : IDisposable
{
    private readonly FakeServerProgram _server = new();

    public void Dispose() => _server.Dispose();

    private sealed class Stack : IAsyncDisposable
    {
        public Stack(InstalledIntegration integration, IInstalledIntegrationStore? store = null, bool confirm = true)
        {
            Store = store ?? new MemoryIntegrationStore(integration);
            Registry = new InstalledIntegrationRegistry(Store, NullLogger<InstalledIntegrationRegistry>.Instance);
            var options = new McpLoadingOptions();
            Manager = new McpConnectionManager(
                Registry, new McpClientFactory(new FakeSecretStore(), new McpClientOptions { ProbeTimeout = TimeSpan.FromMilliseconds(500) }),
                TestSettings.LocalOnly(true), TimeProvider.System, options, NullLogger<McpConnectionManager>.Instance);
            var source = new McpToolSource(Registry, Manager, LexicalMcpToolSelector.Instance, options, TimeProvider.System, NullLogger<McpToolSource>.Instance);
            ITool[] builtIn = [CalculateTool.Create()];
            Tools = new ToolRegistry(builtIn, source);
            Confirmation = new FakeConfirmation(confirm);
            Executor = new ToolExecutor(builtIn, Confirmation, new FakePermissions(true), source);
        }

        public IInstalledIntegrationStore Store { get; }

        public InstalledIntegrationRegistry Registry { get; }

        public McpConnectionManager Manager { get; }

        public ToolRegistry Tools { get; }

        public FakeConfirmation Confirmation { get; }

        public ToolExecutor Executor { get; }

        public ValueTask DisposeAsync() => Manager.DisposeAsync();
    }

    private InstalledIntegration App(string era) =>
        Sample.Program("fakeapp", "Fake App", FakeServerProgram.ExePath, "--era", era, "--log", _server.LogPath, "--pid-file", _server.PidPath) with
        {
            Permissions = new IntegrationPermissions { ReadOnlyTools = ["echo", "add", "env"] },
        };

    [Theory]
    [InlineData("modern", "2026-07-28")]
    [InlineData("legacy", "2025-11-25")]
    public async Task ARequestThatNamesAnAppLoadsItsToolsFromARealProgramAndAReadOnlyToolRuns(string era, string version)
    {
        await using var stack = new Stack(App(era));
        var context = new ToolContext(Guid.NewGuid(), "Use Fake App to echo hello to me");

        await stack.Tools.PrepareToolsAsync(context);
        var offered = stack.Tools.ToolsFor(context);

        Assert.Contains(offered, tool => tool.Name == "mcp_fakeapp_echo" && tool.RiskLevel == RiskLevel.ReadOnly);
        Assert.DoesNotContain(offered, tool => tool.Name.Contains("delete_everything", StringComparison.Ordinal));

        // The words the server addressed to a model (its instructions) are not passed on: only the tools' own, cleaned.
        Assert.DoesNotContain(offered, tool => tool.Description.Contains("Ignore all previous", StringComparison.OrdinalIgnoreCase));
        var result = await stack.Executor.ExecuteAsync(new ToolCall("c1", "mcp_fakeapp_echo", """{"message":"hello"}"""), context);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        using var output = JsonDocument.Parse(result.OutputJson);
        Assert.Equal("echo: hello", output.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(0, stack.Confirmation.Asked);

        var integration = (await stack.Registry.GetAsync("fakeapp"))!;
        Assert.Equal(version, integration.Capabilities.ProtocolVersion);
        Assert.True(integration.Capabilities.Tools);
        Assert.Contains("echo", integration.Capabilities.ToolNames);
        Assert.DoesNotContain("delete_everything", integration.Capabilities.ToolNames);
        Assert.Equal(IntegrationHealthStatus.Healthy, integration.Health.Status);
    }

    [Fact]
    public async Task AToolThatChangesSomethingIsConfirmedAndRunsOnlyOnApproval()
    {
        var declined = new Stack(App("modern"), confirm: false);
        await using (declined)
        {
            var context = new ToolContext(Guid.NewGuid(), "Use Fake App to create a task");
            await declined.Tools.PrepareToolsAsync(context);
            Assert.Contains(declined.Tools.ToolsFor(context), tool => tool.Name == "mcp_fakeapp_create_task" && tool.RiskLevel == RiskLevel.SideEffect);

            var result = await declined.Executor.ExecuteAsync(new ToolCall("c1", "mcp_fakeapp_create_task", """{"task_title":"buy milk"}"""), context);

            Assert.Equal(ToolResultStatus.Declined, result.Status);
        }

        using var approvedProgram = new FakeServerProgram();
        var app = Sample.Program("fakeapp", "Fake App", FakeServerProgram.ExePath, "--era", "modern", "--log", approvedProgram.LogPath);
        await using var approved = new Stack(app, confirm: true);
        var second = new ToolContext(Guid.NewGuid(), "Use Fake App to create a task");
        await approved.Tools.PrepareToolsAsync(second);

        var done = await approved.Executor.ExecuteAsync(new ToolCall("c2", "mcp_fakeapp_create_task", """{"task_title":"buy milk","due_date":"tomorrow"}"""), second);

        Assert.Equal(ToolResultStatus.Succeeded, done.Status);
        using var output = JsonDocument.Parse(done.OutputJson);
        Assert.Equal("Created: buy milk due tomorrow", output.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(1, approved.Confirmation.Asked);
    }

    [Fact]
    public async Task TheAgentLoopRunsABuiltInToolAndAToolOfARealProgramInOneAnswer()
    {
        await using var stack = new Stack(App("modern"));
        var model = new AgentLoopConnectedAppsTests.ScriptedModel(
            [
                AgentLoopConnectedAppsTests.Call("calculate", """{"expression":"2+2"}""", "c1"),
                AgentLoopConnectedAppsTests.Call("mcp_fakeapp_echo", """{"message":"hello"}""", "c2"),
            ],
            [AgentLoopConnectedAppsTests.Words("Four, and the app said hello.")]);
        var traces = new Assistant.Core.Agent.AgentTraceStore();
        var clock = new AgentLoopConnectedAppsTests.Clock(AgentLoopConnectedAppsTests.Start);
        var orchestrator = new AssistantOrchestrator(
            model, new FixedSettings(), new PromptBuilder(), new AgentLoopConnectedAppsTests.NoImages(), clock,
            NullLogger<AssistantOrchestrator>.Instance, toolRegistry: stack.Tools, toolExecutor: stack.Executor, traceSink: traces);
        var session = ConversationSession.Start(clock);

        var results = new List<ToolResult>();
        await foreach (var chunk in orchestrator.AskAsync(session, "Use Fake App to echo hello to me, and what is 2+2"))
        {
            if (chunk.ToolResult is { } result)
            {
                results.Add(result);
            }
        }

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Status == ToolResultStatus.Succeeded, result.OutputJson));
        using var echoed = JsonDocument.Parse(results[1].OutputJson);
        Assert.Equal("echo: hello", echoed.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("Four, and the app said hello.", session.Conversation.Messages[^1].Text);
        var trace = Assert.Single(traces.Recent());
        Assert.Equal(2, trace.ToolsRun);
        Assert.True(File.Exists(_server.PidPath), "The program was not started for a request about it.");
    }

    [Fact]
    public async Task ARequestThatIsNotAboutTheAppNeverStartsItsProgram()
    {
        await using var stack = new Stack(App("modern"));

        await stack.Tools.PrepareToolsAsync(new ToolContext(Guid.NewGuid(), "What is two plus two?"));

        Assert.False(File.Exists(_server.PidPath), "The program was started for a request that has nothing to do with it.");
    }

    [Fact]
    public async Task DisposingTheManagerEndsTheProgram()
    {
        var stack = new Stack(App("modern"));
        var context = new ToolContext(Guid.NewGuid(), "Use Fake App to echo");
        await stack.Tools.PrepareToolsAsync(context);
        var process = Process.GetProcessById(_server.ReadPid());
        Assert.False(process.HasExited);

        await stack.DisposeAsync();

        Assert.True(process.WaitForExit(10_000), "The program was left running.");
        process.Dispose();
    }

    [Fact]
    public async Task AnAppConnectedToBeforeAsTheEarlierProtocolIsNotAskedTheModernQuestionAgain()
    {
        var store = new MemoryIntegrationStore(App("silent"));
        await using (var first = new Stack(App("silent"), store))
        {
            await first.Tools.PrepareToolsAsync(new ToolContext(Guid.NewGuid(), "Use Fake App to echo"));
            Assert.Equal("2025-11-25", (await first.Registry.GetAsync("fakeapp"))!.Capabilities.ProtocolVersion);
        }

        var discoversBefore = _server.Methods.Count(method => method == "server/discover");
        await using var second = new Stack(App("silent"), store);
        var started = Stopwatch.StartNew();
        await second.Tools.PrepareToolsAsync(new ToolContext(Guid.NewGuid(), "Use Fake App to echo"));

        Assert.Equal(discoversBefore, _server.Methods.Count(method => method == "server/discover"));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheProgramOfAnAppNeverSeesTheAssistantsEnvironment()
    {
        const string Leaked = "ASSISTANT_E2E_LEAKED_SECRET";
        Environment.SetEnvironmentVariable(Leaked, "do-not-pass-me-on");
        try
        {
            await using var stack = new Stack(App("modern"));
            var context = new ToolContext(Guid.NewGuid(), "Use Fake App to show the env");
            await stack.Tools.PrepareToolsAsync(context);

            var result = await stack.Executor.ExecuteAsync(new ToolCall("c1", "mcp_fakeapp_env", "{}"), context);

            Assert.Equal(ToolResultStatus.Succeeded, result.Status);
            Assert.DoesNotContain(Leaked, result.OutputJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SystemRoot", result.OutputJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Leaked, null);
        }
    }
}

/// <summary>An MCP server on a real loopback socket, so that the app's own HTTP handler is the one that talks to it.</summary>
public sealed class McpLoopbackHttpTests
{
    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly FakeMcpServerCore _core;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public LoopbackServer(FakeMcpServerCore core)
        {
            _core = core;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Address = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Address);
            _listener.Start();
            _serving = Task.Run(ServeAsync);
        }

        public string Address { get; }

        public List<string> Paths { get; } = [];

        public List<(string Path, string? Authorization, string? ProtocolVersion)> Seen { get; } = [];

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                var request = context.Request;
                lock (Paths)
                {
                    Paths.Add(request.Url!.AbsolutePath);
                    Seen.Add((request.Url.AbsolutePath, request.Headers["Authorization"], request.Headers["MCP-Protocol-Version"]));
                }

                if (request.Url.AbsolutePath == "/redirect")
                {
                    context.Response.StatusCode = 307;
                    context.Response.RedirectLocation = Address + "mcp";
                    context.Response.Close();
                    continue;
                }

                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                var answer = await _core.HandleAsync(body).ConfigureAwait(false);
                if (answer is null)
                {
                    context.Response.StatusCode = 202;
                }
                else
                {
                    context.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                }

                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            try
            {
                _serving.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }
    }

    [Fact]
    public async Task AnAppsOwnHandlerSpeaksToAServerOnARealLoopbackSocket()
    {
        using var server = new LoopbackServer(new FakeMcpServerCore(new FakeMcpOptions { Era = FakeMcpEra.Modern }));
        var integration = Sample.Loopback("loop", "Loop", server.Address + "mcp");
        await using var client = new McpClientFactory().Create(integration);

        await client.ConnectAsync();
        var echo = (await client.ListToolsAsync()).Single(tool => tool.Name == "echo");
        var result = await client.CallToolAsync(echo, Sample.Json("""{"message":"over a socket"}"""));

        Assert.Equal("echo: over a socket", Assert.Single(result.Content).Text);
        Assert.Equal("2026-07-28", client.Server!.ProtocolVersion);
        Assert.All(server.Seen, seen => Assert.Equal("2026-07-28", seen.ProtocolVersion));
    }

    [Fact]
    public async Task ARedirectIsNotFollowedByTheRealHandlerAndTheSignInIsNotSentElsewhere()
    {
        using var server = new LoopbackServer(new FakeMcpServerCore(new FakeMcpOptions { Era = FakeMcpEra.Modern }));
        var secrets = new FakeSecretStore();
        secrets.Secrets["mcp.loop.token"] = "tok-loop";
        var integration = Sample.Loopback("loop", "Loop", server.Address + "redirect") with
        {
            Authentication = new IntegrationAuthentication
            {
                Kind = IntegrationAuthKind.BearerToken,
                Secrets = [new IntegrationSecretBinding("Authorization", "mcp.loop.token")],
            },
        };
        await using var client = new McpClientFactory(secrets).Create(integration);

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.HttpError, exception.Failure);
        Assert.Equal(307, exception.HttpStatus);
        lock (server.Paths)
        {
            Assert.DoesNotContain("/mcp", server.Paths);
        }
    }

    [Fact]
    public async Task NothingListeningAtTheAddressIsAConnectionFailure()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await using var client = new McpClientFactory().Create(Sample.Loopback("loop", "Loop", $"http://127.0.0.1:{port}/mcp"));

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.ConnectFailed, exception.Failure);
    }
}
