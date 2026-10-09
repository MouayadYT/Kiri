using Assistant.Core.Domain;
using Assistant.Tools.Integrations;

namespace Assistant.Tools.Mcp;

/// <summary>
/// The tools one connected app offers, as the tool registry could take them (step 104): what was left out of the server's list and why is only
/// counted. A catalog is kept while it is fresh, so a request that mentions the app does not list its tools again.
/// </summary>
internal sealed class McpToolCatalog
{
    /// <summary>Creates the catalog.</summary>
    public McpToolCatalog(string integrationId, string appName, IReadOnlyList<McpTool> tools, int listed, DateTimeOffset loadedAt)
    {
        IntegrationId = integrationId;
        AppName = appName;
        Tools = tools;
        Listed = listed;
        LoadedAt = loadedAt;
    }

    /// <summary>The app.</summary>
    public string IntegrationId { get; }

    /// <summary>The app's name, cleaned.</summary>
    public string AppName { get; }

    /// <summary>The tools that can be offered, in the order the server listed them.</summary>
    public IReadOnlyList<McpTool> Tools { get; }

    /// <summary>How many tools the server listed.</summary>
    public int Listed { get; }

    /// <summary>How many of them were left out: blocked, destructive, not allowed, with a schema that cannot be used, or not meeting the rules for a tool.</summary>
    public int Skipped => Listed - Tools.Count;

    /// <summary>When it was read.</summary>
    public DateTimeOffset LoadedAt { get; }

    /// <summary>Whether it was made from the tools kept on disk and not read from the program just now (step 109): the program has not been started for it.</summary>
    public bool FromCache { get; init; }
}

/// <summary>Makes the tools of a connected app out of what its server listed.</summary>
internal static class McpToolCatalogBuilder
{
    /// <summary>How long a call of a connected app's tool may take, as far as the tool registry is concerned (the app's own limit is the client's call time).</summary>
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);

    private const int MaxDescriptionLength = 400;

    /// <summary>The tools of <paramref name="integration"/>, made from <paramref name="listed"/>.</summary>
    /// <param name="integration">The app, as installed: its permissions decide what is offered and how much a call may change.</param>
    /// <param name="listed">What the server listed.</param>
    /// <param name="invoker">What calls the app.</param>
    /// <param name="loadedAt">The time, kept with the catalog.</param>
    public static McpToolCatalog Build(InstalledIntegration integration, IReadOnlyList<McpToolDescriptor> listed, IMcpToolInvoker invoker, DateTimeOffset loadedAt)
    {
        var appName = McpText.Clean(integration.Name, IntegrationRules.MaxNameLength) ?? integration.Id;

        // Names are given in a fixed order, so a tool gets the same name every time and two that come out alike are told apart the same way.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in listed.OrderBy(tool => tool.Name, StringComparer.Ordinal))
        {
            var name = McpToolNames.Create(integration.Id, tool.Name);
            if (!taken.Add(name))
            {
                name = McpToolNames.CreateDistinct(integration.Id, tool.Name);
                if (!taken.Add(name))
                {
                    continue;
                }
            }

            names[tool.Name] = name;
        }

        var tools = new List<McpTool>();
        foreach (var descriptor in listed)
        {
            if (!names.TryGetValue(descriptor.Name, out var name) || McpToolPolicy.RiskOf(integration.Permissions, descriptor) is not { } risk
                || McpSchemaAdapter.Adapt(descriptor.InputSchema) is not { } schema)
            {
                continue;
            }

            var words = McpText.Clean(descriptor.Description, MaxDescriptionLength) ?? McpText.Clean(descriptor.Title, 100) ?? McpText.Clean(descriptor.Name, 100) ?? "A tool.";
            var definition = new ToolDefinition(name, $"[{appName}] {words}", schema.Json, risk)
            {
                RequiredPermission = McpToolPermission.For(integration, descriptor),
                Timeout = ToolTimeout,
            };

            // The same rules as for every tool: a stable name, a typed schema, nothing that runs what it is given.
            if (ToolDefinitionGuard.Problem(definition, definition.EffectiveTimeout) is not null)
            {
                continue;
            }

            tools.Add(new McpTool(integration.Id, appName, descriptor, definition, schema.ServerNames, invoker, ReadsEvents(descriptor)));
        }

        return new McpToolCatalog(integration.Id, appName, tools, listed.Count, loadedAt);
    }

    // Whether the tool reads or searches a calendar's events (step 116), by the words of its name, title and description: what it returns is then checked for exams.
    private static bool ReadsEvents(McpToolDescriptor descriptor)
    {
        var facts = new[] { new ToolFacts(descriptor.Name, descriptor.Title, descriptor.Description) };
        return CapabilityMatcher.Match(new IntegrationCapability(CapabilityAction.Read, "event"), facts).Count > 0
            || CapabilityMatcher.Match(new IntegrationCapability(CapabilityAction.Search, "event"), facts).Count > 0;
    }
}
