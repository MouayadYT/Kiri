using Assistant.Core.ImageSearch;

namespace Assistant.Core.Contracts;

/// <summary>
/// Searches the web with a picture, for matches (PROJECT_SPEC §4.6): the optional, online half of Visual Intelligence, and the one part
/// of the Assistant that sends something off this PC. The assistant knows only this contract, never a provider: a provider is
/// an implementation that is chosen and set up, and the core works the same with another. A search is never started by the Assistant on
/// its own, is off while Local Only mode is on, and needs the user's say-so for each picture (<see cref="ImageSearchFlow"/>,
/// <see cref="ImageSearchConsent"/>).
/// </summary>
public interface IImageSearchService
{
    /// <summary>The provider's name, which the results are headed with, such as "Google".</summary>
    string ProviderName { get; }

    /// <summary>
    /// Whether this only makes up sample results, because no real provider is set up: it sends nothing anywhere, and its results say
    /// so. A real provider is <see langword="false"/>.
    /// </summary>
    bool IsSample { get; }

    /// <summary>
    /// Searches with the picture in <paramref name="request"/>. A provider that sends the picture off this PC must first consume the
    /// request's consent (<see cref="ImageSearchConsent.Consume"/>, with <see cref="SendsImageOffPc"/>), and sends nothing else of the user's.
    /// </summary>
    /// <exception cref="ImageSearchNotConfirmedException">The request's consent is spent, stale, for another picture or not given.</exception>
    /// <exception cref="ImageSearchException">The search could not be done.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ImageSearchResults> SearchAsync(ImageSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Whether a search sends the picture off this PC: unless this is a sample, it does.</summary>
    bool SendsImageOffPc => !IsSample;
}

/// <summary>Asks the user whether a picture may be sent off this PC (PROJECT_SPEC §3.4): the explicit user action an image search needs.</summary>
public interface IImageSearchConfirmation
{
    /// <summary>
    /// Shows the user the picture and who it would be sent to, and asks. Returns <see langword="true"/> only when they said yes; closing
    /// the question, or anything else, is no.
    /// </summary>
    Task<bool> ConfirmAsync(ImageSearchDisclosure disclosure, CancellationToken cancellationToken = default);
}
