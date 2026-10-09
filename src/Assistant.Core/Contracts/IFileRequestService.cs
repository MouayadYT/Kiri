using System.Text;
using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>How a request to find files ended.</summary>
public enum FileRequestStatus
{
    /// <summary>At least one file or folder was found.</summary>
    Found = 0,

    /// <summary>The search ran and nothing matched.</summary>
    NothingFound = 1,

    /// <summary>The request holds nothing to search for, so no search ran.</summary>
    NothingToSearch = 2,

    /// <summary>The Files permission is off, so nothing was planned or searched.</summary>
    FilesTurnedOff = 3,

    /// <summary>Windows Search is not there to ask (its service is off, or it has no index).</summary>
    SearchUnavailable = 4,

    /// <summary>The index did not answer in time.</summary>
    SearchTimedOut = 5,

    /// <summary>The index could not run the search.</summary>
    SearchFailed = 6,
}

/// <summary>The answer to a request to find files: what was searched for, and what was found.</summary>
/// <param name="Status">How the request ended.</param>
public sealed record FileRequestResult(FileRequestStatus Status)
{
    /// <summary>What the request was planned as; <see langword="null"/> when it was not planned.</summary>
    public PlannedFileSearch? Plan { get; init; }

    /// <summary>The files and folders found, in the order the plan asked for.</summary>
    public IReadOnlyList<SearchResultItem> Items { get; init; } = [];

    /// <summary>Whether the index can look inside the files the plan covers, when it has a content criterion.</summary>
    public ContentSearchCapability ContentSearch { get; init; } = ContentSearchCapability.NotRequested;

    /// <summary>Whether the request asked for image files only, so that what was found is shown as a gallery.</summary>
    public bool AsksForImages => Plan?.AsksForImages == true;

    /// <summary>
    /// Whether the plan's own search found nothing and <see cref="Items"/> were picked by the model, from files whose names share a
    /// word with the request (<see cref="IFileMatchReviewer"/>). They are then not exact matches, and the answer says so.
    /// </summary>
    public bool Reviewed { get; init; }

    /// <summary>
    /// Whether nothing of the kind asked for held every word, so <see cref="Items"/> are files of another kind that do ("Milestone
    /// Three.mhtml", for a "milestone three doc"). The answer says so.
    /// </summary>
    public bool OtherKind { get; init; }

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Status = {Status}, Items = {Items.Count}, Reviewed = {Reviewed}, Plan = {Plan}");
        return true;
    }
}

/// <summary>
/// Runs a request to find files from start to end (PROJECT_SPEC §4.7): the Files permission is asked first, the request is
/// planned (<see cref="IFileSearchPlanner"/>), the plan is put to Windows Search (<see cref="IFileSearchService"/>) and what is
/// found comes back with how it was found. It is what the Search or Ask bar and the conversation call; neither knows about
/// the index.
/// </summary>
public interface IFileRequestService
{
    /// <summary>
    /// Whether <paramref name="request"/> is clearly a request to find the user's own files or pictures, such as "find the PDF
    /// about biology I edited last Tuesday" or "show me the last 5 screenshots I took", rather than a question for the model.
    /// It is decided by fixed rules, instantly, and never by asking the model.
    /// </summary>
    bool IsFileRequest(string request);

    /// <summary>
    /// Whether <paramref name="request"/> points back at the files of an earlier request in the same conversation ("find that
    /// pdf", "show me it again", "where is it"), so that it is that request asked again rather than a question of its own. It is
    /// decided by fixed rules; the caller knows whether there was an earlier request.
    /// </summary>
    bool IsFollowUp(string request) => false;

    /// <summary>
    /// Whether <paramref name="request"/> may be a request to find files without being clearly one ("i said milestone three doc.
    /// FIND IT", "find milsotne 3"): it has a verb that asks to find, and words that could be a name. Such a request is searched
    /// for, and is a request to find files only if the search finds something; otherwise it is left to the model. Decided by fixed
    /// rules.
    /// </summary>
    bool IsLikelyFileRequest(string request) => false;

    /// <summary>
    /// Finds the files <paramref name="request"/> asks for. A search that fails is a status, not an exception.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<FileRequestResult> FindAsync(string request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds what matches the words being typed in the Search or Ask bar, quickly: a request of a known shape is planned by its
    /// rule, anything else is looked for in file and folder names. It asks the model nothing, never throws because a search
    /// failed or the Files permission is off (there are then no results), and returns at most a few of each kind.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IReadOnlyList<SearchResultItem>> LookUpAsync(string typed, CancellationToken cancellationToken = default);
}
