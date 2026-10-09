using System.Diagnostics;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.History;
using Assistant.Search.Index;
using Microsoft.Extensions.Logging;

namespace Assistant.Search;

/// <summary>
/// Finds files and folders through the Windows Search index (PROJECT_SPEC §4.7) and never walks the disk: it asks the index
/// the user's own Windows already keeps, as the current user, and answers with what it returns. Everything about the
/// provider (the connection, the SQL, the escaping, its value types) stays inside this module; callers see only
/// <see cref="FileSearchQuery"/>, <see cref="SearchResultItem"/> and <see cref="FileSearchException"/>.
/// </summary>
/// <remarks>
/// The <c>file:</c> scope only, so mail and other protocol handlers are not searched. Hidden and system items and everything
/// under the excluded folders of the settings are left out. Apps are not files and are not found here. A query is never
/// logged, and a location that is not indexed simply has no results: there is no crawler to fall back on. A content search
/// finds paths and properties only; what the index holds of a file's text is never read back out of it.
/// </remarks>
public sealed class WindowsFileSearchService : IFileSearchService
{
    /// <summary>How many results a query returns of each kind, at most, whatever it asks for.</summary>
    public const int MaxResultsPerType = 50;

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    // Candidates are fetched several to a result, because hidden and excluded items are removed and the best names chosen
    // among the rest after the index answers.
    private const int CandidatesPerResult = 8;
    private const int MinCandidates = 40;
    private const int MaxCandidates = 300;

    private readonly ISettingsService _settings;
    private readonly ISearchIndexClient _index;
    private readonly IContentTypeCatalog _contentTypes;
    private readonly ILogger _logger;

    /// <summary>Creates the service over the Windows Search index of this PC.</summary>
    public WindowsFileSearchService(ISettingsService settings, ILogger<WindowsFileSearchService> logger)
        : this(settings, new OleDbSearchIndexClient(QueryTimeout), logger)
    {
    }

    internal WindowsFileSearchService(
        ISettingsService settings,
        ISearchIndexClient index,
        ILogger logger,
        IContentTypeCatalog? contentTypes = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _contentTypes = contentTypes ?? new RegistryContentTypeCatalog();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        FileSearchQuery query,
        CancellationToken cancellationToken = default) =>
        (await RunAsync(query, withCapabilities: false, cancellationToken).ConfigureAwait(false)).Items;

    /// <inheritdoc/>
    public Task<FileSearchOutcome> SearchWithCapabilitiesAsync(
        FileSearchQuery query,
        CancellationToken cancellationToken = default) =>
        RunAsync(query, withCapabilities: true, cancellationToken);

    private async Task<FileSearchOutcome> RunAsync(FileSearchQuery query, bool withCapabilities, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        // A query nothing can match asks the index nothing, and neither does one for a folder the user has excluded.
        if (FileSearchPlan.Create(query, MaxResultsPerType) is not { } plan)
        {
            return Nothing(query);
        }

        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var excluded = ExcludedFolders.Create(settings.Privacy.ExcludedFolders);
        if (plan.Scope is not null && excluded.Contains(plan.Scope.Path))
        {
            return Nothing(query);
        }

        var top = Math.Clamp(plan.Limit * CandidatesPerResult, MinCandidates, MaxCandidates);

        var clock = Stopwatch.StartNew();
        var results = new List<SearchResultItem>();
        var candidates = 0;
        bool? locationHoldsItems = null;
        try
        {
            foreach (var type in plan.Types)
            {
                var sql = SearchSqlBuilder.Build(type, plan, top, excluded);
                var rows = await _index.QueryAsync(sql, cancellationToken).ConfigureAwait(false);
                candidates += rows.Count;
                results.AddRange(Best(rows, type, plan, excluded));
            }

            // Whether there is anything to look inside is asked only when a content search in a folder wants to say so.
            if (withCapabilities && plan.ContentRequested && plan.Scope is not null)
            {
                var probe = await _index
                    .QueryAsync(SearchSqlBuilder.BuildLocationProbe(plan.Scope), cancellationToken)
                    .ConfigureAwait(false);
                locationHoldsItems = probe.Count > 0;
            }
        }
        catch (FileSearchException exception)
        {
            SearchLog.Failed(_logger, exception.Failure, exception.ProviderErrorCode ?? 0, clock.ElapsedMilliseconds);
            throw;
        }

        SearchLog.Completed(_logger, results.Count, candidates, plan.Types.Count, clock.ElapsedMilliseconds);

        var capability = withCapabilities
            ? ContentSearchAssessor.Assess(plan, _contentTypes, locationHoldsItems)
            : ContentSearchCapability.NotRequested;
        if (capability.IsLimited)
        {
            SearchLog.ContentSearchLimited(_logger, capability.Support, capability.Limits);
        }

        return new FileSearchOutcome(results, capability);
    }

    /// <summary>The answer to a query that is not put to the index: no results, and nothing said about the index's limits.</summary>
    private static FileSearchOutcome Nothing(FileSearchQuery query) => new(
        [],
        FileSearchPlan.AsksForContents(query) ? ContentSearchCapability.Available : ContentSearchCapability.NotRequested);

    /// <summary>
    /// The best <see cref="FileSearchPlan.Limit"/> of the rows that may be shown. Ordered by relevance, the name that fits the
    /// words best comes first, and among equals the order the index gave (best rank, then newest). Any other order is the
    /// index's own, which is already fixed by the property asked for. Nothing appears twice.
    /// </summary>
    private static IEnumerable<SearchResultItem> Best(
        IReadOnlyList<object?[]> rows,
        SearchResultItemType type,
        FileSearchPlan plan,
        ExcludedFolders excluded)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = rows
            .Select(row => SearchRowMapper.Map(row, type, excluded))
            .OfType<SearchResultItem>()
            .Where(item => seen.Add(item.Path))

            // An excerpt is what the caller opted into with MatchContents. A content term finds paths and properties only.
            .Select(item => plan.MatchContents || item.Snippet is null ? item : item with { Snippet = null });

        if (plan.Order != FileSearchOrder.Relevance)
        {
            return items.Take(plan.Limit);
        }

        var terms = plan.RelevanceTerms;
        return items
            .Select((item, order) => (Item: item, Score: NameRelevance.Score(item, terms), Order: order))
            .OrderBy(entry => entry.Score)
            .ThenBy(entry => entry.Order)
            .Take(plan.Limit)
            .Select(entry => entry.Item);
    }
}
