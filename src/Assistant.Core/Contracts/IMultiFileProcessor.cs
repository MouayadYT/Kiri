using Assistant.Core.MultiFile;

namespace Assistant.Core.Contracts;

/// <summary>
/// Makes the files a question is asked about its context (PROJECT_SPEC §5.5, several files), however many and however long, without
/// putting all of their text into one prompt. Each file is read (a few at a time) and the passages the question needs are chosen;
/// when they fit one prompt they go to the model with the question, and when they do not, each file's passages are cut into pieces,
/// the local model takes short notes on each piece on its own for the question (map), notes are combined when there are too many to
/// read at once (reduce), and the answer is put together from the notes. A question about the whole of one long file ("summarize
/// it") is read the same way, so all of it is read and not a sample. The number of files, the reads and model requests running at
/// once, the requests in all and the text held in memory are all bounded (<see cref="MultiFileLimits"/>).
/// </summary>
/// <remarks>
/// It reads whatever files it is given: the Files permission is the caller's to check. It never logs a path, a name, the question, the
/// files' text or the notes; the notes live in memory only, as context of the question. Asking the model the question itself is the
/// orchestrator's (<see cref="IAssistantOrchestrator"/>), with the context this returns.
/// </remarks>
public interface IMultiFileProcessor
{
    /// <summary>Reads <paramref name="files"/> for <paramref name="question"/> and returns what to ask the model with.</summary>
    /// <param name="files">The files, in the order the user gave them; the same file twice is read once.</param>
    /// <param name="question">What the user asked about them.</param>
    /// <param name="cancellationToken">Stops the work, reading or taking notes.</param>
    /// <returns>
    /// The context, what became of each file (a file that cannot be read is a status, never an exception, and the rest are still used)
    /// and the words that tell the user how they were read.
    /// </returns>
    /// <exception cref="OperationCanceledException">The work was stopped, by <paramref name="cancellationToken"/> or by the user.</exception>
    /// <exception cref="ModelHosting.ModelHostException">The model could not take notes.</exception>
    Task<MultiFileResult> ProcessAsync(IReadOnlyList<QuestionFile> files, string question, CancellationToken cancellationToken = default);
}
