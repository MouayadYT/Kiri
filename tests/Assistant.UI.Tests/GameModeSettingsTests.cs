using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Events;
using Assistant.Core.Settings;
using Assistant.Core.Voice;
using Assistant.UI.Gaming;
using Assistant.UI.Onboarding;
using Assistant.UI.Settings;
using Assistant.UI.Voice;
using Assistant.Windows.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// Game mode where the user meets it: the switch in Settings > General and what it says under it, the question setup asks, and the voice as it
/// is let go of and taken up again.
/// </summary>
public sealed partial class PromptInputControlTests
{
    [Fact]
    public void TheGeneralPageTurnsGameModeOn_AndSaysWhatItIsDoing() => RunSta(() =>
    {
        var gameMode = new FakeGameMode();
        var kit = CreateSettingsKit(gameMode: gameMode);
        var page = kit.Model.General;
        Assert.False(page.GameModeEnabled);
        Assert.False(page.HasGameModeNote);

        page.GameModeEnabled = true;
        kit.Settle();

        Assert.True(kit.Saved.GameMode.Games);
        Assert.False(kit.Saved.GameMode.CreativeApps);
        Assert.Equal(GeneralPage.GameModeWatchingNote, page.GameModeNote);

        // A game is found, on the detector's thread: the page says so on its own.
        gameMode.Set(new GameModeStatus(true, true, "Cyberpunk 2077", false));
        Assert.Equal("Cyberpunk 2077 is running: the AI models are released until it ends.", page.GameModeNote);

        gameMode.Set(new GameModeStatus(true, false, "Cyberpunk 2077", true));
        Assert.Equal("Cyberpunk 2077 is running, and you resumed the local AI for now.", page.GameModeNote);

        gameMode.Set(new GameModeStatus(true, false, null, false));
        Assert.Equal(GeneralPage.GameModeWatchingNote, page.GameModeNote);

        page.GameModeEnabled = false;
        kit.Settle();
        Assert.False(kit.Saved.GameMode.Games);
        Assert.False(page.HasGameModeNote);
        kit.Model.Dispose();
    });

    [Fact]
    public void TheGeneralPageTurnsCreativeAppsOnByItself_WithoutTurningGamesOn() => RunSta(() =>
    {
        var gameMode = new FakeGameMode();
        var kit = CreateSettingsKit(gameMode: gameMode);
        var page = kit.Model.General;
        Assert.False(page.CreativeAppsEnabled);

        page.CreativeAppsEnabled = true;
        kit.Settle();

        Assert.True(kit.Saved.GameMode.CreativeApps);
        Assert.False(kit.Saved.GameMode.Games);
        Assert.False(page.GameModeEnabled);
        Assert.Equal(GeneralPage.GameModeWatchingNote, page.GameModeNote);

        gameMode.Set(new GameModeStatus(true, true, "Adobe After Effects 2025", false));
        Assert.Equal("Adobe After Effects 2025 is running: the AI models are released until it ends.", page.GameModeNote);

        // Both on, then games off again: the creative apps stay as they were.
        page.GameModeEnabled = true;
        kit.Settle();
        page.GameModeEnabled = false;
        kit.Settle();
        Assert.True(kit.Saved.GameMode.CreativeApps);
        Assert.True(page.HasGameModeNote);

        page.CreativeAppsEnabled = false;
        kit.Settle();
        Assert.Equal(new GameModeSettings(), kit.Saved.GameMode);
        Assert.False(page.HasGameModeNote);
        kit.Model.Dispose();
    });

    [Fact]
    public void TheSwitchThatClosesHandy_ShowsOnlyWhileGameModeIsOnAndHandyIsShared() => RunSta(() =>
    {
        var kit = CreateSettingsKit(new AppSettings
        {
            GameMode = new GameModeSettings { Games = true },
            Voice = new VoiceSettings { VoiceInputEnabled = true, SpeechRecognitionModelId = "handy" },
        });
        var page = kit.Model.General;
        Assert.True(page.ShowsCloseHandy);
        Assert.True(page.CloseHandyForGames);

        page.CloseHandyForGames = false;
        kit.Settle();
        Assert.False(kit.Saved.GameMode.ReleaseSharedRecognizer);
        Assert.True(kit.Saved.GameMode.Games);

        page.GameModeEnabled = false;
        kit.Settle();
        Assert.False(page.ShowsCloseHandy);

        // Creative apps alone release the models too, so Handy is as much in question.
        page.CreativeAppsEnabled = true;
        kit.Settle();
        Assert.True(page.ShowsCloseHandy);
        kit.Model.Dispose();

        // A recognizer of the Assistant's own: there is no Handy to close, so the switch is not offered.
        var own = CreateSettingsKit(new AppSettings { GameMode = new GameModeSettings { Games = true, CreativeApps = true } });
        Assert.False(own.Model.General.ShowsCloseHandy);
        own.Model.Dispose();
    });

    [Fact]
    public void TheGeneralPageShowsTheGameModeGroup() => RunSta(() => WithTheme(() =>
    {
        var gameMode = new FakeGameMode();
        var kit = CreateSettingsKit(
            new AppSettings
            {
                GameMode = new GameModeSettings { Games = true, CreativeApps = true },
                Voice = new VoiceSettings { VoiceInputEnabled = true, SpeechRecognitionModelId = "handy" },
            },
            gameMode: gameMode);
        gameMode.Set(new GameModeStatus(true, true, "Cyberpunk 2077", false));
        var (window, _, _) = CreateSettingsWindow(kit);
        using var errors = BindingErrors.Listen();
        try
        {
            window.Show(); Pump(); window.UpdateLayout();

            var toggles = Descendants<CheckBox>(window).Where(box => box.IsVisible).ToDictionary(System.Windows.Automation.AutomationProperties.GetName, box => box.IsChecked);
            Assert.True(toggles["Pause AI when a game is running"]);
            Assert.True(toggles["Pause AI when a creative app is running"]);
            Assert.True(toggles["Close Handy with Kiri"]);
            Assert.Contains(Descendants<TextBlock>(window), text => text.IsVisible && text.Text.StartsWith("Cyberpunk 2077 is running", StringComparison.Ordinal));
            Assert.Empty(errors.Messages);
            RenderFixture(Named<Grid>(window, "Root"), "settings-general-game-mode.png");
        }
        finally { window.CloseForGood(); kit.Model.Dispose(); }
    }));

    [Fact]
    public void SetupAsksAboutGames_AndSavesTheAnswer() => RunSta(() => WithTheme(() =>
    {
        using var kit = new SetupKit();
        SettingsWait(kit.Setup.InitializeAsync(detectHardware: false));
        Assert.False(kit.Setup.EnableGameMode);
        Assert.True(kit.Setup.CanContinueGameMode);

        var window = new OnboardingWindow(kit.Setup, new FakeFrameFactory(), new FakePlacement(), initialize: false) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); Pump();
            window.ShowStep(OnboardingWindow.GamingStepIndex); window.UpdateLayout(); Pump();

            Assert.Equal("Game mode", Named<TextBlock>(window, "Heading").Text);
            Assert.Equal("Next →", Named<Button>(window, "NextButton").Content);
            Assert.True(Named<Button>(window, "NextButton").IsEnabled);
            Assert.False(window.IsFinished);
            var page = Assert.Single(Descendants<GameModeSetupControl>(window));
            Assert.True(page.IsVisible);

            // Two questions about what the PC is used for, and whether to start with Windows: each a checkbox of its own, and none is answered for the user.
            var games = Named<CheckBox>(page, "GamesQuestion");
            var creativeApps = Named<CheckBox>(page, "CreativeAppsQuestion");
            var startOnBoot = Named<CheckBox>(page, "StartOnBootQuestion");
            Assert.Equal(3, Descendants<CheckBox>(page).Count(box => box.IsVisible));
            Assert.Equal(("Start Assistant on boot", false), (startOnBoot.Content, startOnBoot.IsChecked));
            Assert.Equal(["Pause AI when a game is running", "Pause AI when a creative app is running", "Start Assistant on boot"], Descendants<CheckBox>(page).Select(box => (string)box.Content));

            // Nothing is said under the questions but where to change them: no list of how it knows, no card about Handy, no word about resuming.
            var words = Descendants<TextBlock>(page).Select(text => text.Text).ToList();
            Assert.Contains("You can change either anytime in Settings → General.", words);
            Assert.DoesNotContain(words, text => text.Contains("How Kiri knows", StringComparison.Ordinal) || text.Contains("Handy is included", StringComparison.Ordinal)
                || text.Contains("middle of it", StringComparison.Ordinal) || text.Contains("ready at all times", StringComparison.Ordinal));
            Assert.False(games.IsChecked);
            Assert.False(creativeApps.IsChecked);
            RenderFixture((FrameworkElement)window.Content, "onboarding-gaming.png");

            games.IsChecked = true; window.UpdateLayout(); Pump();
            Assert.True(kit.Setup.EnableGameMode);
            Assert.False(kit.Setup.EnableCreativeApps);

            creativeApps.IsChecked = true; window.UpdateLayout(); Pump();
            Assert.True(kit.Setup.EnableCreativeApps);
            RenderFixture((FrameworkElement)window.Content, "onboarding-gaming-on.png");
        }
        finally { window.Close(); }

        Assert.True(SettingsResult(kit.Setup.ApplyGameModeAsync()));
        Assert.Equal(new GameModeSettings { Games = true, CreativeApps = true }, kit.Settings.LoadAsync().Result.GameMode);
        Assert.Equal(string.Empty, kit.Setup.Status);

        // Creative apps without games is an answer too.
        kit.Setup.EnableGameMode = false;
        Assert.True(SettingsResult(kit.Setup.ApplyGameModeAsync()));
        Assert.Equal(new GameModeSettings { CreativeApps = true }, kit.Settings.LoadAsync().Result.GameMode);
        Assert.Equal(string.Empty, kit.Setup.Status);

        kit.Setup.EnableCreativeApps = false;
        Assert.True(SettingsResult(kit.Setup.ApplyGameModeAsync()));
        Assert.Equal(new GameModeSettings(), kit.Settings.LoadAsync().Result.GameMode);
        Assert.Equal(string.Empty, kit.Setup.Status);

        // Opened again from Settings, setup shows the answers that were saved, and leaves the Handy choice made there alone.
        SettingsWait(kit.Settings.SaveAsync(kit.Settings.LoadAsync().Result with { GameMode = new GameModeSettings { Games = true, CreativeApps = true, ReleaseSharedRecognizer = false } }));
        SettingsWait(kit.Setup.InitializeAsync(detectHardware: false));
        Assert.True(kit.Setup.EnableGameMode);
        Assert.True(kit.Setup.EnableCreativeApps);
        kit.Setup.EnableCreativeApps = false;
        Assert.True(SettingsResult(kit.Setup.ApplyGameModeAsync()));
        Assert.Equal(new GameModeSettings { Games = true, ReleaseSharedRecognizer = false }, kit.Settings.LoadAsync().Result.GameMode);
    }));

    [Fact]
    public void SetupSaysThatHandyIsClosed_OnlyWhenGameModeIsOnAndHandyIsShared() => RunSta(() =>
    {
        using var kit = new SetupKit();
        kit.Setup.SelectedAsr = kit.Setup.AsrModels.Single(model => model.Id == "handy");
        Assert.False(kit.Setup.GameModeClosesHandy);

        kit.Setup.EnableGameMode = true;
        Assert.False(kit.Setup.GameModeClosesHandy);

        kit.Setup.EnableVoiceControl = true;
        Assert.True(kit.Setup.GameModeClosesHandy);

        // Creative apps alone make Kiri step aside too.
        kit.Setup.EnableGameMode = false;
        Assert.False(kit.Setup.GameModeClosesHandy);
        kit.Setup.EnableCreativeApps = true;
        Assert.True(kit.Setup.PausesForSomething);
        Assert.True(kit.Setup.GameModeClosesHandy);

        kit.Setup.SelectedAsr = kit.Setup.AsrModels.Single(model => model.Id == "windows");
        Assert.False(kit.Setup.GameModeClosesHandy);
    });

    [Fact]
    public void SuspendingTheVoice_ClosesTheMicrophoneAndUnloadsEveryVoiceModel_UntilItIsResumed() => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        SettingsWait(settings.SaveAsync(new AppSettings { Voice = new VoiceSettings { VoiceInputEnabled = true, WakeWordEnabled = true } }));
        var bus = new AppEventBus(NullLogger<AppEventBus>.Instance);
        var speech = new FakeSpeech();
        var recognizer = new CountingRecognizer();
        var wakeWord = new CountingWakeWord();
        var microphone = new QuietMicrophone();
        using var router = new MicrophoneRouter(microphone, recognizer, wakeWord);
        using var runtime = new VoiceRuntime(settings, bus, speech, recognizer, wakeWord, router, TimeProvider.System, NullLogger<VoiceRuntime>.Instance);
        var changes = 0;
        runtime.WakeWordChanged += (_, _) => Interlocked.Increment(ref changes);
        SettingsWait(runtime.StartAsync(CancellationToken.None));
        Assert.True(router.IsListeningForWakeWord);
        Assert.True(speech.KeepLoaded);
        Assert.Equal(1, microphone.Open);

        SettingsWait(runtime.SuspendAsync());

        Assert.True(runtime.IsSuspended);
        Assert.False(router.IsListeningForWakeWord);
        Assert.False(router.IsEnabled);
        Assert.Equal(VoiceRuntime.SuspendedMessage, router.UnavailableMessage);
        Assert.False(speech.KeepLoaded);
        Assert.Equal(1, recognizer.Unloads);
        Assert.Equal(1, wakeWord.Unloads);
        Assert.True(microphone.WaitUntilClosed());

        // The Voice page says why the wake word, which is on, is not listening.
        Assert.Equal(new WakeWordStatus(true, false, VoiceRuntime.SuspendedMessage), runtime.WakeWord);
        Assert.True(changes > 0);

        // Saving the settings while a game runs does not bring anything back.
        SettingsWait(bus.PublishAsync(new SettingsSaved(settings.LoadAsync().Result)));
        Assert.False(router.IsListeningForWakeWord);
        Assert.False(router.IsEnabled);
        Assert.False(speech.KeepLoaded);

        // Suspending twice is suspending once.
        SettingsWait(runtime.SuspendAsync());
        Assert.Equal(1, recognizer.Unloads);

        SettingsWait(runtime.ResumeAsync());

        Assert.False(runtime.IsSuspended);
        Assert.True(router.IsEnabled);
        Assert.Null(router.UnavailableMessage);
        Assert.True(router.IsListeningForWakeWord);
        Assert.True(speech.KeepLoaded);
        Assert.True(runtime.WakeWord.IsOn);
        Assert.NotEqual(VoiceRuntime.SuspendedMessage, runtime.WakeWord.Message);
        SettingsWait(runtime.StopAsync(CancellationToken.None));
    });

    [Fact]
    public void TheAppBuildsGameModeOverTheRealDetector_TheRealPause_AndTheVoiceItSuspends() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
        });
        using var host = Assistant.UI.Bootstrap.AppHost.Create();
        try
        {
            var services = host.Services;

            // The detector is the one that asks Windows, and it is not watching: it is started only once the settings say game mode is on.
            var detector = Assert.IsType<Assistant.Windows.Gaming.GameDetector>(services.GetRequiredService<Assistant.Core.Gaming.IGameDetector>());
            Assert.Null(detector.Current);
            Assert.True(detector.WatchGames);
            Assert.False(detector.WatchCreativeApps);

            // One controller is both what runs with the app and what the Settings page reads; it starts after the voice it suspends.
            var controller = Assert.Single(services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<GameModeController>());
            Assert.Same(controller, services.GetRequiredService<IGameMode>());
            Assert.Equal(GameModeStatus.Off, controller.Status);
            var hosted = services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToList();
            Assert.True(hosted.IndexOf(controller) > hosted.FindIndex(service => service is VoiceRuntime));

            // What it lets go of is the app's own: the voice the bar speaks with, and the model the tray's Pause Local AI pauses.
            Assert.Same(services.GetRequiredService<IVoiceRuntime>(), services.GetRequiredService<IVoiceSuspension>());
            Assert.Same(services.GetRequiredService<Assistant.Core.ModelHosting.IModelLifecycle>(), services.GetRequiredService<Assistant.Core.ModelHosting.ILocalAiPause>());
            Assert.Equal(Assistant.Core.ModelHosting.LocalAiPauseReason.None, services.GetRequiredService<Assistant.Core.ModelHosting.ILocalAiPause>().Reason);

            // And the Settings window is given it, so its General page can say what game mode is doing.
            Assert.NotNull(services.GetRequiredService<SettingsViewModel>().General);
        }
        finally
        {
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void WhileTheVoiceIsSuspended_TheMicrophoneButtonSaysItIsGameMode() => RunSta(() =>
    {
        var input = new FakeVoiceInput { Enabled = false, Unavailable = VoiceRuntime.SuspendedMessage };
        var voice = new Assistant.UI.ViewModels.VoiceInputViewModel(new FakeMicrophone(), input);

        voice.Start();

        Assert.False(voice.IsListening);
        Assert.Equal(VoiceRuntime.SuspendedMessage, voice.FailureMessage);
    });

    // Game mode as the Settings page reads it; a test sets where it stands.
    private sealed class FakeGameMode : IGameMode
    {
        public GameModeStatus Status { get; private set; } = GameModeStatus.Off;

        public event EventHandler? Changed;

        public void Set(GameModeStatus status)
        {
            Status = status;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class CountingRecognizer : ISpeechToTextService
    {
        public int Unloads { get; private set; }

        public VoiceEngineStatus Status => new(VoiceEngineState.Ready);

        public event EventHandler? StatusChanged { add { } remove { } }

        public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ISpeechRecognitionSession StartSession(SpeechRecognitionOptions? options = null) => throw new NotSupportedException();

        public void Unload() => Unloads++;

        public void Dispose()
        {
        }
    }

    private sealed class CountingWakeWord : IWakeWordService
    {
        public int Unloads { get; private set; }

        public VoiceEngineStatus Status => new(VoiceEngineState.Ready);

        public event EventHandler? StatusChanged { add { } remove { } }

        public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IWakeWordSession StartSession() => new Session();

        public void Unload() => Unloads++;

        public void Dispose()
        {
        }

        private sealed class Session : IWakeWordSession
        {
            public event EventHandler<WakeWordDetectedEventArgs>? Detected { add { } remove { } }

            public void Push(ReadOnlySpan<short> samples)
            {
            }

            public void Reset()
            {
            }

            public void Dispose()
            {
            }
        }
    }

    // A microphone that hears nothing: only whether it is open matters here.
    private sealed class QuietMicrophone : IMicrophoneAudioSource
    {
        private int _open;

        public int Open => Volatile.Read(ref _open);

        public IMicrophoneAudioSubscription Subscribe(MicrophoneAudioHandler? onSamples, Action<MicrophoneFailure> failed)
        {
            Interlocked.Increment(ref _open);
            return new Subscription(this);
        }

        // The router closes an idle microphone on the thread pool.
        public bool WaitUntilClosed() => SpinWait.SpinUntil(() => Open == 0, TimeSpan.FromSeconds(5));

        private sealed class Subscription(QuietMicrophone owner) : IMicrophoneAudioSubscription
        {
            private int _disposed;

            public double Level => 0;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref owner._open);
                }
            }
        }
    }
}
