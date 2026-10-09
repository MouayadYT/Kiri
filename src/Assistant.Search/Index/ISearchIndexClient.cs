using Assistant.Core.Contracts;

namespace Assistant.Search.Index;

/// <summary>
/// Runs one query against the Windows Search index and hands back its rows. It is the only seam between the service and the
/// provider, so everything else is testable without an index.
/// </summary>
internal interface ISearchIndexClient
{
    /// <summary>
    /// Runs <paramref name="sql"/> and returns every row as its values in the order the query selected them, with a missing
    /// value as null.
    /// </summary>
    /// <exception cref="FileSearchException">The index is not available, did not answer in time or failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IReadOnlyList<object?[]>> QueryAsync(string sql, CancellationToken cancellationToken);
}
