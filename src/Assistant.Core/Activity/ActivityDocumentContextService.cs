using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Core.Domain;

namespace Assistant.Core.Activity;

/// <summary>
/// Reports the reading of every file made through another <see cref="IDocumentContextService"/> to the
/// <see cref="IActivityTracker"/>, for as long as it runs, so the Searching chip says "Reading" while a long document is opened and
/// the user can cancel it. Neither the path, the question nor the text is passed on to the tracker.
/// </summary>
public sealed class ActivityDocumentContextService(IDocumentContextService inner, IActivityTracker tracker) : IDocumentContextService
{
    /// <summary>What the chip says while a file is read.</summary>
    public const string StatusText = "Reading";

    /// <inheritdoc/>
    public async Task<DocumentContextResult> GetContextAsync(
        string filePath, string question, DocumentContextOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var activity = tracker.Begin(ActivityKind.FileSearch, StatusText, cancellationToken);
        return await inner.GetContextAsync(filePath, question, options, activity.CancellationToken).ConfigureAwait(false);
    }
}
