using System.Net;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>A page fetcher the test controls: what each address answers, or how it fails, and what was asked.</summary>
internal sealed class FakeDiscoveryHttp : IDiscoveryHttp
{
    private readonly List<(Func<Uri, bool> Matches, Func<Uri, DiscoveryResponse> Answer)> _routes = [];

    public List<Uri> Requests { get; } = [];

    public List<string> Accepts { get; } = [];

    public FakeDiscoveryHttp On(string contains, string body, int status = 200)
    {
        _routes.Add((uri => Uri.UnescapeDataString(uri.AbsoluteUri).Contains(contains, StringComparison.Ordinal), _ => new DiscoveryResponse(status, body)));
        return this;
    }

    public FakeDiscoveryHttp Fail(string contains, DiscoveryFailure failure)
    {
        _routes.Add((uri => Uri.UnescapeDataString(uri.AbsoluteUri).Contains(contains, StringComparison.Ordinal), _ => throw new DiscoveryException(failure)));
        return this;
    }

    public FakeDiscoveryHttp Hang(string contains)
    {
        _routes.Add((uri => Uri.UnescapeDataString(uri.AbsoluteUri).Contains(contains, StringComparison.Ordinal), _ => throw new HangException()));
        return this;
    }

    public async Task<DiscoveryResponse> GetAsync(Uri uri, string accept, int maxBytes, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(uri);
            Accepts.Add(accept);
        }

        Assert.True(DiscoveryHttp.IsAllowed(uri), "The finder asked for an address it may not fetch: " + uri);
        foreach (var (matches, answer) in _routes)
        {
            if (matches(uri))
            {
                try
                {
                    return answer(uri);
                }
                catch (HangException)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
            }
        }

        return new DiscoveryResponse(404, string.Empty);
    }

    public int Asked(string contains)
    {
        lock (Requests)
        {
            return Requests.Count(uri => Uri.UnescapeDataString(uri.AbsoluteUri).Contains(contains, StringComparison.Ordinal));
        }
    }

    private sealed class HangException : Exception;
}

/// <summary>Answers of the places that list integrations, shaped as they really answer (trimmed from real responses of 2026-10-02).</summary>
internal static class DiscoveryFixtures
{
    public const string RegistryTodoist = """
        {"servers":[
          {"server":{"$schema":"https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json","name":"io.github.Doist/todoist-mcp",
            "description":"The official Todoist MCP server","repository":{"url":"https://github.com/Doist/todoist-mcp","source":"github"},"version":"13.4.0",
            "packages":[{"registryType":"npm","identifier":"@doist/todoist-mcp","version":"13.4.0","runtimeHint":"npx","transport":{"type":"stdio"},
              "environmentVariables":[{"name":"TODOIST_API_KEY","isRequired":true,"isSecret":true},{"name":"TODOIST_BASE_URL","isRequired":false}]}]},
           "_meta":{"io.modelcontextprotocol.registry/official":{"status":"active","publishedAt":"2026-09-01T10:00:00Z","updatedAt":"2026-09-30T18:30:00Z","isLatest":true}}},
          {"server":{"name":"io.github.Doist/todoist-mcp","description":"The official Todoist MCP server","version":"13.3.0"},
           "_meta":{"io.modelcontextprotocol.registry/official":{"status":"active","updatedAt":"2026-08-01T10:00:00Z","isLatest":false}}},
          {"server":{"name":"ai.smithery/smithery-todoist","description":"Manage your Todoist tasks and projects","repository":{"url":"https://github.com/smithery-ai/mcp-servers","source":"github","subfolder":"todoist"},
            "version":"1.0.0","remotes":[{"type":"streamable-http","url":"https://server.smithery.ai/@smithery/todoist/mcp",
              "headers":[{"description":"Bearer token for Smithery authentication","isRequired":true,"value":"Bearer {smithery_api_key}","isSecret":true,"name":"Authorization"}]}]},
           "_meta":{"io.modelcontextprotocol.registry/official":{"status":"active","updatedAt":"2025-09-10T18:26:59Z","isLatest":true}}},
          {"server":{"name":"com.todoist/mcp","description":"Todoist, hosted","version":"2.0.0","remotes":[{"type":"streamable-http","url":"https://ai.todoist.net/mcp"}]},
           "_meta":{"io.modelcontextprotocol.registry/official":{"status":"active","updatedAt":"2026-09-20T00:00:00Z","isLatest":true}}},
          {"server":{"name":"io.github.someone/old-todoist","description":"Todoist, retired","version":"0.1.0"},
           "_meta":{"io.modelcontextprotocol.registry/official":{"status":"deprecated","isLatest":true}}},
          {"server":{"name":"io.github.fan/todoist-helper","description":"The unofficial Todoist helper","version":"0.2.0"},
           "_meta":{"io.modelcontextprotocol.registry/official":{"status":"active","isLatest":true}}}
        ],"metadata":{"nextCursor":"x","count":6}}
        """;

    public const string RegistryMicrosoftTodo = """
        {"servers":[
          {"server":{"name":"com.microsoft/todo-mcp","description":"Microsoft To Do tasks for assistants","version":"1.2.0",
            "remotes":[{"type":"streamable-http","url":"https://todo.microsoft.example/mcp"}]},
           "_meta":{"io.modelcontextprotocol.registry/official":{"status":"active","updatedAt":"2026-09-10T00:00:00Z","isLatest":true}}},
          {"server":{"name":"io.github.jordan/microsoft-todo-mcp-server","description":"MCP server for Microsoft To Do via Microsoft Graph","version":"0.9.0",
            "packages":[{"registryType":"pypi","identifier":"microsoft-todo-mcp-server","version":"0.9.0","runtimeHint":"uvx"}]},
           "_meta":{"io.modelcontextprotocol.registry/official":{"status":"active","updatedAt":"2026-08-10T00:00:00Z","isLatest":true}}}
        ]}
        """;

    public const string GitHubTodoist = """
        {"total_count":175,"incomplete_results":false,"items":[
          {"full_name":"Doist/todoist-mcp","html_url":"https://github.com/Doist/todoist-mcp","owner":{"login":"Doist","type":"Organization"},
           "description":"A set of tools to connect to AI agents, to allow them to use Todoist","license":{"key":"mit","spdx_id":"MIT"},"pushed_at":"2026-10-02T09:00:00Z",
           "archived":false,"fork":false,"disabled":false,"private":false,"stargazers_count":553,"topics":["mcp","todoist"],"language":"TypeScript"},
          {"full_name":"abhiz123/todoist-mcp-server","html_url":"https://github.com/abhiz123/todoist-mcp-server","owner":{"login":"abhiz123","type":"User"},
           "description":"MCP server for Todoist integration enabling natural language task management","license":{"key":"mit","spdx_id":"MIT"},"pushed_at":"2025-04-20T00:00:00Z",
           "archived":false,"fork":false,"stargazers_count":393,"topics":[],"language":"TypeScript"},
          {"full_name":"someone/todoist-mcp-fork","html_url":"https://github.com/someone/todoist-mcp-fork","owner":{"login":"someone"},"description":"A fork","fork":true,"stargazers_count":1},
          {"full_name":"old/todoist-mcp","html_url":"https://github.com/old/todoist-mcp","owner":{"login":"old"},"description":"Todoist MCP, retired","archived":true,"fork":false,"stargazers_count":50,
           "license":{"spdx_id":"NOASSERTION"},"pushed_at":"2024-01-01T00:00:00Z"},
          {"full_name":"public-apis/public-apis","html_url":"https://github.com/public-apis/public-apis","owner":{"login":"public-apis"},"description":"A collective list of free APIs","fork":false,"stargazers_count":485477,"language":"Python"}
        ]}
        """;

    public const string GitHubMicrosoftTodo = """
        {"total_count":27,"items":[
          {"full_name":"jordanburke/microsoft-todo-mcp-server","html_url":"https://github.com/jordanburke/microsoft-todo-mcp-server","owner":{"login":"jordanburke","type":"User"},
           "description":null,"license":{"spdx_id":"NOASSERTION"},"pushed_at":"2026-09-13T00:00:00Z","archived":false,"fork":false,"stargazers_count":111,"language":"TypeScript"},
          {"full_name":"MAG-Cie/mcp-microsoft-todo","html_url":"https://github.com/MAG-Cie/mcp-microsoft-todo","owner":{"login":"MAG-Cie","type":"User"},
           "description":"MCP server for Microsoft To Do via Microsoft Graph API. Manage task lists","license":{"spdx_id":"MIT"},"pushed_at":"2026-05-11T00:00:00Z","archived":false,"fork":false,"stargazers_count":14,"language":"Python"}
        ]}
        """;

    public const string NpmTodoist = """
        {"objects":[
          {"package":{"name":"@doist/todoist-mcp","version":"13.4.0","description":"The official Todoist MCP server","license":"MIT","date":"2026-09-30T18:29:14.976Z",
            "keywords":["todoist","mcp"],"publisher":{"username":"GitHub Actions","email":"npm-oidc-no-reply@github.com"},
            "maintainers":[{"username":"ricardoist","email":"ricardo@doist.com"}],
            "links":{"npm":"https://www.npmjs.com/package/@doist/todoist-mcp","homepage":"https://github.com/Doist/todoist-mcp#readme","repository":"git+https://github.com/Doist/todoist-mcp.git"}}},
          {"package":{"name":"todoist-mcp-impostor","version":"1.0.0","description":"Todoist for assistants","license":"ISC","date":"2026-09-01T00:00:00Z",
            "links":{"npm":"https://www.npmjs.com/package/todoist-mcp-impostor","repository":"git+https://github.com/Doist/todoist-mcp.git"}}}
        ],"total":2}
        """;

    public const string PyPiPackage = """
        {"info":{"name":"todoist-mcp","version":"0.4.2","summary":"An MCP server for Todoist","license":"MIT",
          "project_urls":{"Homepage":"https://example.com","Repository":"https://github.com/example/todoist-mcp"}},
         "urls":[{"upload_time_iso_8601":"2026-08-01T12:00:00.000000Z"}]}
        """;

    public const string ReadmeTodoist = """
        # todoist-mcp

        Connect assistants to Todoist. Set `TODOIST_API_TOKEN` before you start; run it with `npx @doist/todoist-mcp`.

        ## Tools

        - `add-tasks` - Create tasks in Todoist.
        - `find-tasks` - Search tasks.
        | `complete-tasks` | Mark tasks done |
        ### `update_task`

        ```json
        {"mcpServers": {"todoist": {"command": "npx", "env": {"TODOIST_API_TOKEN": "x"}}}}
        ```

        ## License

        Use `mcp.json` or `config.ts` to configure. The `README` is nice.
        """;

    public static IntegrationNeed MicrosoftTodo => new("Microsoft To Do", "microsofttodo", new IntegrationCapability(CapabilityAction.Create, "task"), IntegrationNeedSource.Catalog);

    public static IntegrationNeed Todoist => new("Todoist", "todoist", new IntegrationCapability(CapabilityAction.Create, "task"), IntegrationNeedSource.Catalog);

    public static DiscoveryQuery QueryFor(IntegrationNeed need) => DiscoveryQuery.For(need) ?? throw new InvalidOperationException("No query.");
}

/// <summary>A cache the test can look into.</summary>
internal sealed class MemoryDiscoveryCache : IDiscoveryCache
{
    public Dictionary<string, (IntegrationDiscoveryResult Result, DateTimeOffset ExpiresAt)> Entries { get; } = [];

    public int Reads { get; private set; }

    public IntegrationDiscoveryResult? TryGet(string key, DateTimeOffset now)
    {
        Reads++;
        return Entries.TryGetValue(key, out var entry) && entry.ExpiresAt > now ? entry.Result : null;
    }

    public void Put(string key, IntegrationDiscoveryResult result, DateTimeOffset expiresAt) => Entries[key] = (result, expiresAt);
}

/// <summary>A source the test controls.</summary>
internal sealed class FakeSource(string id, DiscoveryStage stage, Func<DiscoveryQuery, CancellationToken, Task<IReadOnlyList<IntegrationCandidate>>> search) : IIntegrationDiscoverySource
{
    public string Id => id;

    public DiscoveryStage Stage => stage;

    public int Asked { get; private set; }

    public Task<IReadOnlyList<IntegrationCandidate>> SearchAsync(DiscoveryQuery query, CancellationToken cancellationToken)
    {
        Asked++;
        return search(query, cancellationToken);
    }

    public static FakeSource Returning(string id, DiscoveryStage stage, params IntegrationCandidate[] candidates) =>
        new(id, stage, (_, _) => Task.FromResult<IReadOnlyList<IntegrationCandidate>>(candidates));

    public static FakeSource Failing(string id, DiscoveryStage stage) =>
        new(id, stage, (_, _) => throw new DiscoveryException(DiscoveryFailure.Network));

    /// <summary>A source that never answers until it is stopped.</summary>
    public static FakeSource Hanging(string id, DiscoveryStage stage) =>
        new(id, stage, async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        });
}

/// <summary>An enricher that adds nothing, or what the test says, and records what it was asked.</summary>
internal sealed class FakeEnricher(Func<IntegrationCandidate, IntegrationCandidate>? enrich = null) : IRepositoryEnricher
{
    public List<string> Enriched { get; } = [];

    public Task<IntegrationCandidate> EnrichAsync(IntegrationCandidate candidate, IntegrationCapability capability, CancellationToken cancellationToken)
    {
        lock (Enriched)
        {
            Enriched.Add(candidate.Name);
        }

        return Task.FromResult(enrich is null ? candidate : enrich(candidate));
    }
}

/// <summary>A model assessor that judges as the test says.</summary>
internal sealed class FakeAssessor(Func<IReadOnlyList<IntegrationCandidate>, IReadOnlyList<CandidateAssessment>?>? judge = null) : ICandidateAssessor
{
    public int Asked { get; private set; }

    public List<IntegrationCandidate> Seen { get; } = [];

    public Task<IReadOnlyList<CandidateAssessment>?> AssessAsync(IntegrationNeed need, IReadOnlyList<IntegrationCandidate> candidates, CancellationToken cancellationToken = default)
    {
        Asked++;
        Seen.AddRange(candidates);
        return Task.FromResult(judge?.Invoke(candidates));
    }
}

internal static class Candidates
{
    public static IntegrationCandidate Make(
        string name,
        CandidateTrust trust = CandidateTrust.Community,
        string? description = null,
        string? repository = null,
        DateTimeOffset? activity = null,
        CapabilityEvidence evidence = CapabilityEvidence.AppOnly,
        int? stars = null,
        bool archived = false,
        IReadOnlyList<string>? tools = null) => new()
    {
        Name = name,
        SourceUrl = repository ?? "https://github.com/" + name,
        RepositoryUrl = repository ?? "https://github.com/" + name,
        Trust = trust,
        Description = description ?? "A Todoist MCP server",
        LastActivity = activity,
        Evidence = evidence,
        Stars = stars,
        Archived = archived,
        ToolNames = tools ?? [],
        FoundIn = ["github"],
    };
}
