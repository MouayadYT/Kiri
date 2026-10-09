using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Search;

/// <summary>
/// The file search service behind the Files permission (PROJECT_SPEC §4.7, §4.9, step 119): every search asks <see cref="IPermissionPolicy"/> first, in the service, so no caller
/// can look through the user's files while Files is off, whatever it checked itself. A search that is not allowed throws <see cref="FileSearchFailure.NotAllowed"/> and has asked
/// Windows Search nothing.
/// </summary>
/// <param name="inner">The service that searches.</param>
/// <param name="permissions">Says whether Files may be used now.</param>
public sealed class PermissionCheckedFileSearchService(IFileSearchService inner, IPermissionPolicy permissions) : IFileSearchService
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(FileSearchQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken).ConfigureAwait(false);
        return await inner.SearchAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<FileSearchOutcome> SearchWithCapabilitiesAsync(FileSearchQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken).ConfigureAwait(false);
        return await inner.SearchWithCapabilitiesAsync(query, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireAsync(CancellationToken cancellationToken)
    {
        if (!(await permissions.CheckAsync(PermissionCapability.Files, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            throw new FileSearchException(FileSearchFailure.NotAllowed);
        }
    }
}
