using Assistant.Core.Contracts;
using Assistant.Core.Settings;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;

namespace Assistant.Tools.Search;

public sealed record WebSearchResponse(string Provider, McpToolResult Result);

/// <summary>Configuration is local. Only SearchAsync makes a request to the selected provider.</summary>
public interface IWebSearchService
{
    Task ConfigureAsync(WebSearchSettings settings, CancellationToken cancellationToken = default);
    Task<WebSearchResponse> SearchAsync(string query, CancellationToken cancellationToken = default);
}

internal sealed class WebSearchService(ISettingsService settings, IInstalledIntegrationRegistry registry,
    McpConnectionManager connections) : IWebSearchService, IMcpToolVeto
{
    public async Task ConfigureAsync(WebSearchSettings choice, CancellationToken cancellationToken = default)
    {
        foreach (var provider in HostedSearchProviders.All)
        {
            var existing = await registry.GetAsync(provider.IntegrationId, cancellationToken).ConfigureAwait(false);
            var enabled = choice.Enabled && choice.Provider == provider.Id;
            if (existing is null && enabled && !provider.NeedsSignIn)
                await registry.AddAsync(new InstalledIntegration
                {
                    Id = provider.IntegrationId, Name = provider.Name, Enabled = true,
                    Source = new(IntegrationSourceKind.Bundled, new Uri(provider.Endpoint).Host),
                    Transport = provider.Transport, Permissions = provider.Permissions,
                }, cancellationToken).ConfigureAwait(false);
            else if (existing is not null)
                await registry.UpdateAsync(existing.Id, current => current with
                {
                    Enabled = enabled, Transport = provider.Transport, Permissions = provider.Permissions,
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<WebSearchResponse> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 500 || query.Any(char.IsControl))
            throw new ArgumentException("Use a search query of 1–500 characters without line breaks.", nameof(query));
        var saved = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!saved.WebSearch.Enabled || saved.Privacy.LocalOnly || !saved.Permissions.ExternalSearch || !Enum.IsDefined(saved.WebSearch.Provider))
            throw new McpException(McpFailure.Blocked);
        var provider = HostedSearchProviders.Find(saved.WebSearch.Provider);
        var integration = await registry.GetAsync(provider.IntegrationId, cancellationToken).ConfigureAwait(false);
        if (integration is not { Enabled: true } || integration.Transport.Endpoint != provider.Endpoint
            || !integration.Permissions.AllowReads || !integration.Permissions.AllowNetwork
            || !integration.Permissions.AllowAccountAccess && provider.NeedsSignIn)
            throw new McpException(McpFailure.Blocked);
        var catalog = await connections.GetCatalogAsync(provider.IntegrationId, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        var tool = catalog?.Tools.FirstOrDefault(tool => tool.Descriptor.Name == provider.ToolName);
        if (tool is null) throw new McpException(McpFailure.NotConfigured);
        // Recheck privacy after catalog loading and immediately before sending the actual query.
        saved = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!saved.WebSearch.Enabled || saved.WebSearch.Provider != provider.Id || saved.Privacy.LocalOnly || !saved.Permissions.ExternalSearch)
            throw new McpException(McpFailure.Blocked);
        integration = await registry.GetAsync(provider.IntegrationId, cancellationToken).ConfigureAwait(false);
        if (integration is not { Enabled: true } || integration.Transport.Endpoint != provider.Endpoint
            || !integration.Permissions.AllowReads || !integration.Permissions.AllowNetwork
            || integration.Permissions.BlockedTools.Contains(provider.ToolName, StringComparer.Ordinal)
            || !integration.Permissions.AllowAccountAccess && integration.Authentication.Kind != IntegrationAuthKind.None)
            throw new McpException(McpFailure.Blocked);
        var result = await connections.CallAsync(provider.IntegrationId, tool.Descriptor, provider.Arguments(query.Trim()), true, cancellationToken).ConfigureAwait(false);
        return new(provider.Name, NormalizeResult(result));
    }

    internal static McpToolResult NormalizeResult(McpToolResult result)
    {
        // Some hosted servers report quota exhaustion in a successful text block rather than setting isError.
        var quota = result.Content.Any(block => block.Kind == McpContentKind.Text && block.Text is { } text &&
            (text.StartsWith("You've hit Exa's free MCP rate limit", StringComparison.OrdinalIgnoreCase)
             || text.StartsWith("You have reached", StringComparison.OrdinalIgnoreCase) && text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
             || text.StartsWith("Rate limit exceeded", StringComparison.OrdinalIgnoreCase)));
        return quota ? new(true, [new(McpContentKind.Text, "This engine's free search allowance has been reached. Choose another engine or add your own API key in Settings → Integrations → Web search.", null, null, null)], null) : result;
    }

    public Task<IReadOnlySet<string>> ReservedToolsAsync(InstalledIntegration integration, IReadOnlyList<McpTool> tools, CancellationToken cancellationToken)
    {
        // These providers are offered through search_web so switching engines cannot leave a second search route active.
        IReadOnlySet<string> names = HostedSearchProviders.All.Any(provider => provider.IntegrationId == integration.Id)
            ? tools.Select(tool => tool.Descriptor.Name).ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
        return Task.FromResult(names);
    }
}
