using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core.Contracts;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Clipboard;
using Assistant.Core.QuickSearch.Routing;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Search;

/// <summary>
/// The results under the Search or Ask bar (PROJECT_SPEC §4.1): what the quick-search providers find for the typed words (applications,
/// files, actions, the clipboard history), ranked by fixed rules (<see cref="QuickSearchRanker"/>), never by the model, and listed as
/// the providers answer. The best match leads in a section of its own with no header, as the reference does, and is highlighted when
/// its name is what was typed or begins with it, so that Enter runs it; the rest are in a group for each kind of result, with a header
/// when there is more than one group. Each result says what Enter does, and the other things it can do are listed with Tab; the keys
/// for showing a file in File Explorer (<c>Shift+Enter</c>) and copying a path (<c>Ctrl+Shift+C</c>) work straight from the row.
/// What is typed and what is found are private content and are never logged.
/// </summary>
internal sealed class QuickSearchResultsSource : IQuickSearchResultsSource
{
    private readonly IQuickSearchCoordinator _coordinator;
    private readonly QuickSearchRanker _ranker;
    private readonly IQueryRouter _router;
    private readonly QuickSearchActionRunner _runner;
    private readonly TimeProvider _clock;
    private readonly IClipboardHistory? _history;
    private readonly ISettingsService? _settings;

    // The user's "Results in each group", read at the start of each search so a change in Settings counts at once.
    private int? _glanceLimit;

    // What each provider answered last, and what was typed and which kind was listed then. While a provider has not yet answered what is typed
    // now, its last answer stands in for it when what is typed only went on from, or back to the start of, what was typed then: a list that is up
    // keeps its rows until the new ones come, instead of losing a group with every key and getting it back a moment later.
    private readonly object _recentGate = new();
    private readonly Dictionary<string, QuickSearchProviderOutcome> _recent = new(StringComparer.Ordinal);
    private string _recentQuery = "";
    private QuickSearchResultType? _recentScope;

    public QuickSearchResultsSource(
        IQuickSearchCoordinator coordinator, QuickSearchRanker ranker, IQueryRouter router, QuickSearchActionRunner runner,
        TimeProvider? clock = null, IClipboardHistory? history = null, ISettingsService? settings = null)
    {
        _settings = settings;
        _coordinator = coordinator;
        _ranker = ranker;
        _router = router;
        _runner = runner;
        _clock = clock ?? TimeProvider.System;
        _history = history;
    }

    /// <inheritdoc/>
    public async Task SearchAsync(
        string query, QuickSearchResultType? scope, Action<SearchResultsSnapshot> report, CancellationToken cancellationToken)
    {
        var text = (query ?? "").Trim();
        _glanceLimit = await GlanceLimitAsync(cancellationToken).ConfigureAwait(false);

        // Nothing typed and no kind chosen is the categories' to show, not a list.
        if (text.Length == 0 && scope is null)
        {
            Forget();
            report(new SearchResultsSnapshot([]));
            return;
        }

        var route = _router.Route(text);

        // A question is not looked up: there is nothing to find for it, and Enter asks. A request for files looks for files only.
        if (scope is null && text.Length > 0 && !route.UsesInstantProviders)
        {
            Forget();
            report(new SearchResultsSnapshot([]));
            return;
        }

        var types = scope is { } narrowed
            ? new HashSet<QuickSearchResultType> { narrowed }
            : route.Kind == QueryRouteKind.FileSearch ? new HashSet<QuickSearchResultType> { QuickSearchResultType.Files } : null;
        var request = new QuickSearchRequest(text)
        {
            MaxResults = scope is null ? Math.Max(QuickSearchRequest.DefaultMaxResults, _glanceLimit ?? 0) : QuickSearchGroups.ScopedLimit,
            Types = types,
        };

        var earlier = EarlierAnswers(text, scope);
        var answers = new Dictionary<string, QuickSearchProviderOutcome>(StringComparer.Ordinal);
        var gate = new object();
        var progress = new InlineProgress(outcome =>
        {
            SearchResultsSnapshot snapshot;
            lock (gate)
            {
                answers[outcome.ProviderId] = outcome;

                // The providers that have answered, and, for the others, what they answered last.
                snapshot = Build(text, scope, [.. answers.Values, .. earlier.Where(pair => !answers.ContainsKey(pair.Key)).Select(pair => pair.Value)], final: false);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                Remember(text, scope, outcome);
                report(snapshot);
            }
        });

        var all = await _coordinator.SearchAsync(request, progress, cancellationToken).ConfigureAwait(false);

        // Everything has answered: the last list, which also says why a narrowed list is empty.
        Remember(text, scope, [.. all.Providers]);
        report(Build(text, scope, all.Providers, final: true));
    }

    // What the providers answered last, when what is typed now goes on from it or goes back toward it (a letter typed or taken back) in the same
    // list; nothing when it is something else, which has no rows to keep.
    private Dictionary<string, QuickSearchProviderOutcome> EarlierAnswers(string text, QuickSearchResultType? scope)
    {
        lock (_recentGate)
        {
            var related = _recent.Count > 0 && _recentScope == scope && text.Length > 0 && _recentQuery.Length > 0
                && (text.StartsWith(_recentQuery, StringComparison.OrdinalIgnoreCase) || _recentQuery.StartsWith(text, StringComparison.OrdinalIgnoreCase));
            if (!related)
            {
                _recent.Clear();
                return new Dictionary<string, QuickSearchProviderOutcome>(StringComparer.Ordinal);
            }

            return new Dictionary<string, QuickSearchProviderOutcome>(_recent, StringComparer.Ordinal);
        }
    }

    private void Remember(string text, QuickSearchResultType? scope, params QuickSearchProviderOutcome[] outcomes)
    {
        lock (_recentGate)
        {
            _recentQuery = text;
            _recentScope = scope;
            foreach (var outcome in outcomes)
            {
                _recent[outcome.ProviderId] = outcome;
            }
        }
    }

    private void Forget()
    {
        lock (_recentGate)
        {
            _recent.Clear();
            _recentQuery = "";
            _recentScope = null;
        }
    }

    private async Task<int?> GlanceLimitAsync(CancellationToken cancellationToken)
    {
        if (_settings is null)
        {
            return null;
        }

        try
        {
            return (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Ui.BarResultsPerGroup;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    // The sections for what the providers have answered so far.
    private SearchResultsSnapshot Build(
        string query, QuickSearchResultType? scope, IReadOnlyCollection<QuickSearchProviderOutcome> outcomes, bool final)
    {
        var now = _clock.GetLocalNow();
        var ranking = _ranker.Rank(
            query, outcomes.SelectMany(outcome => outcome.Results), now, new QuickSearchRankingOptions { Scope = scope, GlanceLimit = _glanceLimit });

        var sections = new List<SearchResultSectionViewModel>();
        var rows = new Dictionary<string, SearchResultViewModel>(StringComparer.Ordinal);
        SearchResultViewModel Row(QuickSearchRankedResult ranked)
        {
            var row = RowOf(ranked.Result, now);
            rows[ranked.Result.Id] = row;
            return row;
        }

        if (ranking.TopHit is { } top)
        {
            sections.Add(new SearchResultSectionViewModel(null, [Row(top)]));
        }

        // A header says which group a section is, but only where there are several to tell apart.
        var titled = ranking.Groups.Count >= 2;
        sections.AddRange(ranking.Groups.Select(group =>
            new SearchResultSectionViewModel(titled ? group.Title : null, group.Results.Select(Row))));

        // The best match is highlighted, and offered beside the text as the reference does ("word — Microsoft Word"), when its name is what was
        // typed, begins with it, or has words that begin with what was typed ("word" for "Microsoft Word", "vsc" for "Visual Studio Code"): Enter
        // opens it. Text that is only somewhere inside a name is less certain, and leaves Enter to ask.
        var first = ranking.Listed.FirstOrDefault();
        var highlighted = first is not null && query.Length > 0 && first.Match.Kind >= QuickSearchMatchKind.Initials
            ? rows.GetValueOrDefault(first.Result.Id)
            : null;
        return new SearchResultsSnapshot(sections)
        {
            Highlighted = highlighted,
            EmptyMessage = final && sections.Count == 0 && scope is not null ? EmptyMessageOf(scope.Value, query.Length > 0) : "",
        };
    }

    private string EmptyMessageOf(QuickSearchResultType scope, bool typed) => scope switch
    {
        QuickSearchResultType.Applications => "No applications found",
        QuickSearchResultType.Files => typed ? "No files found" : "No recent files",
        QuickSearchResultType.Actions => "No actions found",
        QuickSearchResultType.Clipboard when _history is { IsEnabled: false } || _history is null =>
            "Clipboard history is off. Turn it on in Settings, under Permissions.",
        QuickSearchResultType.Clipboard => typed ? "Nothing copied matches" : "Nothing copied yet",
        _ => "No results",
    };

    // One row: its icon, name, what it is or where it is, when, what Enter does and, when it has them, the other things it can do.
    private SearchResultViewModel RowOf(QuickSearchResult result, DateTimeOffset now)
    {
        var alternates = result.Alternates
            .Select(action => new SearchResultAlternate(action.Title, Command(result, action), KeyTextOf(action.Kind)))
            .ToArray();

        // Showing a file and copying a path are also a key each, straight from the row.
        var keys = new List<SearchResultAction>(2);
        foreach (var action in result.Alternates)
        {
            if (action.Kind == QuickSearchActionKind.RevealPath)
            {
                keys.Add(new SearchResultAction(Key.Enter, ModifierKeys.Shift, action.Title, Command(result, action)));
            }
            else if (action.Kind == QuickSearchActionKind.CopyPath)
            {
                keys.Add(new SearchResultAction(Key.C, ModifierKeys.Control | ModifierKeys.Shift, action.Title, Command(result, action)));
            }
        }

        var detail = result.Detail.Length > 0
            ? result.Detail
            : result.When is { } when ? SearchResultMetadata.FormatDate(when, now) : "";
        return new SearchResultViewModel(
            KindOf(result.ResultType),
            result.Title,
            Command(result, result.Primary),
            result.Subtitle,
            detail,
            QuickSearchIcons.ToIcon(result.Icon),
            actionHint: new SearchResultActionHint(result.Primary.Title, "enter"),
            actions: keys,
            alternates: alternates)
        {
            Key = result.Id,
            SecondaryHint = alternates.Length > 0 ? new SearchResultActionHint("Actions", "tab") : null,
        };
    }

    private RelayCommand Command(QuickSearchResult result, QuickSearchAction action) => new(() => _runner.Run(result, action));

    private static SearchResultKind KindOf(QuickSearchResultType type) => type switch
    {
        QuickSearchResultType.Applications => SearchResultKind.App,
        QuickSearchResultType.Files => SearchResultKind.File,
        QuickSearchResultType.Actions => SearchResultKind.Action,
        QuickSearchResultType.Clipboard => SearchResultKind.Clipboard,
        _ => SearchResultKind.Knowledge,
    };

    // The keys an alternate has straight from the row, as they are written on a key cap.
    private static string? KeyTextOf(QuickSearchActionKind kind) => kind switch
    {
        QuickSearchActionKind.RevealPath => "Shift+Enter",
        QuickSearchActionKind.CopyPath => "Ctrl+Shift+C",
        _ => null,
    };

    // Reports on the thread that reports, where Progress<T> would post to a context and so make the list late.
    private sealed class InlineProgress(Action<QuickSearchProviderOutcome> report) : IProgress<QuickSearchProviderOutcome>
    {
        public void Report(QuickSearchProviderOutcome value) => report(value);
    }
}

/// <summary>Turns the picture of a quick-search result into the icon its row draws.</summary>
internal static class QuickSearchIcons
{
    // A picture that came with a result is made into an image once, however many times it is listed.
    private static readonly ConditionalWeakTable<byte[], ImageSource> Images = [];

    /// <summary>The icon for <paramref name="icon"/>: its own picture when it has one, otherwise the glyph of its kind.</summary>
    public static SearchResultIcon ToIcon(QuickSearchIcon icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        if (icon.Image is { Length: > 0 } bytes && ImageOf(bytes) is { } image)
        {
            return SearchResultIcon.FromImage(image);
        }

        return SearchResultIcon.FromGlyph(icon.Kind switch
        {
            QuickSearchIconKind.Application => "Result.Icon.App",
            QuickSearchIconKind.File => "Result.Icon.File",
            QuickSearchIconKind.Folder => "Result.Icon.Folder",
            QuickSearchIconKind.Action => "Result.Icon.Action",
            QuickSearchIconKind.Clipboard => "Result.Icon.Clipboard",
            _ => "Result.Icon.Knowledge",
        });
    }

    // A frozen image, which any thread may make and any thread may draw; null for bytes that are not a picture.
    private static ImageSource? ImageOf(byte[] bytes)
    {
        if (Images.TryGetValue(bytes, out var known))
        {
            return known;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            Images.AddOrUpdate(bytes, image);
            return image;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or InvalidOperationException or ArgumentException)
        {
            // Bytes that are not a picture: the row has its kind's glyph.
            return null;
        }
    }
}

/// <summary>The launcher's four categories (PROJECT_SPEC §4.1): choosing one narrows the results to that kind, which is browsed while nothing is typed.</summary>
internal static class QuickSearchLauncherCommands
{
    private const string CommandKeyGlyph = "Glyph.CommandKey";

    private static readonly (QuickSearchResultType Type, string Icon)[] Categories =
    [
        (QuickSearchResultType.Applications, "Glyph.Applications"),
        (QuickSearchResultType.Files, "Glyph.Files"),
        (QuickSearchResultType.Actions, "Glyph.Actions"),
        (QuickSearchResultType.Clipboard, "Glyph.Clipboard"),
    ];

    /// <summary>The category rows, whose commands narrow <paramref name="results"/>. The shortcuts are the same Ctrl+1 to Ctrl+4 that narrow it from its chips.</summary>
    public static IReadOnlyList<CommandItemViewModel> Create(SearchResultsViewModel results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return
        [
            .. Categories.Select((category, index) => new CommandItemViewModel(
                QuickSearchGroups.TitleOf(category.Type), category.Icon, new RelayCommand(() => results.SetScope(category.Type)),
                new KeyHint(CommandKeyGlyph, (index + 1).ToString()), new KeyGesture(Key.D1 + index, ModifierKeys.Control))),
        ];
    }
}
