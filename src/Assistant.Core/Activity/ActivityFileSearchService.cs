using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Core.Activity;

/// <summary>
/// Reports every search made through another <see cref="IFileSearchService"/> to the <see cref="IActivityTracker"/>
/// as Windows Search activity, for as long as it runs. The query is never passed on.
/// </summary>
public sealed class ActivityFileSearchService(IFileSearchService inner, IActivityTracker tracker) : IFileSearchService
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        FileSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        using var activity = tracker.Begin(ActivityKind.WindowsSearch, cancellationToken: cancellationToken);
        return await inner.SearchAsync(query, activity.CancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<FileSearchOutcome> SearchWithCapabilitiesAsync(
        FileSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        using var activity = tracker.Begin(ActivityKind.WindowsSearch, cancellationToken: cancellationToken);
        return await inner.SearchWithCapabilitiesAsync(query, activity.CancellationToken).ConfigureAwait(false);
    }
}
