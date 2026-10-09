using System.IO;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.ModelHosting;
using Assistant.Core.Models;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Assistant.UI.Onboarding;
using Assistant.UI.Settings;
using Assistant.Voice;
using Assistant.Windows.Frame;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    [Fact]
    public void OlderManagedKokoroOffersUpdateAndDeleteInsteadOfUsingTheWrongSpeaker() => RunSta(() => WithTheme(() =>
    {
        using var kit = new SetupKit();
        var kokoro = kit.Setup.VoiceModels.Single(model => model.Id == "kokoro-82m-onnx");
        kit.Setup.SelectedVoice = kokoro;
        kit.Library.Managed.Add(kokoro.Id);
        var folder = kit.Library.FolderOf(kokoro.Model);
        Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "model.onnx"), "old voice fixture");
        Assert.True(kokoro.NeedsUpdate);
        Assert.False(kit.Setup.CanContinueVoice);
        Assert.True(kit.Setup.DownloadVoiceCommand.CanExecute(null));
        Assert.True(kit.Setup.DeleteCommand.CanExecute(kokoro));
        var window = new OnboardingWindow(kit.Setup, new FakeFrameFactory(), new FakePlacement(), initialize: false);
        try
        {
            window.ShowStep(1); window.Show(); Pump();
            var voice = Assert.Single(Descendants<VoiceSetupControl>(window));
            Assert.Contains(Descendants<Button>(voice), button => Equals(button.Content, "Update") && button.IsEnabled);
            Assert.Contains(Descendants<Button>(voice), button => Equals(button.Content, "Delete") && button.IsVisible);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void VoiceControlIsOptionalAndEnablingWakeWordRequiresBothDownloads() => RunSta(() =>
    {
        using var kit = new SetupKit();
        Assert.True(kit.Setup.CanContinueVoiceControl);
        kit.Setup.EnableVoiceControl = true;
        Assert.False(kit.Setup.CanContinueVoiceControl);
        kit.Library.Downloaded.Add(kit.Setup.Recognizer.Id);
        Assert.True(kit.Setup.CanContinueVoiceControl);
        kit.Setup.EnableWakeWord = true;
        Assert.False(kit.Setup.CanContinueVoiceControl);
        kit.Library.Downloaded.Add(kit.Setup.WakeWord.Id);
        SettingsWait(kit.Setup.SaveVoiceControlAsync());
        Assert.True(kit.Settings.LoadAsync().Result.Voice.VoiceInputEnabled);
        Assert.True(kit.Settings.LoadAsync().Result.Voice.WakeWordEnabled);
        kit.Setup.EnableVoiceControl = false;
        SettingsWait(kit.Setup.SaveVoiceControlAsync());
        Assert.False(kit.Settings.LoadAsync().Result.Voice.VoiceInputEnabled);
        Assert.False(kit.Settings.LoadAsync().Result.Voice.WakeWordEnabled);
    });

    [Fact]
    public void OnboardingAsksWhetherToStartOnBoot_AndWindowsIsToldOnlyWhenTheAnswerChanges() => RunSta(() =>
    {
        var launch = new FakeLaunchAtLogin();
        using var kit = new SetupKit(launch: launch);
        Assert.False(kit.Setup.StartOnBoot);

        kit.Setup.StartOnBoot = true;
        SettingsWait(kit.Setup.SaveGameModeAsync());

        Assert.True(kit.Settings.LoadAsync().Result.LaunchAtLogin.Enabled);
        Assert.Equal((1, 0), (launch.Enabled, launch.Disabled));

        // Going through setup again with the same answer changes nothing in Windows' own list of what starts.
        SettingsWait(kit.Setup.SaveGameModeAsync());
        Assert.Equal((1, 0), (launch.Enabled, launch.Disabled));

        kit.Setup.StartOnBoot = false;
        SettingsWait(kit.Setup.SaveGameModeAsync());

        Assert.False(kit.Settings.LoadAsync().Result.LaunchAtLogin.Enabled);
        Assert.Equal((1, 1), (launch.Enabled, launch.Disabled));
    });

    [Fact]
    public void SettingsScrollsWhenTheWheelIsOverModelText() => RunSta(() => WithTheme(() =>
    {
        using var kit = new SetupKit();
        var settings = CreateSettingsKit(service: kit.Settings, setup: kit.Setup);
        var (window, _, _) = CreateSettingsWindow(settings);
        try
        {
            window.Height = 550;
            settings.Model.SelectedSection = settings.Model.Sections.Single(item => item.Section == SettingsSection.Model);
            window.Show(); Pump(); window.UpdateLayout();
            var scroll = Named<Assistant.UI.Controls.FadingScrollViewer>(window, "PageScroller");
            Assert.True(scroll.ScrollableHeight > 0);
            var text = Descendants<TextBlock>(window).First(item => item.Text == "Qwen 3.5 · 4B");
            text.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
            Pump();
            Assert.True(scroll.VerticalOffset > 0);
        }
        finally { window.CloseForGood(); settings.Model.Dispose(); }
    }));

    [Fact]
    public void DownloadingAiKeepsDarkCardsAndShowsPercentageAndSpeed() => RunSta(() => WithTheme(() =>
    {
        using var kit = new SetupKit();
        var release = new TaskCompletionSource();
        kit.Library.DownloadJob = progress => { progress!.Report(new(25_000_000, 100_000_000, "Downloading…", 5_500_000)); return release.Task; };
        var frames = new FakeFrameFactory();
        var window = new OnboardingWindow(kit.Setup, frames, new FakePlacement(), initialize: false) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); Pump();
            kit.Setup.DownloadAiCommand.Execute(kit.Setup.AiModels[0]);
            Pump(); window.UpdateLayout();
            var list = Assert.Single(Descendants<ListBox>(window));
            Assert.False(list.IsEnabled);
            Assert.DoesNotContain(Descendants<Border>(list), border => border.Background is System.Windows.Media.SolidColorBrush brush && brush.Color == System.Windows.Media.Colors.White);
            Assert.Contains("25%", kit.Setup.Status);
            Assert.Contains("5.5 MB/s", kit.Setup.Status);
            Assert.Equal(new WindowFrameStyle(SystemBackdropKind.Mica, 0x3C3C3C), Assert.Single(frames.Applied).Style);
            RenderFixture((FrameworkElement)window.Content, "onboarding-downloading.png");
        }
        finally { release.SetCanceled(); SettingsUntil(() => !kit.Setup.IsBusy, "download canceled"); window.Close(); }
    }));

    [Fact]
    public void SetupRequiresAnInstalledAiAndVoiceAndOnlyFinishRecordsCompletion() => RunSta(() =>
    {
        using var kit = new SetupKit();
        Assert.False(kit.Setup.CanContinueAi);
        Assert.False(kit.Setup.CanContinueVoice);
        Assert.False(kit.Settings.LoadAsync().Result.Ui.FirstRunCompleted);
        var failed = kit.Setup.FinishAsync();
        SettingsUntil(() => failed.IsCompleted, "empty setup was rejected");
        Assert.False(failed.Result);
        kit.Library.Downloaded.Add(kit.Setup.SelectedAi!.Id);
        Assert.True(kit.Setup.CanContinueAi);
        kit.Setup.UseApi = true;
        kit.Setup.ApiEndpoint = "http://localhost:8880/v1/audio/speech";
        kit.Setup.ApiModel = "kokoro"; kit.Setup.ApiVoice = "af_heart";
        Assert.True(kit.Setup.CanContinueVoice);
        kit.Setup.AiDevice = new("Vulkan1", "Second GPU");
        var finished = kit.Setup.FinishAsync();
        SettingsUntil(() => finished.IsCompleted, "configured setup finished");
        Assert.True(finished.Result);
        var saved = kit.Settings.LoadAsync().Result;
        Assert.True(saved.Ui.FirstRunCompleted);
        Assert.Equal("Vulkan1", saved.Model.GpuDeviceId);
        Assert.True(saved.Model.UseGpuAcceleration);
        // Setup writes down no window: the model is loaded with the ordinary conversations' 8,000 tokens, and the larger only for files.
        Assert.Null(saved.Model.ContextLength);
        Assert.Equal(8000, saved.ContextLimits.NormalContextTokens);
        Assert.Equal(32000, saved.ContextLimits.HeavyContextTokens);
        Assert.Equal("custom-api", saved.Voice.TextToSpeechModelId);
        Assert.Equal("kokoro", saved.Voice.SpeechApiModel);
        Assert.NotNull(saved.Model.ModelFilePath);
    });

    [Fact]
    public void FinishingSetupLoadsTheModelAtOnceRatherThanOnTheFirstQuestion() => RunSta(() =>
    {
        var preloader = new RecordingPreloader();
        using var kit = new SetupKit(preloader: preloader);
        kit.Library.Downloaded.Add(kit.Setup.SelectedAi!.Id);
        kit.Setup.UseApi = true;
        kit.Setup.ApiEndpoint = "http://localhost:8880/v1/audio/speech";
        kit.Setup.ApiModel = "kokoro"; kit.Setup.ApiVoice = "af_heart";

        // Choosing the model on the first page does not load it yet (the voice is still to be measured), and says when it will.
        var chosen = kit.Setup.ApplyAiAsync();
        SettingsUntil(() => chosen.IsCompleted, "the model was chosen");
        Assert.True(chosen.Result);
        Assert.Equal(0, preloader.Calls);
        Assert.Equal(string.Empty, kit.Setup.Status);
        Assert.DoesNotContain("first question", kit.Setup.Status);

        // Finish loads it, in the background: setup is done at once, and the last page says the model is loading.
        var finished = kit.Setup.FinishAsync();
        SettingsUntil(() => finished.IsCompleted, "setup finished");
        Assert.True(finished.Result);
        Assert.Equal(1, preloader.Calls);
        Assert.False(kit.Setup.IsBusy);
        Assert.Equal(SetupViewModel.PreloadingStatus, kit.Setup.Status);
        Assert.False(kit.Setup.Preloading.IsCompleted);

        // Setup's footer, above Back and Next, says nothing of it: it is only good news. Settings shows the status as it is.
        Assert.Equal("", kit.Setup.FooterStatus);

        preloader.Loading!.SetResult(new ModelInfo("model", 4096));
        SettingsUntil(() => kit.Setup.Preloading.IsCompleted, "the model loaded");
        Assert.Equal(SetupViewModel.PreloadedStatus, kit.Setup.Status);
        Assert.Equal("", kit.Setup.FooterStatus);

        // After setup, a model chosen in Settings loads at once too.
        var again = kit.Setup.ApplyAiAsync();
        SettingsUntil(() => again.IsCompleted, "the model was chosen again");
        Assert.Equal(2, preloader.Calls);
        Assert.StartsWith("Model selected. Loading it now", kit.Setup.Status);

        // A load that fails is not an error of setup: the first question tries again, and the status says so.
        preloader.Loading!.SetException(new ModelHostException(ModelHostErrorCode.ModelNotFound));
        SettingsUntil(() => kit.Setup.Preloading.IsCompleted, "the failed load ended");
        Assert.Contains("could not be loaded yet", kit.Setup.Status);

        // What went wrong is said in the footer too.
        Assert.Equal(kit.Setup.Status, kit.Setup.FooterStatus);
    });

    [Fact]
    public void FailedSettingsSaveDoesNotFinishSetup() => RunSta(() =>
    {
        using var kit = new SetupKit(new FailingSetupSettings());
        kit.Library.Downloaded.Add(kit.Setup.SelectedAi!.Id);
        kit.Setup.UseApi = true; kit.Setup.ApiEndpoint = "https://example.test/speech";
        kit.Setup.AllowRemoteSpeech = true;
        kit.Setup.ApiModel = "tts"; kit.Setup.ApiVoice = "voice";
        var finished = kit.Setup.FinishAsync();
        SettingsUntil(() => finished.IsCompleted, "failed setup returned");
        Assert.False(finished.Result);
        Assert.False(kit.Settings.LoadAsync().Result.Ui.FirstRunCompleted);
        Assert.Contains("save", kit.Setup.Status);
    });

    [Fact]
    public void TestUsesChosenVoiceAndReportsMeasuredTiming() => RunSta(() =>
    {
        using var kit = new SetupKit();
        kit.Setup.UseApi = true; kit.Setup.ApiEndpoint = "https://example.test/speech";
        kit.Setup.AllowRemoteSpeech = true;
        kit.Setup.ApiModel = "tts"; kit.Setup.ApiVoice = "voice";
        kit.Speech.Results["custom-api"] = Measured("custom-api", 85, 4);
        Assert.True(kit.Setup.TestCommand.CanExecute(null));
        kit.Setup.TestCommand.Execute(null);
        SettingsUntil(() => !kit.Setup.IsBusy, "voice test finished");
        Assert.Contains("85 ms", kit.Setup.Benchmark);
        Assert.Contains("API", kit.Setup.Benchmark);
        Assert.Single(kit.Speech.Responses);
        Assert.Equal("custom-api", kit.Speech.Status.EngineId);
    });

    [Fact]
    public void UnsupportedVoiceDeviceCannotBeTestedOrFinished() => RunSta(() =>
    {
        using var kit = new SetupKit();
        kit.Library.Downloaded.Add(kit.Setup.SelectedVoice!.Id);
        var folder = kit.Library.FolderOf(kit.Setup.SelectedVoice.Model);
        Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "model.onnx"), "fixture");
        kit.Setup.VoiceDevice = new("npu", "Intel AI Boost", false, "No compatible backend");
        Assert.False(kit.Setup.CanContinueVoice);
        Assert.False(kit.Setup.TestCommand.CanExecute(null));
    });

    [Fact]
    public void RemoteSpeechRequiresAnExplicitChoiceAndPersistsIt() => RunSta(() =>
    {
        using var kit = new SetupKit();
        SettingsWait(kit.Setup.InitializeAsync(detectHardware: false));
        kit.Setup.UseApi = true;
        Assert.False(kit.Setup.CanContinueVoice);
        Assert.False(kit.Setup.TestCommand.CanExecute(null));
        kit.Setup.AllowRemoteSpeech = true;
        Assert.True(kit.Setup.CanContinueVoice);
        SettingsWait(kit.Setup.SaveVoiceAsync());
        Assert.False(kit.Settings.LoadAsync().Result.Privacy.LocalOnly);
    });

    [Fact]
    public void KeepingACustomModelPreservesItsProjectorAndTemplate() => RunSta(() =>
    {
        using var kit = new SetupKit();
        var path = Path.Combine(kit.Library.FolderOf(kit.Setup.AiModels[0].Model), "custom.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture");
        SettingsWait(kit.Settings.SaveAsync(new AppSettings { Model = new ModelSettings { ModelFilePath = path, ProjectorFilePath = Path.Combine(Path.GetDirectoryName(path)!, "custom-projector.gguf"), ChatTemplateFilePath = Path.Combine(Path.GetDirectoryName(path)!, "template.jinja") } }));
        var before = kit.Settings.LoadAsync().Result;
        SettingsWait(kit.Setup.InitializeAsync(detectHardware: false));
        Assert.True(kit.Setup.HasCustomModel);
        SettingsWait(kit.Setup.SaveAiAsync());
        var after = kit.Settings.LoadAsync().Result;
        Assert.Equal(before.Model.ProjectorFilePath, after.Model.ProjectorFilePath);
        Assert.Equal(before.Model.ChatTemplateFilePath, after.Model.ChatTemplateFilePath);
    });

    [Fact]
    public void SettingsShowsDownloadsAndDeleteWorksForBothAiAndVoice() => RunSta(() => WithTheme(() =>
    {
        using var kit = new SetupKit();
        kit.Library.Downloaded.Add(kit.Setup.AiModels[0].Id);
        kit.Library.Downloaded.Add(kit.Setup.VoiceModels[0].Id);
        var settings = CreateSettingsKit(service: kit.Settings, setup: kit.Setup);
        var (window, _, _) = CreateSettingsWindow(settings);
        using var errors = BindingErrors.Listen();
        try
        {
            window.Show(); Pump();
            foreach (var section in new[] { SettingsSection.Model, SettingsSection.Voice })
            {
                settings.Model.SelectedSection = settings.Model.Sections.Single(item => item.Section == section);
                window.UpdateLayout(); Pump();
                var library = Assert.Single(Descendants<LibraryControl>(window), control => control.IsVisible);
                Assert.Equal(2, Descendants<Button>(library).Count(button => Equals(button.Content, "Delete")));
                RenderFixture(Named<Grid>(window, "Root"), $"settings-downloads-{section.ToString().ToLowerInvariant()}.png");
            }
            Assert.Empty(errors.Messages);
            foreach (var model in kit.Setup.Installed.ToArray())
            {
                kit.Setup.DeleteCommand.Execute(model);
                SettingsUntil(() => !kit.Setup.IsBusy, "download deleted");
            }
            Assert.Empty(kit.Library.Downloaded);
            Assert.Empty(kit.Setup.Installed);
        }
        finally { window.CloseForGood(); settings.Model.Dispose(); }
    }));

    [Fact]
    public void OnboardingRendersBothPagesOnSettingsMicaAndRemainsReopenable() => RunSta(() => WithTheme(() =>
    {
        using var kit = new SetupKit();
        kit.Setup.ApiEndpoint = "https://api.openai.com/v1/audio/speech"; kit.Setup.ApiModel = "tts-1"; kit.Setup.ApiVoice = "alloy";
        kit.Setup.AiDevices.Add(new("Vulkan0", "NVIDIA GeForce RTX 5070"));
        kit.Setup.VoiceDevices.Add(new("npu", "Intel AI Boost · NPU (unsupported)", false));
        var frames = new FakeFrameFactory();
        var window = new OnboardingWindow(kit.Setup, frames, new FakePlacement(), initialize: false) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); Pump();

            // The same glass as Settings and the History window: Mica, tinted by the wallpaper, under the same neutral rim and workspace.
            Assert.Equal(new WindowFrameStyle(SystemBackdropKind.Mica, 0x3C3C3C), Assert.Single(frames.Applied).Style);
            Assert.Same(Application.Current.Resources["HistoryWorkspace"], Named<Grid>(window, "Root").Style);
            Assert.Equal(3, Descendants<ListBox>(window).Single(list => list.IsVisible).Items.Count);
            Assert.False(Named<Button>(window, "NextButton").IsEnabled);
            RenderFixture((FrameworkElement)window.Content, "onboarding-ai.png");
            window.ShowStep(1); window.UpdateLayout(); Pump();
            Assert.Equal("Next →", Named<Button>(window, "NextButton").Content);
            RenderFixture((FrameworkElement)window.Content, "onboarding-voice.png");
            kit.Setup.UseApi = true; window.UpdateLayout(); Pump();
            RenderFixture((FrameworkElement)window.Content, "onboarding-api.png");
            window.ShowStep(2); window.UpdateLayout(); Pump();
            Assert.False(window.IsFinished);
            Assert.Equal("Next →", Named<Button>(window, "NextButton").Content);
            RenderFixture((FrameworkElement)window.Content, "onboarding-voice-control.png");
            kit.Setup.EnableVoiceControl = true; kit.Setup.EnableWakeWord = true; window.UpdateLayout(); Pump();
            Assert.False(Named<Button>(window, "NextButton").IsEnabled);
            RenderFixture((FrameworkElement)window.Content, "onboarding-voice-control-enabled.png");
            window.ShowStep(OnboardingWindow.GamingStepIndex); window.UpdateLayout(); Pump();
            Assert.False(window.IsFinished);
            Assert.Equal("Next →", Named<Button>(window, "NextButton").Content);
            Assert.Equal("Game mode", Named<TextBlock>(window, "Heading").Text);
            Assert.True(Named<Button>(window, "NextButton").IsEnabled);
            window.ShowStep(OnboardingWindow.ConnectionsStepIndex); window.UpdateLayout(); Pump();
            Assert.False(window.IsFinished);
            Assert.Equal("Next →", Named<Button>(window, "NextButton").Content);
            Assert.Equal("Let's get you connected.", Named<TextBlock>(window, "Heading").Text);
            RenderFixture((FrameworkElement)window.Content, "onboarding-connections.png");
            window.ShowStep(OnboardingWindow.SearchStepIndex); window.UpdateLayout(); Pump();
            Assert.False(window.IsFinished);
            Assert.Equal("Finish", Named<Button>(window, "NextButton").Content);
            Assert.Equal(3, Descendants<ListBox>(window).Single(list => list.IsVisible).Items.Count);
            RenderFixture((FrameworkElement)window.Content, "onboarding-search.png");
            window.ShowStep(OnboardingWindow.DoneStepIndex); window.UpdateLayout(); Pump();
            Assert.True(window.IsFinished);
            Assert.True(Named<Button>(window, "NextButton").IsEnabled);
            RenderFixture((FrameworkElement)window.Content, "onboarding-done.png");
        }
        finally { window.Close(); }
        var reopened = new OnboardingWindow(kit.Setup, new FakeFrameFactory(), new FakePlacement(), initialize: false);
        try { reopened.Show(); Pump(); Assert.False(reopened.IsFinished); Assert.Equal("Choose an AI model", Named<TextBlock>(reopened, "Heading").Text); }
        finally { reopened.Close(); }
    }));

    private sealed class SetupKit : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "kiri-setup-tests-" + Guid.NewGuid().ToString("N"));
        public SetupKit(ISettingsService? settings = null, ConnectionsSetupViewModel? connections = null, SearchSetupViewModel? search = null, Assistant.UI.Voice.HandyIntegration? handy = null,
            IModelPreloader? preloader = null, Assistant.Core.Startup.ILaunchAtLogin? launch = null,
            Assistant.Windows.Audio.IMicrophoneDevices? microphones = null, Assistant.Windows.Audio.IMicrophoneProbe? probe = null)
        {
            Settings = settings ?? new InMemorySettingsService();
            var paths = new AppPaths(_root);
            Library = new SetupLibrary(paths); Speech = new FakeSpeech();
            Setup = new SetupViewModel(Settings, new AppEventBus(NullLogger<AppEventBus>.Instance), Library, new SettingsLifecycle(), Speech,
                new VoiceModelFolders(paths), new SetupSecrets(), new SettableProfile(TestPc(32)), connections: connections, search: search, handy: handy,
                preloader: preloader, launchAtLogin: launch, microphones: microphones, microphoneProbe: probe);
        }
        public SetupViewModel Setup { get; }
        public ISettingsService Settings { get; }
        public SetupLibrary Library { get; }
        public FakeSpeech Speech { get; }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private sealed class SetupLibrary(AppPaths paths) : IModelLibrary
    {
        public Func<IProgress<ModelDownloadProgress>?, Task>? DownloadJob { get; set; }
        public HashSet<string> Downloaded { get; } = [];
        public HashSet<string> Managed { get; } = [];
        public bool HasManagedFiles(DownloadableModel model) => Downloaded.Contains(model.Id) || Managed.Contains(model.Id);
        public string FolderOf(DownloadableModel model) => Path.Combine(model.Kind == DownloadKind.Voice ? paths.VoicesDirectory : paths.ModelsDirectory, model.Id);
        public bool IsInstalled(DownloadableModel model) => Downloaded.Contains(model.Id);
        public Task DownloadAsync(DownloadableModel model, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken = default) { if (DownloadJob is { } job) return job(progress); Downloaded.Add(model.Id); return Task.CompletedTask; }
        public Task DeleteAsync(DownloadableModel model, CancellationToken cancellationToken = default) { Downloaded.Remove(model.Id); return Task.CompletedTask; }
    }
    // A model load a test finishes by hand: each call starts a new one.
    private sealed class RecordingPreloader : IModelPreloader
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<ModelInfo?>? Loading { get; private set; }

        public Task<ModelInfo?> PreloadAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            Loading = new TaskCompletionSource<ModelInfo?>();
            return Loading.Task;
        }
    }

    private sealed class SetupSecrets : ISecretStore
    {
        public Task SetAsync(string name, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
    private sealed class FailingSetupSettings : ISettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.FromException(new IOException("Could not save settings."));
    }
}
