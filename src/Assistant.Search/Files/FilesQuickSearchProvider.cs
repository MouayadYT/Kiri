using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch;
using Assistant.Search.Planning;

namespace Assistant.Search.Files;

/// <summary>
/// Finds the user's files and folders for what is being typed (PROJECT_SPEC §4.1, §4.7), through the Windows Search index
/// (<see cref="IFileSearchService"/>), and never by crawling the disk. It is built to answer while the user types:
/// <list type="bullet">
/// <item>It is asked only once the typing has paused (<see cref="Debounce"/>) and has two letters or digits in it.</item>
/// <item>It lists at most five files and three folders, as the bar always has, and cuts the query to that many.</item>
/// <item>
/// What is typed is looked for in file and folder names, as it is. Only text that clearly asks to find files ("find the pdf
/// I edited last Tuesday", "the last 5 screenshots") is planned into a structured query by the fixed rules of the planner
/// (<see cref="IFileSearchPlanner"/>, instant, never the model), and a query the user has submitted is planned the same way.
/// The slow work of a request, the second look at names and the model that judges them, is the conversation's
/// (<see cref="IFileRequestService.FindAsync"/>), after the user asked, never here.
/// </item>
/// <item>Nothing is looked up while the Files permission is off, and a search that cannot run (the index is off, too slow) is no results.</item>
/// </list>
/// With nothing typed it lists the files that changed most lately, which is how the Files category is browsed. What is typed and what
/// is found are private content and are never logged.
/// </summary>
public sealed class FilesQuickSearchProvider : IQuickSearchProvider
{
    /// <summary>The provider's identity.</summary>
    public const string ProviderId = "files";

    /// <summary>The fewest letters or digits a typed query has before it is looked up.</summary>
    public const int MinLettersAndDigits = 2;

    /// <summary>The most files listed while typing.</summary>
    public const int MaxFiles = 5;

    /// <summary>The most folders listed while typing.</summary>
    public const int MaxFolders = 3;

    /// <summary>How far back the files that are listed with nothing typed were changed.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(30);

    private readonly IFileSearchService _search;
    private readonly IFileSearchPlanner _planner;
    private readonly IPermissionPolicy _permissions;
    private readonly TimeProvider _clock;
    private readonly string? _userProfile;

    /// <summary>Creates the provider.</summary>
    /// <param name="search">The Windows Search index.</param>
    /// <param name="planner">Reads a request to find files by fixed rules.</param>
    /// <param name="permissions">Says whether the user allows files to be looked at.</param>
    /// <param name="clock">The clock "recent" is counted from; the system's by default.</param>
    /// <param name="userProfile">The folder written <c>~</c> in a result's location; the current user's by default.</param>
    public FilesQuickSearchProvider(
        IFileSearchService search, IFileSearchPlanner planner, IPermissionPolicy permissions, TimeProvider? clock = null,
        string? userProfile = null)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _clock = clock ?? TimeProvider.System;
        _userProfile = userProfile;
    }

    /// <inheritdoc/>
    public string Id => ProviderId;

    /// <inheritdoc/>
    public string DisplayName => "Files";

    /// <inheritdoc/>
    public QuickSearchResultType ResultType => QuickSearchResultType.Files;

    /// <inheritdoc/>
    public int Priority => 60;

    /// <summary>Nothing, so that an empty query can browse; a typed query shorter than two letters or digits finds nothing.</summary>
    public int MinimumQueryLength => 0;

    /// <summary>The pause in the typing before the index is asked: the spec's 150 milliseconds.</summary>
    public TimeSpan Debounce => TimeSpan.FromMilliseconds(150);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<QuickSearchResult>> SearchAsync(QuickSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var typed = request.Query ?? "";
        if (typed.Length > 0 && QuickSearchText.CountLettersAndDigits(typed) < MinLettersAndDigits)
        {
            return [];
        }

        if (!(await _permissions.CheckAsync(PermissionCapability.Files, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            return [];
        }

        var query = typed.Length == 0 ? Recent() : await PlanAsync(typed, request.IsDeliberate, cancellationToken).ConfigureAwait(false);
        if (query is null)
        {
            return [];
        }

        // Only as many as are listed are asked for, so the index stops early. A list that was narrowed to files has room for more.
        var roomy = request.MaxResults > QuickSearchRequest.DefaultMaxResults;
        var files = roomy ? request.MaxResults : MaxFiles;
        var folders = roomy ? request.MaxResults / 2 : MaxFolders;
        query = query with { MaxResultsPerType = Math.Max(files, folders) };

        IReadOnlyList<SearchResultItem> found;
        try
        {
            found = await _search.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (FileSearchException)
        {
            // Typing is never interrupted by a search that could not run: there are no results.
            return [];
        }

        return Results(found, files, folders);
    }

    // The query for what is typed: a known shape by its rule, text that clearly asks to find files (or was submitted) by the planner's
    // rules, and anything else as words in names.
    private async Task<FileSearchQuery?> PlanAsync(string typed, bool deliberate, CancellationToken cancellationToken)
    {
        if (_planner.PlanKnownShape(typed)?.Query is { } known)
        {
            return known;
        }

        if (deliberate || FileRequestClassifier.IsFileRequest(typed))
        {
            var plan = await _planner.PlanAsync(typed, cancellationToken).ConfigureAwait(false);
            return plan.Query ?? KeywordQuery.Live(typed);
        }

        return KeywordQuery.Live(typed);
    }

    // What changed lately, newest first: how the category is browsed.
    private FileSearchQuery Recent() => new()
    {
        Types = [SearchResultItemType.File],
        Modified = new DateRange(From: _clock.GetUtcNow() - RecentWindow),
        Order = FileSearchOrder.ModifiedDescending,
    };

    private IReadOnlyList<QuickSearchResult> Results(IReadOnlyList<SearchResultItem> found, int maxFiles, int maxFolders)
    {
        var files = found.Where(item => item.Type == SearchResultItemType.File).Take(maxFiles);
        var folders = found.Where(item => item.Type == SearchResultItemType.Folder).Take(maxFolders);
        var items = files.Concat(folders).ToArray();
        return [.. items.Select((item, index) => Result(item, 1.0 - ((double)index / Math.Max(1, items.Length))))];
    }

    private QuickSearchResult Result(SearchResultItem item, double relevance)
    {
        var isFolder = item.Type == SearchResultItemType.Folder;
        var path = item.Path;
        var alternates = new List<QuickSearchAction>(4)
        {
            new(QuickSearchActionKind.RevealPath, QuickSearchActionTitles.Reveal, path),
            new(QuickSearchActionKind.CopyFile, isFolder ? QuickSearchActionTitles.CopyFolder : QuickSearchActionTitles.CopyFile, path),
            new(QuickSearchActionKind.CopyPath, QuickSearchActionTitles.CopyPath, path),
        };

        // A picture, or a document the Assistant reads, can be attached to a new conversation: the first alternate, so that Tab and
        // then Enter does what Tab alone did.
        var extension = item.Extension ?? Path.GetExtension(path);
        if (!isFolder && (ImageFileTypes.IsImageExtension(extension) || DocumentFileTypes.IsDocumentExtension(extension)))
        {
            alternates.Insert(0, new QuickSearchAction(QuickSearchActionKind.AttachFile, QuickSearchActionTitles.Attach, path));
        }

        return new QuickSearchResult(
            "file:" + path, QuickSearchResultType.Files, ProviderId, item.DisplayName,
            new QuickSearchAction(QuickSearchActionKind.OpenPath, QuickSearchActionTitles.Open, path))
        {
            Subtitle = QuickSearchPaths.Location(path, _userProfile),
            When = item.ModifiedAt,
            Icon = new QuickSearchIcon(isFolder ? QuickSearchIconKind.Folder : QuickSearchIconKind.File),
            Alternates = alternates,
            Relevance = relevance,
        };
    }
}
