using Assistant.Core.Documents;

namespace Assistant.Core.Contracts;

/// <summary>
/// Makes one file the context of a question (PROJECT_SPEC §4.7): reads its text with the reader for its type, cuts it into
/// passages, and selects those that the question needs, laid out for a prompt with where each comes from. It is what a
/// question about an attached file asks; the answer still goes through the orchestrator.
/// </summary>
/// <remarks>
/// The service reads whatever file it is given: it does not check the Files permission, which belongs to the caller that got
/// the file from the user (§4.9), and it does not call a model. Nothing it logs holds a path, a name, the question or any text.
/// </remarks>
public interface IDocumentContextService
{
    /// <summary>Reads the file at <paramref name="filePath"/> and selects the passages of it that best answer <paramref name="question"/>.</summary>
    /// <param name="filePath">The full path of the file.</param>
    /// <param name="question">What the user asked about the file.</param>
    /// <param name="options">The limits to work within, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>
    /// The text for the prompt, or a status that says why the file gave none (a file that cannot be read is a status, never an
    /// exception).
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<DocumentContextResult> GetContextAsync(
        string filePath, string question, DocumentContextOptions? options = null, CancellationToken cancellationToken = default);
}
