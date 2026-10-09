using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>
/// The model's part in finding files (PROJECT_SPEC §4.7): when no name holds every word of a request, the files whose names hold
/// some of them are put in front of the local model, which says which of them the request could mean ("the pequots book" is the
/// PDF named "The Pequots in southern New England…", though no name holds "book"). It can only pick from the list it is given,
/// by number: it cannot name a file, a folder or a query of its own.
/// </summary>
public interface IFileMatchReviewer
{
    /// <summary>
    /// Picks the <paramref name="candidates"/> that <paramref name="request"/> could mean, in the order the candidates were given;
    /// an empty list when the model judged that none fits. <see langword="null"/> when the model could not judge: none is set up,
    /// it did not answer in time, or it did not answer in the form that was asked for. It never returns an item that was not a
    /// candidate, and throws only when cancelled.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IReadOnlyList<SearchResultItem>?> ChooseAsync(
        string request,
        IReadOnlyList<SearchResultItem> candidates,
        CancellationToken cancellationToken = default);
}
