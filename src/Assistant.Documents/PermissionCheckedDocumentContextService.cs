using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Core.Domain;

namespace Assistant.Documents;

/// <summary>
/// Reading a document behind the Files permission (PROJECT_SPEC §4.7, §4.9, step 119): the service asks <see cref="IPermissionPolicy"/> itself before it opens any file, so a file
/// is never read for a question while Files is off, whoever asks. It answers <see cref="DocumentReadStatus.NotAllowed"/> and has opened nothing.
/// </summary>
/// <param name="inner">The service that reads the document.</param>
/// <param name="permissions">Says whether Files may be used now.</param>
public sealed class PermissionCheckedDocumentContextService(IDocumentContextService inner, IPermissionPolicy permissions) : IDocumentContextService
{
    /// <inheritdoc/>
    public async Task<DocumentContextResult> GetContextAsync(
        string filePath, string question, DocumentContextOptions? options = null, CancellationToken cancellationToken = default) =>
        (await permissions.CheckAsync(PermissionCapability.Files, cancellationToken).ConfigureAwait(false)).IsAllowed
            ? await inner.GetContextAsync(filePath, question, options, cancellationToken).ConfigureAwait(false)
            : DocumentContextResult.Failed(DocumentReadStatus.NotAllowed);
}
