using Assistant.Core.Contracts;
using Assistant.Tools.Mcp;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Integrations;

/// <summary>How the resolver treats what it kept about an installed integration.</summary>
public sealed record IntegrationResolverOptions
{
    /// <summary>
    /// How long the names of an installed integration's tools, as last read from its server, are trusted without connecting to it again. A request
    /// for an integration that was read within this time is answered from the registry alone, with no program started and no network used.
    /// </summary>
    public TimeSpan MetadataLifetime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The most time a request waits for an integration that has to be connected to, to read its tools.</summary>
    public TimeSpan ConnectBudget { get; init; } = TimeSpan.FromSeconds(8);
}

/// <summary>Resolves a request for an external app (PROJECT_SPEC §4.8, step 105).</summary>
public interface IIntegrationResolver
{
    /// <summary>Reads <paramref name="request"/> (private content: never kept or logged) and resolves what it asks, if it asks for an external app.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IntegrationResolution> ResolveRequestAsync(string? request, CancellationToken cancellationToken = default);

    /// <summary>Resolves a need that is already known.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IntegrationResolution> ResolveAsync(IntegrationNeed need, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves a request that involves an external app, in a fixed order (PROJECT_SPEC §4.8, step 105): (1) which app and which capability, read by fixed rules
/// (<see cref="IIntegrationRequestReader"/>); (2) the installed integrations, and a working one is reused at once, from the tool names the
/// registry kept when it was last connected to, without starting or reaching it (when those are missing or older than a day it is connected to
/// once, which also refreshes them); (3) integrations the user already has on this PC in other programs
/// (<see cref="IAvailableIntegrationSource"/>); (4) only when none of those exists, the Integration Finder is allowed to look. An integration that is
/// installed but needs fixing (off, signed out, unreachable) is never looked for again, and a request to delete is refused without looking at
/// anything. No model is asked and nothing is fetched from the web. Logs say the outcome, never the app, the request or the user's words.
/// </summary>
internal sealed partial class IntegrationResolver : IIntegrationResolver
{
    private readonly IInstalledIntegrationRegistry _registry;
    private readonly IMcpCatalogProvider _catalogs;
    private readonly ISettingsService _settings;
    private readonly IIntegrationRequestReader _reader;
    private readonly IReadOnlyList<IAvailableIntegrationSource> _sources;
    private readonly IPermissionPolicy? _permissions;
    private readonly TimeProvider _clock;
    private readonly IntegrationResolverOptions _options;
    private readonly ILogger<IntegrationResolver> _logger;

    /// <summary>Creates the resolver.</summary>
    public IntegrationResolver(
        IInstalledIntegrationRegistry registry,
        IMcpCatalogProvider catalogs,
        ISettingsService settings,
        IIntegrationRequestReader reader,
        IEnumerable<IAvailableIntegrationSource> sources,
        IPermissionPolicy? permissions,
        TimeProvider clock,
        IntegrationResolverOptions options,
        ILogger<IntegrationResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _catalogs = catalogs;
        _settings = settings;
        _reader = reader;
        _sources = [.. sources];
        _permissions = permissions;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IntegrationResolution> ResolveRequestAsync(string? request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request))
        {
            return IntegrationResolution.NotAnAppRequest;
        }

        IReadOnlyList<InstalledIntegration> installed;
        try
        {
            installed = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IntegrationException)
        {
            return IntegrationResolution.CouldNotCheck;
        }

        var need = _reader.Read(request, [.. installed.Select(integration => integration.Name)]);
        return need is null ? IntegrationResolution.NotAnAppRequest : await ResolveAsync(need, installed, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IntegrationResolution> ResolveAsync(IntegrationNeed need, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(need);
        IReadOnlyList<InstalledIntegration> installed;
        try
        {
            installed = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IntegrationException)
        {
            return IntegrationResolution.CouldNotCheck;
        }

        return await ResolveAsync(need, installed, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IntegrationResolution> ResolveAsync(
        IntegrationNeed need, IReadOnlyList<InstalledIntegration> installed, CancellationToken cancellationToken)
    {
        var resolution = await ResolveCoreAsync(need, installed, cancellationToken).ConfigureAwait(false);
        LogResolved(_logger, resolution.Kind, resolution.Problem, resolution.FromCache);
        return resolution;
    }

    private async Task<IntegrationResolution> ResolveCoreAsync(
        IntegrationNeed need, IReadOnlyList<InstalledIntegration> installed, CancellationToken cancellationToken)
    {
        // Deleting in a connected app is refused by design (P8): nothing is looked at, so nothing is started or found for it.
        if (need.Capability.Action == CapabilityAction.Delete)
        {
            return IntegrationResolution.Refuse(need);
        }

        // A need for a kind of thing, with no app named (step 116), is served by whichever installed integration has a tool that does it.
        var matches = need.IsForAnyApp
            ? installed.Where(integration => ToolsDo(integration, need.Capability)).ToList()
            : installed.Where(integration => IsAbout(integration, need)).ToList();
        if (matches.Count > 0)
        {
            return await ResolveInstalledAsync(need, matches, cancellationToken).ConfigureAwait(false);
        }

        // Nothing installed: what the user already has set up in other programs counts before anything is looked up.
        var available = new List<AvailableIntegration>();
        foreach (var source in _sources)
        {
            try
            {
                available.AddRange(await source.FindAsync(need.AppKey, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // A source that cannot say is one that found nothing.
            }
        }

        return available.Count > 0 ? IntegrationResolution.Local(need, available) : IntegrationResolution.Missing(need);
    }

    private async Task<IntegrationResolution> ResolveInstalledAsync(
        IntegrationNeed need, List<InstalledIntegration> matches, CancellationToken cancellationToken)
    {
        var localOnly = (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Privacy.LocalOnly;
        var problems = new List<(InstalledIntegration Integration, InstalledProblem Problem)>();
        foreach (var integration in matches)
        {
            var verdict = await EvaluateAsync(integration, need, localOnly, cancellationToken).ConfigureAwait(false);
            if (verdict.Problem is null)
            {
                return IntegrationResolution.Use(need, verdict.Integration, verdict.ToolNames, verdict.FromCache);
            }

            problems.Add((verdict.Integration, verdict.Problem.Value));
        }

        // What can be put right comes before an integration that works and has nothing for this, since only the second lets the finder look.
        var chosen = problems.OrderBy(entry => entry.Problem == InstalledProblem.LacksCapability ? 1 : 0).First();
        return IntegrationResolution.NotUsable(need, chosen.Integration, chosen.Problem);
    }

    // Whether the tool names the registry kept for the integration include one that does what the capability asks.
    private static bool ToolsDo(InstalledIntegration integration, IntegrationCapability capability) =>
        CapabilityMatcher.Match(capability, [.. integration.Capabilities.ToolNames.Select(name => new ToolFacts(name))]).Count > 0;

    // Whether the installed integration is the app the need is about: by its name, or by its id.
    private static bool IsAbout(InstalledIntegration integration, IntegrationNeed need) =>
        AppIdentity.IsAbout(integration.Name, need.AppKey) || AppIdentity.KeyOf(integration.Id) == need.AppKey;

    private async Task<Verdict> EvaluateAsync(InstalledIntegration integration, IntegrationNeed need, bool localOnly, CancellationToken cancellationToken)
    {
        if (!integration.Enabled)
        {
            return Verdict.Fail(integration, InstalledProblem.Disabled);
        }

        if (McpConnectionManager.LeavesThisPc(integration) && !integration.Permissions.AllowNetwork)
        {
            return Verdict.Fail(integration, InstalledProblem.NetworkOff);
        }

        if (localOnly && McpConnectionManager.LeavesThisPc(integration))
        {
            return Verdict.Fail(integration, InstalledProblem.BlockedByLocalOnly);
        }

        // A permission set to ask every time is not an obstacle here: the user is asked when a tool of the app is used.
        if (integration.Permissions.RequiredCapability is { } capability && _permissions is not null
            && !(await _permissions.CheckAsync(capability, cancellationToken).ConfigureAwait(false)).CouldBeAllowed)
        {
            return Verdict.Fail(integration, InstalledProblem.PermissionOff);
        }

        if (!integration.Permissions.AllowAccountAccess && integration.Authentication.Kind != IntegrationAuthKind.None)
        {
            return Verdict.Fail(integration, InstalledProblem.AccountAccessOff);
        }

        if (!integration.Permissions.AllowReads && !integration.Permissions.AllowSideEffects)
        {
            return Verdict.Fail(integration, InstalledProblem.AccessOff);
        }

        if (integration.Authentication.State is IntegrationAuthState.NeedsSignIn or IntegrationAuthState.Expired or IntegrationAuthState.Rejected)
        {
            return Verdict.Fail(integration, InstalledProblem.NeedsSignIn);
        }

        if (integration.Health.Status == IntegrationHealthStatus.Incompatible)
        {
            return Verdict.Fail(integration, InstalledProblem.Incompatible);
        }

        // A working integration whose tool names were read recently is answered from the registry: nothing is started and nothing is reached.
        var known = integration.Capabilities;
        if (integration.Health.Status == IntegrationHealthStatus.Healthy && known.ToolNames.Count > 0
            && known.RefreshedAt is { } refreshed && _clock.GetUtcNow() - refreshed < _options.MetadataLifetime)
        {
            return Decide(integration, need, [.. known.ToolNames.Select(name => new ToolFacts(name))], fromCache: true);
        }

        // Otherwise it is connected to once; that lists its tools and records their names for the next request.
        var catalog = await _catalogs.GetCatalogAsync(integration.Id, _options.ConnectBudget, cancellationToken).ConfigureAwait(false);
        if (catalog is null)
        {
            var current = await CurrentAsync(integration, cancellationToken).ConfigureAwait(false);
            return Verdict.Fail(current, ProblemOf(current));
        }

        return Decide(
            integration,
            need,
            [.. catalog.Tools.Select(tool => new ToolFacts(tool.Descriptor.Name, tool.Descriptor.Title, tool.Descriptor.Description))],
            fromCache: false);
    }

    private static Verdict Decide(InstalledIntegration integration, IntegrationNeed need, IReadOnlyList<ToolFacts> tools, bool fromCache)
    {
        var matched = CapabilityMatcher.Match(need.Capability, tools);
        return matched.Count > 0
            ? Verdict.Pass(integration, matched, fromCache)
            : Verdict.Fail(integration, InstalledProblem.LacksCapability);
    }

    // The record as it is now: a failed connection will have recorded why.
    private async Task<InstalledIntegration> CurrentAsync(InstalledIntegration integration, CancellationToken cancellationToken)
    {
        try
        {
            return await _registry.GetAsync(integration.Id, cancellationToken).ConfigureAwait(false) ?? integration;
        }
        catch (IntegrationException)
        {
            return integration;
        }
    }

    private static InstalledProblem ProblemOf(InstalledIntegration integration) =>
        integration.Health.Status switch
        {
            IntegrationHealthStatus.AuthRequired => InstalledProblem.NeedsSignIn,
            IntegrationHealthStatus.Incompatible => InstalledProblem.Incompatible,
            _ => !integration.Enabled ? InstalledProblem.Disabled : InstalledProblem.Unreachable,
        };

    [LoggerMessage(EventId = 3140, Level = LogLevel.Information, Message = "Request for an external app resolved: {Kind}, problem {Problem}, from the kept tool names: {FromCache}")]
    private static partial void LogResolved(ILogger logger, IntegrationResolutionKind kind, InstalledProblem? problem, bool fromCache);

    private readonly record struct Verdict(InstalledIntegration Integration, InstalledProblem? Problem, IReadOnlyList<string> ToolNames, bool FromCache)
    {
        public static Verdict Pass(InstalledIntegration integration, IReadOnlyList<string> toolNames, bool fromCache) => new(integration, null, toolNames, fromCache);

        public static Verdict Fail(InstalledIntegration integration, InstalledProblem problem) => new(integration, problem, [], false);
    }
}
