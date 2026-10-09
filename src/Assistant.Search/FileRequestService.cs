using System.Diagnostics;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Search.Planning;
using Microsoft.Extensions.Logging;

namespace Assistant.Search;

/// <summary>
/// Runs a request to find files from start to end (PROJECT_SPEC §4.7): the Files permission is asked first, and when it is off
/// nothing is planned or searched; the request is planned (<see cref="IFileSearchPlanner"/>), the plan is put to Windows Search
/// (<see cref="IFileSearchService"/>), and what is found comes back with how it was found. A search that cannot run is a status,
/// never an exception, so the caller can say why in words.
/// </summary>
/// <remarks>
/// <para>
/// When no name holds the request's words exactly, there is a second look, since names are cut short, abbreviated and spelled
/// other ways ("Anna’s Archi.pdf" for "annas archive", "arhciv" for "archive"). Candidates are gathered by each word alone and by
/// the plan's other limits (<see cref="CandidateSearch"/>), and each name is scored word by word (<see cref="NameMatch"/>). A name
/// that holds every word, in some form, is a match. Names that hold only some of the words go to the local model
/// (<see cref="IFileMatchReviewer"/>), which says which of them the request could mean; without a model's judgement they are shown
/// only when nothing holds every word. What the second look finds is marked <see cref="FileRequestResult.Reviewed"/>.
/// </para>
/// <para>
/// What is typed in the bar is looked up more cheaply (<see cref="LookUpAsync"/>): the model is never asked, and any failure
/// is no results. A request, a plan and what was found are never logged: the log line holds a status, a count, where the plan
/// came from and a duration.
/// </para>
/// </remarks>
public sealed class FileRequestService(
    IFileSearchPlanner planner,
    IFileSearchService search,
    IPermissionPolicy permissions,
    ILogger<FileRequestService> logger,
    IFileMatchReviewer? reviewer = null) : IFileRequestService
{
    /// <summary>The fewest letters or digits a typed word must have before the bar looks it up.</summary>
    public const int MinTypedCharacters = 2;

    /// <summary>The most names that hold only some of the words that are put to the model.</summary>
    public const int MaxReviewed = 20;

    /// <inheritdoc/>
    public bool IsFileRequest(string request) =>
        FileRequestClassifier.IsFileRequest(request) || planner.PlanKnownShape(request) is not null;

    /// <inheritdoc/>
    public bool IsFollowUp(string request) => FileRequestClassifier.IsFollowUp(request);

    /// <inheritdoc/>
    public bool IsLikelyFileRequest(string request) => FileRequestClassifier.IsLikelyFileRequest(request);

    /// <inheritdoc/>
    public async Task<FileRequestResult> FindAsync(string request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var result = await RunAsync(request, cancellationToken).ConfigureAwait(false);
        PlanningLog.RequestEnded(logger, result.Status, result.Items.Count, result.Plan?.Source, clock.ElapsedMilliseconds);
        return result;
    }

    private async Task<FileRequestResult> RunAsync(string request, CancellationToken cancellationToken)
    {
        if (!await IsAllowedAsync(cancellationToken).ConfigureAwait(false))
        {
            return new FileRequestResult(FileRequestStatus.FilesTurnedOff);
        }

        var plan = await planner.PlanAsync(request, cancellationToken).ConfigureAwait(false);
        if (plan.Query is not { } query)
        {
            return new FileRequestResult(FileRequestStatus.NothingToSearch) { Plan = plan };
        }

        FileSearchOutcome outcome;
        try
        {
            outcome = await search.SearchWithCapabilitiesAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (FileSearchException exception)
        {
            return new FileRequestResult(exception.Failure switch
            {
                FileSearchFailure.IndexUnavailable => FileRequestStatus.SearchUnavailable,
                FileSearchFailure.TimedOut => FileRequestStatus.SearchTimedOut,
                _ => FileRequestStatus.SearchFailed,
            })
            {
                Plan = plan,
            };
        }

        // A name holds a number only as a word of its own: "3" is in "HIS-332" as a substring, and is not "Milestone 3".
        if (outcome.Items.Count > 0 && NumberWords(plan.Keywords) is { Count: > 0 } numbers)
        {
            var whole = outcome.Items.Where(item => HoldsNumbers(item.DisplayName, numbers)).ToArray();
            if (whole.Length < outcome.Items.Count)
            {
                outcome = outcome with { Items = whole };
            }
        }

        if (outcome.Items.Count == 0 && plan.Keywords.Count > 0)
        {
            var clock = Stopwatch.StartNew();
            var strict = await GatherAsync(plan.Keywords, query, cancellationToken).ConfigureAwait(false);

            // Names of the kind that hold only some of the words are the second best: a file of another kind that holds them all
            // ("Milestone Three.mhtml" for a "milestone three doc") is what was asked for, and is shown instead.
            if (strict.Full.Count == 0 && (query.Extensions.Count > 0 || query.Kind is not null))
            {
                var otherKind = await GatherAsync(plan.Keywords, query with { Extensions = [], Kind = null }, cancellationToken)
                    .ConfigureAwait(false);
                if (otherKind.Full.Count > 0)
                {
                    PlanningLog.SecondLook(logger, otherKind.Count, otherKind.Full.Count, 0, otherKind.Full.Count, clock.ElapsedMilliseconds);
                    return new FileRequestResult(FileRequestStatus.Found)
                    {
                        Plan = plan,
                        Items = [.. otherKind.Full.Take(query.MaxResultsPerType)],
                        ContentSearch = outcome.ContentSearch,
                        OtherKind = true,
                    };
                }
            }

            var close = await ChooseAsync(request, strict, query.MaxResultsPerType, cancellationToken).ConfigureAwait(false);
            PlanningLog.SecondLook(logger, strict.Count, strict.Full.Count, strict.Partial.Count, close.Count, clock.ElapsedMilliseconds);
            if (close.Count > 0)
            {
                return new FileRequestResult(FileRequestStatus.Found)
                {
                    Plan = plan,
                    Items = close,
                    ContentSearch = outcome.ContentSearch,
                    Reviewed = true,
                };
            }
        }

        return new FileRequestResult(outcome.Items.Count > 0 ? FileRequestStatus.Found : FileRequestStatus.NothingFound)
        {
            Plan = plan,
            Items = outcome.Items,
            ContentSearch = outcome.ContentSearch,
        };
    }

    // The keywords that are small numbers ("3", "three"): names must hold each as a word of its own, in any form.
    private static List<string> NumberWords(IReadOnlyList<string> keywords) =>
        [.. keywords.Select(word => word.ToLowerInvariant()).Where(word => NumberForms.TryValue(word, out _))];

    private static bool HoldsNumbers(string name, IReadOnlyList<string> numbers)
    {
        var tokens = NameMatch.Tokens(name);
        return numbers.All(number => tokens.Any(token => NumberForms.Same(number, token)));
    }

    // The names closest to the words: gathered by each word alone and by the plan's other limits, then scored word by word. A search
    // that cannot run for one word is no candidates from it.
    private async Task<Candidates> GatherAsync(IReadOnlyList<string> words, FileSearchQuery query, CancellationToken cancellationToken)
    {
        var candidates = new List<SearchResultItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var looser in CandidateSearch.Queries(query, words))
        {
            try
            {
                foreach (var item in await search.SearchAsync(looser, cancellationToken).ConfigureAwait(false))
                {
                    if (candidates.Count < CandidateSearch.MaxCandidates && seen.Add(item.Path))
                    {
                        candidates.Add(item);
                    }
                }
            }
            catch (FileSearchException)
            {
                // This word could not be looked for; the others still can.
            }
        }

        // The best fitting first; among equals, the order the searches found them in.
        var scored = candidates
            .Select((item, order) => (Item: item, Fit: NameMatch.Fit(item.DisplayName, words), Order: order))
            .Where(entry => entry.Fit.IsPartial)
            .OrderByDescending(entry => entry.Fit.Matched)
            .ThenByDescending(entry => entry.Fit.Score)
            .ThenBy(entry => entry.Order)
            .ToArray();
        return new Candidates(
            candidates.Count,
            [.. scored.Where(entry => entry.Fit.IsFull).Select(entry => entry.Item)],
            [.. scored.Where(entry => !entry.Fit.IsFull).Select(entry => entry.Item).Take(MaxReviewed)]);
    }

    // Those that hold every word in some form, then those with some that the model judged to be what was asked for.
    private async Task<IReadOnlyList<SearchResultItem>> ChooseAsync(
        string request, Candidates found, int limit, CancellationToken cancellationToken)
    {
        var picked = found.Full.Take(limit).ToList();
        if (picked.Count < limit && found.Partial.Count > 0)
        {
            var judged = reviewer is null
                ? null
                : await reviewer.ChooseAsync(request, found.Partial, cancellationToken).ConfigureAwait(false);
            var kept = judged is not null
                ? found.Partial.Where(item => judged.Any(chosen => string.Equals(chosen.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
                : found.Full.Count == 0 ? found.Partial : [];
            picked.AddRange(kept.Take(limit - picked.Count));
        }

        return picked;
    }

    private sealed record Candidates(int Count, IReadOnlyList<SearchResultItem> Full, IReadOnlyList<SearchResultItem> Partial);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResultItem>> LookUpAsync(string typed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var letters = RequestTextLength(typed);
        if (letters < MinTypedCharacters || !await IsAllowedAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        // A request of a known shape has its rule's query; anything else is looked for in names.
        var query = planner.PlanKnownShape(typed)?.Query ?? KeywordQuery.Live(typed);
        if (query is null)
        {
            return [];
        }

        try
        {
            return await search.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (FileSearchException)
        {
            // Typing is never interrupted by a search that could not run: there are no results, and the search logged why.
            return [];
        }
    }

    private async Task<bool> IsAllowedAsync(CancellationToken cancellationToken) =>
        (await permissions.CheckAsync(PermissionCapability.Files, cancellationToken).ConfigureAwait(false)).IsAllowed;

    // How many letters and digits the typed text has, so that punctuation alone is not looked up.
    private static int RequestTextLength(string? typed) => typed?.Count(char.IsLetterOrDigit) ?? 0;
}
