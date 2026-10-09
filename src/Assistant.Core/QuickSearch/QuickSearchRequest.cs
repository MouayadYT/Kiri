using System.Text;

namespace Assistant.Core.QuickSearch;

/// <summary>
/// What a quick search is asked for: the words typed, how many results a provider may return, whether the user submitted them, and
/// which types of result are wanted. The text is private content (PROJECT_SPEC §3.2), so it is never printed or logged.
/// </summary>
/// <param name="Query">The words, with the whitespace around them taken off. Empty when a category is being browsed.</param>
public sealed record QuickSearchRequest(string Query)
{
    /// <summary>The most results one provider returns for a typed query: the list is only ever a glance.</summary>
    public const int DefaultMaxResults = 8;

    /// <summary>The most results one provider may be asked for, however a category is browsed.</summary>
    public const int HardMaxResults = 50;

    /// <summary>The most results a provider returns; from 1 to <see cref="HardMaxResults"/>.</summary>
    public int MaxResults { get; init; } = DefaultMaxResults;

    /// <summary>
    /// Whether the user submitted the words rather than just typed them: they are put to every provider at once, with nothing
    /// waited for, and a provider may do the work it leaves out while typing.
    /// </summary>
    public bool IsDeliberate { get; init; }

    /// <summary>The types of result wanted, or <see langword="null"/> for all of them.</summary>
    public IReadOnlySet<QuickSearchResultType>? Types { get; init; }

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Length = {Query?.Length ?? 0}, MaxResults = {MaxResults}, IsDeliberate = {IsDeliberate}");
        return true;
    }
}

/// <summary>How a provider's part of a quick search ended.</summary>
public enum QuickSearchProviderStatus
{
    /// <summary>It answered, with results or none.</summary>
    Completed = 0,

    /// <summary>It was not asked: the query was shorter than its <see cref="IQuickSearchProvider.MinimumQueryLength"/>.</summary>
    QueryTooShort = 1,

    /// <summary>It was not asked: the search wanted other types of result.</summary>
    NotWanted = 2,

    /// <summary>It did not answer within the time the coordinator gives a provider, so what it had is dropped.</summary>
    TimedOut = 3,

    /// <summary>It threw. A provider that fails is an empty answer and never stops the others.</summary>
    Failed = 4,
}

/// <summary>What one provider answered to a quick search.</summary>
/// <param name="ProviderId">The provider.</param>
/// <param name="ResultType">Its type of result.</param>
/// <param name="Status">How it ended.</param>
/// <param name="Results">What it found, the best first; empty unless the status is <see cref="QuickSearchProviderStatus.Completed"/>.</param>
/// <param name="Elapsed">How long it took, from the moment it was started (a debounce wait is not counted).</param>
public sealed record QuickSearchProviderOutcome(
    string ProviderId, QuickSearchResultType ResultType, QuickSearchProviderStatus Status,
    IReadOnlyList<QuickSearchResult> Results, TimeSpan Elapsed)
{
    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"ProviderId = {ProviderId}, Status = {Status}, Results = {Results.Count}");
        return true;
    }
}

/// <summary>What a quick search came to: every provider's answer, in the order the providers were registered.</summary>
/// <param name="Providers">Each provider's outcome.</param>
public sealed record QuickSearchOutcome(IReadOnlyList<QuickSearchProviderOutcome> Providers)
{
    /// <summary>Every result of every provider, provider by provider.</summary>
    public IReadOnlyList<QuickSearchResult> Results => [.. Providers.SelectMany(provider => provider.Results)];
}
