using Assistant.Core.Settings;

namespace Assistant.UI.Onboarding;

/// <summary>Setup's questions about games and creative apps: whether the AI models are released while one runs.</summary>
public sealed partial class SetupViewModel
{
    private bool _gameMode;
    private bool _creativeApps;

    /// <summary>Whether the user wants the AI models released while a game runs.</summary>
    public bool EnableGameMode { get => _gameMode; set { if (Set(ref _gameMode, value)) Changed(); } }

    /// <summary>Whether the user wants the AI models released while a creative app is open too.</summary>
    public bool EnableCreativeApps { get => _creativeApps; set { if (Set(ref _creativeApps, value)) Changed(); } }

    /// <summary>Whether either answer is yes, so there is something to explain about getting Kiri back meanwhile.</summary>
    public bool PausesForSomething => EnableGameMode || EnableCreativeApps;

    /// <summary>Whether to tell the user that Handy is closed meanwhile: an answer is yes and the Assistant shares Handy's recognizer.</summary>
    public bool GameModeClosesHandy => PausesForSomething && EnableVoiceControl && UsesHandy;

    /// <summary>The questions have no wrong answer, so the page can always be left.</summary>
    public bool CanContinueGameMode => !IsBusy;

    public async Task<bool> ApplyGameModeAsync() => await RunAsync(SaveGameModeAsync);

    /// <summary>Whether the Assistant starts in the background when Windows starts (Settings, General: Start Assistant on boot).</summary>
    public bool StartOnBoot { get => _startOnBoot; set { if (Set(ref _startOnBoot, value)) Changed(); } }

    private bool _startOnBoot;

    public async Task SaveGameModeAsync()
    {
        // Windows' own list of what starts is touched only when the answer is not what was saved, so that going through setup again changes nothing.
        var startedOnBoot = (await _settings.LoadAsync()).LaunchAtLogin.Enabled;
        var startOnBoot = StartOnBoot;
        await SaveAsync(settings => settings with
        {
            GameMode = settings.GameMode with { Games = EnableGameMode, CreativeApps = EnableCreativeApps },
            LaunchAtLogin = settings.LaunchAtLogin with { Enabled = startOnBoot },
        });
        if (startOnBoot != startedOnBoot && _launchAtLogin is { } launch)
        {
            _ = startOnBoot ? await launch.EnableAsync() : await launch.DisableAsync();
        }

        // Nothing is said under the page once it is saved: the page shows what was chosen.
        Status = string.Empty;
    }

    private void ShowGameMode(GameModeSettings saved)
    {
        EnableGameMode = saved.Games;
        EnableCreativeApps = saved.CreativeApps;
    }

    private void GameModeChanged()
    {
        OnPropertyChanged(nameof(CanContinueGameMode));
        OnPropertyChanged(nameof(PausesForSomething));
        OnPropertyChanged(nameof(GameModeClosesHandy));
    }
}
