using System.Text;

namespace Assistant.Core.Contracts;

/// <summary>Where a <see cref="PlannedFileSearch"/> came from.</summary>
public enum FileSearchPlanSource
{
    /// <summary>
    /// A request of a known shape, such as "the last 5 screenshots I took", read by fixed rules: instant, and the same query
    /// every time.
    /// </summary>
    Template = 0,

    /// <summary>
    /// Any other request, read by fixed rules into a kind of file, days, an order, a place and the words of the name ("find the
    /// PDF about biology I edited last Tuesday"). No model writes any part of it, so its days are always right.
    /// </summary>
    Read = 1,
}

/// <summary>
/// What a request to find files came to (PROJECT_SPEC §4.7): the structured query to put to the index, and how it was made.
/// The query is built by fixed rules from the user's own words; no model writes any part of it.
/// </summary>
/// <param name="Query">
/// The query, or <see langword="null"/> when the request holds nothing to search for (no kind, day, place or word of a name).
/// </param>
/// <param name="Source">How the query was made.</param>
public sealed record PlannedFileSearch(FileSearchQuery? Query, FileSearchPlanSource Source)
{
    /// <summary>
    /// The words the file's name should hold, as they were read from the request: what a second look compares names with when
    /// no name holds them exactly. Empty when the request named no words.
    /// </summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>What the request called the files it asked for, one and several ("PDF", "PDFs"), when it named a kind.</summary>
    public (string Singular, string Plural)? KindName { get; init; }

    /// <summary>Whether there is something to search for.</summary>
    public bool HasQuery => Query is not null;

    /// <summary>Whether the query can only find image files, so its answer is shown as a gallery.</summary>
    public bool AsksForImages => Query is { } query && ImageFileTypes.IsImagesOnly(query);

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Source = {Source}, HasQuery = {HasQuery}, Keywords = {Keywords.Count}");
        return true;
    }
}

/// <summary>
/// Turns what the user asked for in words into a <see cref="FileSearchQuery"/> (PROJECT_SPEC §4.7): "find the PDF about biology
/// I edited last Tuesday" becomes a file type, a word of the name or text, and a day. It reads requests by fixed rules that
/// forgive typing mistakes ("fidn the pdff"), so the days, the kinds and the order are worked out in code and are never guessed.
/// </summary>
public interface IFileSearchPlanner
{
    /// <summary>
    /// Plans a request of a known shape ("the last 5 screenshots"), or returns <see langword="null"/> for any other request.
    /// It is instant, so what is being typed can be planned on every change.
    /// </summary>
    PlannedFileSearch? PlanKnownShape(string request);

    /// <summary>Plans <paramref name="request"/>: by its known shape when it has one, otherwise by reading it word by word.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<PlannedFileSearch> PlanAsync(string request, CancellationToken cancellationToken = default);
}
