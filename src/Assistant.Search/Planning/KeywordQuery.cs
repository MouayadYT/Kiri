using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.History;

namespace Assistant.Search.Planning;

/// <summary>
/// The plain text search for what is being typed in the bar: the words as they are, or, for what starts like a request, its words
/// without the filler around them ("find", "the", "I edited"), looked for in file and folder names, only ever as words to look
/// for, which the search escapes.
/// </summary>
internal static class KeywordQuery
{
    /// <summary>How many files and how many folders the bar lists for what is being typed, at most.</summary>
    public const int MaxLiveResultsPerType = 5;

    // Words that ask, point or place in time, and so say nothing about what a name holds.
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "please", "hey", "hi", "ok", "okay", "can", "could", "would", "will", "you", "i", "ive", "im", "id", "want", "need",
        "like", "find", "show", "search", "look", "looking", "locate", "list", "display", "get", "give", "fetch", "pull",
        "bring", "up", "for", "me", "my", "mine", "our", "the", "a", "an", "all", "any", "some", "of", "in", "on", "at", "to",
        "from", "with", "about", "that", "this", "those", "these", "is", "are", "was", "were", "be", "been", "have", "has",
        "had", "do", "did", "does", "where", "what", "which", "who", "and", "or", "file", "files", "document", "documents",
        "doc", "docs", "folder", "folders", "took", "taken", "take", "made", "make", "edited", "edit", "created", "create",
        "modified", "saved", "save", "downloaded", "opened", "open", "last", "latest", "newest", "recent", "recently", "most",
        "yesterday", "today", "week", "month", "year", "ago", "put", "store", "keep", "leave", "monday", "tuesday", "wednesday",
        "thursday", "friday", "saturday", "sunday",
    };

    /// <summary>
    /// The words of <paramref name="request"/> that are left when the filler is taken out, as one line to look for in names, or
    /// <see langword="null"/> when nothing is left. At most as many words as a search reads; a number of one or two digits (a
    /// count, as in "the last 5") is filler too.
    /// </summary>
    public static string? Keywords(string? request)
    {
        var all = RequestText.Words(request);
        var words = all
            .Where((word, index) => !Filler.Contains(word) && !IsCount(all, index))
            .Take(SearchQuery.MaxTerms)
            .ToArray();
        return words.Length == 0 ? null : string.Join(' ', words);
    }

    // A small number is a count ("the last 5", "top 3", "5 pdfs") only beside the words that count; "milestone 3" is a name.
    private static bool IsCount(IReadOnlyList<string> words, int index)
    {
        var word = words[index];
        if (word.Length > 2 || !word.All(char.IsAsciiDigit))
        {
            return false;
        }

        var before = index > 0 ? words[index - 1] : null;
        var after = index + 1 < words.Count ? words[index + 1] : null;
        return before is null or "last" or "latest" or "newest" or "recent" or "top" or "first" or "oldest" or "past"
            || Filler.Contains(before)
            || (after is not null && (FileTypeWords.Read(after) is { Plural: true } || after is "files" or "folders" or "screenshots"));
    }

    /// <summary>
    /// The query for what is being typed in the bar: the words as they are, in the names of files and folders, unless they
    /// start like a request ("find my resume"), when the keywords of it are used instead. <see langword="null"/> when there is
    /// nothing to look for.
    /// </summary>
    public static FileSearchQuery? Live(string? typed)
    {
        var words = RequestText.Words(typed);
        RequestGrammar.SkipLeading(words, out var isRequest);
        var text = isRequest ? Keywords(typed) : RequestText.Clean(typed);
        return string.IsNullOrEmpty(text) || !text.Any(char.IsLetterOrDigit)
            ? null
            : new FileSearchQuery(text)
            {
                Types = [SearchResultItemType.File, SearchResultItemType.Folder],
                MaxResultsPerType = MaxLiveResultsPerType,
            };
    }
}
