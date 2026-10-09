using System.Text.Json;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Assistant.Tools.Integrations;

namespace Assistant.Tools.Search;

/// <summary>Fixed hosted endpoints: none starts or installs a local program.</summary>
public sealed record HostedSearchProvider(WebSearchProvider Id, string IntegrationId, string Name, string Detail,
    string Access, string Icon, string Endpoint, string ToolName, string Documentation, bool NeedsSignIn = false)
{
    public bool Recommended => Id == WebSearchProvider.Exa;
    public KnownEndpoint SignInEndpoint => new(IntegrationId, Name, Endpoint, new Uri(Endpoint).Host, Capability: PermissionCapability.ExternalSearch);
    public IntegrationTransport Transport => new()
    {
        Endpoint = Endpoint,
        Headers = Id == WebSearchProvider.Tavily ? new Dictionary<string, string> { ["X-Tavily-Access-Mode"] = "keyless" } : new Dictionary<string, string>(),
    };
    public IntegrationPermissions Permissions => new()
    {
        RequiredCapability = PermissionCapability.ExternalSearch, LeavesThisPc = true, AllowSideEffects = false,
        ReadOnlyTools = [ToolName],
    };
    internal JsonElement Arguments(string query) => JsonSerializer.SerializeToElement(Id switch
    {
        WebSearchProvider.Exa => (object)new { query, numResults = 3, objective = "Find reliable sources that answer this query, prioritizing current information: " + query },
        WebSearchProvider.Tavily => new { query, max_results = 3, search_depth = "basic", include_raw_content = false },
        _ => new { q = query, safeSearch = "moderate" },
    });
}

public static class HostedSearchProviders
{
    public static IReadOnlyList<HostedSearchProvider> All { get; } =
    [
        new(WebSearchProvider.Exa, "websearchexa", "Exa", "Web search with relevant source text for AI answers.",
            "No account or API key · free, rate limited", "exa", "https://mcp.exa.ai/mcp?tools=web_search_exa", "web_search_exa", "https://exa.ai/docs/get-started/exa-mcp"),
        new(WebSearchProvider.Tavily, "websearchtavily", "Tavily", "Search built for AI, with source links and snippets.",
            "No account or API key · free, rate limited", "tavily", "https://mcp.tavily.com/mcp/", "tavily_search", "https://docs.tavily.com/documentation/keyless"),
        new(WebSearchProvider.DuckDuckGo, "websearchduckduckgo", "DuckDuckGo", "Ranked DuckDuckGo results through HasData's hosted MCP.",
            "HasData sign-in required · account limits apply", "duckduckgo", "https://mcp.hasdata.com/mcp?apis=duckduckgo", "hasdata_duckduckgo_serp_getSearchResults", "https://github.com/HasData/duckduckgo-mcp", true),
    ];
    public static HostedSearchProvider Find(WebSearchProvider id) => All.First(provider => provider.Id == id);
}
