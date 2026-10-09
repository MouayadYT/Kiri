using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>Finds local apps, files and folders through the Windows Search index.</summary>
public interface IFileSearchService
{
    /// <summary>
    /// Runs <paramref name="query"/> as the current user. Results omit hidden and system files and anything under
    /// the excluded folders.
    /// </summary>
    /// <exception cref="FileSearchException">Windows Search is not available, timed out or failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        FileSearchQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="query"/> like <see cref="SearchAsync"/> and also says whether the index can look inside the files
    /// it covers, when the query has a content criterion. The results are the same either way; what the flag adds is that an
    /// empty answer from a location or a file type the index keeps no text for is not taken for "no file contains it".
    /// </summary>
    /// <exception cref="FileSearchException">Windows Search is not available, timed out or failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<FileSearchOutcome> SearchWithCapabilitiesAsync(
        FileSearchQuery query,
        CancellationToken cancellationToken = default);
}
