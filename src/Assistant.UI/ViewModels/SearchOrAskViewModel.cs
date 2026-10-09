using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Assistant.Core.QuickSearch.Routing;
using Assistant.Core.Tools;
using Assistant.UI.Messages;

namespace Assistant.UI.ViewModels;

/// <summary>
/// View model for the Search or Ask bar (PROJECT_SPEC §4.1). Asking hands the question to the floating conversation,
/// which the bar grows into. While the field is empty, the launcher's categories show under the bar; while it has text,
/// the sections of results for it do, when its source has any.
/// </summary>
public sealed class SearchOrAskViewModel : INotifyPropertyChanged
{
    private const string DefaultPlaceholder = "Search or Ask";

    private const char EmDash = (char)0x2014;

    private readonly RelayCommand _askCommand;
    private readonly IQueryRouter? _router;
    private readonly ICalculator? _calculator;
    private readonly CalculationCardViewModel _card;
    private string _query = "";
    private QueryRoute _route = new(QueryRouteKind.InstantSearch, QueryRouteReason.Empty);
    private bool _hasCalculation;
    private bool _launcherWaits;
    private bool _launcherRevealed;

    public SearchOrAskViewModel(
        VoiceInputViewModel voice, LauncherViewModel? launcher = null, SearchResultsViewModel? results = null,
        ActivityViewModel? activity = null, IQueryRouter? router = null, ICalculator? calculator = null, ITextClipboard? clipboard = null)
    {
        Voice = voice;
        Activity = activity;
        _router = router;
        _calculator = calculator;
        _card = new CalculationCardViewModel(clipboard);
        Launcher = launcher ?? new LauncherViewModel([]);
        Results = results ?? new SearchResultsViewModel();
        Results.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                // Narrowing the list takes the category panel away (and giving it up brings it back), as typing does.
                case nameof(SearchResultsViewModel.HasResults):
                case nameof(SearchResultsViewModel.ShowsPanel):
                case nameof(SearchResultsViewModel.Scope):
                    OnPropertyChanged(nameof(IsResultsVisible));
                    OnPropertyChanged(nameof(IsLauncherVisible));
                    break;
                case nameof(SearchResultsViewModel.SelectedItem):
                    OnPropertyChanged(nameof(Completion));
                    OnPropertyChanged(nameof(TrailingIcon));
                    OnPropertyChanged(nameof(HasTrailingIcon));
                    OnPropertyChanged(nameof(RouteLabel));
                    break;
            }
        };
        _askCommand = new RelayCommand(_ => Ask(), _ => CanAsk);
        voice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(VoiceInputViewModel.FailureMessage) or nameof(VoiceInputViewModel.IsListening))
            {
                OnPropertyChanged(nameof(Placeholder));
            }
        };

        // What was said is asked as soon as the speaker stops (step 125), as a spoken request.
        voice.UtteranceEnded += (_, utterance) =>
        {
            if (utterance.HasRequest)
            {
                AskSpoken(utterance.Text);
            }
        };
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised with the question, exactly as typed, when the user asks it. The draft stays in the bar, on screen, while
    /// the bar grows into the conversation; whoever handles the question clears it (set <see cref="Query"/> to an empty
    /// string) once the bar has gone.
    /// </summary>
    public event EventHandler<string>? AskRequested;

    /// <summary>The bar's voice input, shown by its visualizer and its microphone button.</summary>
    public VoiceInputViewModel Voice { get; }

    /// <summary>
    /// The Searching chip's state, shared with the floating conversation: it shows under the bar while a search, the
    /// model or a tool runs. Esc cancels what it shows. It is <see langword="null"/> where nothing reports activity.
    /// </summary>
    public ActivityViewModel? Activity { get; }

    /// <summary>The category panel that shows under the bar while <see cref="Query"/> is empty.</summary>
    public LauncherViewModel Launcher { get; }

    /// <summary>
    /// Whether the category panel shows: while the field is empty and no category has been chosen, and only when the launcher has
    /// commands. Typing the first character takes it away, and emptying the field brings it back with nothing highlighted. Where
    /// <see cref="LauncherWaitsForPointer"/> is on, it also waits to be asked for (<see cref="RevealLauncher"/>).
    /// </summary>
    public bool IsLauncherVisible => HasLauncherToShow && (!_launcherWaits || _launcherRevealed);

    /// <summary>
    /// Whether the bar opens as the bar alone, as the reference's does: the categories come under it only once the pointer moves or the arrow
    /// keys ask for them (<see cref="RevealLauncher"/>), and are put away again with the bar (<see cref="ResetLauncher"/>). Off, they
    /// are there whenever the field is empty.
    /// </summary>
    public bool LauncherWaitsForPointer
    {
        get => _launcherWaits;
        set
        {
            if (_launcherWaits != value)
            {
                _launcherWaits = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLauncherVisible));
            }
        }
    }

    /// <summary>Whether the categories are there to show and wait to be asked for.</summary>
    public bool IsLauncherWaiting => HasLauncherToShow && _launcherWaits && !_launcherRevealed;

    /// <summary>Shows the categories from now on, whenever the field is empty: the pointer moved, or the arrow keys asked.</summary>
    public void RevealLauncher()
    {
        if (!_launcherRevealed)
        {
            _launcherRevealed = true;
            OnPropertyChanged(nameof(IsLauncherVisible));
        }
    }

    /// <summary>Puts the categories away until they are asked for again: the bar was put away.</summary>
    public void ResetLauncher()
    {
        if (_launcherRevealed)
        {
            _launcherRevealed = false;
            Launcher.ClearSelection();
            OnPropertyChanged(nameof(IsLauncherVisible));
        }
    }

    private bool HasLauncherToShow => _query.Length == 0 && Results.Scope is null && Launcher.HasItems;

    /// <summary>The sections of results that show under the bar while something is typed.</summary>
    public SearchResultsViewModel Results { get; }

    /// <summary>
    /// Whether the results show: while the field has text and there is something to list, or a category was chosen (the chips then stay,
    /// even when it has nothing to show).
    /// </summary>
    public bool IsResultsVisible => (_query.Length > 0 || Results.Scope is not null) && Results.ShowsPanel;

    /// <summary>
    /// Where what is typed goes (PROJECT_SPEC §4.1): instant search, a file search, a calculation, or the model. Decided by fixed rules
    /// on every change, never by the model; Enter on a highlighted result still runs it and Ctrl+Enter always asks.
    /// </summary>
    public QueryRoute Route => _route;

    /// <summary>
    /// The words that say the route when it is not the plain search ("Ask", "File search", "Calculate", "Ask with tools"), shown at the
    /// right of the field while no result is highlighted to show its icon there; empty otherwise.
    /// </summary>
    public string RouteLabel =>
        _query.Trim().Length > 0 && Results.SelectedItem is null && _route.Kind != QueryRouteKind.InstantSearch ? _route.Label : "";

    /// <summary>
    /// What the highlighted result adds to what was typed, drawn after it as the reference does ("Browser — Open" after "brave"): the rest
    /// of its name, when the name begins with what was typed, and what Enter does with it. Empty when no result is highlighted or
    /// its name does not begin with the text.
    /// </summary>
    public string Completion => CompletionOf(_query, Results.SelectedItem);

    /// <summary>
    /// The answer to the sum that is typed, shown in the bar under the field while <see cref="HasCalculation"/> (PROJECT_SPEC §4.1): worked out as each
    /// key is pressed, as PowerToys Run and the reference do, with no model and nothing to wait for. It is always the same card; its words change.
    /// </summary>
    public CalculationCardViewModel Calculation => _card;

    /// <summary>
    /// Whether the bar shows <see cref="Calculation"/>: from the moment what is typed is a sum with a value, for as long as it is still a sum being
    /// typed (a sign or a bracket typed after it keeps the last value up, rather than taking the card away between two keys).
    /// </summary>
    public bool HasCalculation => _hasCalculation;

    /// <summary>The highlighted result's icon, drawn at the right of the field as the reference does; null for none.</summary>
    public SearchResultIcon? TrailingIcon => _query.Length > 0 ? Results.SelectedItem?.Icon : null;

    /// <summary>Whether <see cref="TrailingIcon"/> has an icon to draw.</summary>
    public bool HasTrailingIcon => TrailingIcon is not null;

    /// <summary>Text typed into the bar.</summary>
    public string Query
    {
        get => _query;
        set
        {
            value ??= "";
            if (_query == value)
            {
                return;
            }

            _query = value;
            _route = _router?.Route(_query)
                ?? new QueryRoute(QueryRouteKind.InstantSearch, _query.Trim().Length == 0 ? QueryRouteReason.Empty : QueryRouteReason.LooksLikeAName);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlaceholderVisible));
            Launcher.ClearSelection();
            UpdateCalculation();
            Results.Update(_query);
            OnPropertyChanged(nameof(IsLauncherVisible));
            OnPropertyChanged(nameof(IsResultsVisible));
            OnPropertyChanged(nameof(Route));
            OnPropertyChanged(nameof(RouteLabel));
            OnPropertyChanged(nameof(Completion));
            OnPropertyChanged(nameof(TrailingIcon));
            OnPropertyChanged(nameof(HasTrailingIcon));
            _askCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Whether the placeholder shows, which it does while the query is empty.</summary>
    public bool IsPlaceholderVisible => _query.Length == 0;

    /// <summary>"Search or Ask", "Listening…" while speech is being heard, or why the microphone just stopped.</summary>
    public string Placeholder =>
        Voice.FailureMessage ?? (Voice.RecognizesSpeech && Voice.IsListening ? ListeningPlaceholder : DefaultPlaceholder);

    /// <summary>What the field says while the microphone is on and nothing has been understood yet.</summary>
    public const string ListeningPlaceholder = "Listening…";

    /// <summary>
    /// Whether the question that is being asked (<see cref="AskRequested"/> is being raised) was spoken with the microphone, so that its answer is also read
    /// aloud (PROJECT_SPEC §4.2, step 125). It is only <see langword="true"/> while the event is being handled.
    /// </summary>
    public bool AskingBySpeech { get; private set; }

    /// <summary>Asks the query (Enter). Only a query with visible text can be asked.</summary>
    public ICommand AskCommand => _askCommand;

    private bool CanAsk => !string.IsNullOrWhiteSpace(_query);

    /// <summary>
    /// Handles Esc, backing out one step at a time: the first press turns voice input off, the next cancels an operation that is
    /// running, the next goes back from the list of a result's actions to the results, the next clears the query, the next lifts the
    /// narrowing to one kind of result, and a press with none of them dismisses the bar.
    /// </summary>
    /// <returns><see langword="true"/> when the bar should close.</returns>
    public bool HandleEscape()
    {
        if (Voice.IsListening)
        {
            Voice.Stop();
            return false;
        }

        if (Activity?.Cancel() == true)
        {
            return false;
        }

        if (Results.HandleEscape())
        {
            return false;
        }

        if (_query.Length > 0)
        {
            Query = "";
            return false;
        }

        if (Results.Scope is not null)
        {
            Results.SetScope(null);
            return false;
        }

        return true;
    }

    /// <summary>
    /// What the highlighted result adds to <paramref name="query"/> as the field draws it after the text, as the reference does: the rest of the
    /// result's name when it begins with the text, a dash, and what Enter does with it ("Browser — Open"); or, for a name that does not begin with it,
    /// a dash and the whole name ("word — Microsoft Word"). Empty when no result is highlighted.
    /// </summary>
    internal static string CompletionOf(string query, SearchResultViewModel? selected)
    {
        if (selected is null || query.Length == 0 || selected.Title.Length == 0)
        {
            return "";
        }

        if (selected.Title.Length < query.Length || !selected.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return " " + EmDash + " " + selected.Title;
        }

        var verb = selected.ActionHint?.Text;
        var rest = selected.Title[query.Length..];
        return string.IsNullOrEmpty(verb) ? rest : rest + " " + EmDash + " " + verb;
    }

    // The card for the sum that is typed: worked out at once, on this thread, so its answer is on screen with the key that made it.
    private void UpdateCalculation()
    {
        var shows = false;
        if (_calculator is not null && _route is { Kind: QueryRouteKind.Calculation, Expression: { Length: > 0 } expression }
            && _calculator.TryEvaluate(expression, out var output))
        {
            _card.Caption = CaptionOf(_query);
            _card.Value = output.Result;
            shows = true;
        }
        else if (_hasCalculation && IsSumBeingTyped(_query))
        {
            // "9+10" then "9+10-": the last answer stays until the next number makes a new one.
            shows = true;
        }

        if (_hasCalculation != shows)
        {
            _hasCalculation = shows;
            OnPropertyChanged(nameof(HasCalculation));
        }
    }

    // The sum as it was typed, with the equals sign after it: "9+10 =".
    private static string CaptionOf(string query) => query.Trim().TrimEnd('=', '?', ' ') + " =";

    // Text that is still nothing but a sum on its way: digits, signs, brackets and spaces, with a digit in it.
    private static bool IsSumBeingTyped(string query) =>
        query.Any(char.IsAsciiDigit) && query.All(c => char.IsAsciiDigit(c) || "+-*/x×÷^%()., =".Contains(c, StringComparison.Ordinal));

    private void Ask() => AskRequested?.Invoke(this, _query);

    // A request that was spoken: the words show in the field, without searching for them, and are asked as they were heard.
    private void AskSpoken(string request)
    {
        if (_query != request)
        {
            _query = request;
            _route = new QueryRoute(QueryRouteKind.DirectAnswer, QueryRouteReason.ForcedAsk);
            OnPropertyChanged(nameof(Query));
            OnPropertyChanged(nameof(IsPlaceholderVisible));
            OnPropertyChanged(nameof(Route));
            OnPropertyChanged(nameof(RouteLabel));
            _askCommand.RaiseCanExecuteChanged();
        }

        AskingBySpeech = true;
        try
        {
            Ask();
        }
        finally
        {
            AskingBySpeech = false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
