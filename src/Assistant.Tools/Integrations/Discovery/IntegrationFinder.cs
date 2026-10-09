using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Integrations;

/// <summary>How the Integration Finder searches and how long it keeps what it found.</summary>
public sealed record IntegrationFinderOptions
{
    /// <summary>The most time one place that lists integrations is given. The official registry has been seen to take tens of seconds.</summary>
    public TimeSpan SourceTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The most time a whole search takes, the model's judgement included.</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(75);

    /// <summary>The most time reading one candidate's README and latest commit is given.</summary>
    public TimeSpan EnrichTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The most candidates a result holds.</summary>
    public int MaxCandidates { get; init; } = 5;

    /// <summary>How many of the best candidates have their README and latest commit read.</summary>
    public int EnrichCount { get; init; } = 3;

    /// <summary>How long a search that found something is kept.</summary>
    public TimeSpan FoundLifetime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How long a search that found nothing that fits is kept (short, since a new integration may appear).</summary>
    public TimeSpan EmptyLifetime { get; init; } = TimeSpan.FromHours(2);

    /// <summary>The most answers kept.</summary>
    public int MaxCacheEntries { get; init; } = 100;
}

/// <summary>The Integration Finder (PROJECT_SPEC §4.8, step 106).</summary>
public interface IIntegrationFinder
{
    /// <summary>
    /// Looks for MCP integrations that do what <paramref name="need"/> asks, and says what it found. It never installs, downloads or runs
    /// anything; a result is a list of leads. It sends nothing while Local Only mode is on or the External Web and Image Search permission is
    /// off (<see cref="DiscoveryStatus.Blocked"/>), and an answer kept from an earlier search sends nothing either.
    /// </summary>
    /// <param name="need">The app and the capability; the only things the search is made from.</param>
    /// <param name="exclude">Origins to leave out of the result (what is already installed, so that it is not offered again).</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IntegrationDiscoveryResult> FindAsync(IntegrationNeed need, IReadOnlyCollection<string>? exclude = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// The MCP Integration Finder (PROJECT_SPEC §4.8, step 106): focused web discovery for an app that has no usable integration. It is a fixed procedure, not an agent. It is
/// asked only for a <see cref="IntegrationNeed"/> and searches with the app's name, one word for what is wanted and the word MCP. It asks, in a
/// trust-aware order, the app maker's own accounts and the official MCP registry and GitHub together (it stops waiting for the slower ones once
/// the maker's own integration is in), and the package registries (npm, PyPI) only when that found nothing about the app that fits. It reads the
/// README and latest commit of the best few GitHub candidates, drops what is not about the app, merges duplicates (never across different levels of
/// trust: a package that merely names a repository is not the repository's), ranks by trust, evidence, maintenance and popularity, and keeps
/// five. The local model may then judge the few that remain (<see cref="ICandidateAssessor"/>); its answer can only reorder them. Fixed hosts,
/// bounded time, bytes and requests; nothing is downloaded, installed or run, and no connector is written. Answers are cached
/// (<see cref="IDiscoveryCache"/>). Logs say counts and outcomes, never the app, the candidates or the request.
/// </summary>
internal sealed partial class IntegrationFinder : IIntegrationFinder
{
    private readonly IReadOnlyList<IIntegrationDiscoverySource> _sources;
    private readonly IRepositoryEnricher _enricher;
    private readonly ICandidateAssessor? _assessor;
    private readonly IDiscoveryCache _cache;
    private readonly ISettingsService _settings;
    private readonly IPermissionPolicy _permissions;
    private readonly TimeProvider _clock;
    private readonly IntegrationFinderOptions _options;
    private readonly ILogger<IntegrationFinder> _logger;

    /// <summary>Creates the finder.</summary>
    public IntegrationFinder(
        IEnumerable<IIntegrationDiscoverySource> sources,
        IRepositoryEnricher enricher,
        ICandidateAssessor? assessor,
        IDiscoveryCache cache,
        ISettingsService settings,
        IPermissionPolicy permissions,
        TimeProvider clock,
        IntegrationFinderOptions options,
        ILogger<IntegrationFinder> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(enricher);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _sources = [.. sources];
        _enricher = enricher;
        _assessor = assessor;
        _cache = cache;
        _settings = settings;
        _permissions = permissions;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IntegrationDiscoveryResult> FindAsync(
        IntegrationNeed need, IReadOnlyCollection<string>? exclude = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(need);
        var now = _clock.GetUtcNow();

        // The two locks come first and nothing is sent, not even to look in the cache of a remote, before both are open.
        if ((await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Privacy.LocalOnly)
        {
            LogBlocked(_logger, DiscoveryBlock.LocalOnly);
            return IntegrationDiscoveryResult.BlockedBy(DiscoveryBlock.LocalOnly, now);
        }

        var permission = await _permissions.CheckAsync(PermissionCapability.ExternalSearch, cancellationToken).ConfigureAwait(false);
        if (!permission.IsAllowed)
        {
            // Set to ask every time and not asked about: the caller that can ask does so before it gets here (step 119); this is a look nobody agreed to.
            var block = permission.NeedsAsking ? DiscoveryBlock.NotAllowedNow : DiscoveryBlock.PermissionOff;
            LogBlocked(_logger, block);
            return IntegrationDiscoveryResult.BlockedBy(block, now);
        }

        var leaveOut = exclude is { Count: > 0 };
        if (!leaveOut && _cache.TryGet(need.CacheKey, now) is { } kept)
        {
            LogFinished(_logger, kept.Status, kept.Candidates.Count, true, 0, 0);
            return kept with { FromCache = true };
        }

        if (DiscoveryQuery.For(need) is not { } query)
        {
            // An app whose name has nothing that can be searched with is one nothing can be asked about.
            return new IntegrationDiscoveryResult { Status = DiscoveryStatus.NothingPlausible, SearchedAt = now };
        }

        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(_options.TotalTimeout);
        IntegrationDiscoveryResult result;
        try
        {
            result = await SearchAsync(need, query, exclude ?? [], now, total.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The whole search ran out of time: nothing was answered in it.
            result = new IntegrationDiscoveryResult { Status = DiscoveryStatus.Failed, SearchedAt = now, SourcesFailed = [.. _sources.Select(source => source.Id)] };
        }

        // Only an answer is kept: a search that could not be made is made again next time.
        if (!leaveOut && result.Status is DiscoveryStatus.Found or DiscoveryStatus.NothingPlausible && result.SourcesAnswered.Count > 0)
        {
            _cache.Put(need.CacheKey, result, now + (result.Status == DiscoveryStatus.Found ? _options.FoundLifetime : _options.EmptyLifetime));
        }

        LogFinished(_logger, result.Status, result.Candidates.Count, false, result.SourcesAnswered.Count, result.SourcesFailed.Count);
        return result;
    }

    private async Task<IntegrationDiscoveryResult> SearchAsync(
        IntegrationNeed need, DiscoveryQuery query, IReadOnlyCollection<string> exclude, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var answered = new List<string>();
        var failed = new List<string>();
        var pool = new List<IntegrationCandidate>();
        var enriched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await RunStageAsync(DiscoveryStage.First, query, pool, answered, failed, cancellationToken).ConfigureAwait(false);
        var candidates = Prepare(pool, query, exclude);
        candidates = await EnrichAsync(candidates, need.Capability, enriched, cancellationToken).ConfigureAwait(false);

        // The package registries are asked when nothing about the app that fits has turned up.
        if (!candidates.Any(candidate => candidate.Evidence >= CapabilityEvidence.Described))
        {
            await RunStageAsync(DiscoveryStage.WhenNeeded, query, pool, answered, failed, cancellationToken).ConfigureAwait(false);
            candidates = Prepare(pool, query, exclude);
            candidates = await EnrichAsync(candidates, need.Capability, enriched, cancellationToken).ConfigureAwait(false);
        }

        candidates = [.. Rank(candidates).Take(_options.MaxCandidates)];
        candidates = await AssessAsync(need, candidates, cancellationToken).ConfigureAwait(false);

        var status = candidates.Count > 0 ? DiscoveryStatus.Found : answered.Count == 0 && failed.Count > 0 ? DiscoveryStatus.Failed : DiscoveryStatus.NothingPlausible;
        return new IntegrationDiscoveryResult
        {
            Status = status,
            Candidates = candidates,
            SourcesAnswered = [.. answered.Distinct(StringComparer.Ordinal)],
            SourcesFailed = [.. failed.Distinct(StringComparer.Ordinal)],
            SearchedAt = now,
        };
    }

    // Asks the sources of a stage together. Each is given its own time. In the first stage, once the app maker's own integration is among the
    // answers the slower sources are no longer waited for, so a slow registry does not hold up an answer that is already the best there is.
    private async Task RunStageAsync(
        DiscoveryStage stage,
        DiscoveryQuery query,
        List<IntegrationCandidate> pool,
        List<string> answered,
        List<string> failed,
        CancellationToken cancellationToken)
    {
        var sources = _sources.Where(source => source.Stage == stage).ToList();
        if (sources.Count == 0)
        {
            return;
        }

        using var stageCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = sources.Select(source => AskAsync(source, query, stageCancel.Token)).ToList();
        while (running.Count > 0)
        {
            var finished = await Task.WhenAny(running).ConfigureAwait(false);
            running.Remove(finished);
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await finished.ConfigureAwait(false);
            if (outcome.Skipped)
            {
                continue;
            }

            if (outcome.Candidates is null)
            {
                failed.Add(outcome.SourceId);
                continue;
            }

            answered.Add(outcome.SourceId);
            pool.AddRange(outcome.Candidates);
            if (stage == DiscoveryStage.First && running.Count > 0 && HasVendorsOwn(pool, query))
            {
                await stageCancel.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<SourceOutcome> AskAsync(IIntegrationDiscoverySource source, DiscoveryQuery query, CancellationToken stageToken)
    {
        using var own = CancellationTokenSource.CreateLinkedTokenSource(stageToken);
        own.CancelAfter(_options.SourceTimeout);
        try
        {
            return new SourceOutcome(source.Id, await source.SearchAsync(query, own.Token).ConfigureAwait(false), false);
        }
        catch (DiscoveryException)
        {
            return new SourceOutcome(source.Id, null, false);
        }
        catch (OperationCanceledException)
        {
            // Out of time is a source that failed; stopped because the stage no longer needs it, or by the caller, is not.
            return new SourceOutcome(source.Id, null, stageToken.IsCancellationRequested);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new SourceOutcome(source.Id, null, false);
        }
    }

    // Whether the app maker's own integration, about the app and still maintained, is already among the candidates.
    private static bool HasVendorsOwn(List<IntegrationCandidate> pool, DiscoveryQuery query) =>
        pool.Any(candidate => candidate.Trust == CandidateTrust.VerifiedVendor && !candidate.Archived && IsAbout(candidate, query));

    // The candidates that are about the app, are not archived or left out, with duplicates merged and the evidence worked out.
    private static List<IntegrationCandidate> Prepare(List<IntegrationCandidate> pool, DiscoveryQuery query, IReadOnlyCollection<string> exclude)
    {
        var kept = new List<IntegrationCandidate>();
        foreach (var candidate in pool.Select(CandidateSanitizer.Clean).OfType<IntegrationCandidate>())
        {
            if (candidate.Archived || !IsAbout(candidate, query) || IsExcluded(candidate, exclude))
            {
                continue;
            }

            var index = kept.FindIndex(other => SameProject(other, candidate));
            kept.Add(candidate);
            if (index >= 0)
            {
                kept[index] = Merge(kept[index], candidate);
                kept.RemoveAt(kept.Count - 1);
            }
        }

        return [.. kept.Select(candidate => candidate with { Evidence = Max(candidate.Evidence, CapabilityMatcher.EvidenceOf(query.Need.Capability, candidate.ToolNames, candidate.Description)) })];
    }

    // The same project found twice. Two candidates of different trust are never one: a package that names a repository as its own is not thereby the repository's.
    private static bool SameProject(IntegrationCandidate a, IntegrationCandidate b) =>
        a.Trust == b.Trust
        && (a.RepositoryUrl is not null && string.Equals(a.RepositoryUrl, b.RepositoryUrl, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    private static IntegrationCandidate Merge(IntegrationCandidate first, IntegrationCandidate second) =>
        first with
        {
            RepositoryUrl = first.RepositoryUrl ?? second.RepositoryUrl,
            Publisher = first.Publisher ?? second.Publisher,
            License = first.License ?? second.License,
            LastActivity = Latest(first.LastActivity, second.LastActivity),
            Stars = first.Stars is null ? second.Stars : second.Stars is null ? first.Stars : Math.Max(first.Stars.Value, second.Stars.Value),
            Description = first.Description ?? second.Description,
            Packages = [.. first.Packages.Concat(second.Packages).DistinctBy(package => (package.Method, package.Identifier)).Take(8)],
            RemoteUrl = first.RemoteUrl ?? second.RemoteUrl,
            Runtime = first.Runtime != CandidateRuntime.Unknown ? first.Runtime : second.Runtime,
            RequiredSecrets = [.. first.RequiredSecrets.Concat(second.RequiredSecrets).Distinct(StringComparer.Ordinal).Take(8)],
            ToolNames = [.. first.ToolNames.Concat(second.ToolNames).Distinct(StringComparer.Ordinal).Take(60)],
            Version = first.Version ?? second.Version,
            CommitSha = first.CommitSha ?? second.CommitSha,
            Evidence = Max(first.Evidence, second.Evidence),
            FoundIn = [.. first.FoundIn.Concat(second.FoundIn).Distinct(StringComparer.Ordinal)],
        };

    // Whether the candidate is about the app: its name, repository or description names it.
    private static bool IsAbout(IntegrationCandidate candidate, DiscoveryQuery query) =>
        AppIdentity.IsAbout(candidate.Name, query.Need.AppKey)
        || AppIdentity.IsAbout(candidate.Description, query.Need.AppKey)
        || candidate.RepositoryUrl is not null && AppIdentity.IsAbout(candidate.RepositoryUrl.Replace("https://github.com/", string.Empty, StringComparison.Ordinal), query.Need.AppKey);

    // An installed integration's origin names what it came from; a candidate that is that is not offered again.
    private static bool IsExcluded(IntegrationCandidate candidate, IReadOnlyCollection<string> exclude) =>
        exclude.Any(origin => !string.IsNullOrWhiteSpace(origin)
            && (Same(origin, candidate.Name) || Same(origin, candidate.SourceUrl) || Same(origin, candidate.RepositoryUrl)
                || candidate.Packages.Any(package => Same(origin, package.Identifier))));

    private static bool Same(string a, string? b) => b is not null && string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    // Reads the README and latest commit of the best few candidates that live on GitHub and have not been read yet.
    private async Task<List<IntegrationCandidate>> EnrichAsync(
        List<IntegrationCandidate> candidates, IntegrationCapability capability, HashSet<string> enriched, CancellationToken cancellationToken)
    {
        var chosen = Rank(candidates)
            .Where(candidate => candidate.RepositoryUrl is not null && !enriched.Contains(candidate.RepositoryUrl))
            .Take(Math.Max(0, _options.EnrichCount - enriched.Count))
            .ToList();
        if (chosen.Count == 0)
        {
            return candidates;
        }

        foreach (var candidate in chosen)
        {
            enriched.Add(candidate.RepositoryUrl!);
        }

        var results = await Task.WhenAll(chosen.Select(candidate => EnrichOneAsync(candidate, capability, cancellationToken))).ConfigureAwait(false);
        var replacements = chosen.Zip(results).ToDictionary(pair => pair.First.Name, pair => pair.Second, StringComparer.OrdinalIgnoreCase);
        return [.. candidates.Select(candidate => replacements.TryGetValue(candidate.Name, out var richer) ? richer : candidate)];
    }

    private async Task<IntegrationCandidate> EnrichOneAsync(IntegrationCandidate candidate, IntegrationCapability capability, CancellationToken cancellationToken)
    {
        using var own = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        own.CancelAfter(_options.EnrichTimeout);
        try
        {
            return CandidateSanitizer.Clean(await _enricher.EnrichAsync(candidate, capability, own.Token).ConfigureAwait(false)) ?? candidate;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Too slow: the candidate is kept as it was.
            return candidate;
        }
    }

    // The local model judges the few that remain, unless their own tool lists already settle it. Its answer reorders them and changes nothing else.
    private async Task<List<IntegrationCandidate>> AssessAsync(IntegrationNeed need, List<IntegrationCandidate> candidates, CancellationToken cancellationToken)
    {
        if (_assessor is null || candidates.All(candidate => candidate.Evidence == CapabilityEvidence.ToolListed) || candidates.Count == 0)
        {
            return candidates;
        }

        IReadOnlyList<CandidateAssessment>? judged;
        try
        {
            judged = await _assessor.AssessAsync(need, candidates, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return candidates;
        }

        if (judged is null || judged.Count != candidates.Count)
        {
            return candidates;
        }

        // A candidate whose own tool list names the capability is not demoted by a small model's doubt.
        var assessed = candidates.Select((candidate, index) => candidate with
        {
            Assessment = candidate.Evidence == CapabilityEvidence.ToolListed && judged[index] == CandidateAssessment.DoesNotSupport ? CandidateAssessment.NotJudged : judged[index],
        });
        return [.. Rank(assessed)];
    }

    // Best first: what the model found does not fit comes last, then the app maker's own, then a claim of being official, then the evidence, the upkeep and the popularity.
    private List<IntegrationCandidate> Rank(IEnumerable<IntegrationCandidate> candidates) =>
        [.. candidates
            .OrderBy(candidate => candidate.Assessment == CandidateAssessment.DoesNotSupport ? 1 : 0)
            .ThenByDescending(candidate => candidate.Trust)
            .ThenByDescending(candidate => candidate.Assessment == CandidateAssessment.Supports ? 1 : 0)
            .ThenByDescending(candidate => candidate.Evidence)
            .ThenByDescending(candidate => Upkeep(candidate))
            .ThenByDescending(candidate => Math.Log10((candidate.Stars ?? 0) + 1))
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)];

    // How recently it was changed: within three months, within a year, older or unknown.
    private int Upkeep(IntegrationCandidate candidate)
    {
        if (candidate.LastActivity is not { } at)
        {
            return 0;
        }

        var age = _clock.GetUtcNow() - at;
        return age <= TimeSpan.FromDays(90) ? 2 : age <= TimeSpan.FromDays(365) ? 1 : 0;
    }

    private static CapabilityEvidence Max(CapabilityEvidence a, CapabilityEvidence b) => a >= b ? a : b;

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    [LoggerMessage(EventId = 3151, Level = LogLevel.Information, Message = "Integration search not allowed: {Block}")]
    private static partial void LogBlocked(ILogger logger, DiscoveryBlock block);

    [LoggerMessage(EventId = 3152, Level = LogLevel.Information, Message = "Integration search ended: {Status}, {Candidates} candidates, from the cache: {FromCache}, {Answered} places answered, {Failed} failed")]
    private static partial void LogFinished(ILogger logger, DiscoveryStatus status, int candidates, bool fromCache, int answered, int failed);

    private readonly record struct SourceOutcome(string SourceId, IReadOnlyList<IntegrationCandidate>? Candidates, bool Skipped);
}
