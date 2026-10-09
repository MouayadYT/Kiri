using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Assistant.Core.Contracts;
using Assistant.Core.QuickSearch;

namespace Assistant.UI.ViewModels;

/// <summary>
/// The results under the Search or Ask bar while something is typed (PROJECT_SPEC §4.1): sections of results, of which
/// one row may be highlighted, moved through the sections with the arrow keys. As with the launcher's categories, the
/// bar's editor keeps the focus, so typing still goes to it; only the keys that move or run a row are taken here.
/// Nothing is highlighted when results first appear, so Enter still asks what was typed, unless a source highlights a result
/// whose name is what was typed or begins with it, which Enter then opens.
/// </summary>
/// <remarks>
/// A list can be narrowed to one kind of result with the chips over it (<see cref="Chips"/>, <c>Ctrl+1</c> to <c>Ctrl+4</c>), and the
/// other things a highlighted result can do are listed with <c>Tab</c> (<see cref="IsShowingAlternates"/>), where <c>Enter</c> runs the
/// highlighted one and <c>Esc</c> or <c>Tab</c> goes back to the list.
/// </remarks>
public sealed class SearchResultsViewModel : INotifyPropertyChanged
{
    /// <summary>How long the typing pauses before the results that take time (Windows Search) are asked for.</summary>
    public static readonly TimeSpan DefaultLiveDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How long the typing pauses before the whole list shows, as in the reference: its best match is beside the text from the first key, and the
    /// list follows once the user stops typing. Measured from the reference recording: the list began to appear 0.667 s after the last key of
    /// "word" and 0.625 s after the last key of "fortnite".
    /// </summary>
    public static readonly TimeSpan ReferenceRevealDelay = TimeSpan.FromMilliseconds(650);

    private readonly ISearchResultsSource? _source;
    private readonly IQuickSearchResultsSource? _quick;
    private readonly SearchScopeChipViewModel[] _chips;
    private readonly ISettingsService? _settings;
    private readonly TimeSpan _revealDelay;
    private readonly TimeProvider _clock;
    private ITimer? _revealTimer;
    private bool _revealed;
    private IReadOnlyList<SearchResultSectionViewModel> _sections = [];
    private IReadOnlyList<SearchResultViewModel> _items = [];
    private SearchResultViewModel? _selected;
    private CancellationTokenSource? _live;
    private string _query = "";
    private string _emptyMessage = "";
    private string _notice = "";
    private QuickSearchResultType? _scope;
    private bool _searching;
    private bool _answered;
    private bool _alternatesWhenListed;
    private bool _userMoved;

    // The list the alternates replaced, to give back, and the answer that came while they were shown, to show then.
    private SearchResultViewModel? _alternatesOf;
    private IReadOnlyList<SearchResultSectionViewModel> _savedSections = [];
    private SearchResultsSnapshot? _pending;

    /// <param name="source">Results that are there at once, such as samples; when it has some for a query, they are the results.</param>
    /// <param name="liveSource">
    /// Results that take time, for a query the first source has none for: asked after <paramref name="liveDelay"/> of no typing,
    /// shown when they arrive, and dropped if something was typed since.
    /// </param>
    /// <param name="liveDelay">The pause before <paramref name="liveSource"/> is asked; <see cref="DefaultLiveDelay"/> by default.</param>
    /// <param name="quickSource">
    /// Results that come as they are found, with no pause (each provider has its own): asked on every change, narrowed by
    /// <see cref="Scope"/>, and used instead of <paramref name="liveSource"/> when both are given.
    /// </param>
    /// <param name="chips">The chips that narrow the list; each kind a provider finds, in the order they are listed.</param>
    /// <param name="revealDelay">
    /// How long the typing pauses before the list of what is typed shows (<see cref="IsRevealed"/>); until then only the best match is offered, beside
    /// the text. Nothing (the list shows with its first results) unless given; the app gives <see cref="ReferenceRevealDelay"/>.
    /// </param>
    /// <param name="clock">The clock the pause is timed on; the system's by default.</param>
    public SearchResultsViewModel(
        ISearchResultsSource? source = null, IAsyncSearchResultsSource? liveSource = null, TimeSpan? liveDelay = null,
        IQuickSearchResultsSource? quickSource = null, IEnumerable<QuickSearchResultType>? chips = null,
        ISettingsService? settings = null, TimeSpan? revealDelay = null, TimeProvider? clock = null)
    {
        _settings = settings;
        _revealDelay = revealDelay ?? TimeSpan.Zero;
        _clock = clock ?? TimeProvider.System;
        _source = source;
        _quick = quickSource ?? (liveSource is null ? null : new LiveSourceAdapter(liveSource, liveDelay ?? DefaultLiveDelay));
        _chips = [.. (chips ?? QuickSearchGroups.Order).Select(Chip)];
        Chips = new ReadOnlyCollection<SearchScopeChipViewModel>(_chips);
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The sections, top to bottom. A section without results is left out.</summary>
    public IReadOnlyList<SearchResultSectionViewModel> Sections => _sections;

    /// <summary>Every result of every section, top to bottom.</summary>
    public IReadOnlyList<SearchResultViewModel> Items => _items;

    /// <summary>Whether there is anything to list.</summary>
    public bool HasResults => _items.Count > 0;

    /// <summary>
    /// Whether the results of what was typed are still on their way: some providers have not answered yet. Until the first has, the list
    /// shown, if any, is for what was typed before.
    /// </summary>
    public bool IsSearching => _searching;

    /// <summary>The chips over the results, one for each kind of result, which narrow the list to it.</summary>
    public IReadOnlyList<SearchScopeChipViewModel> Chips { get; }

    /// <summary>The kind of result the list is narrowed to, or <see langword="null"/> when it lists everything.</summary>
    public QuickSearchResultType? Scope => _scope;

    /// <summary>
    /// Whether the panel shows: there is something to list and the list has been revealed, or the list is narrowed to a kind, so that its chips stay
    /// under the pointer even when that kind has nothing to show.
    /// </summary>
    public bool ShowsPanel => (HasResults && IsRevealed) || _scope is not null;

    /// <summary>
    /// Whether the list of what is typed may show: once the typing has paused for the reveal delay, or at once when the user moves through it with
    /// the arrow keys, narrows it to a kind or has nothing typed. Revealed, it stays revealed while there is text, however it changes, and while the
    /// bar is put away and opened again; emptying the field starts again. Before that, the best match is offered beside the text only.
    /// </summary>
    public bool IsRevealed => _revealDelay <= TimeSpan.Zero || _revealed || _scope is not null;

    /// <summary>Shows the list of what is typed now, without waiting for the typing to pause.</summary>
    public void Reveal()
    {
        StopRevealTimer();
        if (_revealed)
        {
            return;
        }

        _revealed = true;
        OnPropertyChanged(nameof(IsRevealed));
        OnPropertyChanged(nameof(ShowsPanel));
    }

    // Each key starts the pause again; the list shows when it ends. An empty field forgets that the list was shown.
    private void UpdateReveal(string query)
    {
        if (_revealDelay <= TimeSpan.Zero)
        {
            return;
        }

        if (query.Length == 0)
        {
            StopRevealTimer();
            if (_revealed)
            {
                _revealed = false;
                OnPropertyChanged(nameof(IsRevealed));
                OnPropertyChanged(nameof(ShowsPanel));
            }

            return;
        }

        if (_revealed)
        {
            return;
        }

        StopRevealTimer();
        var context = SynchronizationContext.Current;
        ITimer? timer = null;
        timer = _clock.CreateTimer(
            _ =>
            {
                void Fire()
                {
                    if (ReferenceEquals(_revealTimer, timer))
                    {
                        Reveal();
                    }
                }

                if (context is null)
                {
                    Fire();
                }
                else
                {
                    context.Post(_ => Fire(), null);
                }
            },
            null, _revealDelay, Timeout.InfiniteTimeSpan);
        _revealTimer = timer;
    }

    private void StopRevealTimer()
    {
        _revealTimer?.Dispose();
        _revealTimer = null;
    }

    /// <summary>What a list narrowed to one kind of result says when it is empty ("No files found"); empty at other times.</summary>
    public string EmptyMessage => HasResults ? "" : _emptyMessage;

    /// <summary>Whether <see cref="EmptyMessage"/> has words.</summary>
    public bool HasEmptyMessage => EmptyMessage.Length > 0;

    /// <summary>
    /// What to tell the user about what they chose from the list ("That could not be opened"), shown above the results until the typing
    /// changes; empty for nothing.
    /// </summary>
    public string Notice => _notice;

    /// <summary>Whether <see cref="Notice"/> has words.</summary>
    public bool HasNotice => _notice.Length > 0;

    /// <summary>Tells the user something about what they chose; the next change to the typed text takes it away.</summary>
    public void ShowNotice(string text)
    {
        _notice = text ?? "";
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(HasNotice));
    }

    /// <summary>Whether the other things the highlighted result can do are listed in place of the results (<c>Tab</c>).</summary>
    public bool IsShowingAlternates => _alternatesOf is not null;

    /// <summary>The highlighted result, or <see langword="null"/> when none is.</summary>
    public SearchResultViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            if (value is not null && !_items.Contains(value))
            {
                value = null;
            }

            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            if (_selected is not null)
            {
                _selected.IsSelected = false;
            }

            _selected = value;
            if (_selected is not null)
            {
                _selected.IsSelected = true;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Asks the source for the results of <paramref name="query"/> and lists them, with nothing highlighted unless the source highlights
    /// one. When the first source has none and there is a source for results that take time, it is asked; what was listed stays until
    /// its answer comes, so the list does not flicker as the user types, and an answer for an older query is never shown.
    /// </summary>
    public void Update(string query)
    {
        _query = query;
        UpdateReveal(query);
        if (_notice.Length > 0)
        {
            ShowNotice("");
        }

        LeaveAlternates(restore: false);
        CancelLive();
        _alternatesWhenListed = false;
        if (query.Length == 0 && _scope is null)
        {
            SetSections([]);
            return;
        }

        var sections = query.Length == 0 ? [] : _source?.Search(query) ?? [];
        if (_quick is null || sections.Any(section => section.Items.Count > 0))
        {
            SetSections(sections);
            _emptyMessage = "";
            return;
        }

        _userMoved = false;
        SelectedItem = null;
        StartLive(_quick, query);
    }

    /// <summary>Lists <paramref name="sections"/>, with nothing highlighted. Sections without results are dropped.</summary>
    public void SetSections(IEnumerable<SearchResultSectionViewModel> sections)
    {
        _userMoved = false;
        ReplaceSections(sections);
        SelectedItem = null;
    }

    /// <summary>Lists nothing, and drops any answer that is still on its way.</summary>
    public void Clear()
    {
        LeaveAlternates(restore: false);
        CancelLive();
        _alternatesWhenListed = false;
        SetSections([]);
    }

    /// <summary>
    /// Narrows the list to <paramref name="type"/>, or, for <see langword="null"/>, lists everything again, and looks again for what is
    /// typed. A list that is narrowed with nothing typed lists what is there to browse: the applications used most, the files that
    /// changed lately, the actions, what was copied.
    /// </summary>
    public void SetScope(QuickSearchResultType? type)
    {
        if (_scope == type)
        {
            return;
        }

        _scope = type;
        foreach (var chip in _chips)
        {
            chip.IsActive = chip.Type == type;
        }

        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(ShowsPanel));
        Update(_query);
    }

    /// <summary>
    /// Puts the list back to how the bar starts: narrowed to Files when "Search files by default" is on, and otherwise listing everything.
    /// It reads the setting when it is called, so a change in Settings counts the next time the bar is opened. Call it on the UI thread.
    /// </summary>
    public void ResetScope()
    {
        if (_settings is null)
        {
            SetScope(null);
            return;
        }

        _ = ResetScopeAsync();
    }

    private async Task ResetScopeAsync()
    {
        QuickSearchResultType? start = null;
        try
        {
            var settings = await _settings!.LoadAsync().ConfigureAwait(true);
            start = settings.Ui.FilesScopeOnByDefault && _chips.Any(chip => chip.Type == QuickSearchResultType.Files) ? QuickSearchResultType.Files : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Settings that cannot be read leave the bar listing everything, as it always did.
        }

        SetScope(start);
    }

    /// <summary>Narrows the list to <paramref name="type"/>, or, when it already is, lists everything again.</summary>
    public void ToggleScope(QuickSearchResultType type) => SetScope(_scope == type ? null : type);

    /// <summary>Looks again for what was typed last, for a source whose answers have changed.</summary>
    public void Refresh() => Update(_query);

    /// <summary>
    /// Handles a key the bar received while results show. <c>↓</c> and <c>↑</c> move the highlight, from none to the
    /// first or the last, through every section and around the ends; <c>Enter</c> runs the highlighted result. With
    /// none highlighted, Enter is the bar's own. A highlighted result may have keys of its own for other things it can do
    /// (<see cref="SearchResultViewModel.Actions"/>), such as showing a file in File Explorer, and they are taken first. <c>Tab</c>
    /// lists the other things the highlighted result can do (<see cref="SearchResultViewModel.Alternates"/>), or, with none highlighted,
    /// those of the first result that has any; a result with a <c>Tab</c> action of its own runs it. <c>Ctrl+1</c> to <c>Ctrl+4</c>
    /// narrow the list to one kind of result.
    /// </summary>
    /// <returns><see langword="true"/> when the key was the results', so the editor must not see it.</returns>
    public bool HandleKey(Key key, ModifierKeys modifiers)
    {
        // The chips' shortcuts, while the panel shows: the launcher's own keys, which narrow the same way.
        if (modifiers == ModifierKeys.Control && ChipOfKey(key) is { } chip && ShowsPanel && !IsShowingAlternates)
        {
            chip.Command.Execute(null);
            return true;
        }

        // Tab is the results' while a list shows or is on its way, and never moves the focus to the microphone: with the results
        // still coming it waits for them, and with them showing it lists what the highlighted result can do, or, with none highlighted,
        // what the first result that has something to offer can do (a person who has typed a file's name and pressed Tab has no other use
        // for it in a one-line bar). A highlighted result with nothing to offer leaves the key unused rather than taking it elsewhere.
        if (key == Key.Tab && modifiers == ModifierKeys.None && (_searching || HasResults || IsShowingAlternates))
        {
            // Asking for what a result can do shows the list it is in.
            Reveal();
            if (IsShowingAlternates)
            {
                CloseAlternates();
            }
            else if (_searching && !_answered)
            {
                _alternatesWhenListed = true;
            }
            else
            {
                RunTab();
            }

            return true;
        }

        if (!HasResults)
        {
            return false;
        }

        if (_selected?.TryRunAction(key, modifiers) == true)
        {
            return true;
        }

        if (modifiers != ModifierKeys.None)
        {
            return false;
        }

        switch (key)
        {
            // Moving through the list shows it at once, without waiting for the pause.
            case Key.Down:
                Reveal();
                MoveSelection(1);
                return true;
            case Key.Up:
                Reveal();
                MoveSelection(-1);
                return true;
            case Key.Enter when _selected is not null:
                _selected.Activate();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Moves the highlight <paramref name="steps"/> rows down (or up, when negative), around the ends.</summary>
    public void MoveSelection(int steps)
    {
        if (!HasResults)
        {
            return;
        }

        _userMoved = true;
        var count = _items.Count;
        var current = _selected is null ? -1 : IndexOf(_selected);
        var next = current < 0
            ? (steps > 0 ? steps - 1 : count + steps)
            : current + steps;
        SelectedItem = _items[((next % count) + count) % count];
    }

    /// <summary>Highlights nothing.</summary>
    public void ClearSelection() => SelectedItem = null;

    /// <summary>
    /// Handles Esc for the results: the list of what the highlighted result can do goes back to the results.
    /// </summary>
    /// <returns><see langword="true"/> when there was such a list, so Esc has done its work.</returns>
    public bool HandleEscape()
    {
        if (!IsShowingAlternates)
        {
            return false;
        }

        CloseAlternates();
        return true;
    }

    /// <summary>
    /// Lists what <paramref name="result"/> can do besides what Enter does, in place of the results, with the first highlighted. Nothing
    /// changes for a result with nothing else to do.
    /// </summary>
    /// <returns>Whether the list was shown.</returns>
    public bool ShowAlternates(SearchResultViewModel result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Alternates.Count == 0 || !_items.Contains(result) || IsShowingAlternates)
        {
            return false;
        }

        _alternatesOf = result;
        _savedSections = _sections;
        var rows = result.Alternates.Select(alternate => new SearchResultViewModel(
            SearchResultKind.Action, alternate.Title, alternate.Command,
            icon: AlternateIcon(alternate),
            actionHint: alternate.KeyText is null ? null : new SearchResultActionHint(null, alternate.KeyText),
            size: SearchResultRowSize.Standard) { Key = "alternate:" + alternate.Title });
        ReplaceSections([new SearchResultSectionViewModel("Actions for " + result.Title, rows)]);
        _userMoved = true;
        SelectedItem = _items[0];
        OnPropertyChanged(nameof(IsShowingAlternates));
        return true;
    }

    /// <summary>Goes back from the list of what a result can do to the results, with that result highlighted.</summary>
    public void CloseAlternates() => LeaveAlternates(restore: true);

    // Shows each alternate as one of the Assistant's own actions, with the glyph for what it does.
    private static SearchResultIcon AlternateIcon(SearchResultAlternate alternate) =>
        SearchResultIcon.FromGlyph("Result.Icon.Action");

    // Gives the list back, or, for a list that is being replaced anyway, forgets it.
    private void LeaveAlternates(bool restore)
    {
        if (_alternatesOf is not { } owner)
        {
            return;
        }

        var saved = _savedSections;
        var pending = _pending;
        _alternatesOf = null;
        _savedSections = [];
        _pending = null;
        OnPropertyChanged(nameof(IsShowingAlternates));
        if (!restore)
        {
            return;
        }

        if (pending is not null)
        {
            // A newer answer came while the actions were shown: it is the list now, keeping the highlight where the user left it.
            ReplaceSections(pending.Sections);
            _emptyMessage = pending.EmptyMessage;
            SelectedItem = _items.FirstOrDefault(item => owner.Key is not null && item.Key == owner.Key);
            return;
        }

        ReplaceSections(saved);
        SelectedItem = _items.FirstOrDefault(item => ReferenceEquals(item, owner)) ?? _items.FirstOrDefault(item => owner.Key is not null && item.Key == owner.Key);
    }

    // The chip that Ctrl and this key press, if any: Ctrl+1 is the first chip.
    private SearchScopeChipViewModel? ChipOfKey(Key key)
    {
        var index = key is >= Key.D1 and <= Key.D9 ? key - Key.D1 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad1 : -1;
        return index >= 0 && index < _chips.Length ? _chips[index] : null;
    }

    private SearchScopeChipViewModel Chip(QuickSearchResultType type)
    {
        var index = QuickSearchGroups.PlaceOf(type);
        return new SearchScopeChipViewModel(
            type, QuickSearchGroups.TitleOf(type), new RelayCommand(() => ToggleScope(type)), "Ctrl+" + (index + 1));
    }

    // Tab with the results listed: what the row's own Tab action does, or what else the row can do.
    private void RunTab()
    {
        if (_selected is null)
        {
            // None highlighted: the first row with a Tab action of its own runs it; failing that, the first row with something to offer
            // is highlighted and lists it.
            if (_items.Any(item => item.TryRunAction(Key.Tab, ModifierKeys.None)))
            {
                return;
            }

            if (_items.FirstOrDefault(item => item.Alternates.Count > 0) is { } first)
            {
                SelectedItem = first;
                ShowAlternates(first);
            }

            return;
        }

        if (!_selected.TryRunAction(Key.Tab, ModifierKeys.None))
        {
            ShowAlternates(_selected);
        }
    }

    // A Tab pressed before the results came is kept for them, as typing ahead is: it is done as soon as there is an answer to what was typed.
    private void RunPendingTab()
    {
        if (_alternatesWhenListed)
        {
            _alternatesWhenListed = false;
            RunTab();
        }
    }

    // Replaces the sections and what is listed, and says so; the highlight is the caller's to set.
    private void ReplaceSections(IEnumerable<SearchResultSectionViewModel> sections)
    {
        var kept = sections.Where(section => section.Items.Count > 0).ToList();
        if (kept.Count == _sections.Count && kept.Zip(_sections).All(pair => ReferenceEquals(pair.First, pair.Second)))
        {
            return;
        }

        SelectedItem = null;
        var hadResults = HasResults;
        _sections = new ReadOnlyCollection<SearchResultSectionViewModel>(kept);
        _items = new ReadOnlyCollection<SearchResultViewModel>([.. kept.SelectMany(section => section.Items)]);
        OnPropertyChanged(nameof(Sections));
        OnPropertyChanged(nameof(Items));
        if (hadResults != HasResults)
        {
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(ShowsPanel));
        }

        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(HasEmptyMessage));
    }

    // What the source has found so far: it replaces the list, and the highlight stays where the user put it, or, if they have not moved
    // it, is the result the source highlights.
    private void ShowSnapshot(SearchResultsSnapshot snapshot)
    {
        if (IsShowingAlternates)
        {
            _pending = snapshot;
            return;
        }

        _answered = true;
        var keep = _userMoved ? _selected?.Key : null;
        ReplaceSections(snapshot.Sections);
        _emptyMessage = snapshot.EmptyMessage;
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(HasEmptyMessage));
        SelectedItem = _userMoved
            ? (keep is null ? null : _items.FirstOrDefault(item => item.Key == keep))
            : snapshot.Highlighted is { } highlighted && _items.Contains(highlighted) ? highlighted : null;

        // A Tab that was typed ahead waits only for the first answer to what was typed, not for the slow ones after it.
        RunPendingTab();
    }

    // Asks the source for the results of the query, off the UI thread, and shows what it finds on it as it finds it.
    private void StartLive(IQuickSearchResultsSource source, string query)
    {
        var live = _live = new CancellationTokenSource();
        _searching = true;
        _answered = false;
        _ = RunLiveAsync(source, query, _scope, live, SynchronizationContext.Current);
    }

    private async Task RunLiveAsync(
        IQuickSearchResultsSource source, string query, QuickSearchResultType? scope, CancellationTokenSource live,
        SynchronizationContext? context)
    {
        // Taken now, while the source is certainly not disposed: a newer query disposes it.
        var token = live.Token;

        void Post(Action action)
        {
            if (context is null)
            {
                action();
            }
            else
            {
                context.Post(_ => action(), null);
            }
        }

        void Show(SearchResultsSnapshot snapshot) => Post(() =>
        {
            // Only the newest query's answers are shown.
            if (ReferenceEquals(_live, live) && !live.IsCancellationRequested)
            {
                ShowSnapshot(snapshot);
            }
        });

        try
        {
            await source.SearchAsync(query, scope, Show, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // A search that could not run lists nothing; typing is never interrupted by it.
            Show(new SearchResultsSnapshot([]));
        }

        Post(() =>
        {
            if (ReferenceEquals(_live, live) && !live.IsCancellationRequested)
            {
                _searching = false;
                RunPendingTab();
            }
        });
    }

    // Drops the answer that is on its way, if any: the query has changed or the list was cleared.
    private void CancelLive()
    {
        _searching = false;
        _answered = false;
        if (_live is { } live)
        {
            _live = null;
            live.Cancel();
            live.Dispose();
        }
    }

    private int IndexOf(SearchResultViewModel item)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (ReferenceEquals(_items[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    // A source that answers once, after a pause, as the bar's slow results always did.
    private sealed class LiveSourceAdapter(IAsyncSearchResultsSource source, TimeSpan delay) : IQuickSearchResultsSource
    {
        public async Task SearchAsync(
            string query, QuickSearchResultType? scope, Action<SearchResultsSnapshot> report, CancellationToken cancellationToken)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var sections = await source.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            report(new SearchResultsSnapshot(sections));
        }
    }
}
