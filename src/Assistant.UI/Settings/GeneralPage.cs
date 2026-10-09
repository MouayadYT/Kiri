using System.Windows.Threading;
using Assistant.Core.Settings;
using Assistant.Core.Startup;
using Assistant.UI.Gaming;

namespace Assistant.UI.Settings;

/// <summary>
/// General: starting the Assistant and the Search or Ask bar. Starting at sign-in is live (PROJECT_SPEC §4.9): the switch adds or removes the
/// entry Windows starts the app with. The bar's result count and Files scope wait for the step that feeds the bar from Windows Search, so
/// those controls show what is saved.
/// </summary>
public sealed class GeneralPage : SettingsPage, IDisposable
{
    /// <summary>What the page says under the sign-in switch when Windows holds the entry off.</summary>
    internal const string TurnedOffInWindowsNote = "Windows has this turned off for the Assistant. Turn it on in Windows Settings > Apps > Startup.";

    /// <summary>What the page says under the game mode switches while one is on and nothing it watches for runs.</summary>
    internal const string GameModeWatchingNote = "";

    private readonly ILaunchAtLogin? _launchAtLogin;
    private readonly IGameMode? _gameMode;
    private readonly Dispatcher? _dispatcher;
    private bool _startAtSignIn;
    private string _startAtSignInNote = "";
    private int _resultsPerGroup;
    private bool _filesByDefault;
    private bool _gameModeEnabled;
    private bool _creativeAppsEnabled;
    private bool _closeHandyForGames;
    private bool _usesHandy;
    private string _gameModeNote = "";
    private bool _isCreativeListOpen;
    private System.Windows.Input.ICommand? _openCreativeList;

    internal GeneralPage(SettingsViewModel root, ILaunchAtLogin? launchAtLogin = null, IGameMode? gameMode = null, Dispatcher? dispatcher = null)
        : base(root, SettingsSection.General)
    {
        _launchAtLogin = launchAtLogin;
        _gameMode = gameMode;
        _dispatcher = dispatcher;
        if (_gameMode is not null)
        {
            _gameMode.Changed += OnGameModeChanged;
        }
    }

    /// <summary>Whether the AI models are released while a game runs.</summary>
    public bool GameModeEnabled
    {
        get => _gameModeEnabled;
        set
        {
            if (Set(ref _gameModeEnabled, value))
            {
                Commit(settings => settings with { GameMode = settings.GameMode with { Games = value } });
                ShowGameMode();
            }
        }
    }

    /// <summary>Whether the AI models are released while a creative app is open too.</summary>
    public bool CreativeAppsEnabled
    {
        get => _creativeAppsEnabled;
        set
        {
            if (Set(ref _creativeAppsEnabled, value))
            {
                Commit(settings => settings with { GameMode = settings.GameMode with { CreativeApps = value } });
                ShowGameMode();
            }
        }
    }

    /// <summary>Whether Handy, when the Assistant shares its recognizer, is closed while the models are released.</summary>
    public bool CloseHandyForGames
    {
        get => _closeHandyForGames;
        set
        {
            if (Set(ref _closeHandyForGames, value))
            {
                Commit(settings => settings with { GameMode = settings.GameMode with { ReleaseSharedRecognizer = value } });
            }
        }
    }

    /// <summary>The creative apps game mode watches for, by the names people know them by, for the list that opens from "View the full list".</summary>
    public IReadOnlyList<string> CreativeAppNames => Assistant.Windows.Gaming.CreativeAppNames.All;

    /// <summary>Whether the list of creative apps is open.</summary>
    public bool IsCreativeListOpen
    {
        get => _isCreativeListOpen;
        set => Set(ref _isCreativeListOpen, value);
    }

    /// <summary>Opens the list of creative apps. It closes by itself when the user clicks anywhere else.</summary>
    public System.Windows.Input.ICommand OpenCreativeListCommand => _openCreativeList ??= new ViewModels.RelayCommand(_ => IsCreativeListOpen = true);

    /// <summary>Whether the switch for Handy has anything to act on: game mode is on, for games or creative apps, and the Assistant uses Handy's recognizer.</summary>
    public bool ShowsCloseHandy => (_gameModeEnabled || _creativeAppsEnabled) && _usesHandy;

    /// <summary>What game mode is doing right now, or nothing while it is off.</summary>
    public string GameModeNote
    {
        get => _gameModeNote;
        private set
        {
            if (Set(ref _gameModeNote, value))
            {
                OnPropertyChanged(nameof(HasGameModeNote));
            }
        }
    }

    /// <summary>Whether there is a <see cref="GameModeNote"/> to show.</summary>
    public bool HasGameModeNote => _gameModeNote.Length > 0;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_gameMode is not null)
        {
            _gameMode.Changed -= OnGameModeChanged;
        }
    }

    /// <summary>What to say about <paramref name="status"/> under the switch.</summary>
    internal static string DescribeGameMode(GameModeStatus status) => status switch
    {
        { Enabled: false } => "",
        { IsActive: true, Game: { Length: > 0 } game } => $"{game} is running: the AI models are released until it ends.",
        { IsActive: true } => "A game or creative app is running: the AI models are released until it ends.",
        { Overridden: true, Game: { Length: > 0 } game } => $"{game} is running, and you resumed the local AI for now.",
        { Overridden: true } => "A game or creative app is running, and you resumed the local AI for now.",
        _ => GameModeWatchingNote,
    };

    // The status changes on a background thread; the page is the UI thread's.
    private void OnGameModeChanged(object? sender, EventArgs e)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            ShowGameMode();
        }
        else
        {
            _dispatcher.BeginInvoke(ShowGameMode);
        }
    }

    private void ShowGameMode()
    {
        OnPropertyChanged(nameof(ShowsCloseHandy));

        // What the detector says, once it has caught up with the switches; until then, what the switches say.
        var status = _gameMode?.Status;
        GameModeNote = !(_gameModeEnabled || _creativeAppsEnabled) ? "" : status is { Enabled: true } ? DescribeGameMode(status) : GameModeWatchingNote;
    }

    /// <summary>Whether the Assistant starts when the user signs in to Windows.</summary>
    public bool StartAtSignIn
    {
        get => _startAtSignIn;
        set
        {
            if (!Set(ref _startAtSignIn, value))
            {
                return;
            }

            AppSettings Change(AppSettings settings) =>
                settings with { LaunchAtLogin = settings.LaunchAtLogin with { Enabled = value } };
            if (_launchAtLogin is not { } launchAtLogin)
            {
                Commit(Change);
            }
            else if (value)
            {
                Commit(Change, () => launchAtLogin.EnableAsync(), "The Assistant couldn't be set to start when you sign in.");
            }
            else
            {
                Commit(Change, () => launchAtLogin.DisableAsync(), "The Assistant couldn't be taken out of what starts when you sign in.");
            }
        }
    }

    /// <summary>What to tell the user about the sign-in entry when it is not working as the switch says, or nothing.</summary>
    public string StartAtSignInNote
    {
        get => _startAtSignInNote;
        private set
        {
            if (Set(ref _startAtSignInNote, value))
            {
                OnPropertyChanged(nameof(HasStartAtSignInNote));
            }
        }
    }

    /// <summary>Whether there is a <see cref="StartAtSignInNote"/> to show.</summary>
    public bool HasStartAtSignInNote => _startAtSignInNote.Length > 0;

    /// <summary>How many local results each group of the Search or Ask bar shows.</summary>
    public int ResultsPerGroup
    {
        get => _resultsPerGroup;
        set
        {
            if (Set(ref _resultsPerGroup, value))
            {
                Commit(settings => settings with { Ui = settings.Ui with { BarResultsPerGroup = value } });
            }
        }
    }

    /// <summary>The numbers of results the bar can show in each group.</summary>
    public IReadOnlyList<int> ResultsPerGroupChoices { get; } =
        [.. Enumerable.Range(SettingsLimits.MinBarResultsPerGroup, SettingsLimits.MaxBarResultsPerGroup - SettingsLimits.MinBarResultsPerGroup + 1)];

    /// <summary>Whether the bar starts with its Files scope on.</summary>
    public bool FilesByDefault
    {
        get => _filesByDefault;
        set
        {
            if (Set(ref _filesByDefault, value))
            {
                Commit(settings => settings with { Ui = settings.Ui with { FilesScopeOnByDefault = value } });
            }
        }
    }

    internal override void Apply(AppSettings settings, bool fresh)
    {
        StartAtSignIn = settings.LaunchAtLogin.Enabled;
        StartAtSignInNote = settings.LaunchAtLogin.Enabled && _launchAtLogin?.GetState() == LaunchAtLoginState.TurnedOffInWindows
            ? TurnedOffInWindowsNote
            : "";
        ResultsPerGroup = settings.Ui.BarResultsPerGroup;
        FilesByDefault = settings.Ui.FilesScopeOnByDefault;
        GameModeEnabled = settings.GameMode.Games;
        CreativeAppsEnabled = settings.GameMode.CreativeApps;
        CloseHandyForGames = settings.GameMode.ReleaseSharedRecognizer;
        _usesHandy = settings.Voice.VoiceInputEnabled && settings.Voice.SpeechRecognitionModelId == "handy";
        ShowGameMode();
    }
}
