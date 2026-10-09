namespace Assistant.Core.QuickSearch;

/// <summary>A result with the reasons it is where it is.</summary>
/// <param name="Result">The result.</param>
/// <param name="Match">How its name matches what was typed.</param>
/// <param name="UsageScore">How much the user has run it, from 0 to <see cref="QuickSearchRanker.MaxUsageScore"/>.</param>
/// <param name="Priority">The priority of the provider that found it.</param>
public sealed record QuickSearchRankedResult(QuickSearchResult Result, QuickSearchMatch Match, int UsageScore, int Priority);

/// <summary>The results of one type, best first.</summary>
/// <param name="ResultType">The type.</param>
/// <param name="Title">The group's header words.</param>
/// <param name="Results">The results listed in it, best first.</param>
public sealed record QuickSearchGroup(QuickSearchResultType ResultType, string Title, IReadOnlyList<QuickSearchRankedResult> Results);

/// <summary>Everything quick search found, ordered, with the result that leads and the groups that follow it.</summary>
/// <param name="Ordered">Every result, best first, with duplicates taken out.</param>
/// <param name="TopHit">The result that leads, when one matches the typed words well enough; <see langword="null"/> otherwise.</param>
/// <param name="Groups">The rest, by type in <see cref="QuickSearchGroups.Order"/>, each cut to the number listed. The top hit is not repeated.</param>
public sealed record QuickSearchRanking(
    IReadOnlyList<QuickSearchRankedResult> Ordered, QuickSearchRankedResult? TopHit, IReadOnlyList<QuickSearchGroup> Groups)
{
    /// <summary>Nothing found.</summary>
    public static QuickSearchRanking Empty { get; } = new([], null, []);

    /// <summary>Every result that is listed: the top hit, then each group's.</summary>
    public IReadOnlyList<QuickSearchRankedResult> Listed =>
        [.. (TopHit is null ? [] : new[] { TopHit }).Concat(Groups.SelectMany(group => group.Results))];
}

/// <summary>How a ranking is made up.</summary>
public sealed record QuickSearchRankingOptions
{
    /// <summary>
    /// The type of result the list is narrowed to, or <see langword="null"/> for all. A narrowed list has no top hit and lists more
    /// of the one group (<see cref="QuickSearchGroups.ScopedLimit"/>).
    /// </summary>
    public QuickSearchResultType? Scope { get; init; }

    /// <summary>
    /// The most results listed from each group while every group is listed at once (the user's "Results in each group" setting), or
    /// <see langword="null"/> for each group's own (<see cref="QuickSearchGroups.GlanceLimit"/>). It never changes a narrowed list.
    /// </summary>
    public int? GlanceLimit { get; init; }

    /// <summary>The least good match that leads the list as the top hit.</summary>
    public QuickSearchMatchKind TopHitMinimum { get; init; } = QuickSearchMatchKind.Tokens;
}

/// <summary>
/// Orders what the quick-search providers found, the same way every time, with no model (PROJECT_SPEC §4.1). A result comes before
/// another by, in this order: how well its name matches what was typed (the name is it, begins with it, has each word begin one of
/// its own, has the letters as initials, or only contains it); how much the user has run it, which counts most for what they run
/// often and lately; the priority of the provider that found it; how relevant that provider itself found it; then the shorter name
/// (when something was typed), the name in alphabetical order and last its id, so that two runs over the same results never differ.
/// </summary>
public sealed class QuickSearchRanker
{
    /// <summary>The most a result's use can count for: 60 for how often, 60 for how lately.</summary>
    public const int MaxUsageScore = 120;

    private readonly IReadOnlyDictionary<string, int> _priorities;
    private readonly IQuickSearchUsage? _usage;

    /// <summary>Creates the ranker for the results of <paramref name="providers"/>.</summary>
    /// <param name="providers">The providers, whose priorities settle ties.</param>
    /// <param name="usage">What the user runs; without it nothing counts as used.</param>
    public QuickSearchRanker(IEnumerable<IQuickSearchProvider> providers, IQuickSearchUsage? usage = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _priorities = providers.ToDictionary(provider => provider.Id, provider => provider.Priority, StringComparer.Ordinal);
        _usage = usage;
    }

    /// <summary>
    /// Orders <paramref name="results"/> for <paramref name="query"/>, picks the top hit and groups the rest. A result found twice
    /// (the same id) counts once, as its best-ranked copy.
    /// </summary>
    /// <param name="query">What was typed; empty when a group is only being browsed.</param>
    /// <param name="results">What the providers found.</param>
    /// <param name="now">The moment it is ranked at, for how lately a result was used.</param>
    /// <param name="options">How the ranking is made up; all groups, with a top hit, by default.</param>
    public QuickSearchRanking Rank(
        string? query, IEnumerable<QuickSearchResult> results, DateTimeOffset now, QuickSearchRankingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        options ??= new QuickSearchRankingOptions();

        var ranked = results
            .Where(result => result is not null && (options.Scope is null || result.ResultType == options.Scope))
            .Select(result => new QuickSearchRankedResult(
                result, QuickSearchMatch.Evaluate(query, result), UsageScore(result.Id, now),
                _priorities.GetValueOrDefault(result.ProviderId)))
            .OrderBy(candidate => candidate, Comparer)
            .ToList();

        // The best copy of each id stays.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = ranked.Where(candidate => seen.Add(candidate.Result.Id)).ToList();
        if (ordered.Count == 0)
        {
            return QuickSearchRanking.Empty;
        }

        var topHit = options.Scope is null && ordered[0].Match.Kind >= options.TopHitMinimum ? ordered[0] : null;
        var groups = ordered
            .Where(candidate => !ReferenceEquals(candidate, topHit))
            .GroupBy(candidate => candidate.Result.ResultType)
            .OrderBy(group => QuickSearchGroups.PlaceOf(group.Key))
            .Select(group => new QuickSearchGroup(
                group.Key, QuickSearchGroups.TitleOf(group.Key),
                [.. group.Take(options.Scope is null ? options.GlanceLimit ?? QuickSearchGroups.GlanceLimit(group.Key) : QuickSearchGroups.ScopedLimit)]))
            .ToArray();
        return new QuickSearchRanking(ordered, topHit, groups);
    }

    /// <summary>
    /// How much the user has run <paramref name="resultId"/>: up to 60 for how often (20 for each doubling of the count) and up to 60
    /// for how lately (the last hour, day, week and month count 60, 45, 30 and 15), so that use settles which of two equally
    /// good matches comes first and never makes a poor match beat a good one.
    /// </summary>
    public int UsageScore(string resultId, DateTimeOffset now)
    {
        if (_usage?.Find(resultId) is not { } entry)
        {
            return 0;
        }

        var often = Math.Min(60, (int)Math.Round(20 * Math.Log2(1 + entry.Uses)));
        var age = now - entry.LastUsed;
        var lately = age <= TimeSpan.FromHours(1) ? 60
            : age <= TimeSpan.FromDays(1) ? 45
            : age <= TimeSpan.FromDays(7) ? 30
            : age <= TimeSpan.FromDays(30) ? 15
            : 0;
        return often + lately;
    }

    private static IComparer<QuickSearchRankedResult> Comparer { get; } = System.Collections.Generic.Comparer<QuickSearchRankedResult>.Create(Compare);

    private static int Compare(QuickSearchRankedResult? first, QuickSearchRankedResult? second)
    {
        if (ReferenceEquals(first, second))
        {
            return 0;
        }

        if (first is null || second is null)
        {
            return first is null ? 1 : -1;
        }

        // Best first: a higher number sorts earlier, a lower one later.
        var order = second.Match.Kind.CompareTo(first.Match.Kind);
        if (order != 0)
        {
            return order;
        }

        // Of two equal matches, the one in the title comes before the one in a keyword.
        order = first.Match.ViaKeyword.CompareTo(second.Match.ViaKeyword);
        if (order != 0)
        {
            return order;
        }

        order = second.UsageScore.CompareTo(first.UsageScore);
        if (order != 0)
        {
            return order;
        }

        order = second.Priority.CompareTo(first.Priority);
        if (order != 0)
        {
            return order;
        }

        order = second.Result.Relevance.CompareTo(first.Result.Relevance);
        if (order != 0)
        {
            return order;
        }

        // A shorter name that matches is closer to what was typed. With nothing typed there is no such thing, and the alphabet decides.
        if (first.Match.Kind != QuickSearchMatchKind.None)
        {
            order = first.Result.Title.Length.CompareTo(second.Result.Title.Length);
            if (order != 0)
            {
                return order;
            }
        }

        order = string.Compare(first.Result.Title, second.Result.Title, StringComparison.OrdinalIgnoreCase);
        if (order != 0)
        {
            return order;
        }

        return string.CompareOrdinal(first.Result.Id, second.Result.Id);
    }
}
