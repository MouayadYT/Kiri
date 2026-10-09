using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.QuickSearch;

/// <summary>
/// Runs the quick-search providers side by side for what the user is typing (PROJECT_SPEC §4.1) and reports each provider's answer
/// the moment it arrives, so the fast ones (applications, actions) are on screen before the slow one (the file index) has answered.
/// </summary>
public interface IQuickSearchCoordinator
{
    /// <summary>The providers, in the order their outcomes are returned.</summary>
    IReadOnlyList<IQuickSearchProvider> Providers { get; }

    /// <summary>
    /// Asks every provider that wants the query at once. A provider that is not asked (the query is too short, or its type is not
    /// wanted), is late or fails does not hold up or fail the others. <paramref name="progress"/> is told of each provider's
    /// outcome as it ends, from whichever thread that provider ended on, and never after the search has been cancelled.
    /// </summary>
    /// <returns>Every provider's outcome, in the order the providers are registered.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled, which is how a newer query
    /// stops an older one: providers still waiting out their debounce are never asked.</exception>
    Task<QuickSearchOutcome> SearchAsync(
        QuickSearchRequest request, IProgress<QuickSearchProviderOutcome>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The app's <see cref="IQuickSearchCoordinator"/>: runs the providers concurrently, each after its own debounce and each under a
/// time limit, and isolates their failures. What a provider found is trimmed to the number asked for and always carries that
/// provider's identity and type, so a provider cannot list results under another's name.
/// </summary>
public sealed class QuickSearchCoordinator : IQuickSearchCoordinator
{
    /// <summary>How long a provider may take before its answer is dropped.</summary>
    public static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _clock;
    private readonly TimeSpan _providerTimeout;
    private readonly ILogger _logger;

    /// <summary>Creates the coordinator over <paramref name="providers"/>.</summary>
    /// <param name="providers">The providers; each has an id of its own.</param>
    /// <param name="clock">The clock the debounce and the time limit run on; the system's by default.</param>
    /// <param name="providerTimeout">How long a provider may take; <see cref="DefaultProviderTimeout"/> by default.</param>
    /// <param name="logger">Where a provider's failure is noted, by its id and the type of its exception only.</param>
    /// <exception cref="ArgumentException">Two providers have the same id.</exception>
    public QuickSearchCoordinator(
        IEnumerable<IQuickSearchProvider> providers, TimeProvider? clock = null, TimeSpan? providerTimeout = null,
        ILogger<QuickSearchCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        Providers = [.. providers];
        if (Providers.Select(provider => provider.Id).Distinct(StringComparer.Ordinal).Count() != Providers.Count)
        {
            throw new ArgumentException("Two quick-search providers have the same id.", nameof(providers));
        }

        if (Providers.Any(provider => string.IsNullOrWhiteSpace(provider.Id)))
        {
            throw new ArgumentException("A quick-search provider has no id.", nameof(providers));
        }

        _clock = clock ?? TimeProvider.System;
        _providerTimeout = providerTimeout ?? DefaultProviderTimeout;
        if (_providerTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(providerTimeout), "The time limit must be positive.");
        }

        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <inheritdoc/>
    public IReadOnlyList<IQuickSearchProvider> Providers { get; }

    /// <inheritdoc/>
    public async Task<QuickSearchOutcome> SearchAsync(
        QuickSearchRequest request, IProgress<QuickSearchProviderOutcome>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var query = (request.Query ?? "").Trim();
        var maxResults = Math.Clamp(request.MaxResults, 1, QuickSearchRequest.HardMaxResults);
        var asked = request with { Query = query, MaxResults = maxResults };

        // Each provider starts on the thread pool, so what one does before its first wait never holds up another, or the caller.
        var tasks = Providers.Select(provider => Task.Run(
            () => RunAsync(provider, asked, progress, cancellationToken), CancellationToken.None)).ToArray();
        var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);

        // A search that was cancelled has no answer, whatever the providers made of it.
        cancellationToken.ThrowIfCancellationRequested();
        return new QuickSearchOutcome(outcomes);
    }

    private async Task<QuickSearchProviderOutcome> RunAsync(
        IQuickSearchProvider provider, QuickSearchRequest request, IProgress<QuickSearchProviderOutcome>? progress,
        CancellationToken cancellationToken)
    {
        if (request.Types is { } wanted && !wanted.Contains(provider.ResultType))
        {
            return Skipped(provider, QuickSearchProviderStatus.NotWanted);
        }

        if (request.Query.Length < provider.MinimumQueryLength)
        {
            return Skipped(provider, QuickSearchProviderStatus.QueryTooShort);
        }

        // Cancelled by the next keystroke: a provider whose wait is cut short is never asked. Nothing is typed when a list is only being
        // browsed, and nothing is waited for then.
        var debounce = request.IsDeliberate || request.Query.Length == 0 ? TimeSpan.Zero : provider.Debounce;
        if (debounce > TimeSpan.Zero)
        {
            await Task.Delay(debounce, _clock, cancellationToken).ConfigureAwait(false);
        }

        var clock = Stopwatch.StartNew();
        var outcome = await AskAsync(provider, request, clock, cancellationToken).ConfigureAwait(false);
        if (!cancellationToken.IsCancellationRequested)
        {
            progress?.Report(outcome);
        }

        return outcome;
    }

    private async Task<QuickSearchProviderOutcome> AskAsync(
        IQuickSearchProvider provider, QuickSearchRequest request, Stopwatch clock, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_providerTimeout, _clock);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var found = await provider.SearchAsync(request, limit.Token).ConfigureAwait(false);
            var results = Normalize(provider, found, request.MaxResults);
            return new QuickSearchProviderOutcome(
                provider.Id, provider.ResultType, QuickSearchProviderStatus.Completed, results, clock.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled search has no outcome: it ends the whole search.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Not the caller's doing, so the provider's own time ran out.
            QuickSearchLog.TimedOut(_logger, provider.Id, clock.ElapsedMilliseconds);
            return Ended(provider, QuickSearchProviderStatus.TimedOut, clock);
        }
        catch (Exception exception)
        {
            // Only the type: an exception's message can hold the query or a path.
            QuickSearchLog.Failed(_logger, provider.Id, exception.GetType().Name);
            return Ended(provider, QuickSearchProviderStatus.Failed, clock);
        }
    }

    // What the provider found, as many as were asked for, each marked with the provider that found it.
    private static IReadOnlyList<QuickSearchResult> Normalize(
        IQuickSearchProvider provider, IReadOnlyList<QuickSearchResult>? found, int maxResults) =>
        found is null
            ? []
            : [.. found
                .Where(result => result is not null)
                .Take(maxResults)
                .Select(result => result with { ProviderId = provider.Id, ResultType = provider.ResultType })];

    private static QuickSearchProviderOutcome Skipped(IQuickSearchProvider provider, QuickSearchProviderStatus status) =>
        new(provider.Id, provider.ResultType, status, [], TimeSpan.Zero);

    private static QuickSearchProviderOutcome Ended(IQuickSearchProvider provider, QuickSearchProviderStatus status, Stopwatch clock) =>
        new(provider.Id, provider.ResultType, status, [], clock.Elapsed);
}

/// <summary>Quick-search log messages: a provider's id and what happened, never the query or what was found.</summary>
internal static partial class QuickSearchLog
{
    [LoggerMessage(EventId = 6100, Level = LogLevel.Debug, Message = "Quick-search provider {Component} timed out after {ElapsedMs} ms")]
    public static partial void TimedOut(ILogger logger, string component, long elapsedMs);

    [LoggerMessage(EventId = 6101, Level = LogLevel.Warning, Message = "Quick-search provider {Component} failed: {ExceptionType}")]
    public static partial void Failed(ILogger logger, string component, string exceptionType);
}
