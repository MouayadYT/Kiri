namespace Assistant.Core.QuickSearch;

/// <summary>
/// Finds one type of result for what the user is typing in the Search or Ask bar, fast and on this PC (PROJECT_SPEC §4.1). Providers
/// do not know each other: the <see cref="IQuickSearchCoordinator"/> runs them side by side, and the ranking orders what they
/// return. A provider is asked on every pause in the typing, so it must be quick, must never use the model, and must never throw
/// because it found nothing or could not look (that is an empty answer).
/// </summary>
/// <remarks>
/// What is asked and what is found is private content (PROJECT_SPEC §3.2): a provider never logs either.
/// </remarks>
public interface IQuickSearchProvider
{
    /// <summary>The provider's stable identity (<c>applications</c>, <c>files</c>); no two providers in one coordinator share it.</summary>
    string Id { get; }

    /// <summary>The provider's name for the user, as its chip reads.</summary>
    string DisplayName { get; }

    /// <summary>The type of every result it returns, which is also the group they are listed in.</summary>
    QuickSearchResultType ResultType { get; }

    /// <summary>
    /// How much the provider's results are preferred when the ranking cannot tell two apart (the same kind of match, the same
    /// use): the higher wins. From 0 to 100.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// The fewest characters the query must have before the provider is asked at all. Zero for a provider that has something to
    /// list for an empty query, which is how a category is browsed.
    /// </summary>
    int MinimumQueryLength { get; }

    /// <summary>
    /// How long the typing must pause before a typed query is put to the provider: nothing for one that only looks in memory, a
    /// little for one that asks an index. A query the user submitted, and an empty one that only browses a list, are never delayed.
    /// </summary>
    TimeSpan Debounce => TimeSpan.Zero;

    /// <summary>
    /// Finds the results for <paramref name="request"/>, at most <see cref="QuickSearchRequest.MaxResults"/> of them, the best
    /// first. It is cancelled as soon as the query changes.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IReadOnlyList<QuickSearchResult>> SearchAsync(QuickSearchRequest request, CancellationToken cancellationToken);
}
