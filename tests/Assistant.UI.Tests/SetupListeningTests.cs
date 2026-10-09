using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Settings;
using Assistant.UI.Onboarding;
using Assistant.UI.Settings;
using Assistant.Windows.Audio;
using Xunit;

namespace Assistant.UI.Tests;

// What 0.1.147 changed about listening, in setup and in Settings, under ASR: the microphone is chosen under the wake word and can be tried, a recognizer shows
// how big its download is beside its name and is used once it is downloaded, a change in Settings is used at once, and setup can go on without a voice.
public sealed partial class PromptInputControlTests
{
    // A PC like the one this was reported on: Windows' own choice is a virtual device that nothing feeds, and the real microphone is another.
    private sealed class ListedMicrophones : IMicrophoneDevices
    {
        public List<MicrophoneDevice> Devices { get; } =
        [
            new("virtual", "Virtual Microphone (Virtual Audio Device)", true),
            new("studio", "Microphone (Studio USB)", false),
        ];

        public IReadOnlyList<MicrophoneDevice> List() => [.. Devices];
    }

    private sealed class ScriptedProbe : IMicrophoneProbe
    {
        public Dictionary<string, MicrophoneProbeResult> Results { get; } = [];

        public List<string?> Tried { get; } = [];

        public Task<MicrophoneProbeResult> ListenAsync(string? deviceId, TimeSpan duration, Action<double>? level = null, CancellationToken cancellationToken = default)
        {
            Tried.Add(deviceId);
            var result = Results.GetValueOrDefault(deviceId ?? "", new MicrophoneProbeResult(0, false, null));
            level?.Invoke(result.Peak);
            return Task.FromResult(result);
        }
    }

    private static InMemorySettingsService SetUpBefore()
    {
        var settings = new InMemorySettingsService();
        SettingsWait(settings.SaveAsync(new AppSettings { Ui = new UiSettings { FirstRunCompleted = true } }));
        return settings;
    }

    [Fact]
    public void TheMicrophoneIsChosenUnderTheWakeWord_AndIsSavedAsItIsChosen() => RunSta(() => WithTheme(() =>
    {
        var microphones = new ListedMicrophones();
        using var kit = new SetupKit(microphones: microphones, probe: new ScriptedProbe());
        SettingsWait(kit.Setup.InitializeAsync(detectHardware: false));

        // The one Windows uses, then each that is connected, the default marked.
        Assert.True(kit.Setup.HasMicrophones);
        Assert.Equal(
            ["Windows default", "Virtual Microphone (Virtual Audio Device) (default)", "Microphone (Studio USB)"],
            kit.Setup.Microphones.Select(option => option.Name));
        Assert.Null(kit.Setup.SelectedMicrophone!.Id);
        Assert.Null(kit.Settings.LoadAsync().Result.Voice.MicrophoneDeviceId);

        kit.Setup.SelectedMicrophone = kit.Setup.Microphones.Single(option => option.Id == "studio");
        SettingsUntil(() => kit.Settings.LoadAsync().Result.Voice.MicrophoneDeviceId == "studio", "the microphone was saved");

        // One plugged in since is in the list when the list is looked at again, and what was chosen stays chosen.
        microphones.Devices.Add(new("headset", "Microphone (Wireless Headset)", false));
        kit.Setup.RefreshMicrophones();
        Assert.Equal(4, kit.Setup.Microphones.Count);
        Assert.Equal("studio", kit.Setup.SelectedMicrophone!.Id);
        Assert.Equal("studio", kit.Settings.LoadAsync().Result.Voice.MicrophoneDeviceId);

        // One that was chosen and is unplugged stays chosen, and is shown as that.
        microphones.Devices.RemoveAll(device => device.Id == "studio");
        kit.Setup.RefreshMicrophones();
        Assert.Equal(("studio", true), (kit.Setup.SelectedMicrophone!.Id, kit.Setup.SelectedMicrophone.IsMissing));

        // In setup it is under the wake word, with a button to try it.
        kit.Setup.EnableVoiceControl = true;
        using var errors = BindingErrors.Listen();
        var window = new OnboardingWindow(kit.Setup, new FakeFrameFactory(), new FakePlacement(), initialize: false) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.ShowStep(2); window.Show(); Pump();
            var list = Assert.Single(Descendants<ComboBox>(window), box => box.IsVisible && System.Windows.Automation.AutomationProperties.GetName(box) == "Microphone");
            var wake = Descendants<CheckBox>(window).Single(box => box.IsVisible && box.Content.ToString()!.Contains("Wake Kiri", StringComparison.Ordinal));
            Assert.True(list.TranslatePoint(default, window).Y > wake.TranslatePoint(default, window).Y);
            Assert.Same(kit.Setup.SelectedMicrophone, list.SelectedItem);
            Assert.Contains(Descendants<Button>(window), button => button.IsVisible && Equals(button.Content, "Test"));
            RenderFixture((FrameworkElement)window.Content, "onboarding-microphone.png");
            Assert.Empty(errors.Messages);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TryingAMicrophoneSaysWhetherItHeardAnything() => RunSta(() =>
    {
        var probe = new ScriptedProbe();
        probe.Results[""] = new MicrophoneProbeResult(0, false, null);
        probe.Results["studio"] = new MicrophoneProbeResult(0.09, true, null);
        probe.Results["virtual"] = new MicrophoneProbeResult(0.003, false, null);
        using var kit = new SetupKit(microphones: new ListedMicrophones(), probe: probe);
        SettingsWait(kit.Setup.InitializeAsync(detectHardware: false));
        Assert.True(kit.Setup.CanTestMicrophone);
        Assert.Equal("", kit.Setup.MicrophoneStatus);

        // Windows' own choice here is a device nothing feeds: that is said, with what to do.
        Try();
        Assert.Equal(SetupViewModel.MicrophoneSilent, kit.Setup.MicrophoneStatus);

        kit.Setup.SelectedMicrophone = kit.Setup.Microphones.Single(option => option.Id == "studio");
        Assert.Equal("", kit.Setup.MicrophoneStatus);
        Try();
        Assert.Equal(SetupViewModel.MicrophoneHeard, kit.Setup.MicrophoneStatus);

        kit.Setup.SelectedMicrophone = kit.Setup.Microphones.Single(option => option.Id == "virtual");
        Try();
        Assert.Equal(SetupViewModel.MicrophoneQuiet, kit.Setup.MicrophoneStatus);

        probe.Results["virtual"] = new MicrophoneProbeResult(0, false, MicrophoneFailure.AccessDenied);
        Try();
        Assert.Equal("Microphone access is off in Settings.", kit.Setup.MicrophoneStatus);

        // Each was tried as it was chosen, the level is put back, and the button is free again.
        Assert.Equal([null, "studio", "virtual", "virtual"], probe.Tried);
        Assert.Equal(0, kit.Setup.MicrophoneLevel);
        Assert.False(kit.Setup.IsTestingMicrophone);
        Assert.True(kit.Setup.TestMicrophoneCommand.CanExecute(null));

        void Try()
        {
            kit.Setup.TestMicrophoneCommand.Execute(null);
            SettingsUntil(() => kit.Setup.MicrophoneTest.IsCompleted, "the microphone was tried");
        }
    });

    [Fact]
    public void WithoutAListOfMicrophonesNoneIsOffered() => RunSta(() =>
    {
        using var kit = new SetupKit();
        SettingsWait(kit.Setup.InitializeAsync(detectHardware: false));

        Assert.False(kit.Setup.HasMicrophones);
        Assert.False(kit.Setup.CanTestMicrophone);
        Assert.Empty(kit.Setup.Microphones);
        Assert.False(kit.Setup.TestMicrophoneCommand.CanExecute(null));
    });

    [Fact]
    public void SetupCanGoOnWithoutAVoice_AndStillBeFinished() => RunSta(() => WithTheme(() =>
    {
        using var kit = new SetupKit();
        kit.Library.Downloaded.Add(kit.Setup.SelectedAi!.Id);
        var chosenBefore = kit.Settings.LoadAsync().Result.Voice.TextToSpeechModelId;
        Assert.False(kit.Setup.CanContinueVoice);
        Assert.False(kit.Setup.CanFinishVoice);

        var window = new OnboardingWindow(kit.Setup, new FakeFrameFactory(), new FakePlacement(), initialize: false) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            // Skip is on the voice's page only, beside Next, which still waits for a voice.
            window.Show(); Pump();
            var skip = Named<Button>(window, "SkipButton");
            Assert.False(skip.IsVisible);
            window.ShowStep(1); window.UpdateLayout(); Pump();
            Assert.True(skip.IsVisible);
            Assert.True(skip.IsEnabled);
            Assert.Equal("Skip", skip.Content);
            Assert.False(Named<Button>(window, "NextButton").IsEnabled);
            RenderFixture((FrameworkElement)window.Content, "onboarding-voice-skip.png");

            Click(skip);
            Pump();

            Assert.True(kit.Setup.SkipVoice);
            Assert.Equal("Voice mode", Named<TextBlock>(window, "Heading").Text);
            Assert.False(skip.IsVisible);
        }
        finally { window.Close(); }

        // Nothing was chosen or downloaded for it, and setup finishes.
        Assert.True(kit.Setup.CanFinishVoice);
        var finished = kit.Setup.FinishAsync();
        SettingsUntil(() => finished.IsCompleted, "setup finished without a voice");
        Assert.True(finished.Result);
        var saved = kit.Settings.LoadAsync().Result;
        Assert.True(saved.Ui.FirstRunCompleted);
        Assert.Equal(chosenBefore, saved.Voice.TextToSpeechModelId);
        Assert.Empty(kit.Speech.Responses);
    }));

    [Fact]
    public void AVoiceThatIsChosenAfterAllIsNotSkipped() => RunSta(() =>
    {
        using var kit = new SetupKit();
        kit.Setup.SkipVoice = true;
        kit.Setup.UseApi = true;
        kit.Setup.ApiEndpoint = "http://localhost:8880/v1/audio/speech";
        kit.Setup.ApiModel = "kokoro"; kit.Setup.ApiVoice = "af_heart";

        SettingsWait(kit.Setup.SaveVoiceAsync());

        Assert.False(kit.Setup.SkipVoice);
        Assert.Equal("custom-api", kit.Settings.LoadAsync().Result.Voice.TextToSpeechModelId);
    });

    [Fact]
    public void InSettingsAChangeToListeningIsUsedAtOnce_AndARecognizerIsUsedOnceItIsDownloaded() => RunSta(() =>
    {
        using var kit = new SetupKit(SetUpBefore());
        SettingsWait(kit.Setup.InitializeAsync(detectHardware: false));
        var whisper = kit.Setup.AsrModels.Single(option => option.Id == "asr-whisper-small");

        // Chosen but not there yet: nothing changes, and nothing is asked for that cannot be done.
        kit.Setup.SelectedAsr = whisper;
        kit.Setup.EnableVoiceControl = true;
        SettingsWait(kit.Setup.VoiceControlApplying);
        Assert.Equal("speech-recognition", kit.Settings.LoadAsync().Result.Voice.SpeechRecognitionModelId);

        // Downloaded, it is the recognizer: nobody has to press anything else. (This is what was missing when "it didn't recognize anything".)
        kit.Setup.DownloadRecognizerCommand.Execute(null);
        SettingsUntil(() => !kit.Setup.IsBusy, "the recognizer was downloaded");
        var voice = kit.Settings.LoadAsync().Result.Voice;
        Assert.Equal(("asr-whisper-small", true, false), (voice.SpeechRecognitionModelId, voice.VoiceInputEnabled, voice.WakeWordEnabled));

        // The wake word: on once its model is there, and off again, each as it is clicked.
        kit.Library.Downloaded.Add(kit.Setup.WakeWord.Id);
        kit.Setup.EnableWakeWord = true;
        SettingsWait(kit.Setup.VoiceControlApplying);
        Assert.True(kit.Settings.LoadAsync().Result.Voice.WakeWordEnabled);

        kit.Setup.EnableVoiceControl = false;
        SettingsWait(kit.Setup.VoiceControlApplying);
        voice = kit.Settings.LoadAsync().Result.Voice;
        Assert.Equal((false, false), (voice.VoiceInputEnabled, voice.WakeWordEnabled));

        // Another recognizer that is already there is used as it is chosen.
        kit.Setup.EnableVoiceControl = true;
        SettingsWait(kit.Setup.VoiceControlApplying);
        var windows = kit.Setup.AsrModels.Single(option => option.Id == "asr-moonshine-tiny");
        kit.Library.Downloaded.Add(windows.Id);
        kit.Setup.SelectedAsr = windows;
        SettingsWait(kit.Setup.VoiceControlApplying);
        Assert.Equal("asr-moonshine-tiny", kit.Settings.LoadAsync().Result.Voice.SpeechRecognitionModelId);
    });

    [Fact]
    public void DuringTheFirstSetupNothingAboutListeningIsSavedUntilThePageIsLeft() => RunSta(() =>
    {
        using var kit = new SetupKit();
        SettingsWait(kit.Setup.InitializeAsync(detectHardware: false));
        var whisper = kit.Setup.AsrModels.Single(option => option.Id == "asr-whisper-small");
        kit.Library.Downloaded.Add(whisper.Id);

        kit.Setup.SelectedAsr = whisper;
        kit.Setup.EnableVoiceControl = true;
        SettingsWait(kit.Setup.VoiceControlApplying);
        Assert.Equal("speech-recognition", kit.Settings.LoadAsync().Result.Voice.SpeechRecognitionModelId);

        SettingsWait(kit.Setup.SaveVoiceControlAsync());
        Assert.Equal("asr-whisper-small", kit.Settings.LoadAsync().Result.Voice.SpeechRecognitionModelId);
    });

    [Fact]
    public void ARecognizerShowsHowBigItsDownloadIsBesideItsName() => RunSta(() => WithTheme(() =>
    {
        using var kit = new SetupKit();
        var whisper = kit.Setup.AsrModels.Single(option => option.Id == "asr-whisper-small");
        Assert.Equal("488 MB", whisper.FileSize);
        Assert.Equal("1.62 GB", kit.Setup.AsrModels.Single(option => option.Id == "asr-whisper-turbo").FileSize);
        Assert.Equal("", kit.Setup.AsrModels.Single(option => option.Id == "handy").FileSize);
        Assert.Equal("", kit.Setup.AsrModels.Single(option => option.Id == "windows").FileSize);

        kit.Setup.EnableVoiceControl = true;
        kit.Setup.SelectedAsr = whisper;
        var window = new OnboardingWindow(kit.Setup, new FakeFrameFactory(), new FakePlacement(), initialize: false) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.ShowStep(2); window.Show(); Pump();
            var name = Descendants<TextBlock>(window).First(block => block.IsVisible && block.Text == "Whisper Small");
            var size = Descendants<TextBlock>(window).First(block => block.IsVisible && block.Text == "488 MB");

            // On the name's own line, to its right.
            Assert.True(size.TranslatePoint(default, window).X > name.TranslatePoint(default, window).X);
            Assert.True(Math.Abs(size.TranslatePoint(new Point(0, size.ActualHeight), window).Y - name.TranslatePoint(new Point(0, name.ActualHeight), window).Y) < 6);
            RenderFixture((FrameworkElement)window.Content, "onboarding-asr-size.png");

            // One that downloads nothing shows no size.
            kit.Setup.SelectedAsr = kit.Setup.AsrModels.Single(option => option.Id == "handy");
            window.UpdateLayout(); Pump();
            Assert.DoesNotContain(Descendants<TextBlock>(window), block => block.IsVisible && block.Text == "488 MB");
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void InSettingsTheAsrPageHasTheMicrophoneUnderTheWakeWord_NoApplyButton_AndNoLineAboutARecognizerThatIsNotChosen() => RunSta(() => WithTheme(() =>
    {
        using var kit = new SetupKit(SetUpBefore(), microphones: new ListedMicrophones(), probe: new ScriptedProbe());
        var whisper = kit.Setup.AsrModels.Single(option => option.Id == "asr-whisper-small");
        kit.Library.Downloaded.Add(whisper.Id);
        SettingsWait(kit.Settings.SaveAsync(kit.Settings.LoadAsync().Result with
        {
            Voice = new VoiceSettings { SpeechRecognitionModelId = whisper.Id, VoiceInputEnabled = true },
        }));

        // A recognizer that has not been loaded yet, and a voices folder with nothing in it: what the page was shown with when the line was wrong.
        var recognizer = new FakeRecognizer();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "assistant-asr-page-" + Guid.NewGuid().ToString("N"));
        var folders = new Assistant.Voice.VoiceModelFolders(new Assistant.Core.Storage.AppPaths(root));
        var settings = CreateSettingsKit(
            service: kit.Settings, setup: kit.Setup, microphones: new ListedMicrophones(), speech: new FakeSpeech(), recognizer: recognizer, voiceFolders: folders);
        var (window, _, _) = CreateSettingsWindow(settings);
        using var errors = BindingErrors.Listen();
        try
        {
            window.Height = 900;
            settings.Model.SelectedSection = settings.Model.Sections.Single(item => item.Section == SettingsSection.Asr);
            window.Show(); Pump();
            SettingsUntil(() => kit.Setup.Microphones.Count > 0, "the microphones were listed");
            window.UpdateLayout(); Pump();

            // One list of microphones, the setup's own, under the wake word; what is chosen there is used at once, so there is no button to press.
            var lists = Descendants<ComboBox>(window).Where(box => box.IsVisible && System.Windows.Automation.AutomationProperties.GetName(box) == "Microphone").ToList();
            var list = Assert.Single(lists);
            var wake = Descendants<CheckBox>(window).Single(box => box.IsVisible && (box.Content?.ToString() ?? "").Contains("Wake Kiri", StringComparison.Ordinal));
            Assert.True(list.TranslatePoint(default, window).Y > wake.TranslatePoint(default, window).Y);
            Assert.DoesNotContain(Descendants<Button>(window), button => button.IsVisible && Equals(button.Content, "Apply voice control"));
            Assert.DoesNotContain(Descendants<TextBlock>(window), block => block.IsVisible && block.Text == "Listen with");

            // The line that said "Not installed. Put its files in the voices folder below." was about another recognizer than the one chosen.
            Assert.DoesNotContain(Descendants<TextBlock>(window), block => block.IsVisible && block.Text.Contains("voices folder", StringComparison.Ordinal));
            Assert.DoesNotContain(Descendants<TextBlock>(window), block => block.IsVisible && block.Text == "Recognizer");
            Assert.Equal("", settings.Model.Voice.RecognizerStatus);
            Assert.False(settings.Model.Voice.RecognizerNeedsAttention);
            RenderFixture(Named<Grid>(window, "Root"), "settings-asr-microphone.png");

            // A recognizer that fails still says so, whichever it is.
            recognizer.SetStatus(new Assistant.Core.Voice.VoiceEngineStatus(Assistant.Core.Voice.VoiceEngineState.Failed, "This speech model could not run."));
            Pump();
            Assert.Equal("Failed: This speech model could not run.", settings.Model.Voice.RecognizerStatus);
            Assert.Contains(Descendants<TextBlock>(window), block => block.IsVisible && block.Text == "Failed: This speech model could not run.");
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.CloseForGood();
            settings.Model.Dispose();
            if (System.IO.Directory.Exists(root))
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
        }
    }));
}
