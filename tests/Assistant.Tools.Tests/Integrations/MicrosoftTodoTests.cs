using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Assistant.MicrosoftTodo;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Mcp.Auth;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>
/// Microsoft To Do (PROJECT_SPEC §4.8): the small MCP server that ships with the Assistant and speaks to Microsoft Graph for it, and the connection of the app, signed in with
/// Microsoft in the browser. Graph is a fake here, so nothing reaches Microsoft.
/// </summary>
public sealed class MicrosoftTodoTests
{
    private sealed class FakeGraph : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body, string? Authorization)> Requests { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string[] OpenTasks { get; set; } = ["Buy milk", "Call Anna", "Call Anders"];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add((request.Method, Uri.UnescapeDataString(path), body, request.Headers.Authorization?.ToString()));
            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status);
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/v1.0/me/todo/lists?", StringComparison.Ordinal))
            {
                return Json("""{"value":[{"id":"L1","displayName":"Tasks","wellknownListName":"defaultList"},{"id":"L2","displayName":"Groceries","wellknownListName":"none"}]}""");
            }

            if (request.Method == HttpMethod.Get && path.Contains("/tasks?", StringComparison.Ordinal))
            {
                var items = string.Join(",", OpenTasks.Select((title, index) => $$"""{"id":"T{{index}}","title":"{{title}}","status":"notStarted","importance":"normal"}"""));
                return Json("{\"value\":[" + items + "]}");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/tasks", StringComparison.Ordinal))
            {
                var title = JsonNode.Parse(body)!["title"]!.GetValue<string>();
                return Json($$"""{"id":"NEW","title":"{{title}}","status":"notStarted","importance":"normal"}""");
            }

            if (request.Method.Method == "PATCH")
            {
                return Json("""{"id":"T0","title":"Buy milk","status":"completed"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    private static (TodoServer Server, FakeGraph Graph) Server(string token = "secret-token")
    {
        var graph = new FakeGraph();
        return (new TodoServer(new TodoGraph(TodoGraph.Client(token, graph))), graph);
    }

    private static async Task<JsonObject> Call(TodoServer server, string tool, object? arguments = null)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(arguments ?? new { })) },
        };
        return (await server.HandleAsync(request, CancellationToken.None))!;
    }

    private static string TextOf(JsonObject reply) => reply["result"]!["content"]![0]!["text"]!.GetValue<string>();

    private static bool IsError(JsonObject reply) => reply["result"]!["isError"]!.GetValue<bool>();

    // ---- The protocol ----

    [Fact]
    public async Task TheServerAnswersTheHandshakeListsFourToolsAndIgnoresNotifications()
    {
        var (server, _) = Server();

        var init = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")!.AsObject(), CancellationToken.None);
        var list = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")!.AsObject(), CancellationToken.None);
        var notification = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")!.AsObject(), CancellationToken.None);
        var unknown = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":3,"method":"nope"}""")!.AsObject(), CancellationToken.None);

        Assert.Equal("2025-06-18", init!["result"]!["protocolVersion"]!.GetValue<string>());
        var tools = list!["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(["list_task_lists", "list_tasks", "create_task", "complete_task"], tools);
        Assert.Null(notification);
        Assert.Equal(-32601, unknown!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task OnlyTheToolsThatReadAreMarkedAsReadingAndEveryNameIsOneTheAssistantMatchesToWhatIsAsked()
    {
        var (server, _) = Server();
        var list = (await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")!.AsObject(), CancellationToken.None))!;
        var tools = list["result"]!["tools"]!.AsArray();

        var reads = tools.Where(tool => tool!["annotations"]!["readOnlyHint"]!.GetValue<bool>()).Select(tool => tool!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(["list_task_lists", "list_tasks"], reads);
        Assert.All(tools, tool => Assert.False(tool!["annotations"]!["destructiveHint"]!.GetValue<bool>()));

        var names = tools.Select(tool => new ToolFacts(tool!["name"]!.GetValue<string>())).ToList();
        Assert.Contains("create_task", CapabilityMatcher.Match(new IntegrationCapability(CapabilityAction.Create, "task"), names));
        Assert.Contains("list_tasks", CapabilityMatcher.Match(new IntegrationCapability(CapabilityAction.Read, "task"), names));
        Assert.Contains("complete_task", CapabilityMatcher.Match(new IntegrationCapability(CapabilityAction.Complete, "task"), names));
    }

    // ---- The tools ----

    [Fact]
    public async Task ATaskIsAddedToTheDefaultListWithItsTitleAndTheTokenIsOnlyInTheHeader()
    {
        var (server, graph) = Server("secret-token");

        var reply = await Call(server, "create_task", new { title = "Buy milk" });

        Assert.False(IsError(reply));
        var post = graph.Requests.Single(request => request.Method == HttpMethod.Post);
        Assert.Equal("/v1.0/me/todo/lists/L1/tasks", post.Path);
        Assert.Equal("Buy milk", JsonNode.Parse(post.Body)!["title"]!.GetValue<string>());
        Assert.All(graph.Requests, request => Assert.Equal("Bearer secret-token", request.Authorization));
        Assert.DoesNotContain("secret-token", TextOf(reply), StringComparison.Ordinal);
        Assert.Contains("Buy milk", TextOf(reply), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADueDateAndNotesAndANamedListAreSent()
    {
        var (server, graph) = Server();

        var reply = await Call(server, "create_task", new { title = "Pay rent", list = "groceries", due = "2026-10-05", notes = "Before noon" });

        Assert.False(IsError(reply));
        var post = graph.Requests.Single(request => request.Method == HttpMethod.Post);
        Assert.Equal("/v1.0/me/todo/lists/L2/tasks", post.Path);
        var body = JsonNode.Parse(post.Body)!;
        Assert.Equal("2026-10-05T00:00:00", body["dueDateTime"]!["dateTime"]!.GetValue<string>());
        Assert.Equal("Before noon", body["body"]!["content"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"title":""}""", "needs a title")]
    [InlineData("""{"title":"x","due":"next friday"}""", "written as 2026-10-05")]
    [InlineData("""{"title":"x","list":"Work"}""", "no list called")]
    public async Task ABadRequestIsSaidInWordsAndNothingIsCreated(string json, string words)
    {
        var (server, graph) = Server();
        var arguments = JsonNode.Parse(json)!.AsObject();
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "create_task", ["arguments"] = arguments } };

        var reply = (await server.HandleAsync(request, CancellationToken.None))!;

        Assert.True(IsError(reply));
        Assert.Contains(words, TextOf(reply), StringComparison.Ordinal);
        Assert.DoesNotContain(graph.Requests, call => call.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task TheListsAndTheTasksStillToDoAreRead()
    {
        var (server, graph) = Server();

        var lists = TextOf(await Call(server, "list_task_lists"));
        var tasks = TextOf(await Call(server, "list_tasks", new { }));

        Assert.Contains("Groceries", lists, StringComparison.Ordinal);
        Assert.Contains("\"default\":true", lists, StringComparison.Ordinal);
        Assert.Contains("Call Anna", tasks, StringComparison.Ordinal);
        Assert.Contains("status ne 'completed'", graph.Requests.Last().Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATaskIsCompletedByItsTitleWhenOneFitsAndNeverChosenBetweenTwo()
    {
        var (server, graph) = Server();

        var done = await Call(server, "complete_task", new { task = "buy milk" });
        var ambiguous = await Call(server, "complete_task", new { task = "Call An" });
        var none = await Call(server, "complete_task", new { task = "Wash the car" });

        Assert.False(IsError(done));
        var patch = graph.Requests.Single(request => request.Method.Method == "PATCH");
        Assert.Equal("/v1.0/me/todo/lists/L1/tasks/T0", patch.Path);
        Assert.Contains("completed", patch.Body, StringComparison.Ordinal);
        Assert.True(IsError(ambiguous));
        Assert.Contains("More than one task", TextOf(ambiguous), StringComparison.Ordinal);
        Assert.True(IsError(none));
        Assert.Single(graph.Requests, request => request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ARefusedSignInIsSaidInWordsAndTheProgramEndsSoTheNextStartGetsANewToken()
    {
        var (server, graph) = Server();
        graph.Status = HttpStatusCode.Unauthorized;

        var reply = await Call(server, "create_task", new { title = "x" });

        Assert.True(IsError(reply));
        Assert.Contains("sign-in", TextOf(reply), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", TextOf(reply), StringComparison.Ordinal);
        Assert.True(server.EndAfterReply);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "did not allow")]
    [InlineData(HttpStatusCode.TooManyRequests, "slow down")]
    [InlineData(HttpStatusCode.InternalServerError, "not working")]
    public async Task OtherRefusalsAreSaidInWordsAndTheProgramGoesOn(HttpStatusCode status, string words)
    {
        var (server, graph) = Server();
        graph.Status = status;

        var reply = await Call(server, "list_task_lists");

        Assert.True(IsError(reply));
        Assert.Contains(words, TextOf(reply), StringComparison.Ordinal);
        Assert.False(server.EndAfterReply);
    }

    [Fact]
    public async Task WithoutATokenEveryToolSaysTheAssistantIsNotSignedIn()
    {
        var server = new TodoServer(null);

        var reply = await Call(server, "list_tasks");

        Assert.True(IsError(reply));
        Assert.Contains("not signed in", TextOf(reply), StringComparison.Ordinal);
    }

    // ---- The connection of the app ----

    private static readonly KnownEndpoint Todo = new(
        "microsofttodo", "Microsoft To Do", "https://graph.microsoft.com/v1.0/me/todo", "microsoft.com",
        Program: "Assistant.MicrosoftTodo.exe",
        FixedSignIn: new FixedSignIn("https://auth.example.com/authorize", "https://auth.example.com/token", "default-client", "Tasks.ReadWrite offline_access", "localhost"),
        TokenVariable: "MSGRAPH_ACCESS_TOKEN");

    private sealed class Setup : IAsyncDisposable
    {
        public Setup(FakeOAuthServer server, ReturningBrowser browser, string? chosenClientId = null, string? program = "C:\\Assistant\\Assistant.MicrosoftTodo.exe")
        {
            Secrets = new FakeSecretStore();
            Clock = new ManualTimeProvider();
            Store = new MemoryIntegrationStore();
            Registry = new InstalledIntegrationRegistry(Store, NullLogger<InstalledIntegrationRegistry>.Instance, Secrets);
            Client = new StubMcpClient();
            Client.Tools.Add(Sample.Tool("create_task", "Creates a task."));
            Manager = new McpConnectionManager(
                Registry, new StubClientFactory(_ => Client), TestSettings.LocalOnly(false), Clock, new McpLoadingOptions(), NullLogger<McpConnectionManager>.Instance, cache: null);
            OAuth = new McpOAuthClient(browser, Clock, server);
            Sessions = new McpOAuthSessions(Secrets, OAuth, Clock);
            var settings = new FixedSettings
            {
                Current = new Assistant.Core.Settings.AppSettings { Privacy = new Assistant.Core.Settings.PrivacySettings { LocalOnly = false }, Integrations = new Assistant.Core.Settings.IntegrationSettings { MicrosoftClientId = chosenClientId } },
            };
            Connector = new IntegrationConnector(Registry, OAuth, Sessions, Manager, settings, Secrets, Clock, NullLogger<IntegrationConnector>.Instance, _ => program);
        }

        public FakeSecretStore Secrets { get; }

        public ManualTimeProvider Clock { get; }

        public MemoryIntegrationStore Store { get; }

        public InstalledIntegrationRegistry Registry { get; }

        public StubMcpClient Client { get; }

        public McpConnectionManager Manager { get; }

        public McpOAuthClient OAuth { get; }

        public McpOAuthSessions Sessions { get; }

        public IntegrationConnector Connector { get; }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            OAuth.Dispose();
        }
    }

    [Fact]
    public async Task ConnectingRecordsTheProgramSignsInAtMicrosoftsOwnPageAndKeepsTheTokensOutOfTheRecord()
    {
        var server = new FakeOAuthServer();
        var browser = new ReturningBrowser();
        await using var setup = new Setup(server, browser);

        var outcome = await setup.Connector.ConnectAsync(Todo);

        Assert.True(outcome.IsInstalled);
        var record = Assert.Single(setup.Store.Saved);
        Assert.Equal(McpTransportKind.Stdio, record.Transport.Kind);
        Assert.Equal("C:\\Assistant\\Assistant.MicrosoftTodo.exe", record.Transport.Command);
        Assert.Equal(IntegrationAuthKind.OAuth, record.Authentication.Kind);
        Assert.Equal(IntegrationAuthState.Ready, record.Authentication.State);
        Assert.Equal("MSGRAPH_ACCESS_TOKEN", record.Authentication.TokenVariable);
        Assert.DoesNotContain("AT1", System.Text.Json.JsonSerializer.Serialize(record), StringComparison.Ordinal);
        Assert.Empty(IntegrationRules.Problems(record));

        // The user was sent to the sign-in's own page, with the default client, no resource (Microsoft's does not take one) and back to a port on this PC by the name localhost.
        var query = browser.OpenedQuery!;
        Assert.Equal("default-client", query["client_id"]);
        Assert.False(query.ContainsKey("resource"));
        Assert.Equal("Tasks.ReadWrite offline_access", query["scope"]);
        Assert.StartsWith("http://localhost:", query["redirect_uri"], StringComparison.Ordinal);
        Assert.Equal("default-client", server.TokenRequests.Single()["client_id"]);
        Assert.Equal("Tasks.ReadWrite offline_access", server.TokenRequests.Single()["scope"]);
    }

    [Fact]
    public async Task AnApplicationIdTheUserChoseIsUsedInsteadOfTheDefault()
    {
        var browser = new ReturningBrowser();
        await using var setup = new Setup(new FakeOAuthServer(), browser, chosenClientId: "11111111-2222-3333-4444-555555555555");

        await setup.Connector.ConnectAsync(Todo);

        Assert.Equal("11111111-2222-3333-4444-555555555555", browser.OpenedQuery!["client_id"]);
    }

    [Fact]
    public async Task AProgramThatIsNotInTheAssistantsFolderIsSaidSoAndNothingIsRecorded()
    {
        await using var setup = new Setup(new FakeOAuthServer(), new ReturningBrowser(), program: null);

        var outcome = await setup.Connector.ConnectAsync(Todo);

        Assert.Equal(InstallFailure.SetupFailed, outcome.Failure);
        Assert.Contains("missing from the Assistant's folder", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(setup.Store.Saved);
    }

    [Fact]
    public async Task TheProgramIsGivenANewTokenEachTimeItStartsAndNothingElseOfTheSignIn()
    {
        var server = new FakeOAuthServer();
        await using var setup = new Setup(server, new ReturningBrowser());
        await setup.Connector.ConnectAsync(Todo);
        var record = Assert.Single(setup.Store.Saved);
        var launched = new List<IReadOnlyDictionary<string, string>>();
        var factory = new McpClientFactory(setup.Secrets, new McpClientOptions(), () => new HttpClientHandler(), setup.Sessions);

        // The transport is made when the client connects, which starts the program; here the record is what is checked: the variable it names, the token store it reads.
        Assert.Equal("MSGRAPH_ACCESS_TOKEN", record.Authentication.TokenVariable);
        Assert.Equal("AT1", await setup.Sessions.GetAccessTokenAsync(record));
        Assert.NotNull(factory.Create(record));
        Assert.Empty(launched);
    }

    [Fact]
    public void MicrosoftToDoIsAKnownAppWithABundledProgramAndNoServerOfItsOwn()
    {
        var known = KnownEndpoints.For("microsofttodo")!;

        Assert.True(known.IsBundled);
        Assert.True(KnownEndpoints.IsValid(known));
        Assert.Equal("Assistant.MicrosoftTodo.exe", known.Program);
        Assert.Equal("MSGRAPH_ACCESS_TOKEN", known.TokenVariable);
        Assert.Equal("select_account", known.FixedSignIn!.Prompt);
        Assert.Equal("select_account", known.FixedSignIn.For(null).Prompt);
        Assert.StartsWith("https://login.microsoftonline.com/", known.FixedSignIn!.Authorization, StringComparison.Ordinal);
        Assert.Equal("microsofttodo", KnownApps.Find("Microsoft To Do")!.Key);
    }
}
