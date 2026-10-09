using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Files;
using Assistant.Core.Tools;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Search;

/// <summary>
/// The Assistant's answer to a request to find files (PROJECT_SPEC §4.1, §4.2): what was found is the answer, at once and before
/// any model is asked to write anything, and it is shown by what it is. A request for pictures ("show me the last 5
/// screenshots I took") is answered with an <see cref="ImageCollection"/>, the gallery three across; any other with a
/// <see cref="FileCollection"/>, the list of files with the actions of a file row. A line of prose leads it, which says what was
/// found and what was looked for ("I found 3 PDFs with “biology” in the name or text, changed on Tuesday, Sep 29"); it is written
/// from the plan and the count, never by the model, so it cannot claim what was not found. When nothing was found or the search
/// could not run, the answer says so in words, and never shows an empty gallery. A request that points back at the last one in the
/// same conversation ("find that pdf") is that request asked again.
/// </summary>
internal sealed partial class FileRequestAnswers(IFileRequestService files, TimeProvider clock, IConversationFiles? known = null)
{
    /// <summary>What the answer says when the Files permission is off.</summary>
    public const string FilesTurnedOffText =
        "Files are turned off in Settings, under Permissions, so I didn't look for any. Turn Files on there to find files.";

    /// <summary>What the answer says when the request has nothing in it to look for.</summary>
    public const string NothingToSearchText = "I couldn't tell what to look for. Try naming the file, or what it is about.";

    /// <summary>What the answer says when Windows Search is not there to ask.</summary>
    public const string SearchUnavailableText =
        "Windows Search is turned off or has no index on this PC, so I can't look for files. Turn it on in Windows (the Windows Search service, and Indexing Options), then ask again.";

    /// <summary>What the answer says when Windows Search did not answer in time.</summary>
    public const string SearchTimedOutText =
        "Windows Search took too long to answer, so I found nothing. Try again, or ask for something narrower.";

    /// <summary>What the answer says when Windows Search could not run the search.</summary>
    public const string SearchFailedText = "Windows Search couldn't run that search.";

    /// <summary>What the answer adds when a search found nothing, since the index only holds the places Windows is set to read.</summary>
    public const string OnlyIndexedPlacesText = "Windows Search only finds files in the places it indexes.";

    // The last request to find files in each conversation, so that "find that pdf" can be asked of it again. Only in memory.
    private readonly Dictionary<Guid, string> _lastRequests = [];
    private readonly object _gate = new();

    /// <summary>Whether <paramref name="request"/> is clearly a request to find files, rather than a question for the model.</summary>
    public bool IsFileRequest(string request) => IsFileRequest(request, null);

    /// <summary>
    /// Whether <paramref name="request"/> is a request to find files: clearly one by its own words, or one that points back
    /// ("find that pdf") at a request to find files earlier in the conversation <paramref name="conversationId"/>.
    /// </summary>
    public bool IsFileRequest(string request, Guid? conversationId) =>
        files.IsFileRequest(request) || (Previous(conversationId) is not null && (files.IsFollowUp(request) || Corrected(request, conversationId) is not null));

    /// <summary>
    /// Whether <paramref name="request"/> may be a request to find files without being clearly one ("i said milestone three doc.
    /// FIND IT", "milestone 3 doc"); <see cref="TryAnswerAsync"/> decides, by whether anything is found.
    /// </summary>
    public bool IsLikelyFileRequest(string request) => files.IsLikelyFileRequest(request);

    /// <summary>
    /// Finds the files <paramref name="request"/> may ask for, and makes the Assistant's answer only when something was found: a
    /// request that is only maybe about files is otherwise left to the model. <see langword="null"/> when nothing was found.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<MessageViewModel?> TryAnswerAsync(string request, Guid? conversationId, CancellationToken cancellationToken)
    {
        var result = await files.FindAsync(request, cancellationToken).ConfigureAwait(true);
        if (result.Status != FileRequestStatus.Found)
        {
            return null;
        }

        if (conversationId is { } id)
        {
            lock (_gate)
            {
                _lastRequests[id] = request;
            }

            known?.Offer(id, result.Items);
        }

        return Describe(result);
    }

    /// <summary>Finds the files <paramref name="request"/> asks for and makes the Assistant's message of what came of it.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<MessageViewModel> AnswerAsync(string request, CancellationToken cancellationToken) =>
        AnswerAsync(request, null, cancellationToken);

    /// <summary>
    /// Finds the files <paramref name="request"/> asks for in the conversation <paramref name="conversationId"/>; a request that
    /// points back at the last one there is asked as that one, with whatever it adds ("find that pdf in my downloads").
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<MessageViewModel> AnswerAsync(string request, Guid? conversationId, CancellationToken cancellationToken)
    {
        var asked = Corrected(request, conversationId)
            ?? (Previous(conversationId) is { } previous && files.IsFollowUp(request) ? $"{previous} {request}" : request);
        if (conversationId is { } id)
        {
            lock (_gate)
            {
                _lastRequests[id] = asked;
            }
        }

        var result = await files.FindAsync(asked, cancellationToken).ConfigureAwait(true);

        // The files found become known to the conversation, so that the model can name one by its id later.
        if (conversationId is { } chat && result.Status == FileRequestStatus.Found)
        {
            known?.Offer(chat, result.Items);
        }

        return Describe(result);
    }

    /// <summary>
    /// What the model is shown of a request to find files that the app answered itself: the search as a call of the tool that finds
    /// files and what it found, by id and name (<see cref="FileToolResults"/>), as the model would have made them, so that what the
    /// user says next ("the first one", "3 not 4") is understood in what went before. Never a path or a file's text.
    /// </summary>
    /// <returns>The call and its result, or <see langword="null"/> when the conversation's files are not kept.</returns>
    public (ToolCall Call, ToolResult Result)? ToolExchange(Guid conversationId, string search, MessageViewModel answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (known is null)
        {
            return null;
        }

        var paths = answer.Content.OfType<FileCollection>().SelectMany(collection => collection.Files).Select(file => file.Path)
            .Concat(answer.Content.OfType<ImageCollection>().SelectMany(collection => collection.Images).Select(image => image.Path ?? string.Empty));
        var found = paths.Select(path => known.Find(conversationId, path)).OfType<KnownFile>().ToList();
        var call = new ToolCall(
            "call_" + Guid.NewGuid().ToString("N")[..8],
            FileToolResults.SearchFiles,
            JsonSerializer.Serialize(new { query = search }));
        var output = FileToolResults.Found(found, note: null, images: answer.Content.OfType<ImageCollection>().Any());
        return (call, new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, output));
    }

    /// <summary>Makes the Assistant's message for <paramref name="result"/>.</summary>
    internal MessageViewModel Describe(FileRequestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var message = new MessageViewModel(MessageRole.Assistant, Lead(result)) { CreatedAt = clock.GetUtcNow() };
        if (result.Status == FileRequestStatus.Found)
        {
            // What was found is shown by what it is: pictures as the gallery, everything else as the list of files.
            message.Content.Add(result.AsksForImages
                ? new ImageCollection(result.Items.Select(item => new ImageItem(item.DisplayName, item.Path)))
                : FileCollection.From(result.Items, clock));
        }

        return message;
    }

    // "3 not 4", "i meant three", "no, milestone three not four": the request before, with the word that was wrong put right (or the
    // word added, when the request did not have it). Null when it is not such a correction, or there was no request before.
    private string? Corrected(string request, Guid? conversationId)
    {
        if (Previous(conversationId) is not { } previous)
        {
            return null;
        }

        string right;
        string? wrong = null;
        if (Replacement().Match(request) is { Success: true } replace)
        {
            (right, wrong) = (replace.Groups["a"].Value, replace.Groups["b"].Value);
        }
        else if (Restatement().Match(request) is { Success: true } restate)
        {
            right = restate.Groups["a"].Value;
        }
        else
        {
            return null;
        }

        var parts = previous.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var replaced = false;
        if (wrong is not null)
        {
            for (var i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i].Trim(',', '.', '!', '?'), wrong, StringComparison.OrdinalIgnoreCase))
                {
                    parts[i] = right;
                    replaced = true;
                }
            }
        }

        return replaced ? string.Join(' ', parts) : $"{previous} {right}";
    }

    [GeneratedRegex(@"^\s*(?:no\s*[,.]?\s+)?(?:i\s+(?:meant|said|want(?:ed)?)\s+)?(?<a>[\w'-]+)\s+(?:not|instead\s+of|rather\s+than)\s+(?<b>[\w'-]+)\s*[.!?]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Replacement();

    [GeneratedRegex(@"^\s*(?:no\s*[,.]?\s+)?(?:i\s+(?:meant|said|want(?:ed)?)|make\s+it)\s+(?<a>[\w'-]+)\s*[.!?]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Restatement();

    private string? Previous(Guid? conversationId)
    {
        if (conversationId is not { } id)
        {
            return null;
        }

        lock (_gate)
        {
            return _lastRequests.GetValueOrDefault(id);
        }
    }

    private string Lead(FileRequestResult result)
    {
        switch (result.Status)
        {
            case FileRequestStatus.FilesTurnedOff:
                return FilesTurnedOffText;
            case FileRequestStatus.NothingToSearch:
                return NothingToSearchText;
            case FileRequestStatus.SearchUnavailable:
                return SearchUnavailableText;
            case FileRequestStatus.SearchTimedOut:
                return SearchTimedOutText;
            case FileRequestStatus.SearchFailed:
                return SearchFailedText;
        }

        var plan = result.Plan;
        var query = plan?.Query;
        var noun = Nouns.Of(plan, result.AsksForImages);
        var count = result.Items.Count;
        var what = query is null ? "" : LookedFor(query);

        if (result.Status == FileRequestStatus.NothingFound)
        {
            var note = ContentNote(query, result.ContentSearch) ?? OnlyIndexedPlacesText;
            return $"I couldn't find any {noun.Plural}{what}. {note}";
        }

        if (result.OtherKind)
        {
            // Nothing of the kind asked for has the words, but a file of another kind has them all: said first, as it is not what the
            // kind said.
            var held = string.Join(' ', plan?.Keywords ?? []);
            return count == 1
                ? $"No {noun.Singular} has “{held}” in its name, but this file does."
                : $"No {noun.Singular} has “{held}” in its name, but these {count} files do.";
        }

        if (result.Reviewed)
        {
            // Not exact matches: said first, so nobody takes them for what the words asked for.
            var words = string.Join(' ', plan?.Keywords ?? []);
            return count == 1
                ? $"No name has “{words}” exactly, so here is the closest {noun.Singular}."
                : $"No name has “{words}” exactly, so here are the {count} closest {noun.Plural}.";
        }

        // Newest first with nothing else to match is what "the last 5" and "my latest" ask for, so the answer says so.
        var newest = query is { Order: FileSearchOrder.ModifiedDescending or FileSearchOrder.CreatedDescending, Text: null, ContentTerm: null };
        if (newest)
        {
            return count == 1
                ? $"Here is your most recent {noun.Singular}{what}."
                : $"Here are your {count} most recent {noun.Plural}{what}.";
        }

        return count == 1 ? $"I found 1 {noun.Singular}{what}." : $"I found {count} {noun.Plural}{what}.";
    }

    // What was looked for besides the kind: " with “biology” in the name or text, changed on Tuesday, Sep 29, in Downloads".
    private string LookedFor(FileSearchQuery query)
    {
        var parts = new List<string>();
        if (query.Text is { Length: > 0 } text)
        {
            parts.Add($"with “{text.Replace("\"", "", StringComparison.Ordinal)}” in the name{(query.MatchContents ? " or text" : "")}");
        }

        if (query.ContentTerm is { } phrase)
        {
            parts.Add($"that mention “{phrase}”");
        }

        if (!query.Modified.IsUnbounded)
        {
            parts.Add($"changed {Days(query.Modified)}");
        }
        else if (!query.Created.IsUnbounded)
        {
            parts.Add($"made {Days(query.Created)}");
        }

        if (query.Folder is { } folder && Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } place)
        {
            parts.Add($"in {place}");
        }

        return parts.Count == 0 ? "" : " " + string.Join(", ", parts);
    }

    // "on Tuesday, Sep 29", "between Sep 1 and Sep 30", "since Aug 2", "before Jan 1": whole local days.
    private string Days(DateRange range)
    {
        var zone = clock.LocalTimeZone;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        DateOnly? first = range.From is { } from ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime) : null;
        DateOnly? last = range.To is { } to ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to.AddTicks(-1), zone).DateTime) : null;
        string Day(DateOnly day) => day == today ? "today"
            : day == today.AddDays(-1) ? "yesterday"
            : today.DayNumber - day.DayNumber < 7 ? day.ToString("dddd, MMM d", CultureInfo.InvariantCulture)
            : day.Year == today.Year ? day.ToString("MMM d", CultureInfo.InvariantCulture)
            : day.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

        return (first, last) switch
        {
            ({ } one, { } other) when one == other => Day(one) is "today" or "yesterday" ? Day(one) : $"on {Day(one)}",
            ({ } one, { } other) when other >= today => $"since {Day(one)}",
            ({ } one, { } other) => $"between {Day(one)} and {Day(other)}",
            ({ } one, null) => $"since {Day(one)}",
            (null, { } other) => $"before {Day(other.AddDays(1))}",
            _ => "",
        };
    }

    // Why an empty answer to a search inside files does not mean no file has the words.
    private static string? ContentNote(FileSearchQuery? query, ContentSearchCapability capability)
    {
        if (query?.ContentTerm is null || !capability.IsLimited)
        {
            return null;
        }

        var reasons = new List<string>();
        if (capability.Limits.HasFlag(ContentSearchLimits.LocationNotIndexed))
        {
            reasons.Add("that folder isn't indexed");
        }

        if (capability.Limits.HasFlag(ContentSearchLimits.FileTypeNotContentIndexed))
        {
            reasons.Add(capability.UnsupportedExtensions.Count > 0
                ? $"it keeps no text for {string.Join(", ", capability.UnsupportedExtensions)} files"
                : "it keeps no text for some of those file types");
        }

        return $"Windows Search can't look inside all of these files ({string.Join(" and ", reasons)}), so that doesn't mean none of them has it.";
    }

    // What the things found are called.
    private readonly record struct Nouns(string Singular, string Plural)
    {
        public static Nouns Files { get; } = new("file", "files");

        public static Nouns Of(PlannedFileSearch? plan, bool images)
        {
            if (plan is { KindName: { } kind })
            {
                return new Nouns(kind.Singular, kind.Plural);
            }

            if (plan?.Query is not { } query)
            {
                return Files;
            }

            if (images)
            {
                return query.Filename?.Contains("screenshot", StringComparison.OrdinalIgnoreCase) == true
                    ? new Nouns("screenshot", "screenshots")
                    : new Nouns("image", "images");
            }

            // A type, a phrase inside or a size is only a file's, so such a search finds files alone.
            var fileOnly = query.Extensions.Count > 0 || query.Kind is not null || query.ContentTerm is not null || !query.Size.IsUnbounded;
            var hasFiles = query.Types.Contains(SearchResultItemType.File);
            var hasFolders = query.Types.Contains(SearchResultItemType.Folder) && !(fileOnly && hasFiles);
            return hasFolders && !hasFiles ? new Nouns("folder", "folders")
                : hasFolders ? new Nouns("file or folder", "files or folders")
                : Files;
        }
    }
}
