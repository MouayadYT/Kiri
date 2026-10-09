using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Mcp;

/// <summary>
/// The connected apps' tools, loaded lazily by the request (PROJECT_SPEC §4.8, step 104): the <see cref="IDynamicToolSource"/> that lets the tool
/// registry and the executor take the tools of installed MCP servers in beside the built-in ones, under the same rules. It never offers all of them.
/// For a request it asks the selector which installed (and enabled) apps the request seems to be about, connects to those only (an app the
/// request has nothing to do with costs no connection), and offers the few tools of them that fit the request, within a budget of characters, so
/// that a small model's window is not spent on tools it does not need. What it offered a conversation for a request is remembered, and only
/// that can be called in it: the model cannot call a tool of an app it was not given, whatever name it writes. Nothing it does is logged beyond counts.
/// </summary>
internal sealed partial class McpToolSource : IDynamicToolSource
{
    private const int MaxRememberedConversations = 64;
    private const int MaxKnownTools = 2000;

    private readonly IInstalledIntegrationRegistry _registry;
    private readonly McpConnectionManager _connections;
    private readonly IMcpToolSelector _selector;
    private readonly McpLoadingOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<McpToolSource> _logger;
    private readonly IReadOnlyList<IMcpToolVeto> _vetoes;
    private readonly ConcurrentDictionary<Guid, Offer> _offers = new();
    private readonly ConcurrentDictionary<Guid, Recent> _recent = new();
    private readonly ConcurrentDictionary<string, ToolDefinition> _known = new(StringComparer.Ordinal);

    /// <summary>Creates the source.</summary>
    public McpToolSource(
        IInstalledIntegrationRegistry registry,
        McpConnectionManager connections,
        IMcpToolSelector selector,
        McpLoadingOptions options,
        TimeProvider clock,
        ILogger<McpToolSource> logger,
        IEnumerable<IMcpToolVeto>? vetoes = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _connections = connections;
        _selector = selector;
        _options = options;
        _clock = clock;
        _logger = logger;
        _vetoes = [.. vetoes ?? []];
    }

    /// <inheritdoc/>
    public async Task PrepareAsync(ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // What was offered for an earlier request is not what is offered for this one.
        _offers.TryRemove(context.ConversationId, out _);
        if (string.IsNullOrWhiteSpace(context.Request))
        {
            return;
        }

        IReadOnlyList<InstalledIntegration> installed;
        try
        {
            installed = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IntegrationException)
        {
            return;
        }

        var enabled = installed.Where(integration => integration.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return;
        }

        var candidates = _selector.SelectIntegrations(context.Request, enabled).Take(_options.MaxIntegrationsPerRequest).ToList();

        // A request that names no app, in a conversation where an app's tool has just been used, goes on with that app: the answer to a question the Assistant
        // asked ("which list?" "Tasks") says nothing about the app, and without its tools the model reaches for whatever else it has.
        Recent? followed = null;
        if (candidates.Count == 0 && _recent.TryGetValue(context.ConversationId, out var recent) && _clock.GetUtcNow() - recent.At <= _options.FollowUpLifetime)
        {
            candidates = [.. enabled.Where(integration => recent.IntegrationIds.Contains(integration.Id, StringComparer.Ordinal)).Take(_options.MaxIntegrationsPerRequest)];
            followed = candidates.Count > 0 ? recent : null;
        }

        if (candidates.Count == 0)
        {
            return;
        }

        // The apps the request is about are connected to side by side, each for no longer than a request can wait.
        var catalogs = await Task.WhenAll(
            candidates.Select(candidate => _connections.GetCatalogAsync(candidate.Id, _options.PrepareTimeout, cancellationToken))).ConfigureAwait(false);

        var chosen = new List<McpTool>();
        var chosenNames = new HashSet<string>(StringComparer.Ordinal);
        var characters = 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            if (catalogs[index] is not { } catalog)
            {
                continue;
            }

            foreach (var tool in catalog.Tools)
            {
                _known[tool.Definition.Name] = tool.Definition;
            }

            // What the Assistant sends with by itself (step 116) is not offered to the model beside it.
            var reserved = await ReservedAsync(candidates[index], catalog.Tools, context, cancellationToken).ConfigureAwait(false);
            var offerable = reserved.Count == 0 ? catalog.Tools : [.. catalog.Tools.Where(tool => !reserved.Contains(tool.Descriptor.Name))];
            // Going on with an app, the tools it was offered with come first: the one that was just used is the one the answer is for.
            var selected = _selector.SelectTools(context.Request, candidates[index], offerable, _options.MaxToolsPerIntegration);
            if (followed is not null)
            {
                selected =
                [
                    .. offerable.Where(tool => followed.ToolNames.Contains(tool.Definition.Name, StringComparer.Ordinal))
                        .Concat(selected).Distinct().Take(_options.MaxToolsPerIntegration),
                ];
            }

            foreach (var tool in selected)
            {
                var size = tool.Definition.Description.Length + tool.Definition.InputSchemaJson.Length;
                if (chosen.Count >= _options.MaxToolsOffered || characters + size > _options.MaxDefinitionCharacters)
                {
                    continue;
                }

                // Two tools are never offered under one name, however they came to have it: a call would reach only one of them.
                if (!chosenNames.Add(tool.Definition.Name))
                {
                    continue;
                }

                chosen.Add(tool);
                characters += size;
            }
        }

        if (_known.Count > MaxKnownTools)
        {
            _known.Clear();
            foreach (var tool in chosen)
            {
                _known[tool.Definition.Name] = tool.Definition;
            }
        }

        if (chosen.Count > 0)
        {
            Remember(context.ConversationId, new Offer(Hash(context.Request), chosen, _clock.GetUtcNow()));
        }

        LogOffered(_logger, candidates.Count, catalogs.Count(catalog => catalog is not null), chosen.Count, characters);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ITool> Offered(ToolContext context) => Current(context) is { } offer ? [.. offer.Tools] : [];

    /// <inheritdoc/>
    public ITool? Find(ToolContext context, string name)
    {
        if (Current(context) is not { } offer || offer.Tools.FirstOrDefault(tool => string.Equals(tool.Definition.Name, name, StringComparison.Ordinal)) is not { } found)
        {
            return null;
        }

        // The model is calling a tool of this app: for a while the conversation is about the app, whatever its next requests say (see PrepareAsync).
        _recent[context.ConversationId] = new Recent(
            [found.IntegrationId],
            [.. offer.Tools.Where(tool => tool.IntegrationId == found.IntegrationId).Select(tool => tool.Definition.Name)],
            _clock.GetUtcNow());
        while (_recent.Count > MaxRememberedConversations)
        {
            var oldest = _recent.MinBy(pair => pair.Value.At);
            if (!_recent.TryRemove(oldest.Key, out _))
            {
                break;
            }
        }

        return found;
    }

    /// <inheritdoc/>
    public ToolDefinition? Describe(string name) => name is not null && _known.TryGetValue(name, out var definition) ? definition : null;

    // The tools of the app that a veto keeps from the model; a veto that fails keeps none (the tool would still be asked about before it ran).
    private async Task<IReadOnlySet<string>> ReservedAsync(InstalledIntegration integration, IReadOnlyList<McpTool> tools, ToolContext context, CancellationToken cancellationToken)
    {
        if (_vetoes.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var veto in _vetoes)
        {
            try
            {
                reserved.UnionWith(await veto.ReservedToolsAsync(integration, tools, context, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // A veto that cannot say reserves nothing.
            }
        }

        return reserved;
    }

    // What was offered to the conversation, if it was for this request and is not too old.
    private Offer? Current(ToolContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Request) || !_offers.TryGetValue(context.ConversationId, out var offer))
        {
            return null;
        }

        return _clock.GetUtcNow() - offer.At <= _options.OfferLifetime && CryptographicOperations.FixedTimeEquals(offer.RequestHash, Hash(context.Request))
            ? offer
            : null;
    }

    // What is offered is kept for a few conversations at most; the oldest is forgotten first.
    private void Remember(Guid conversationId, Offer offer)
    {
        _offers[conversationId] = offer;
        while (_offers.Count > MaxRememberedConversations)
        {
            var oldest = _offers.MinBy(pair => pair.Value.At);
            if (!_offers.TryRemove(oldest.Key, out _))
            {
                break;
            }
        }
    }

    // The request is private content: only a hash of it is kept, to tell that a later call is for the same request.
    private static byte[] Hash(string request) => SHA256.HashData(Encoding.UTF8.GetBytes(request));

    [LoggerMessage(EventId = 3120, Level = LogLevel.Information, Message = "Connected apps for a request: {Selected} selected, {Ready} ready, {OfferedTools} tools offered ({Characters} characters)")]
    private static partial void LogOffered(ILogger logger, int selected, int ready, int offeredTools, int characters);

    private sealed record Offer(byte[] RequestHash, IReadOnlyList<McpTool> Tools, DateTimeOffset At);

    // The app whose tool a conversation last called, and the tools it was offered with then: names only, nothing of the request.
    private sealed record Recent(IReadOnlyList<string> IntegrationIds, IReadOnlyList<string> ToolNames, DateTimeOffset At);
}
