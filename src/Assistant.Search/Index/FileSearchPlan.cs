using System.Text;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.History;

namespace Assistant.Search.Index;

/// <summary>A span of UTC times to whole seconds, which is what the index records, from one (included) up to another (excluded).</summary>
/// <param name="From">The earliest time, or null for no start.</param>
/// <param name="To">The time it stops at, or null for no end.</param>
internal readonly record struct TimeBounds(DateTime? From, DateTime? To)
{
    /// <summary>Whether neither end is given.</summary>
    public bool IsUnbounded => From is null && To is null;
}

/// <summary>The folder a search stays in: its full path, its <c>file:</c> URL as the index writes it, and whether it includes subfolders.</summary>
/// <param name="Path">The full path, without a trailing separator (a drive's root keeps it).</param>
/// <param name="Url">The path as a <c>file:</c> URL with forward slashes.</param>
/// <param name="IncludeSubfolders">Whether the folders inside it are searched too.</param>
internal sealed record FolderScope(string Path, string Url, bool IncludeSubfolders);

/// <summary>
/// A <see cref="FileSearchQuery"/> read into what the index can be asked, once and in one place: the words cleaned and counted,
/// the extensions and folder normalized, the dates and sizes checked. Whatever the query holds that cannot be met makes
/// <see cref="Create"/> return nothing, so a criterion that is set is never dropped and a search never gets wider than it
/// was asked to be. <see cref="SearchSqlBuilder"/> writes only from a plan, so nothing the caller typed reaches the SQL by
/// another road.
/// </summary>
internal sealed record FileSearchPlan
{
    private static readonly char[] ExtensionSymbols = ['_', '-', '+', '~', '#', '$', '@', '!'];
    private const int MaxExtensionLength = 20;

    /// <summary>The kinds to search: files and folders, each once, in the order asked for.</summary>
    public required IReadOnlyList<SearchResultItemType> Types { get; init; }

    /// <summary>How many results of each kind to return, at most.</summary>
    public required int Limit { get; init; }

    /// <summary>The order of the results.</summary>
    public FileSearchOrder Order { get; init; }

    /// <summary>The words of the query's text: each in the name or, with <see cref="MatchContents"/>, in the text or properties.</summary>
    public IReadOnlyList<SearchTerm> Terms { get; init; } = [];

    /// <summary>Whether <see cref="Terms"/> are also looked for in the items' text and properties. Never set without terms.</summary>
    public bool MatchContents { get; init; }

    /// <summary>The words the file name must contain.</summary>
    public IReadOnlyList<SearchTerm> FilenameTerms { get; init; } = [];

    /// <summary>The phrase the text inside the file must contain: words, single-spaced, with no quote, star or control character.</summary>
    public string? ContentPhrase { get; init; }

    /// <summary>The extensions, lower case with their dot, any of which the file may have. Empty for any.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>The kind of file Windows classes it as.</summary>
    public FileKind? Kind { get; init; }

    /// <summary>When the item was created.</summary>
    public TimeBounds Created { get; init; }

    /// <summary>When the item was last modified.</summary>
    public TimeBounds Modified { get; init; }

    /// <summary>The smallest size in bytes, or null.</summary>
    public long? MinBytes { get; init; }

    /// <summary>The largest size in bytes, or null.</summary>
    public long? MaxBytes { get; init; }

    /// <summary>The folder to stay in, or null for the whole index.</summary>
    public FolderScope? Scope { get; init; }

    /// <summary>Whether the search asks the index to look inside files.</summary>
    public bool ContentRequested => ContentPhrase is not null || MatchContents;

    /// <summary>The words a name is judged by when the results are ordered by relevance.</summary>
    public IReadOnlyList<SearchTerm> RelevanceTerms => Terms.Count > 0 ? Terms : FilenameTerms;

    /// <summary>Whether the plan limits to what only a file can be: a folder has no extension, kind, size or text.</summary>
    public bool FilesOnly => Extensions.Count > 0 || Kind is not null || MinBytes is not null || MaxBytes is not null
        || ContentPhrase is not null;

    /// <summary>Whether the plan has any criterion at all. A search without one would ask for the whole index.</summary>
    public bool HasCriteria => Terms.Count > 0 || FilenameTerms.Count > 0 || ContentPhrase is not null
        || Extensions.Count > 0 || Kind is not null || !Created.IsUnbounded || !Modified.IsUnbounded
        || MinBytes is not null || MaxBytes is not null || Scope is not null;

    /// <summary>
    /// Whether <paramref name="query"/> asks the index to look inside files, judged from what the caller wrote: a query
    /// that never gets to the index still can be one that would have.
    /// </summary>
    public static bool AsksForContents(FileSearchQuery query) =>
        !string.IsNullOrWhiteSpace(query.ContentTerm) || (query.MatchContents && !string.IsNullOrWhiteSpace(query.Text));

    /// <summary>
    /// The plan for <paramref name="query"/>, or null when nothing can match it: it has no criterion, a criterion that is set
    /// cannot be met (see <see cref="FileSearchQuery"/>), it wants no results or no kind of item this index holds.
    /// </summary>
    /// <param name="query">What was asked.</param>
    /// <param name="maxResults">The most results of each kind any query may have.</param>
    public static FileSearchPlan? Create(FileSearchQuery query, int maxResults)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = Math.Min(query.MaxResultsPerType, maxResults);

        // A kind that is not one of the defined ones is a criterion that cannot be met, not one to drop.
        if (limit <= 0
            || (query.Kind is { } kind && !Enum.IsDefined(kind))
            || !TryWords(query.Text, out var terms)
            || !TryWords(query.Filename, out var filename)
            || !TryPhrase(query.ContentTerm, out var phrase)
            || !TryExtensions(query.Extensions, out var extensions)
            || !TryBounds(query.Created, out var created)
            || !TryBounds(query.Modified, out var modified)
            || !TrySize(query.Size, out var minBytes, out var maxBytes)
            || !TryScope(query.Folder, query.IncludeSubfolders, out var scope))
        {
            return null;
        }

        var plan = new FileSearchPlan
        {
            Types = [],
            Limit = limit,
            Order = Enum.IsDefined(query.Order) ? query.Order : FileSearchOrder.Relevance,
            Terms = terms,
            MatchContents = query.MatchContents && terms.Count > 0,
            FilenameTerms = filename,
            ContentPhrase = phrase,
            Extensions = extensions,
            Kind = query.Kind,
            Created = created,
            Modified = modified,
            MinBytes = minBytes,
            MaxBytes = maxBytes,
            Scope = scope,
        };

        var types = (query.Types ?? [])
            .Where(type => type is SearchResultItemType.File or SearchResultItemType.Folder)
            .Where(type => type == SearchResultItemType.File || !plan.FilesOnly)
            .Distinct()
            .ToArray();
        return types.Length == 0 || !plan.HasCriteria ? null : plan with { Types = types };
    }

    /// <summary>
    /// The words to look for in <paramref name="text"/>: its words and quoted phrases, without any control character. False
    /// when text is given but holds no word; true with no words when it is not given at all.
    /// </summary>
    private static bool TryWords(string? text, out IReadOnlyList<SearchTerm> terms)
    {
        terms = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        terms =
        [
            .. SearchQuery.Parse(text).Terms
                .Select(term => term with { Text = new string([.. term.Text.Where(character => !char.IsControl(character))]).Trim() })
                .Where(term => term.Text.Any(char.IsLetterOrDigit)),
        ];
        return terms.Count > 0;
    }

    /// <summary>
    /// The content phrase: <paramref name="text"/> in one line, without the quote and star that mean something to the
    /// full-text parser inside a phrase, cut to <see cref="FileSearchQuery.MaxContentTermLength"/> characters. False when text
    /// is given but has no letter or digit, which the parser refuses.
    /// </summary>
    private static bool TryPhrase(string? text, out string? phrase)
    {
        phrase = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var line = new StringBuilder(Math.Min(text.Length, FileSearchQuery.MaxContentTermLength + 1));
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = line.Length > 0;
                continue;
            }

            if (character is '"' or '*')
            {
                continue;
            }

            if (pendingSpace)
            {
                line.Append(' ');
                pendingSpace = false;
            }

            line.Append(character);
            if (line.Length > FileSearchQuery.MaxContentTermLength)
            {
                break;
            }
        }

        var cleaned = line.ToString();
        if (cleaned.Length > FileSearchQuery.MaxContentTermLength)
        {
            var cut = char.IsHighSurrogate(cleaned[FileSearchQuery.MaxContentTermLength - 1])
                ? FileSearchQuery.MaxContentTermLength - 1
                : FileSearchQuery.MaxContentTermLength;
            cleaned = cleaned[..cut];
        }

        cleaned = cleaned.TrimEnd();
        if (!cleaned.Any(char.IsLetterOrDigit))
        {
            return false;
        }

        phrase = cleaned;
        return true;
    }

    private static bool TryExtensions(IReadOnlyList<string>? extensions, out IReadOnlyList<string> result)
    {
        result = [];
        if (extensions is not { Count: > 0 })
        {
            return true;
        }

        var kept = new List<string>();
        foreach (var raw in extensions)
        {
            if (NormalizeExtension(raw) is { } extension && !kept.Contains(extension))
            {
                kept.Add(extension);
                if (kept.Count == FileSearchQuery.MaxExtensions)
                {
                    break;
                }
            }
        }

        result = kept;
        return kept.Count > 0;
    }

    /// <summary>
    /// <c>.pdf</c>, <c>PDF</c> and <c>*.pdf</c> are all <c>.pdf</c>. Null for anything that cannot be the extension of a file:
    /// blank, with a separator, a space, a dot inside it, a wildcard, a control character, or too long.
    /// </summary>
    internal static string? NormalizeExtension(string? raw)
    {
        var text = raw?.Trim().TrimStart('*');
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text[0] == '.')
        {
            text = text[1..];
        }

        if (text.Length is 0 or > MaxExtensionLength
            || !text.All(character => char.IsLetterOrDigit(character) || ExtensionSymbols.Contains(character)))
        {
            return null;
        }

        return "." + text.ToLowerInvariant();
    }

    private static bool TryBounds(DateRange range, out TimeBounds bounds)
    {
        var from = range.From is { } start ? WholeSeconds(start.UtcDateTime) : (DateTime?)null;
        var to = range.To is { } end ? WholeSeconds(end.UtcDateTime) : (DateTime?)null;
        bounds = new TimeBounds(from, to);
        return from is null || to is null || from < to;
    }

    private static DateTime WholeSeconds(DateTime utc) =>
        new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    private static bool TrySize(SizeRange range, out long? minBytes, out long? maxBytes)
    {
        minBytes = range.MinBytes is > 0 ? range.MinBytes : null;
        maxBytes = range.MaxBytes;
        if (maxBytes < 0)
        {
            return false;
        }

        return minBytes is null || maxBytes is null || minBytes <= maxBytes;
    }

    private static bool TryScope(string? folder, bool includeSubfolders, out FolderScope? scope)
    {
        scope = null;
        if (string.IsNullOrWhiteSpace(folder))
        {
            return true;
        }

        // A relative folder would mean whatever the process's current folder is, which is never what was meant.
        var trimmed = folder.Trim();
        if (trimmed.Any(char.IsControl) || !Path.IsPathFullyQualified(trimmed))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(trimmed);
            var root = Path.GetPathRoot(full);
            if (full.Length > (root?.Length ?? 0))
            {
                full = full.TrimEnd('\\', '/');
            }

            scope = new FolderScope(full, "file:" + full.Replace('\\', '/'), includeSubfolders);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
